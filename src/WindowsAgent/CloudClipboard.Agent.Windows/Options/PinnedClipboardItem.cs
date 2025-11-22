using System;
using CloudClipboard.Core.Models;

namespace CloudClipboard.Agent.Windows.Options;

public sealed record PinnedClipboardItem
{
    public string Id { get; init; } = string.Empty;
    public ClipboardPayloadType PayloadType { get; init; }
    public string DisplayLabel { get; init; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; init; }
    public string Device { get; init; } = string.Empty;

    public static PinnedClipboardItem FromDto(ClipboardItemDto item, string displayLabel)
        => new()
        {
            Id = item.Id,
            PayloadType = item.PayloadType,
            DisplayLabel = displayLabel,
            CreatedUtc = item.CreatedUtc,
            Device = item.Device
        };
}
