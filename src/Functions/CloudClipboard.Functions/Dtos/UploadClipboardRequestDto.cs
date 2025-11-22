using System;
using System.ComponentModel.DataAnnotations;
using CloudClipboard.Core.Models;

namespace CloudClipboard.Functions.Dtos;

public sealed class UploadClipboardRequestDto
{
    [Required]
    public string OwnerId { get; init; } = default!;

    [Required]
    public ClipboardPayloadType PayloadType { get; init; }

    [Required]
    public string ContentBase64 { get; init; } = default!;

    [Required]
    public string ContentType { get; init; } = default!;

    public string? FileName { get; init; }

    public string? DeviceName { get; init; }

    public DateTimeOffset? ExpiresUtc { get; init; }
}
