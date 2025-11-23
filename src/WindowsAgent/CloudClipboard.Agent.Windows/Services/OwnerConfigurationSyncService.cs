using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using CloudClipboard.Agent.Windows.Configuration;
using CloudClipboard.Agent.Windows.Options;
using CloudClipboard.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudClipboard.Agent.Windows.Services;

public sealed class OwnerConfigurationSyncService : BackgroundService, IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly ILogger<OwnerConfigurationSyncService> _logger;
    private readonly ICloudClipboardClient _client;
    private readonly IAgentSettingsStore _settingsStore;
    private readonly IOptionsMonitor<AgentOptions> _options;
    private readonly Channel<AgentOptions> _uploadQueue = Channel.CreateBounded<AgentOptions>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        AllowSynchronousContinuations = false,
        FullMode = BoundedChannelFullMode.DropOldest
    });

    private IDisposable? _optionsSubscription;
    private DateTimeOffset _suppressUploadsUntil = DateTimeOffset.MinValue;
    private volatile bool _initialSyncCompleted;

    public OwnerConfigurationSyncService(
        ILogger<OwnerConfigurationSyncService> logger,
        ICloudClipboardClient client,
        IAgentSettingsStore settingsStore,
        IOptionsMonitor<AgentOptions> options)
    {
        _logger = logger;
        _client = client;
        _settingsStore = settingsStore;
        _options = options;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _optionsSubscription = _options.OnChange(OnOptionsChanged);
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await WaitForOwnerIdAsync(stoppingToken).ConfigureAwait(false);
        if (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        await SyncFromServerAsync(stoppingToken).ConfigureAwait(false);
        _initialSyncCompleted = true;

        while (!stoppingToken.IsCancellationRequested)
        {
            AgentOptions snapshot;
            try
            {
                snapshot = await _uploadQueue.Reader.ReadAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await UploadAsync(snapshot, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task WaitForOwnerIdAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (!string.IsNullOrWhiteSpace(_options.CurrentValue.OwnerId))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
        }
    }

    private async Task SyncFromServerAsync(CancellationToken cancellationToken)
    {
        var ownerId = _options.CurrentValue.OwnerId;
        if (string.IsNullOrWhiteSpace(ownerId))
        {
            _logger.LogDebug("OwnerId not configured; skipping remote configuration download.");
            return;
        }

        try
        {
            OwnerConfiguration? remote = await _client.GetOwnerConfigurationAsync(ownerId, cancellationToken).ConfigureAwait(false);
            if (remote is null || string.IsNullOrWhiteSpace(remote.ConfigurationJson))
            {
                _logger.LogInformation("No remote configuration found for {OwnerId}; uploading local settings.", ownerId);
                await UploadAsync(CloneOptions(_options.CurrentValue), cancellationToken).ConfigureAwait(false);
                return;
            }

            var remoteOptions = DeserializeOptions(remote.ConfigurationJson);
            if (remoteOptions is null)
            {
                _logger.LogWarning("Remote configuration for {OwnerId} is invalid JSON; skipping import.", ownerId);
                return;
            }

            NormalizeOptions(remoteOptions);
            remoteOptions.OwnerId = ownerId;

            if (AreEquivalent(remoteOptions, _options.CurrentValue))
            {
                _logger.LogDebug("Remote configuration already matches local settings for {OwnerId}.", ownerId);
                return;
            }

            _logger.LogInformation("Applying remote configuration for {OwnerId} (updated {UpdatedUtc:O}).", ownerId, remote.UpdatedUtc);
            _suppressUploadsUntil = DateTimeOffset.UtcNow.AddSeconds(2);
            _settingsStore.Save(remoteOptions, BackupScope.Sync);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to synchronize configuration for {OwnerId}", ownerId);
        }
    }

    private async Task UploadAsync(AgentOptions options, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.OwnerId))
        {
            _logger.LogDebug("Skipping configuration upload because OwnerId is not set.");
            return;
        }

        try
        {
            NormalizeOptions(options);
            var payload = SerializeOptions(options);
            await _client.SetOwnerConfigurationAsync(options.OwnerId, payload, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Uploaded configuration for {OwnerId}.", options.OwnerId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to upload configuration for {OwnerId}", options.OwnerId);
        }
    }

    private void OnOptionsChanged(AgentOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.OwnerId))
        {
            return;
        }

        if (!_initialSyncCompleted)
        {
            // Queue the latest snapshot but let ExecuteAsync finish the remote download first.
            _uploadQueue.Writer.TryWrite(CloneOptions(options));
            return;
        }

        if (DateTimeOffset.UtcNow <= _suppressUploadsUntil)
        {
            _logger.LogDebug("Suppressing configuration upload triggered by remote sync.");
            return;
        }

        if (!_uploadQueue.Writer.TryWrite(CloneOptions(options)))
        {
            _logger.LogDebug("Configuration upload queue is full; overwriting pending snapshot.");
        }
    }

    private static void NormalizeOptions(AgentOptions options)
    {
        options.PinnedItems ??= new();
        var defaults = FunctionsDeploymentOptions.CreateDefault();
        options.FunctionsDeployment ??= defaults;
        if (string.IsNullOrWhiteSpace(options.FunctionsDeployment.PackagePath))
        {
            options.FunctionsDeployment.PackagePath = defaults.PackagePath;
        }
    }

    private static AgentOptions? DeserializeOptions(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<AgentOptions>(json, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string SerializeOptions(AgentOptions options)
        => JsonSerializer.Serialize(options, SerializerOptions);

    private static AgentOptions CloneOptions(AgentOptions source)
    {
        var json = SerializeOptions(source);
        return JsonSerializer.Deserialize<AgentOptions>(json, SerializerOptions)!;
    }

    private static bool AreEquivalent(AgentOptions left, AgentOptions right)
        => string.Equals(SerializeOptions(left), SerializeOptions(right), StringComparison.Ordinal);

    public override void Dispose()
    {
        base.Dispose();
        _optionsSubscription?.Dispose();
        _uploadQueue.Writer.TryComplete();
    }
}
