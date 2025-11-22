using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using CloudClipboard.Core.Models;

namespace CloudClipboard.Core.Services;

public sealed class ClipboardPayloadSerializer
{
    public async Task<SerializedClipboardPayload> SerializeAsync(ClipboardPayloadDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        if (descriptor.Parts.Count == 1 && descriptor.PayloadType != ClipboardPayloadType.FileSet)
        {
            return await SerializeSinglePartAsync(descriptor.Parts[0], cancellationToken).ConfigureAwait(false);
        }

        return await SerializeAsArchiveAsync(descriptor, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<SerializedClipboardPayload> SerializeSinglePartAsync(ClipboardPayloadPart part, CancellationToken cancellationToken)
    {
        var stream = new MemoryStream();
        using var source = part.StreamFactory();
        await source.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
        stream.Position = 0;
        return new SerializedClipboardPayload(part.ContentType, stream.Length, stream);
    }

    private static async Task<SerializedClipboardPayload> SerializeAsArchiveAsync(ClipboardPayloadDescriptor descriptor, CancellationToken cancellationToken)
    {
        var archiveStream = new MemoryStream();
        using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var part in descriptor.Parts)
            {
                var entry = archive.CreateEntry(part.Name, CompressionLevel.Fastest);
                await using var entryStream = entry.Open();
                using var source = part.StreamFactory();
                await source.CopyToAsync(entryStream, cancellationToken).ConfigureAwait(false);
            }
        }

        archiveStream.Position = 0;
        var contentType = string.IsNullOrWhiteSpace(descriptor.PreferredContentType)
            ? "application/zip"
            : descriptor.PreferredContentType;

        return new SerializedClipboardPayload(contentType, archiveStream.Length, archiveStream);
    }
}
