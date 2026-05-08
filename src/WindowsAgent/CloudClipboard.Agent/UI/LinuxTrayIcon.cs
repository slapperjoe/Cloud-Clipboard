using System;
using CloudClipboard.Agent.UI;
using Microsoft.Extensions.Logging;

namespace CloudClipboard.Agent.UI;

/// <summary>
/// Linux tray icon implementation.
/// Falls back to console logging since full D-Bus StatusNotifierItem requires external libraries.
/// </summary>
public sealed class LinuxTrayIcon : ITrayIcon
{
    private readonly ILogger<LinuxTrayIcon> _logger;
    private bool _visible;
    private string _tooltip = "Cloud Clipboard";

    public event Action<string>? ItemActivated;

    public LinuxTrayIcon(ILogger<LinuxTrayIcon> logger)
    {
        _logger = logger;
    }

    public void Show()
    {
        _visible = true;
        _logger.LogInformation("Cloud Clipboard Agent is running (PID {Pid})", System.Diagnostics.Process.GetCurrentProcess().Id);
        _logger.LogInformation("Use Ctrl+C to stop the agent.");
    }

    public void Hide()
    {
        _visible = false;
        _logger.LogDebug("Tray icon hidden.");
    }

    public void SetTooltip(string tooltip)
    {
        _tooltip = tooltip;
        _logger.LogDebug("Tooltip updated to: {Tooltip}", tooltip);
    }

    public void SetMenu(string menuXml)
    {
        _logger.LogDebug("Menu XML received (no-op on Linux without D-Bus library).");
    }
}
