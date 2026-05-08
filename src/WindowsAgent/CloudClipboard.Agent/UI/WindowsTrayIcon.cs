#if WINDOWS
using System.Drawing;
using System.Windows.Forms;

using System;
using CloudClipboard.Agent.UI;
using Microsoft.Extensions.Logging;

namespace CloudClipboard.Agent.UI;

/// <summary>
/// Windows tray icon implementation using WinForms NotifyIcon.
/// </summary>
public sealed class WindowsTrayIcon : ITrayIcon, IDisposable
{
    private readonly ILogger<WindowsTrayIcon> _logger;
    private readonly IAppIconProvider _iconProvider;
    private NotifyIcon? _notifyIcon;
    private ContextMenuStrip? _contextMenu;
    private Thread? _uiThread;
    private SynchronizationContext? _uiContext;
    private readonly TaskCompletionSource<bool> _uiReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public event Action<string>? ItemActivated;

    public WindowsTrayIcon(
        ILogger<WindowsTrayIcon> logger,
        IAppIconProvider iconProvider)
    {
        _logger = logger;
        _iconProvider = iconProvider;
    }

    public void Show()
    {
        if (_uiThread is not null && _uiThread.IsAlive)
        {
            return;
        }

        _uiThread = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                _uiContext = SynchronizationContext.Current;

                _contextMenu = new ContextMenuStrip();
                _contextMenu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) =>
                {
                    Application.ExitThread();
                });

                _notifyIcon = new NotifyIcon
                {
                    Icon = _iconProvider.GetIcon(32),
                    Text = "Cloud Clipboard",
                    ContextMenuStrip = _contextMenu,
                    Visible = true
                };

                _uiReady.TrySetResult(true);
                Application.Run();
            }
            catch (Exception ex)
            {
                _uiReady.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "CloudClipboard.TrayUi"
        };

        _uiThread.SetApartmentState(ApartmentState.STA);
        _uiThread.Start();
    }

    public void Hide()
    {
        InvokeOnUi(() =>
        {
            if (_notifyIcon is { } icon)
            {
                icon.Visible = false;
            }
        });
    }

    public void SetTooltip(string tooltip)
    {
        InvokeOnUi(() =>
        {
            if (_notifyIcon is { } icon)
            {
                icon.Text = tooltip;
            }
        });
    }

    public void SetMenu(string menuXml)
    {
        // For Windows, we parse the XML and rebuild the context menu
        InvokeOnUi(() => RebuildMenu(menuXml));
    }

    private void RebuildMenu(string menuXml)
    {
        if (_contextMenu is not null)
        {
            _contextMenu.Items.Clear();
            // Parse XML menu definition and add items
            foreach (var item in ParseMenuItems(menuXml))
            {
                var menuItem = new ToolStripMenuItem(item.Text, null, (_, _) =>
                {
                    ItemActivated?.Invoke(item.Action);
                });
                _contextMenu.Items.Add(menuItem);
            }
        }
    }

    private static (string Text, string Action)[] ParseMenuItems(string menuXml)
    {
        // Simple XML parsing for menu items
        // Format: <menu><item text="Refresh" action="refresh" /></menu>
        var items = new System.Collections.Generic.List<(string Text, string Action)>();
        foreach (var line in menuXml.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("<item"))
            {
                var textStart = trimmed.IndexOf("text=\"");
                var actionStart = trimmed.IndexOf("action=\"");
                if (textStart >= 0 && actionStart >= 0)
                {
                    var textEnd = trimmed.IndexOf("\"", textStart + 6);
                    var actionEnd = trimmed.IndexOf("\"", actionStart + 8);
                    if (textEnd >= 0 && actionEnd >= 0)
                    {
                        var text = trimmed.Substring(textStart + 6, textEnd - textStart - 6);
                        var action = trimmed.Substring(actionStart + 8, actionEnd - actionStart - 8);
                        items.Add((text, action));
                    }
                }
            }
        }
        return items.ToArray();
    }

    private void InvokeOnUi(Action action)
    {
        _uiContext?.Post(_ => action(), null);
    }

    public void Dispose()
    {
        InvokeOnUi(() =>
        {
            _notifyIcon?.Dispose();
            _contextMenu?.Dispose();
            Application.ExitThread();
        });
        _uiThread?.Join();
    }
}
#else
namespace CloudClipboard.Agent.UI;

/// <summary>
/// Stub for non-Windows platforms (compile-only).
/// </summary>
public sealed class WindowsTrayIcon : ITrayIcon
{
    public event Action<string>? ItemActivated;
    public void Show() { }
    public void Hide() { }
    public void SetTooltip(string _) { }
    public void SetMenu(string _) { }
}
#endif
