using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CloudClipboard.Agent.Windows.Options;
using CloudClipboard.Core.Models;

namespace CloudClipboard.Agent.Windows.Services;

public sealed class ClipboardSyncWorker : BackgroundService
{
    private readonly IClipboardUploadQueue _queue;
    private readonly ICloudClipboardClient _client;
    private readonly ILogger<ClipboardSyncWorker> _logger;
    private readonly IOwnerStateCache _ownerStateCache;
    private readonly IAgentDiagnostics _diagnostics;
    private readonly IOptionsMonitor<AgentOptions> _optionsMonitor;
    private readonly IClipboardHistoryCache _historyCache;
    private readonly ILocalUploadTracker _localUploadTracker;

    public ClipboardSyncWorker(
        IClipboardUploadQueue queue,
        ICloudClipboardClient client,
        IOwnerStateCache ownerStateCache,
        ILogger<ClipboardSyncWorker> logger,
        IAgentDiagnostics diagnostics,
        IOptionsMonitor<AgentOptions> optionsMonitor,
        IClipboardHistoryCache historyCache,
        ILocalUploadTracker localUploadTracker)
    {
        _queue = queue;
        _client = client;
        _ownerStateCache = ownerStateCache;
        _logger = logger;
        _diagnostics = diagnostics;
        _optionsMonitor = optionsMonitor;
        _historyCache = historyCache;
        _localUploadTracker = localUploadTracker;
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

                var uploadedItem = await _client.UploadAsync(request, stoppingToken).ConfigureAwait(false);
                _logger.LogInformation("Uploaded clipboard item for owner {OwnerId}", request.OwnerId);
                var elapsed = TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - uploadStart) / (double)Stopwatch.Frequency);
                _diagnostics.RecordUploadSuccess(DateTimeOffset.UtcNow, elapsed);
                if (uploadedItem is not null)
                {
                    _localUploadTracker.Record(request.OwnerId, uploadedItem.Id, uploadedItem.CreatedUtc);
                    UpdateHistoryAfterUpload(uploadedItem);
                }
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

    private void UpdateHistoryAfterUpload(ClipboardItemDto uploaded)
    {
        try
        {
            var snapshot = _historyCache.Snapshot;
            var maxItems = Math.Max(1, _optionsMonitor.CurrentValue.HistoryLength);
            var list = new List<ClipboardItemDto>(Math.Min(maxItems, snapshot.Count + 1)) { uploaded };

            foreach (var item in snapshot)
            {
                if (string.Equals(item.Id, uploaded.Id, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                list.Add(item);
                if (list.Count >= maxItems)
                {
                    break;
                }
            }

            _historyCache.Update(list);
        }
        catch
        {
            // Non-critical: history will refresh via polling or push.
        }
    }
}
