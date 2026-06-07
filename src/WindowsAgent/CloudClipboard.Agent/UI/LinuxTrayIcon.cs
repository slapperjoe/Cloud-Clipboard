using System.IO;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using CloudClipboard.Agent.UI;
using Microsoft.Extensions.Logging;
using Tmds.DBus.Protocol;

namespace CloudClipboard.Agent.UI;

public sealed class LinuxTrayIcon : ITrayIcon
{
    private readonly ILogger<LinuxTrayIcon> _logger;
    private bool _visible;
    private string _tooltip = "Cloud Clipboard";
    private string _title = "Cloud Clipboard";
    private readonly int _instanceId;
    private Connection? _connection;
    private readonly string _iconPath;

    // Embedded 64x64 clipboard PNG icon
    private static readonly byte[] IconPngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAYAAACqaXHeAAAAgElEQVR4nO3RMQ6AIBREQe5/AzqPSEOtrTE2AvpR5yXbEhhSuqGcl3W/6HNCOl6+d9HvuRwAAAAAAADwY4DPNPonnx6A0QCl1KkHAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAGgHeNsASJIkSadtCQtp+qH+/jgAAAAASUVORK5CYII=");

    public event Action<string>? ItemActivated;
    public List<MenuItem> _menuItems = new();

    public LinuxTrayIcon(ILogger<LinuxTrayIcon> logger)
    {
        _logger = logger;
        _instanceId = Environment.ProcessId;
        _iconPath = Path.Combine(Path.GetTempPath(), "cloud-clipboard-tray.png");
        File.WriteAllBytes(_iconPath, IconPngBytes);
    }

