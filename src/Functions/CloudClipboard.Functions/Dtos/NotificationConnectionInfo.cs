using System;
using System.Text.Json.Serialization;

namespace CloudClipboard.Functions.Dtos;

public sealed record NotificationConnectionInfo
{
    [JsonPropertyName("OwnerId")]
    public string OwnerId { get; init; } = string.Empty;

    [JsonPropertyName("Url")]
    public string Url { get; init; } = string.Empty;

    [JsonPropertyName("ExpiresUtc")]
    public DateTimeOffset ExpiresUtc { get; init; }
        = DateTimeOffset.MinValue;
}
