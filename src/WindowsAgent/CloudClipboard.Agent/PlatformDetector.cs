using System.Runtime.InteropServices;

namespace CloudClipboard.Agent;

/// <summary>
/// Platform detection utilities.
/// </summary>
public static class PlatformDetector
{
    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    public static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    public static bool IsMacOS => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    public static string OsName => IsWindows ? "Windows" : IsLinux ? "Linux" : IsMacOS ? "macOS" : "Unknown";
}