    public async Task ShowAsync()
    {
        if (_visible) return;
        _visible = true;

        try
        {
            var addr = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS") ?? "unix:path=/run/user/1000/bus";
            _connection = new Connection(addr);
            await _connection.ConnectAsync();
            _logger.LogInformation("D-Bus connected as {Name}", _connection.UniqueName);

            using (var w = _connection.GetMessageWriter())
            {
                w.WriteMethodCallHeader("org.freedesktop.DBus", "/org/freedesktop/DBus",
                    "org.freedesktop.DBus", "RequestName", "su");
                w.WriteString("com.cloudclipboard.agent");
                w.WriteUInt32(0);
                _connection.CallMethodAsync(w.CreateMessage());
            }

            _connection.AddMethodHandler(new SniHandler(this, _logger, _instanceId));
            _logger.LogInformation("SNI handler at /StatusNotifierItem/{Id}", _instanceId);

            await RegisterWithWatcherAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "D-Bus tray init failed");
            _visible = false;
        }
    }

    private async Task RegisterWithWatcherAsync()
    {
        try
        {
            using var p = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo("dbus-send")
                {
                    ArgumentList = { "--session", "--dest=org.kde.StatusNotifierWatcher",
                        "--print-reply", "/StatusNotifierWatcher",
                        "org.kde.StatusNotifierWatcher.RegisterStatusNotifierItem",
                        $"string:com.cloudclipboard.agent/StatusNotifierItem/{_instanceId}" },
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false,
                },
            };
            p.Start();
            await p.WaitForExitAsync();
            _logger.LogInformation("Watcher: {Code}", p.ExitCode);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Watcher failed"); }
    }

    public void Hide() { _connection?.Dispose(); _visible = false; }
    public void SetTooltip(string t) => _tooltip = t;
    public void SetMenu(string xml) { try { _menuItems = ParseMenuXml(xml); } catch { } }
    public string GetTooltip() => _tooltip;
    public string GetTitle() => _title;
    public void Show() { _ = ShowAsync(); }

    internal void OnActivated(string item) => ItemActivated?.Invoke(item);

    private static List<MenuItem> ParseMenuXml(string xml)
    {
        var items = new List<MenuItem>();
        foreach (var el in XDocument.Parse(xml).Descendants("menuitem"))
            items.Add(new MenuItem { Name = el.Attribute("name")?.Value ?? "", Label = el.Attribute("label")?.Value ?? "", SubItems = new() });
        return items;
    }

    private sealed class SniHandler : IMethodHandler
    {
        private readonly LinuxTrayIcon _parent;
        private readonly ILogger _logger;
        private readonly int _pid;

        // 48x48 clipboard PNG icon
        private static readonly byte[] IconPng = {
            0x89,0x50,0x4E,0x47,0x0D,0x0A,0x1A,0x0A,0x00,0x00,0x00,0x0D,0x49,0x48,0x44,0x52,
            0x00,0x00,0x00,0x30,0x00,0x00,0x00,0x30,0x08,0x06,0x00,0x00,0x00,0x57,0x02,0xF9,
            0x87,0x00,0x00,0x00,0x5F,0x49,0x44,0x41,0x54,0x78,0x9C,0xED,0xD4,0xB9,0x0D,0x80,
            0x20,0x10,0x05,0x40,0xFA,0xEF,0x80,0x8C,0x26,0x49,0x48,0xA0,0x57,0x1D,0xDF,0x58,
            0x72,0xEE,0x49,0x9C,0xD2,0x64,0x72,0x2E,0x3D,0xB2,0xB3,0x3B,0x3E,0xE7,0xD8,0xE1,
            0x00,0x00,0xB7,0x02,0xA2,0xDF,0xE5,0xF7,0xB7,0x5A,0x3D,0x30,0x1C,0x50,0x6B,0x5B,
            0x5A,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0xDE,0x03,
            0xEC,0xD6,0xFB,0x01,0x22,0x8F,0x65,0x00,0x89,0x1F,0x60,0x5F,0x91,0xA1,0x02,0xAE,
            0x00,0x00,0x00,0x00,0x49,0x45,0x4E,0x44,0xAE,0x42,0x60,0x82
        };

        public string Path => $"/StatusNotifierItem/{_pid}";

        public SniHandler(LinuxTrayIcon parent, ILogger logger, int pid)
        { _parent = parent; _logger = logger; _pid = pid; }

        public bool RunMethodHandlerSynchronously(Message msg) => false;

        public ValueTask HandleMethodAsync(MethodContext ctx)
        {
            try
            {
                var req = ctx.Request;
                if (ctx.IsDBusIntrospectRequest) { ctx.ReplyIntrospectXml(IntrospectXml); return default; }

                var iface = req.InterfaceAsString ?? "";
                var member = req.MemberAsString ?? "";

                switch (iface)
                {
                    case "org.freedesktop.DBus.Properties" when member == "GetAll":
                        HandleGetAll(ctx); break;
                    case "org.freedesktop.DBus.Properties" when member == "Get":
                        HandleGet(ctx); break;
                    case "org.kde.StatusNotifierItem" when member == "Activate":
                case "org.kde.StatusNotifierItem" when member == "SecondaryActivate":
                case "org.kde.StatusNotifierItem" when member == "ContextMenu":
                    _parent.OnActivated(member);
                    using (var w = ctx.CreateReplyWriter("")) ctx.Reply(w.CreateMessage());
                    break;
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "SNI handler error"); }
            return default;
        }

        private void HandleGetAll(MethodContext ctx)
        {
            var r = ctx.Request.GetBodyReader();
            if (r.ReadString() != "org.kde.StatusNotifierItem") return;

            var dict = new Dictionary<string, Variant>
            {
                ["Menu"] = MenuPath, ["IconName"] = _parent._iconPath,
                ["IconTheme"] = "", ["Title"] = _parent._title,
                ["ToolTip"] = _parent._tooltip, ["Status"] = "Active",
                ["Id"] = "CloudClipboard-" + _pid, ["ItemIsMenu"] = false,
                ["Category"] = "ApplicationStatus",
            };
            using var w = ctx.CreateReplyWriter("a{sv}");
            w.WriteDictionary(dict);
            ctx.Reply(w.CreateMessage());
        }

        private void HandleGet(MethodContext ctx)
        {
            var r = ctx.Request.GetBodyReader();
            r.ReadString();
            var prop = r.ReadString();

            var dict = new Dictionary<string, Variant>
            {
                ["Menu"] = MenuPath, ["IconName"] = _parent._iconPath,
                ["IconTheme"] = "", ["Title"] = _parent._title,
                ["ToolTip"] = _parent._tooltip, ["Status"] = "Active",
                ["Id"] = "CloudClipboard-" + _pid, ["ItemIsMenu"] = false,
                ["Category"] = "ApplicationStatus",
            };
            if (!dict.TryGetValue(prop, out var val)) return;

            using var w = ctx.CreateReplyWriter("v");
            w.WriteVariant(val);
            ctx.Reply(w.CreateMessage());
        }

        private string MenuPath => $"/StatusNotifierItem/{_pid}/menu";

        private static readonly ReadOnlyMemory<byte>[] IntrospectXml =
        [
            "<!DOCTYPE node PUBLIC \"-//freedesktop//DTD D-BUS Object Introspection 1.0//EN\"\n\"http://www.freedesktop.org/standards/dbus/1.0/introspect.dtd\">\n<node>"u8.ToArray(),
            "<interface name=\"org.kde.StatusNotifierItem\">"u8.ToArray(),
            "<property name=\"Menu\" type=\"o\" access=\"read\"/><property name=\"IconName\" type=\"s\" access=\"read\"/><property name=\"IconTheme\" type=\"s\" access=\"read\"/><property name=\"Title\" type=\"s\" access=\"read\"/><property name=\"ToolTip\" type=\"s\" access=\"read\"/><property name=\"Status\" type=\"s\" access=\"read\"/><property name=\"Id\" type=\"s\" access=\"read\"/><property name=\"ItemIsMenu\" type=\"b\" access=\"read\"/><property name=\"Category\" type=\"s\" access=\"read\"/>"u8.ToArray(),
            "<method name=\"Activate\"><arg direction=\"in\" type=\"i\" name=\"x\"/><arg direction=\"in\" type=\"i\" name=\"y\"/></method><method name=\"SecondaryActivate\"><arg direction=\"in\" type=\"i\" name=\"x\"/><arg direction=\"in\" type=\"i\" name=\"y\"/></method><method name=\"ContextMenu\"><arg direction=\"in\" type=\"i\" name=\"x\"/><arg direction=\"in\" type=\"i\" name=\"y\"/></method>"u8.ToArray(),
            "</interface>"u8.ToArray(),
            "</node>"u8.ToArray(),
        ];
    }
}

public class MenuItem
{
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public string Type { get; set; } = "ITEM";
    public bool Enabled { get; set; } = true;
    public string? Shortcut { get; set; }
    public List<MenuItem> SubItems { get; set; } = new();
}
