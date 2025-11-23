using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace CloudClipboard.Agent.Windows.Services;

public sealed class AppIconProvider : IAppIconProvider
{
    private readonly Dictionary<int, IconEntry> _cache = new();
    private readonly object _lock = new();

    public Icon GetIcon(int size)
    {
        size = Math.Clamp(size, 16, 256);
        lock (_lock)
        {
            if (!_cache.TryGetValue(size, out var entry))
            {
                entry = CreateIcon(size);
                _cache[size] = entry;
            }

            return entry.Icon;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var entry in _cache.Values)
            {
                entry.Icon.Dispose();
                if (entry.Handle != IntPtr.Zero)
                {
                    NativeMethods.DestroyIcon(entry.Handle);
                }
            }

            _cache.Clear();
        }
    }

    private static IconEntry CreateIcon(int size)
    {
        using var bitmap = new Bitmap(size, size);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.FromArgb(32, 32, 32));

            var glyph = "\u2702"; // scissors
            var fontSize = size * 0.65f;
            using var font = new Font("Segoe UI Symbol", fontSize, FontStyle.Regular, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(Color.White);
            var textSize = graphics.MeasureString(glyph, font);
            var location = new PointF((size - textSize.Width) / 2f, (size - textSize.Height) / 2f);
            graphics.DrawString(glyph, font, brush, location);
        }

        var handle = bitmap.GetHicon();
        var icon = Icon.FromHandle(handle);
        return new IconEntry(icon, handle);
    }

    private sealed record IconEntry(Icon Icon, IntPtr Handle);

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr hIcon);
    }
}
