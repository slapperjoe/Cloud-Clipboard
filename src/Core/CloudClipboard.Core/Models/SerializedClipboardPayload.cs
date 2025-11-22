using System.IO;

namespace CloudClipboard.Core.Models;

public sealed record SerializedClipboardPayload(string ContentType, long Length, Stream Stream);
