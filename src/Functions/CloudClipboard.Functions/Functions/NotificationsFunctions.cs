using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using CloudClipboard.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace CloudClipboard.Functions.Functions;

public sealed class NotificationsFunctions
{
    private readonly IClipboardNotificationService _notificationService;
    private readonly IRealtimeClipboardNotificationService _realtimeNotifications;

    public NotificationsFunctions(IClipboardNotificationService notificationService, IRealtimeClipboardNotificationService realtimeNotifications)
    {
        _notificationService = notificationService;
        _realtimeNotifications = realtimeNotifications;
    }

    [Function("PollClipboardNotifications")]
    public async Task<HttpResponseData> PollClipboardNotificationsAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "clipboard/owners/{ownerId}/notifications")] HttpRequestData req,
        string ownerId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        var timeoutSeconds = GetTimeoutSeconds(req);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        var collected = new List<ClipboardNotification>();

        while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            var batch = await _notificationService.DrainAsync(ownerId, 25, cancellationToken).ConfigureAwait(false);
            if (batch.Count > 0)
            {
                collected.AddRange(batch);
                break;
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            var delay = remaining < TimeSpan.FromSeconds(2) ? remaining : TimeSpan.FromSeconds(2);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            OwnerId = ownerId,
            Events = collected
        }, cancellationToken);
        return response;
    }

    [Function("NegotiateClipboardNotifications")]
    public async Task<HttpResponseData> NegotiateClipboardNotificationsAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "clipboard/owners/{ownerId}/notifications/negotiate")] HttpRequestData req,
        string ownerId,
        CancellationToken cancellationToken)
    {
        if (!_realtimeNotifications.IsEnabled || string.IsNullOrWhiteSpace(ownerId))
        {
            return req.CreateResponse(HttpStatusCode.NotFound);
        }

        var info = await _realtimeNotifications.NegotiateAsync(ownerId, cancellationToken).ConfigureAwait(false);
        if (info is null)
        {
            return req.CreateResponse(HttpStatusCode.NotFound);
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(info, cancellationToken);
        return response;
    }

    private static int GetTimeoutSeconds(HttpRequestData req)
    {
        var result = 25;
        var query = req.Url.Query.TrimStart('?');
        if (string.IsNullOrEmpty(query))
        {
            return result;
        }

        var pairs = query.Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var pair in pairs)
        {
            var segments = pair.Split('=', 2, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 2 && segments[0].Equals("timeoutSeconds", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(Uri.UnescapeDataString(segments[1]), out var parsed) && parsed is > 0 and <= 60)
                {
                    result = parsed;
                }

                break;
            }
        }

        return result;
    }
}
