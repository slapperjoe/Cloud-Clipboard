using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using CloudClipboard.Core.Models;

namespace CloudClipboard.Agent.Services;

public sealed class ClipboardPasteService
{
    private readonly IClipboardAccess _clipboardAccess;

    public ClipboardPasteService(IClipboardAccess clipboardAccess)
    {
        _clipboardAccess = clipboardAccess;
    }

    public async Task PasteAsync(ClipboardItemDto item)
    {
        if (string.IsNullOrEmpty(item.ContentBase64))
        {
            return;
        }

        var bytes = Convert.FromBase64String(item.ContentBase64);
        switch (item.PayloadType)
        {
            case ClipboardPayloadType.Text:
                var text = Encoding.UTF8.GetString(bytes);
                await _clipboardAccess.WriteTextAsync(text);
                break;
            case ClipboardPayloadType.Image:
                await _clipboardAccess.WriteImageAsync(bytes);
                break;
            case ClipboardPayloadType.FileSet:
                await ExtractZipToTempAsync(bytes);
                break;
        }
    }

    private async Task ExtractZipToTempAsync(byte[] zipBytes)
    {
        var tempFolder = Path.Combine(Path.GetTempPath(), "CloudClipboard", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempFolder);
        using var archiveStream = new MemoryStream(zipBytes);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);
        archive.ExtractToDirectory(tempFolder);

        var files = Directory.GetFiles(tempFolder).ToList();
        await _clipboardAccess.WriteFilesAsync(files.ToArray());
        await Task.CompletedTask;
    }
}
