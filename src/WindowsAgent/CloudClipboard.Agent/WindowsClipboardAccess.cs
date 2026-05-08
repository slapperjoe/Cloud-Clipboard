#if WINDOWS
using System.Drawing;
#endif
using System.IO;
using System.Threading;
using System.Threading.Tasks;
#if WINDOWS
using System.Windows.Forms;
#endif
using CloudClipboard.Agent;

namespace CloudClipboard.Agent;

/// <summary>
/// Windows clipboard access using WinForms.
/// </summary>
public sealed class WindowsClipboardAccess : IClipboardAccess
{
    public Task<string?> ReadTextAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
#if WINDOWS
        var text = StaThreadRunner.Run(() => Clipboard.GetText(TextDataFormat.UnicodeText));
        return Task.FromResult<string?>(text);
#else
        return Task.FromResult<string?>(null);
#endif
    }

    public Task<byte[]?> ReadImageAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        byte[]? result = null;
#if WINDOWS
        try
        {
            var image = StaThreadRunner.Run(() => Clipboard.GetData(DataFormats.PNG));
            if (image is Image img)
            {
                using var ms = new MemoryStream();
                img.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                result = ms.ToArray();
            }
        }
        catch (Exception)
        {
            result = null;
        }
#endif
        return Task.FromResult(result);
    }

    public Task WriteTextAsync(string text, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
#if WINDOWS
        StaThreadRunner.Run(() => Clipboard.SetText(text, TextDataFormat.UnicodeText));
#endif
        return Task.CompletedTask;
    }

    public Task WriteImageAsync(byte[] imageData, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
#if WINDOWS
        using var stream = new MemoryStream(imageData);
        using var image = Image.FromStream(stream);
        StaThreadRunner.Run(() => Clipboard.SetImage(image));
#endif
        return Task.CompletedTask;
    }

    public Task WriteFilesAsync(string[] filePaths, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
#if WINDOWS
        var collection = new System.Collections.Specialized.StringCollection();
        foreach (var path in filePaths)
        {
            collection.Add(path);
        }
        StaThreadRunner.Run(() => Clipboard.SetFileDropList(collection));
#endif
        return Task.CompletedTask;
    }
}
