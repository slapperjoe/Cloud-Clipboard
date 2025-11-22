using System;

namespace CloudClipboard.Core.Models;

public sealed record ClipboardOwnerState(
    string OwnerId,
    bool IsPaused,
    DateTimeOffset? UpdatedUtc = null
);
