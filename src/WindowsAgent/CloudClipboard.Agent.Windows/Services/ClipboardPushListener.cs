using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.WebPubSub.Clients;
using CloudClipboard.Agent.Windows.Options;
using CloudClipboard.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudClipboard.Agent.Windows.Services;

public sealed class ClipboardPushListener : BackgroundService
{
    private readonly ILogger<ClipboardPushListener> _logger;
    private readonly IOptionsMonitor<AgentOptions> _options;
    private readonly ICloudClipboardClient _client;
    private readonly IClipboardHistoryCache _historyCache;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public ClipboardPushListener(
        ILogger<ClipboardPushListener> logger,
        IOptionsMonitor<AgentOptions> options,
        ICloudClipboardClient client,
        IClipboardHistoryCache historyCache)
    {
        _logger = logger;
        _options = options;
        _client = client;
        _historyCache = historyCache;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!ShouldConnect(out var ownerId))
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                continue;
            }

            var transport = _options.CurrentValue.NotificationTransport;

            try
            {
                if (transport == NotificationTransport.PubSub)
                {
                    var handled = await RunPubSubLoopAsync(ownerId, stoppingToken).ConfigureAwait(false);
                    if (!handled)
                    {
                        var fallbackWindow = TimeSpan.FromSeconds(Math.Max(5, _options.CurrentValue.PushReconnectSeconds));
                        await PollLoopAsync(ownerId, stoppingToken, fallbackWindow, ignoreTransportPreference: true).ConfigureAwait(false);
                    }
                }
                else
                {
                    await PollLoopAsync(ownerId, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // Swallow and retry if the host did not request shutdown.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Notification listener failed for {OwnerId}", ownerId);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private bool ShouldConnect(out string ownerId)
    {
        var options = _options.CurrentValue;
        ownerId = options.OwnerId;
        return options.EnablePushNotifications && options.IsDownloadEnabled && !string.IsNullOrWhiteSpace(ownerId);
    }

    private async Task PollLoopAsync(string ownerId, CancellationToken cancellationToken, TimeSpan? maxRun = null, bool ignoreTransportPreference = false)
    {
        // Long-poll the Functions notifications endpoint so uploads fan out quickly without WebSockets.
        var timeoutSeconds = GetPollTimeoutSeconds();
        _logger.LogInformation("Starting notification polling for {OwnerId}", ownerId);
        var started = DateTimeOffset.UtcNow;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!ShouldConnect(out var currentOwner) || !string.Equals(currentOwner, ownerId, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Stopping notification polling for {OwnerId}", ownerId);
                return;
            }

            if (!ignoreTransportPreference && _options.CurrentValue.NotificationTransport != NotificationTransport.Polling)
            {
                _logger.LogInformation("Notification transport switched to {Transport}; exiting polling loop for {OwnerId}", _options.CurrentValue.NotificationTransport, ownerId);
                return;
            }

            if (!_options.CurrentValue.IsDownloadEnabled)
            {
                _logger.LogInformation("Downloads disabled; exiting polling loop for {OwnerId}", ownerId);
                return;
            }

            IReadOnlyList<ClipboardNotificationEvent> events;
            try
            {
                events = await _client.PollNotificationsAsync(ownerId, timeoutSeconds, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException httpEx)
            {
                if (IsExpectedHttpStatus(httpEx.StatusCode))
                {
                    _logger.LogDebug("Notification poll returned {StatusCode} for {OwnerId}", httpEx.StatusCode, ownerId);
                }
                else
                {
                    _logger.LogInformation("Notification poll failed for {OwnerId}: {Message}", ownerId, httpEx.Message);
                }

                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
                continue;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to poll notifications for {OwnerId}", ownerId);
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
                continue;
            }

            if (events.Count == 0)
            {
                continue;
            }

            var hasOwnerEvent = false;
            foreach (var notification in events)
            {
                if (string.Equals(notification.OwnerId, ownerId, StringComparison.OrdinalIgnoreCase))
                {
                    hasOwnerEvent = true;
                    break;
                }
            }

            if (hasOwnerEvent)
            {
                await RefreshHistoryAsync(ownerId).ConfigureAwait(false);
            }

            if (maxRun.HasValue && DateTimeOffset.UtcNow - started >= maxRun.Value)
            {
                _logger.LogInformation("Polling fallback window elapsed for {OwnerId}; re-evaluating transport", ownerId);
                return;
            }
        }
    }

    private async Task<bool> RunPubSubLoopAsync(string ownerId, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Azure Web PubSub listener for {OwnerId}", ownerId);

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!IsOwnerActive(ownerId))
            {
                _logger.LogInformation("Stopping Web PubSub listener for {OwnerId}", ownerId);
                return true;
            }

            if (_options.CurrentValue.NotificationTransport != NotificationTransport.PubSub)
            {
                _logger.LogInformation("Notification transport changed to {Transport}; stopping Web PubSub listener", _options.CurrentValue.NotificationTransport);
                return true;
            }

            if (!_options.CurrentValue.IsDownloadEnabled)
            {
                _logger.LogInformation("Downloads disabled; stopping Web PubSub listener for {OwnerId}", ownerId);
                return true;
            }

            NotificationConnectionInfo? connectionInfo;
            try
            {
                connectionInfo = await _client.GetNotificationConnectionAsync(ownerId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to negotiate Web PubSub connection for {OwnerId}", ownerId);
                await DelayReconnectAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (connectionInfo is null || string.IsNullOrWhiteSpace(connectionInfo.Url) || !Uri.TryCreate(connectionInfo.Url, UriKind.Absolute, out var uri))
            {
                _logger.LogWarning("Web PubSub negotiation is not available for {OwnerId}; falling back to polling", ownerId);
                return false;
            }

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            await using var webPubSubClient = CreateWebPubSubClient(uri);
            webPubSubClient.Connected += _ =>
            {
                _logger.LogDebug("Web PubSub connected for {OwnerId}", ownerId);
                return Task.CompletedTask;
            };
            webPubSubClient.Disconnected += args =>
            {
                _logger.LogInformation("Web PubSub disconnected for {OwnerId}", ownerId);
                completion.TrySetResult(true);
                return Task.CompletedTask;
            };
            webPubSubClient.GroupMessageReceived += args =>
            {
                if (args is null)
                {
                    return Task.CompletedTask;
                }

                return HandlePubSubMessageAsync(args.Message.Data, ownerId);
            };

            try
            {
                await webPubSubClient.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Failed to start Web PubSub client for {OwnerId}", ownerId);
                await DelayReconnectAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            var renewDelay = GetRenewalDelay(connectionInfo.ExpiresUtc);
            var finished = await Task.WhenAny(completion.Task, Task.Delay(renewDelay, cancellationToken)).ConfigureAwait(false);
            if (finished != completion.Task)
            {
                _logger.LogDebug("Renewing Web PubSub connection for {OwnerId}", ownerId);
            }

            try
            {
                await webPubSubClient.StopAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error stopping Web PubSub client for {OwnerId}", ownerId);
            }
        }

        return true;
    }

    private async Task RefreshHistoryAsync(string ownerId)
    {
        // Avoid pile-ups by letting only one refresh hit the Functions list endpoint at a time.
        if (!_refreshLock.Wait(0))
        {
            return;
        }

        try
        {
            var items = await _client.ListAsync(ownerId, _options.CurrentValue.HistoryLength, CancellationToken.None).ConfigureAwait(false);
            _historyCache.Update(items);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh history after push notification");
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private int GetPollTimeoutSeconds()
    {
        // Functions requests time out after one minute, so keep our long-poll window safely below that.
        var options = _options.CurrentValue;
        var timeout = Math.Max(5, options.PushReconnectSeconds);
        return Math.Min(timeout, 55);
    }

    private bool IsOwnerActive(string ownerId)
    {
        var options = _options.CurrentValue;
        return options.EnablePushNotifications
               && !string.IsNullOrWhiteSpace(options.OwnerId)
               && options.IsDownloadEnabled
               && string.Equals(options.OwnerId, ownerId, StringComparison.OrdinalIgnoreCase);
    }

    private WebPubSubClient CreateWebPubSubClient(Uri uri)
        => new(uri, new WebPubSubClientOptions
        {
            Protocol = new WebPubSubJsonProtocol()
        });

    private async Task DelayReconnectAsync(CancellationToken cancellationToken)
    {
        var delaySeconds = Math.Max(5, _options.CurrentValue.PushReconnectSeconds);
        await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken).ConfigureAwait(false);
    }

    private static TimeSpan GetRenewalDelay(DateTimeOffset expiresUtc)
    {
        if (expiresUtc <= DateTimeOffset.UtcNow)
        {
            return TimeSpan.FromSeconds(10);
        }

        var buffer = TimeSpan.FromMinutes(1);
        var delay = expiresUtc - DateTimeOffset.UtcNow - buffer;
        if (delay <= TimeSpan.Zero)
        {
            delay = TimeSpan.FromSeconds(15);
        }

        return delay;
    }

    private Task HandlePubSubMessageAsync(BinaryData data, string ownerId)
    {
        if (!_options.CurrentValue.IsDownloadEnabled)
        {
            return Task.CompletedTask;
        }

        ClipboardNotificationEvent? notification = null;
        try
        {
            notification = data.ToObjectFromJson<ClipboardNotificationEvent>();
        }
        catch (JsonException jsonEx)
        {
            _logger.LogDebug(jsonEx, "Failed to parse Web PubSub notification payload");
        }

        if (notification is null || !string.Equals(notification.OwnerId, ownerId, StringComparison.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }

        return RefreshHistoryAsync(ownerId);
    }

    private static bool IsExpectedHttpStatus(HttpStatusCode? statusCode)
    {
        return statusCode is null or HttpStatusCode.NotFound or HttpStatusCode.BadRequest;
    }
}
