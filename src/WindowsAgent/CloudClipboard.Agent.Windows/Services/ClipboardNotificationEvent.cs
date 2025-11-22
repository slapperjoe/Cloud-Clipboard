using System;
using System.Text.Json.Serialization;

namespace CloudClipboard.Agent.Windows.Services;

public sealed record ClipboardNotificationEvent
{
    [JsonPropertyName("OwnerId")]
    public string OwnerId { get; init; } = string.Empty;

    [JsonPropertyName("ItemId")]
    public string ItemId { get; init; } = string.Empty;

    [JsonPropertyName("CreatedUtc")]
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
}
