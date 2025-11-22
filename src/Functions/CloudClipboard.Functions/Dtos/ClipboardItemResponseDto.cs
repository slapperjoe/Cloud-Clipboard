using System;
using CloudClipboard.Core.Models;

namespace CloudClipboard.Functions.Dtos;

public sealed record ClipboardItemResponseDto
(
    string Id,
    ClipboardPayloadType PayloadType,
    string ContentType,
    long ContentLength,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? ExpiresUtc,
    string? FileName,
    string Device,
    string? ContentBase64 = null
)
{
    public static ClipboardItemResponseDto FromMetadata(ClipboardItemMetadata metadata, string? contentBase64 = null)
        => new(
            metadata.Id,
            metadata.PayloadType,
            metadata.ContentType,
            metadata.ContentLength,
            metadata.CreatedUtc,
            metadata.ExpiresUtc,
            metadata.FileName,
            metadata.Properties.TryGetValue("device", out var device) ? device : "unknown",
            contentBase64
        );
}
