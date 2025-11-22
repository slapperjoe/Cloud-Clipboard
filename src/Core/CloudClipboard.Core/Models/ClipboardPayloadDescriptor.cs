using System;
using System.Collections.Generic;
using System.IO;

namespace CloudClipboard.Core.Models;

/// <summary>
/// Lightweight description of the actual clipboard content before it is uploaded.
/// </summary>
public sealed record ClipboardPayloadDescriptor(
    ClipboardPayloadType PayloadType,
    IReadOnlyList<ClipboardPayloadPart> Parts,
    string? PreferredContentType = null,
    DateTimeOffset? ExpiresUtc = null
);

/// <summary>
/// Describes an individual fragment that will be serialized into Azure Blob Storage.
/// </summary>
public sealed record ClipboardPayloadPart(
    string Name,
    string ContentType,
    long Length,
    Func<Stream> StreamFactory
);
