namespace CloudClipboard.Functions.Dtos;

public sealed record OwnerStateDto(
    string OwnerId,
    bool IsPaused,
    string? UpdatedUtc
);
