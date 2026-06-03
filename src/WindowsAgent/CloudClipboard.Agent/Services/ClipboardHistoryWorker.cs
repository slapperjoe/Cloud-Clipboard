using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CloudClipboard.Agent.Options;
using CloudClipboard.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudClipboard.Agent.Services;

public sealed class ClipboardHistoryWorker : BackgroundService
{
    private readonly ICloudClipboardClient _client;
    private readonly IClipboardHistoryCache _cache;
    private readonly ILogger<ClipboardHistoryWorker> _logger;
    private readonly IOptionsMonitor<AgentOptions> _options;
    private readonly ClipboardPasteService _pasteService;
    private readonly IOwnerStateCache _ownerStateCache;
    private bool _autoPastePerformed;
    private DateTimeOffset? _lastRefreshUtc;

    public ClipboardHistoryWorker(
        ICloudClipboardClient client,
        IClipboardHistoryCache cache,
        ILogger<ClipboardHistoryWorker> logger,
        IOptionsMonitor<AgentOptions> options,
        ClipboardPasteService pasteService,
        IOwnerStateCache ownerStateCache)
    {
        _client = client;
        _cache = cache;
        _logger = logger;
        _options = options;
        _pasteService = pasteService;
        _ownerStateCache = ownerStateCache;
        _cache.HistoryChanged += OnHistoryChanged;
    }

    private int _consecutiveConnectionErrors;
    private const int MaxBackoffSeconds = 30;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var options = _options.CurrentValue;
                if (string.IsNullOrWhiteSpace(options.OwnerId))
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }

                if (!options.IsDownloadEnabled)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(2, options.HistoryPollSeconds)), stoppingToken);
                    continue;
                }

                var pushActive = options.EnablePushNotifications && options.NotificationTransport == NotificationTransport.PubSub;
                var threshold = pushActive ? TimeSpan.FromMinutes(10) : TimeSpan.Zero;
                if (pushActive && _cache.Snapshot.Count > 0 && _lastRefreshUtc.HasValue)
                {
                    var sinceLast = DateTimeOffset.UtcNow - _lastRefreshUtc.Value;
                    if (sinceLast < threshold)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                        continue;
                    }
                }

                var items = await _client.ListAsync(options.OwnerId, options.HistoryLength, stoppingToken);
                _cache.Update(items);
                _lastRefreshUtc = DateTimeOffset.UtcNow;
                _consecutiveConnectionErrors = 0;

                if (!_autoPastePerformed && !_ownerStateCache.IsPaused && options.AutoPasteLatestOnStartup && items.Count > 0)
                {
                    var latest = items[0];
                    var fullItem = await _client.DownloadAsync(options.OwnerId, latest.Id, stoppingToken);
                    if (fullItem is not null)
                    {
                        await _pasteService.PasteAsync(fullItem);
                        _autoPastePerformed = true;
                    }
                }
                else if (_ownerStateCache.IsPaused && options.AutoPasteLatestOnStartup)
                {
                    _logger.LogInformation("Skipping auto-paste because uploads are paused for owner {OwnerId}", options.OwnerId);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to refresh clipboard history");

                if (IsConnectionRefused(ex))
                {
                    _consecutiveConnectionErrors++;
                    var backoff = Math.Min(1 << (_consecutiveConnectionErrors - 1), MaxBackoffSeconds);
                    _logger.LogWarning("Connection refused ({Count} consecutive errors) — backing off for {Seconds}s.", _consecutiveConnectionErrors, backoff);
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(backoff), stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    continue;
                }
            }

            var delay = TimeSpan.FromSeconds(Math.Max(2, _options.CurrentValue.HistoryPollSeconds));
            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static bool IsConnectionRefused(Exception ex)
    {
        var current = ex;
        while (current is not null)
        {
            if (current is System.Net.Http.HttpRequestException or System.Net.Sockets.SocketException)
                return true;
            current = current.InnerException;
        }
        return false;
    }

    public override void Dispose()
    {
        _cache.HistoryChanged -= OnHistoryChanged;
        base.Dispose();
    }

    private void OnHistoryChanged(object? sender, IReadOnlyList<ClipboardItemDto> _) => _lastRefreshUtc = DateTimeOffset.UtcNow;
}
