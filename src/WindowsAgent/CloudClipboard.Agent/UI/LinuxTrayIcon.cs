using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using CloudClipboard.Agent.UI;
using Microsoft.Extensions.Logging;
using Tmds.DBus;

namespace CloudClipboard.Agent.UI;

/// <summary>
/// Linux tray icon implementation using D-Bus StatusNotifierItem.
/// Implements freedesktop.org systemtray specification via Tmds.DBus.
/// </summary>
public sealed class LinuxTrayIcon : ITrayIcon
{
    private readonly ILogger<LinuxTrayIcon> _logger;
    private bool _visible;
    private string _tooltip = "Cloud Clipboard";
    private string _title = "Cloud Clipboard";
    private string _iconName = "application-clipboard";
    public List<MenuItem> _menuItems = new();

    private StatusNotifierItem? _item;
    private StatusNotifierMenu? _menu;
    private Connection? _connection;
    private readonly int _instanceId;

    public event Action<string>? ItemActivated;

    public LinuxTrayIcon(ILogger<LinuxTrayIcon> logger)
    {
        _logger = logger;
        _instanceId = System.Diagnostics.Process.GetCurrentProcess().Id;
    }

    public async Task ShowAsync()
    {
        if (_visible)
        {
            _logger.LogDebug("Tray icon already visible.");
            return;
        }
        _visible = true;

        var itemPath = new ObjectPath($"/StatusNotifierItem/{_instanceId}");
        var menuPath = new ObjectPath($"/StatusNotifierItem/{_instanceId}/menu");

        _item = new StatusNotifierItem(itemPath, this, _logger);
        _menu = new StatusNotifierMenu(menuPath, this, _logger);
        _connection = new Connection(Address.Session);

        _connection.StateChanged += OnStateChanged;

        const string BusName = "com.cloudclipboard.agent";

        try
        {
            await _connection.ConnectAsync();
            await _connection.RegisterServiceAsync(BusName);
            _logger.LogInformation("D-Bus service name {BusName} registered.", BusName);
            await _connection.RegisterObjectAsync(_item);
            await _connection.RegisterObjectAsync(_menu);
            _logger.LogInformation("D-Bus StatusNotifierItem registered.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register D-Bus StatusNotifierItem");
            _visible = false;
        }
    }

    public void Hide()
    {
        if (_connection != null)
        {
            try
            {
                if (_item != null)
                    _connection.UnregisterObject(_item);
                if (_menu != null)
                    _connection.UnregisterObject(_menu);
                _connection.StateChanged -= OnStateChanged;
                // Disposing the connection automatically unregisters the service name and objects
                _connection.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to unregister D-Bus objects");
            }
            _connection = null;
        }
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
        try
        {
            _menuItems = ParseMenuXml(menuXml);
            _logger.LogDebug("Menu updated with {Count} items.", _menuItems.Count);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse menu XML");
        }
    }

    public string GetTooltip() => _tooltip;
    public string GetTitle() => _title;
    public string GetIconName() => _iconName;
    public List<MenuItem> GetMenuItems() => _menuItems;

    public void Show()
    {
        _ = ShowAsync().ContinueWith(t =>
        {
            if (t.IsFaulted)
                _logger.LogError(t.Exception, "Tray icon show failed");
        });
    }

    private List<MenuItem> ParseMenuXml(string menuXml)
    {
        var items = new List<MenuItem>();
        try
        {
            var doc = XDocument.Parse(menuXml);
            foreach (var item in doc.Descendants("menuitem"))
            {
                var menuItem = new MenuItem
                {
                    Name = item.Attribute("name")?.Value ?? "",
                    Label = item.Attribute("label")?.Value ?? item.Attribute("name")?.Value ?? "",
                    Type = item.Attribute("type")?.Value ?? "ITEM",
                    Enabled = item.Attribute("enabled")?.Value != "false",
                    Shortcut = item.Attribute("shortcut")?.Value,
                    IconName = item.Attribute("icon-name")?.Value,
                    IconData = item.Attribute("icon-data")?.Value,
                    ToggleType = item.Attribute("toggle-type")?.Value,
                    ToggleState = item.Attribute("toggle-state")?.Value,
                    SubItems = new List<MenuItem>()
                };
                foreach (var subItem in item.Elements("menuitem"))
                {
                    menuItem.SubItems.Add(new MenuItem
                    {
                        Name = subItem.Attribute("name")?.Value ?? "",
                        Label = subItem.Attribute("label")?.Value ?? subItem.Attribute("name")?.Value ?? "",
                        Type = subItem.Attribute("type")?.Value ?? "ITEM",
                        Enabled = subItem.Attribute("enabled")?.Value != "false",
                        Shortcut = subItem.Attribute("shortcut")?.Value,
                        IconName = subItem.Attribute("icon-name")?.Value,
                        IconData = subItem.Attribute("icon-data")?.Value,
                    });
                }
                items.Add(menuItem);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse menu XML");
        }
        return items;
    }

    private void OnStateChanged(object? sender, ConnectionStateChangedEventArgs e)
    {
        if (e.State == ConnectionState.Connected)
            _logger.LogInformation("Connected to session bus.");
        else if (e.State == ConnectionState.Disconnected)
            _logger.LogWarning("Disconnected from session bus. Reason: {Reason}", e.DisconnectReason);
    }

    internal void OnItemActivated(string itemName)
    {
        ItemActivated?.Invoke(itemName);
    }
}

/// <summary>
/// D-Bus object implementing org.kde.StatusNotifierItem interface.
/// Tmds.DBus requires [Property] on FIELDS, not properties.
/// </summary>
[DBusInterface("org.kde.StatusNotifierItem")]
public sealed class StatusNotifierItem : IDBusObject
{
    private readonly LinuxTrayIcon _parent;
    private readonly ILogger _logger;
    private readonly int _instanceId;

