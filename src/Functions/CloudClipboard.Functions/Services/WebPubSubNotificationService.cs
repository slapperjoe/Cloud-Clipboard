using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.WebPubSub;
using CloudClipboard.Core.Models;
using CloudClipboard.Functions.Dtos;
using CloudClipboard.Functions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudClipboard.Functions.Services;

public sealed class WebPubSubNotificationService : IRealtimeClipboardNotificationService
{
    private readonly PubSubOptions _options;
    private readonly WebPubSubServiceClient? _serviceClient;
    private readonly ILogger<WebPubSubNotificationService> _logger;

    public WebPubSubNotificationService(IOptions<PubSubOptions> options, ILogger<WebPubSubNotificationService> logger)
    {
        _options = options.Value;
        _logger = logger;
        if (!string.IsNullOrWhiteSpace(_options.ConnectionString) && !string.IsNullOrWhiteSpace(_options.Hub))
        {
            _serviceClient = new WebPubSubServiceClient(_options.ConnectionString, _options.Hub);
        }
        else
        {
            _logger.LogWarning("Azure Web PubSub configuration is incomplete; realtime notifications are disabled.");
        }
    }

    public bool IsEnabled => _serviceClient is not null;

    public async Task PublishAsync(string ownerId, ClipboardItemMetadata metadata, CancellationToken cancellationToken)
    {
        if (_serviceClient is null)
        {
            return;
        }

        var payload = new ClipboardNotification(ownerId, metadata.Id, metadata.CreatedUtc);
        var group = ResolveGroup(ownerId);
        await _serviceClient.SendToGroupAsync(
            group,
            BinaryData.FromObjectAsJson(payload),
            contentType: "application/json").ConfigureAwait(false);
    }

    public async Task<NotificationConnectionInfo?> NegotiateAsync(string ownerId, CancellationToken cancellationToken)
    {
        if (_serviceClient is null || string.IsNullOrWhiteSpace(ownerId))
        {
            return null;
        }

        var expires = TimeSpan.FromMinutes(Math.Max(1, _options.TokenTtlMinutes));
        var uri = await _serviceClient.GetClientAccessUriAsync(
            userId: ownerId,
            groups: new[] { ResolveGroup(ownerId) },
            expiresAfter: expires,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return new NotificationConnectionInfo
        {
            OwnerId = ownerId,
            Url = uri.AbsoluteUri,
            ExpiresUtc = DateTimeOffset.UtcNow.Add(expires)
        };
    }

    private string ResolveGroup(string ownerId)
        => string.IsNullOrWhiteSpace(_options.OwnerGroupPrefix)
            ? ownerId
            : $"{_options.OwnerGroupPrefix}-{ownerId}";
}
