using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CloudClipboard.Agent.Windows;

internal static class DpiAwareness
{
    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        TrySetHighDpiMode();
        TrySetProcessAwareness();
    }

    private static void TrySetHighDpiMode()
    {
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        }
        catch (InvalidOperationException)
        {
            // Another component already selected a mode; nothing else to do.
        }
        catch (PlatformNotSupportedException)
        {
        }
    }

    private static void TrySetProcessAwareness()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            if (NativeMethods.SetProcessDpiAwarenessContext(NativeMethods.PerMonitorAwareV2))
            {
                return;
            }
        }
        catch (EntryPointNotFoundException)
        {
            // OS does not expose this API; fall back to the legacy call below.
        }
        catch (DllNotFoundException)
        {
        }

        try
        {
            NativeMethods.SetProcessDPIAware();
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    private static class NativeMethods
    {
        public static readonly IntPtr PerMonitorAwareV2 = new(-4);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();
    }
}
