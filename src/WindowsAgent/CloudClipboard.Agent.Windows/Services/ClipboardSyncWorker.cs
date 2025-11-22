using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CloudClipboard.Agent.Windows.Options;

namespace CloudClipboard.Agent.Windows.Services;

public sealed class ClipboardSyncWorker : BackgroundService
{
    private readonly IClipboardUploadQueue _queue;
    private readonly ICloudClipboardClient _client;
    private readonly ILogger<ClipboardSyncWorker> _logger;
    private readonly IOwnerStateCache _ownerStateCache;
    private readonly IAgentDiagnostics _diagnostics;
    private readonly IOptionsMonitor<AgentOptions> _optionsMonitor;

    public ClipboardSyncWorker(
        IClipboardUploadQueue queue,
        ICloudClipboardClient client,
        IOwnerStateCache ownerStateCache,
        ILogger<ClipboardSyncWorker> logger,
        IAgentDiagnostics diagnostics,
        IOptionsMonitor<AgentOptions> optionsMonitor)
    {
        _queue = queue;
        _client = client;
        _ownerStateCache = ownerStateCache;
        _logger = logger;
        _diagnostics = diagnostics;
        _optionsMonitor = optionsMonitor;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var request in _queue.ReadAllAsync(stoppingToken))
        {
            _diagnostics.DecrementPendingUploads();
            try
            {
                await WaitForUploadsAsync(stoppingToken).ConfigureAwait(false);
                var uploadStart = Stopwatch.GetTimestamp();
                if (_ownerStateCache.IsPaused)
                {
                    _logger.LogInformation("Owner {OwnerId} paused. Waiting before uploading.", request.OwnerId);
                    await _ownerStateCache.WaitForResumeAsync(stoppingToken);
                }

                await _client.UploadAsync(request, stoppingToken);
                _logger.LogInformation("Uploaded clipboard item for owner {OwnerId}", request.OwnerId);
                var elapsed = TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - uploadStart) / (double)Stopwatch.Frequency);
                _diagnostics.RecordUploadSuccess(DateTimeOffset.UtcNow, elapsed);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to upload clipboard item");
                _diagnostics.RecordUploadFailure(DateTimeOffset.UtcNow);
            }
        }
    }

    private async Task WaitForUploadsAsync(CancellationToken stoppingToken)
    {
        var logged = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            var options = _optionsMonitor.CurrentValue;
            if (options.IsUploadEnabled)
            {
                if (logged)
                {
                    _logger.LogInformation("Uploads re-enabled; resuming clipboard dispatch.");
                }
                return;
            }

            if (!logged)
            {
                _logger.LogInformation("Uploads disabled by sync direction; holding queue until uploads resume.");
                logged = true;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
        }
    }
}
