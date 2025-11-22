using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CloudClipboard.Agent.Windows.Interop;
using CloudClipboard.Agent.Windows.Options;
using CloudClipboard.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudClipboard.Agent.Windows.Services;

public sealed class ClipboardCaptureService : BackgroundService
{
    private readonly ILogger<ClipboardCaptureService> _logger;
    private readonly IOptionsMonitor<AgentOptions> _optionsMonitor;
    private readonly IClipboardUploadQueue _queue;
    private readonly IOwnerStateCache _ownerStateCache;
    private readonly IManualUploadStore _manualUploadStore;
    private readonly IAgentDiagnostics _diagnostics;
    private string? _lastSignature;

    public ClipboardCaptureService(
        ILogger<ClipboardCaptureService> logger,
        IOptionsMonitor<AgentOptions> options,
        IClipboardUploadQueue queue,
        IOwnerStateCache ownerStateCache,
        IManualUploadStore manualUploadStore,
        IAgentDiagnostics diagnostics)
    {
        _logger = logger;
        _queue = queue;
        _optionsMonitor = options;
        _ownerStateCache = ownerStateCache;
        _manualUploadStore = manualUploadStore;
        _diagnostics = diagnostics;
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

                var snapshot = StaThreadRunner.Run(CaptureSnapshot);
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

    private ClipboardSnapshot? CaptureSnapshot()
    {
        if (Clipboard.ContainsImage())
        {
            using var image = Clipboard.GetImage();
            if (image is null)
            {
                return null;
            }

            using var memoryStream = new MemoryStream();
            image.Save(memoryStream, ImageFormat.Png);
            var buffer = memoryStream.ToArray();
            var descriptor = new ClipboardPayloadDescriptor(
                ClipboardPayloadType.Image,
                new[]
                {
                    new ClipboardPayloadPart(
                        "clipboard.png",
                        "image/png",
                        buffer.LongLength,
                        () => new MemoryStream(buffer, writable: false))
                },
                PreferredContentType: "image/png");

            var options = _optionsMonitor.CurrentValue;
            var request = new ClipboardUploadRequest(options.OwnerId, descriptor, options.DeviceName, "clipboard.png");
            return new ClipboardSnapshot(request, BuildHash(buffer, prefix: "img"));
        }

        if (Clipboard.ContainsFileDropList())
        {
            var files = Clipboard.GetFileDropList();
            if (files is { Count: > 0 })
            {
                var parts = new List<ClipboardPayloadPart>();
                var signatureBuilder = new StringBuilder();
                foreach (string? path in files)
                {
                    if (path is null || !File.Exists(path))
                    {
                        continue;
                    }

                    var info = new FileInfo(path);
                    signatureBuilder.Append(info.FullName)
                        .Append('|')
                        .Append(info.Length)
                        .Append('|')
                        .Append(info.LastWriteTimeUtc.Ticks)
                        .Append(';');

                    parts.Add(new ClipboardPayloadPart(
                        Name: info.Name,
                        ContentType: ContentTypeGuesser.GuessFromExtension(info.Extension),
                        Length: info.Length,
                        StreamFactory: () => File.OpenRead(info.FullName)));
                }

                if (parts.Count > 0)
                {
                    var descriptor = new ClipboardPayloadDescriptor(
                        ClipboardPayloadType.FileSet,
                        parts,
                        PreferredContentType: "application/zip");
                    var options = _optionsMonitor.CurrentValue;
                    var request = new ClipboardUploadRequest(options.OwnerId, descriptor, options.DeviceName, "files.zip");
                    return new ClipboardSnapshot(request, BuildHash(Encoding.UTF8.GetBytes(signatureBuilder.ToString()), prefix: "files"));
                }
            }
        }

        if (Clipboard.ContainsText())
        {
            var text = Clipboard.GetText(TextDataFormat.UnicodeText);
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
        }

        return null;
    }

    private static string BuildHash(ReadOnlySpan<byte> buffer, string prefix)
    {
        var hash = SHA256.HashData(buffer);
        return $"{prefix}:{Convert.ToHexString(hash)}";
    }

    private sealed record ClipboardSnapshot(ClipboardUploadRequest Request, string Signature);

    private static class ContentTypeGuesser
    {
        private static readonly IReadOnlyDictionary<string, string> Map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".txt"] = "text/plain",
            [".json"] = "application/json",
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".pdf"] = "application/pdf"
        };

        public static string GuessFromExtension(string extension)
            => Map.TryGetValue(extension, out var value) ? value : "application/octet-stream";
    }
}
