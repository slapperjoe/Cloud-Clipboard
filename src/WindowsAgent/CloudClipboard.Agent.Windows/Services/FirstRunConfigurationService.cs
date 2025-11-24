using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CloudClipboard.Agent.Windows.Configuration;
using CloudClipboard.Agent.Windows.Options;
using CloudClipboard.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;


namespace CloudClipboard.Agent.Windows.Services;

public sealed class FirstRunConfigurationService : BackgroundService
{
    private readonly ILogger<FirstRunConfigurationService> _logger;
    private readonly IAgentSettingsStore _settingsStore;
    private readonly ICloudClipboardClient _client;
    private readonly IAzureCliAuthenticator _authenticator;

    public FirstRunConfigurationService(
        ILogger<FirstRunConfigurationService> logger,
        IAgentSettingsStore settingsStore,
        ICloudClipboardClient client,
        IAzureCliAuthenticator authenticator)
    {
        _logger = logger;
        _settingsStore = settingsStore;
        _client = client;
        _authenticator = authenticator;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await BootstrapAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "First-run configuration bootstrap failed.");
        }
    }

    private async Task BootstrapAsync(CancellationToken cancellationToken)
    {
        var settingsPath = AgentSettingsPathProvider.GetSettingsPath();
        if (File.Exists(settingsPath))
        {
            _logger.LogDebug("Agent settings already exist at {SettingsPath}; bootstrap skipped.", settingsPath);
            return;
        }

        var principal = await _authenticator.EnsureLoginAsync(cancellationToken).ConfigureAwait(false);
        if (principal is null)
        {
            _logger.LogWarning("Azure login was not completed; initial configuration will not be created.");
            return;
        }

        var ownerId = OwnerIdGenerator.CreateOwnerIdFromLogin(principal.LoginName);
        var remoteOptions = await TryLoadRemoteAsync(ownerId, cancellationToken).ConfigureAwait(false);
        if (remoteOptions is null)
        {
            _logger.LogWarning("No remote configuration exists for owner {OwnerId}. Provision settings via the Functions API, then restart the agent.", ownerId);
            return;
        }

        remoteOptions.OwnerId = ownerId;
        AgentOptionsJson.Normalize(remoteOptions);
        _settingsStore.Save(remoteOptions, BackupScope.Sync);
        _logger.LogInformation("Imported remote configuration for owner {OwnerId} and created agentsettings.json.", ownerId);
    }

    private async Task<AgentOptions?> TryLoadRemoteAsync(string ownerId, CancellationToken cancellationToken)
    {
        try
        {
            OwnerConfiguration? remote = await _client.GetOwnerConfigurationAsync(ownerId, cancellationToken).ConfigureAwait(false);
            if (remote is null || string.IsNullOrWhiteSpace(remote.ConfigurationJson))
            {
                _logger.LogInformation("No remote configuration found for {OwnerId} during bootstrap.", ownerId);
                return null;
            }

            var parsed = AgentOptionsJson.Deserialize(remote.ConfigurationJson);
            if (parsed is null)
            {
                _logger.LogWarning("Remote configuration for {OwnerId} is invalid JSON during bootstrap.", ownerId);
                return null;
            }

            AgentOptionsJson.Normalize(parsed);
            parsed.OwnerId = ownerId;
            _logger.LogInformation("Downloaded remote configuration for {OwnerId} during bootstrap.", ownerId);
            return parsed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download remote configuration for {OwnerId}.", ownerId);
            return null;
        }
    }
}
