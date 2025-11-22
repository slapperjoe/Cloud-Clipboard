using System;
using System.Collections.Specialized;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using CloudClipboard.Agent.Windows.Interop;
using CloudClipboard.Core.Models;

namespace CloudClipboard.Agent.Windows.Services;

public sealed class ClipboardPasteService
{
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
                StaThreadRunner.Run(() => Clipboard.SetText(text, TextDataFormat.UnicodeText));
                break;
            case ClipboardPayloadType.Image:
                using (var stream = new MemoryStream(bytes))
                using (var image = Image.FromStream(stream))
                {
                    StaThreadRunner.Run(() => Clipboard.SetImage(image));
                }
                break;
            case ClipboardPayloadType.FileSet:
                await ExtractZipToTempAsync(bytes);
                break;
        }
    }

    private static async Task ExtractZipToTempAsync(byte[] zipBytes)
    {
        var tempFolder = Path.Combine(Path.GetTempPath(), "CloudClipboard", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempFolder);
        using var archiveStream = new MemoryStream(zipBytes);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);
        archive.ExtractToDirectory(tempFolder);
        var collection = new StringCollection();
        foreach (var file in Directory.GetFiles(tempFolder))
        {
            collection.Add(file);
        }

        StaThreadRunner.Run(() => Clipboard.SetFileDropList(collection));
        await Task.CompletedTask;
    }
}
