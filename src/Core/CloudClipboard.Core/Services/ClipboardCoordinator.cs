using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using CloudClipboard.Core.Abstractions;
using CloudClipboard.Core.Models;

namespace CloudClipboard.Core.Services;

/// <summary>
/// Coordinates the serialization of clipboard payloads and persists both metadata and content.
/// </summary>
public sealed class ClipboardCoordinator
{
    private readonly IClipboardMetadataStore _metadataStore;
    private readonly IClipboardPayloadStore _payloadStore;
    private readonly ClipboardPayloadSerializer _serializer;
    private readonly TimeProvider _timeProvider;

    public ClipboardCoordinator(
        IClipboardMetadataStore metadataStore,
        IClipboardPayloadStore payloadStore,
        ClipboardPayloadSerializer serializer,
        TimeProvider? timeProvider = null)
    {
        _metadataStore = metadataStore;
        _payloadStore = payloadStore;
        _serializer = serializer;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ClipboardItemMetadata> SaveAsync(ClipboardUploadRequest request, CancellationToken cancellationToken = default)
    {
        var itemId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var blobName = BuildBlobName(request.OwnerId, itemId, request.Payload.PayloadType);
        var payload = await _serializer.SerializeAsync(request.Payload, cancellationToken);

        await using (payload.Stream)
        {
            await _payloadStore.UploadAsync(blobName, payload.Stream, payload.ContentType, cancellationToken);
        }

        var metadata = new ClipboardItemMetadata(
            Id: itemId,
            OwnerId: request.OwnerId,
            PayloadType: request.Payload.PayloadType,
            BlobName: blobName,
            ContentType: payload.ContentType,
            ContentLength: payload.Length,
            CreatedUtc: _timeProvider.GetUtcNow(),
            ExpiresUtc: request.Payload.ExpiresUtc,
            FileName: request.FileName,
            IsEncrypted: request.EncryptPayload,
            Properties: new Dictionary<string, string>
            {
                ["device"] = request.DeviceName ?? "unknown"
            }
        );

        return await _metadataStore.AddAsync(metadata, cancellationToken);
    }

    public async Task<int> DeleteOwnerAsync(string ownerId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
        {
            return 0;
        }

        var items = await _metadataStore.ListAllAsync(ownerId, cancellationToken).ConfigureAwait(false);
        foreach (var item in items)
        {
            await _payloadStore.DeleteAsync(item.BlobName, cancellationToken).ConfigureAwait(false);
            await _metadataStore.RemoveAsync(ownerId, item.Id, cancellationToken).ConfigureAwait(false);
        }

        return items.Count;
    }

    private static string BuildBlobName(string ownerId, string itemId, ClipboardPayloadType payloadType)
    {
        var today = DateTimeOffset.UtcNow;
        return $"{ownerId}/{today:yyyy/MM/dd}/{payloadType.ToString().ToLowerInvariant()}/{itemId}.bin";
    }

}
