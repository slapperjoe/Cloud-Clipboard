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
        if (descriptor.PayloadType == ClipboardPayloadType.Text && descriptor.Parts.Count == 1)
        {
            var part = descriptor.Parts[0];
            var stream = new MemoryStream();
            using var source = part.StreamFactory();
            await source.CopyToAsync(stream, cancellationToken);
            stream.Position = 0;
            return new SerializedClipboardPayload(part.ContentType, stream.Length, stream);
        }

        var archiveStream = new MemoryStream();
        using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var part in descriptor.Parts)
            {
                var entry = archive.CreateEntry(part.Name, CompressionLevel.Fastest);
                await using var entryStream = entry.Open();
                using var source = part.StreamFactory();
                await source.CopyToAsync(entryStream, cancellationToken);
            }
        }

        archiveStream.Position = 0;
        return new SerializedClipboardPayload(descriptor.PreferredContentType ?? "application/zip", archiveStream.Length, archiveStream);
    }
}
