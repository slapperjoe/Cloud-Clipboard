using System;
using System.Collections.Generic;

namespace CloudClipboard.Core.Models;

/// <summary>
/// Holds metadata required to locate and describe a clipboard item persisted in cloud storage.
/// </summary>
public sealed record ClipboardItemMetadata(
    string Id,
    string OwnerId,
    ClipboardPayloadType PayloadType,
    string BlobName,
    string ContentType,
    long ContentLength,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? ExpiresUtc,
    string? FileName,
    bool IsEncrypted,
    IReadOnlyDictionary<string, string> Properties
);
