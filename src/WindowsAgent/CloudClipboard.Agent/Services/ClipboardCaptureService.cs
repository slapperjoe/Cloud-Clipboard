using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CloudClipboard.Agent.Options;
using CloudClipboard.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudClipboard.Agent.Services;

public sealed class ClipboardCaptureService : BackgroundService
{
    private readonly ILogger<ClipboardCaptureService> _logger;
    private readonly IOptionsMonitor<AgentOptions> _optionsMonitor;
    private readonly IClipboardUploadQueue _queue;
    private readonly IOwnerStateCache _ownerStateCache;
    private readonly IManualUploadStore _manualUploadStore;
    private readonly IAgentDiagnostics _diagnostics;
    private readonly IClipboardAccess _clipboardAccess;
    private string? _lastSignature;

    public ClipboardCaptureService(
        ILogger<ClipboardCaptureService> logger,
        IOptionsMonitor<AgentOptions> options,
        IClipboardUploadQueue queue,
        IOwnerStateCache ownerStateCache,
        IManualUploadStore manualUploadStore,
        IAgentDiagnostics diagnostics,
        IClipboardAccess clipboardAccess)
    {
        _logger = logger;
        _queue = queue;
        _optionsMonitor = options;
        _ownerStateCache = ownerStateCache;
        _manualUploadStore = manualUploadStore;
        _diagnostics = diagnostics;
        _clipboardAccess = clipboardAccess;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var options = _optionsMonitor.CurrentValue;
                if (string.IsNullOrWhiteSpace(options.OwnerId))
                {
                    _logger.LogWarning("Agent OwnerId not configured. Clipboard capture paused.");
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }

                if (!options.IsUploadEnabled)
                {
                    _logger.LogDebug("Uploads disabled by sync direction; skipping capture tick for {OwnerId}", options.OwnerId);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.PollIntervalSeconds)), stoppingToken);
                    continue;
                }

                if (_ownerStateCache.IsPaused)
                {
                    _logger.LogDebug("Owner {OwnerId} is paused. Skipping capture tick.", options.OwnerId);
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    continue;
                }

                if (options.UploadMode == ClipboardUploadMode.Auto && _manualUploadStore.TryTake(out var stagedAutoRequest) && stagedAutoRequest is not null)
                {
                    await _queue.EnqueueAsync(stagedAutoRequest, stoppingToken);
                    _logger.LogInformation("Uploaded cached clipboard payload after switching to auto mode for owner {OwnerId}", options.OwnerId);
                    _diagnostics.IncrementPendingUploads();
                }

                var snapshot = await CaptureSnapshotAsync();
                if (snapshot is not null && snapshot.Signature != _lastSignature)
                {
                    _lastSignature = snapshot.Signature;
                    _diagnostics.RecordCapture(DateTimeOffset.UtcNow);
                    if (options.UploadMode == ClipboardUploadMode.Manual)
                    {
                        _manualUploadStore.Store(snapshot.Request);
                        _logger.LogInformation("Cached clipboard payload {Signature} for manual upload mode", snapshot.Signature);
                    }
                    else
                    {
                        await _queue.EnqueueAsync(snapshot.Request, stoppingToken);
                        _logger.LogInformation("Queued clipboard payload {Signature}", snapshot.Signature);
                        _diagnostics.IncrementPendingUploads();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to inspect clipboard contents");
            }

            var delay = TimeSpan.FromSeconds(Math.Max(1, _optionsMonitor.CurrentValue.PollIntervalSeconds));
            await Task.Delay(delay, stoppingToken);
        }
    }

    private async Task<ClipboardSnapshot?> CaptureSnapshotAsync()
    {
        // Try image first
        var imageBytes = await _clipboardAccess.ReadImageAsync();
        if (imageBytes is not null)
        {
            var descriptor = new ClipboardPayloadDescriptor(
                ClipboardPayloadType.Image,
                new[]
                {
                    new ClipboardPayloadPart(
                        "clipboard.png",
                        "image/png",
                        imageBytes.LongLength,
                        () => new MemoryStream(imageBytes, writable: false))
                },
                PreferredContentType: "image/png");

            var options = _optionsMonitor.CurrentValue;
            var request = new ClipboardUploadRequest(options.OwnerId, descriptor, options.DeviceName, "clipboard.png");
            return new ClipboardSnapshot(request, BuildHash(imageBytes, prefix: "img"));
        }

        // Try text
        var text = await _clipboardAccess.ReadTextAsync();
        if (!string.IsNullOrEmpty(text))
        {
            var buffer = Encoding.UTF8.GetBytes(text);
            var descriptor = new ClipboardPayloadDescriptor(
                ClipboardPayloadType.Text,
                new[]
                {
                    new ClipboardPayloadPart(
                        "clipboard.txt",
                        "text/plain; charset=utf-8",
                        buffer.LongLength,
                        () => new MemoryStream(buffer, writable: false))
                },
                PreferredContentType: "text/plain; charset=utf-8");
            var options = _optionsMonitor.CurrentValue;
            var request = new ClipboardUploadRequest(options.OwnerId, descriptor, options.DeviceName, "clipboard.txt");
            return new ClipboardSnapshot(request, BuildHash(buffer, prefix: "text"));
        }

        return null;
    }

    private static string BuildHash(ReadOnlySpan<byte> buffer, string prefix)
    {
        var hash = SHA256.HashData(buffer);
        return $"{prefix}:{Convert.ToHexString(hash)}";
    }

    private sealed record ClipboardSnapshot(ClipboardUploadRequest Request, string Signature);
}