    public ObjectPath ObjectPath { get; }

    [Property(Name = "menu", Access = PropertyAccess.Read)]
    public string menu;

    [Property(Name = "iconName", Access = PropertyAccess.Read)]
    public string iconName;

    [Property(Name = "iconTheme", Access = PropertyAccess.Read)]
    public string iconTheme;

    [Property(Name = "title", Access = PropertyAccess.Read)]
    public string title;

    [Property(Name = "tooltip", Access = PropertyAccess.Read)]
    public string tooltip;

    [Property(Name = "status", Access = PropertyAccess.Read)]
    public string status;

    [Property(Name = "attentionIconName", Access = PropertyAccess.Read)]
    public string attentionIconName;

    [Property(Name = "attentionIconTheme", Access = PropertyAccess.Read)]
    public string attentionIconTheme;

    [Property(Name = "id", Access = PropertyAccess.Read)]
    public string id;

    [Property(Name = "itemIsMenu", Access = PropertyAccess.Read)]
    public bool itemIsMenu;

    [Property(Name = "category", Access = PropertyAccess.Read)]
    public string category;

    public StatusNotifierItem(ObjectPath path, LinuxTrayIcon parent, ILogger logger)
    {
        ObjectPath = path;
        _parent = parent;
        _logger = logger;
        _instanceId = System.Diagnostics.Process.GetCurrentProcess().Id;

        // Initialize D-Bus fields with values from parent
        menu = $"/StatusNotifierItem/{_instanceId}/menu";
        iconName = parent.GetIconName();
        iconTheme = "";
        title = parent.GetTitle();
        tooltip = parent.GetTooltip();
        status = "Active";
        attentionIconName = "";
        attentionIconTheme = "";
        id = $"CloudClipboard-{_instanceId}";
        itemIsMenu = false;
        category = "ApplicationStatus";
    }

    public void Activate([Argument("x")] int x, [Argument("y")] int y)
    {
        _logger.LogDebug("Tray icon activated (left-click) at ({X}, {Y})", x, y);
        _parent.OnItemActivated("Activate");
    }

    public void SecondaryActivate([Argument("x")] int x, [Argument("y")] int y)
    {
        _logger.LogDebug("Tray icon secondary activated (right-click) at ({X}, {Y})", x, y);
    }

    public void ContextMenu([Argument("x")] int x, [Argument("y")] int y)
    {
        _logger.LogDebug("Context menu at ({X}, {Y})", x, y);
    }
}

/// <summary>
/// D-Bus object implementing com.canonical.dbusmenu interface.
/// </summary>
[DBusInterface("com.canonical.dbusmenu")]
public sealed class StatusNotifierMenu : IDBusObject
{
    private readonly LinuxTrayIcon _parent;
    private readonly ILogger _logger;
    private readonly int _instanceId;

    public ObjectPath ObjectPath { get; }
    public List<MenuItem> _menuItems = new();

    [Property(Name = "Version", Access = PropertyAccess.Read)]
    public string version;

    public StatusNotifierMenu(ObjectPath path, LinuxTrayIcon parent, ILogger logger)
    {
        ObjectPath = path;
        _parent = parent;
        _logger = logger;
        _instanceId = System.Diagnostics.Process.GetCurrentProcess().Id;
        version = "1.0";
    }

    public int[] GetItemIds()
    {
        var menuItems = _menuItems;
        var ids = new int[menuItems.Count];
        for (int i = 0; i < menuItems.Count; i++)
            ids[i] = i;
        return ids;
    }

    public Dictionary<string, object>?[] GetProperty([Argument("id")] int itemId, [Argument("names")] string[] names, [Argument("revision")] string revision)
    {
        var props = new Dictionary<string, object>?[names.Length];
        var menuItems = _menuItems;

        for (int i = 0; i < names.Length; i++)
        {
            var dict = new Dictionary<string, object>();
            if (itemId < menuItems.Count)
            {
                var item = menuItems[itemId];
                switch (names[i])
                {
                    case "type":
                        dict.Add("type", Encoding.UTF8.GetBytes(item.Type));
                        break;
                    case "label":
                        dict.Add("label", item.Label);
                        break;
                    case "enabled":
                        dict.Add("enabled", item.Enabled);
                        break;
                    case "toggle-type":
                        dict.Add("toggle-type", item.ToggleType ?? "");
                        break;
                    case "toggle-state":
                        dict.Add("toggle-state", item.ToggleState ?? "");
                        break;
                    case "shortcut":
                        dict.Add("shortcut", item.Shortcut ?? "");
                        break;
                    case "icon-name":
                        dict.Add("icon-name", item.IconName ?? "");
                        break;
                    case "icon-data":
                        dict.Add("icon-data", item.IconData ?? "");
                        break;
                    case "disjoint-set":
                        if (item.SubItems != null && item.SubItems.Count > 0)
                        {
                            var childIds = new int[item.SubItems.Count];
                            for (int j = 0; j < item.SubItems.Count; j++)
                                childIds[j] = (itemId * 1000) + j;
                            dict.Add("disjoint-set", childIds);
                        }
                        break;
                }
            }
            props[i] = dict;
        }
        return props;
    }
}

/// <summary>
/// Menu item model.
/// </summary>
public class MenuItem
{
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public string Type { get; set; } = "ITEM";
    public bool Enabled { get; set; } = true;
    public string? Shortcut { get; set; }
    public string? IconName { get; set; }
    public string? IconData { get; set; }
    public string? ToggleType { get; set; }
    public string? ToggleState { get; set; }
    public List<MenuItem> SubItems { get; set; } = new();
}
