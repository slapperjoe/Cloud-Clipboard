using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Text;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CloudClipboard.Agent.Windows.Configuration;
using CloudClipboard.Agent.Windows.Interop;
using CloudClipboard.Agent.Windows.Options;
using CloudClipboard.Agent.Windows.Services;
using CloudClipboard.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudClipboard.Agent.Windows.UI;

public sealed class TrayIconHostedService : BackgroundService, IDisposable
{
    private readonly IClipboardHistoryCache _historyCache;
    private readonly ClipboardPasteService _pasteService;
    private readonly ICloudClipboardClient _client;
    private readonly IOptionsMonitor<AgentOptions> _options;
    private readonly IAgentSettingsStore _settingsStore;
    private readonly IOwnerStateCache _ownerStateCache;
    private readonly ILogger<TrayIconHostedService> _logger;
    private readonly IManualUploadStore _manualUploadStore;
    private readonly IClipboardUploadQueue _uploadQueue;
    private readonly IPinnedClipboardStore _pinnedStore;
    private readonly IAgentDiagnostics _diagnostics;
    private NotifyIcon? _notifyIcon;
    private SynchronizationContext? _uiContext;
    private readonly TaskCompletionSource<bool> _uiReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Thread? _uiThread;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private ToolStripMenuItem? _pauseMenuItem;
    private ToolStripMenuItem? _manualUploadMenuItem;
    private ToolStripMenuItem? _manualDownloadMenuItem;
    private ToolStripMenuItem? _pinnedMenuItem;
    private ToolStripMenuItem? _autoModeMenuItem;
    private ToolStripMenuItem? _manualModeMenuItem;
    private GlobalHotKeyRegistration? _manualUploadHotKey;
    private GlobalHotKeyRegistration? _manualDownloadHotKey;
    private DiagnosticsForm? _diagnosticsForm;
    private SettingsForm? _settingsForm;
    private IDisposable? _optionsSubscription;
    private Icon? _scissorsIcon;
    private IntPtr _scissorsIconHandle;
    private bool _stateInitialized;
    private static int _hotKeyCounter;

    public TrayIconHostedService(
        IClipboardHistoryCache historyCache,
        ClipboardPasteService pasteService,
        IOptionsMonitor<AgentOptions> options,
        IAgentSettingsStore settingsStore,
        ILogger<TrayIconHostedService> logger,
        ICloudClipboardClient client,
        IOwnerStateCache ownerStateCache,
        IManualUploadStore manualUploadStore,
        IClipboardUploadQueue uploadQueue,
        IPinnedClipboardStore pinnedStore,
        IAgentDiagnostics diagnostics)
    {
        _historyCache = historyCache;
        _pasteService = pasteService;
        _options = options;
        _settingsStore = settingsStore;
        _logger = logger;
        _client = client;
        _ownerStateCache = ownerStateCache;
        _manualUploadStore = manualUploadStore;
        _uploadQueue = uploadQueue;
        _pinnedStore = pinnedStore;
        _diagnostics = diagnostics;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        StartUiThread();
        _historyCache.HistoryChanged += OnHistoryChanged;
        _ownerStateCache.StateChanged += OnOwnerStateChanged;
        _manualUploadStore.PendingChanged += OnManualUploadPendingChanged;
        _pinnedStore.Changed += OnPinnedItemsChanged;
        _diagnostics.Changed += OnDiagnosticsChanged;

        using var registration = stoppingToken.Register(() => InvokeOnUi(Application.ExitThread));
        await _uiReady.Task.ConfigureAwait(false);
        RefreshTrayPresentation();
        _optionsSubscription = _options.OnChange(_ => RefreshTrayPresentation());

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when stopping
        }
    }

    private void StartUiThread()
    {
        if (_uiThread is not null)
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

                _notifyIcon = new NotifyIcon
                {
                    Icon = GetOrCreateScissorsIcon(),
                    Text = "Cloud Clipboard",
                    Visible = true,
                    ContextMenuStrip = BuildContextMenu()
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

    private Icon GetOrCreateScissorsIcon()
    {
        if (_scissorsIcon is not null)
        {
            return _scissorsIcon;
        }

        var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.FromArgb(32, 32, 32));
            using var font = new Font("Segoe UI Symbol", 20, FontStyle.Regular, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(Color.White);
            var glyph = "\u2702";
            var size = graphics.MeasureString(glyph, font);
            var location = new PointF((bitmap.Width - size.Width) / 2f, (bitmap.Height - size.Height) / 2f);
            graphics.DrawString(glyph, font, brush, location);
        }

        _scissorsIconHandle = bitmap.GetHicon();
        _scissorsIcon = Icon.FromHandle(_scissorsIconHandle);
        bitmap.Dispose();
        return _scissorsIcon;
    }

    private void OnHistoryChanged(object? sender, IReadOnlyList<ClipboardItemDto> items)
    {
        InvokeOnUi(() =>
        {
            if (_notifyIcon?.ContextMenuStrip is ContextMenuStrip menu)
            {
                RefreshHistoryMenu(menu, items);
            }

            UpdateManualDownloadMenuStateCore();
        });
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();
        RefreshHistoryMenu(menu, _historyCache.Snapshot);
        menu.Items.Add(new ToolStripSeparator());
        _pinnedMenuItem = new ToolStripMenuItem("Pinned Items");
        menu.Items.Add(_pinnedMenuItem);
        UpdatePinnedMenu();
        menu.Items.Add(new ToolStripSeparator());
        var refreshItem = new ToolStripMenuItem("Refresh Now", null, async (_, _) => await RefreshHistoryAsync());
        menu.Items.Add(refreshItem);
        _manualDownloadMenuItem = new ToolStripMenuItem("Download Latest Clipboard", null, async (_, _) => await TriggerManualDownloadAsync("menu"))
        {
            Enabled = false
        };
        menu.Items.Add(_manualDownloadMenuItem);
        _manualUploadMenuItem = new ToolStripMenuItem("Upload Cached Clipboard", null, async (_, _) => await TriggerManualUploadAsync("menu"))
        {
            Enabled = false
        };
        menu.Items.Add(_manualUploadMenuItem);
        var uploadModeMenu = new ToolStripMenuItem("Upload Mode");
        _autoModeMenuItem = new ToolStripMenuItem("Automatic", null, async (_, _) => await SetUploadModeAsync(ClipboardUploadMode.Auto))
        {
            CheckOnClick = false
        };
        uploadModeMenu.DropDownItems.Add(_autoModeMenuItem);
        _manualModeMenuItem = new ToolStripMenuItem("Manual", null, async (_, _) => await SetUploadModeAsync(ClipboardUploadMode.Manual))
        {
            CheckOnClick = false
        };
        uploadModeMenu.DropDownItems.Add(_manualModeMenuItem);
        menu.Items.Add(uploadModeMenu);
        _pauseMenuItem = new ToolStripMenuItem("Pause Uploading", null, async (_, _) => await TogglePauseAsync());
        _pauseMenuItem.Checked = _ownerStateCache.IsPaused;
        menu.Items.Add(_pauseMenuItem);
        var statusItem = new ToolStripMenuItem("Show Status", null, (_, _) => ShowStatus());
        menu.Items.Add(statusItem);
        var diagnosticsItem = new ToolStripMenuItem("Diagnostics", null, (_, _) => ShowDiagnostics());
        menu.Items.Add(diagnosticsItem);
        var helpItem = new ToolStripMenuItem("Help", null, (_, _) => ShowHelp());
        menu.Items.Add(helpItem);
        var wipeItem = new ToolStripMenuItem("Wipe Cloud Storage...", null, async (_, _) => await WipeCloudStorageAsync());
        menu.Items.Add(wipeItem);
        menu.Items.Add(new ToolStripSeparator());
        var settingsItem = new ToolStripMenuItem("Settings", null, (_, _) => OpenSettings());
        menu.Items.Add(settingsItem);
        var exitItem = new ToolStripMenuItem("Exit", null, (_, _) => Application.ExitThread());
        menu.Items.Add(exitItem);
        UpdatePauseMenuText(_ownerStateCache.State);
        UpdateUploadModeMenuState(_options.CurrentValue.UploadMode);
        UpdateManualUploadMenuState();
        UpdateManualDownloadMenuState();
        return menu;
    }

    private void RefreshHistoryMenu(ContextMenuStrip menu, IReadOnlyList<ClipboardItemDto> items)
    {
        for (var i = menu.Items.Count - 1; i >= 0; i--)
        {
            if (menu.Items[i] is ToolStripMenuItem { Tag: ClipboardItemDto })
            {
                menu.Items.RemoveAt(i);
            }
        }

        foreach (var item in items.Take(10))
        {
            var label = BuildHistoryLabel(item);
            var entry = new ToolStripMenuItem(label)
            {
                Tag = item
            };
            var currentItem = item;
            entry.Click += async (_, _) => await PasteItemAsync(currentItem);

            var pinItem = new ToolStripMenuItem("Pin for Quick Access", null, (_, _) => PinItem(currentItem));
            entry.DropDownItems.Add(pinItem);
            var pinDownloadItem = new ToolStripMenuItem("Download && Pin", null, async (_, _) => await PinAndDownloadAsync(currentItem));
            entry.DropDownItems.Add(pinDownloadItem);

            menu.Items.Insert(0, entry);
        }
    }

    private async Task PasteItemAsync(ClipboardItemDto item)
    {
        if (!EnsureDownloadsEnabled("download clipboard items"))
        {
            return;
        }

        try
        {
            var ownerId = _options.CurrentValue.OwnerId;
            if (string.IsNullOrWhiteSpace(ownerId))
            {
                _logger.LogWarning("Cannot paste clipboard item because OwnerId is not configured");
                return;
            }

            var fullItem = item.ContentBase64 is not null
                ? item
                : await _client.DownloadAsync(ownerId, item.Id, CancellationToken.None);

            if (fullItem is null)
            {
                _logger.LogWarning("Clipboard item {ItemId} no longer exists", item.Id);
                return;
            }

            await _pasteService.PasteAsync(fullItem);
            _diagnostics.RecordManualDownload(DateTimeOffset.UtcNow);
            if (_options.CurrentValue.ShowNotifications)
            {
                InvokeOnUi(() => _notifyIcon?.ShowBalloonTip(2000, "Cloud Clipboard", "Item pasted locally", ToolTipIcon.Info));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to paste clipboard item");
        }
    }

    private async Task RefreshHistoryAsync()
    {
        if (!EnsureDownloadsEnabled("refresh history"))
        {
            return;
        }

        var ownerId = _options.CurrentValue.OwnerId;
        if (string.IsNullOrWhiteSpace(ownerId))
        {
            _logger.LogWarning("Cannot refresh clipboard history because OwnerId is not configured");
            ShowBalloon("Cloud Clipboard", "OwnerId is not configured.");
            return;
        }

        if (!_refreshLock.Wait(0))
        {
            return;
        }

        try
        {
            var items = await _client.ListAsync(ownerId, _options.CurrentValue.HistoryLength, CancellationToken.None);
            _historyCache.Update(items);
            ShowBalloon("Cloud Clipboard", "History refreshed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh clipboard history on demand");
            ShowBalloon("Cloud Clipboard", "Refresh failed. Check logs.");
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private void ShowStatus()
    {
        var options = _options.CurrentValue;
        var builder = new StringBuilder();
        if (string.IsNullOrWhiteSpace(options.OwnerId))
        {
            builder.Append("OwnerId not set");
        }
        else
        {
            var uploads = _ownerStateCache.IsPaused ? "Paused" : "Active";
            var mode = options.UploadMode == ClipboardUploadMode.Manual ? "Manual" : "Auto";

            builder.AppendLine($"Owner: {options.OwnerId}");
            builder.AppendLine($"History: {_historyCache.Snapshot.Count} items");
            builder.AppendLine($"Uploads: {uploads}");
            builder.AppendLine($"Mode: {mode}");
            builder.AppendLine($"Download hotkey: {GetDownloadHotkey()}");
            builder.AppendLine($"Pinned items: {_pinnedStore.Snapshot.Count}");
            builder.AppendLine($"Pending uploads: {_diagnostics.PendingUploadCount}");
            if (options.UploadMode == ClipboardUploadMode.Manual)
            {
                builder.AppendLine($"Upload hotkey: {GetUploadHotkey()}");
                builder.AppendLine($"Staged clipboard item: {(_manualUploadStore.HasPending ? "Yes" : "No")}");
            }
            else
            {
                builder.Append("Staged clipboard item: N/A");
            }
        }

        ShowBalloon("Cloud Clipboard", builder.ToString());
    }

    private void ShowHelp()
    {
        var builder = new StringBuilder();
        builder.AppendLine("Cloud Clipboard keeps your Windows clipboard in sync via Azure Functions.");
        builder.AppendLine();
        builder.AppendLine("Key actions:");
        builder.AppendLine($"- Automatic mode uploads immediately. Manual mode stages until you press {GetUploadHotkey()} or use Upload Cached Clipboard.");
        builder.AppendLine($"- {GetDownloadHotkey()} downloads the newest cloud clipboard entry.");
        builder.AppendLine("- Use Pinned Items for quick access to frequently used captures.");
        builder.AppendLine("- Diagnostics shows capture/upload timestamps, pending queue, and staging state.");
        builder.AppendLine();
        builder.AppendLine("Configuration:");
        builder.AppendLine($"- Settings live beside the executable at: {AgentSettingsPathProvider.GetSettingsPath()}.");
        builder.AppendLine("- Update OwnerId, DeviceName, and FunctionKey before connecting to your Functions API.");

        InvokeOnUi(() => MessageBox.Show(builder.ToString(), "Cloud Clipboard Help", MessageBoxButtons.OK, MessageBoxIcon.Information));
    }

    private async Task WipeCloudStorageAsync()
    {
        var ownerId = _options.CurrentValue.OwnerId;
        if (string.IsNullOrWhiteSpace(ownerId))
        {
            ShowBalloon("Cloud Clipboard", "OwnerId is not configured.");
            return;
        }

        var confirmation = await ShowConfirmationAsync(
            "This will permanently delete all clipboard history stored in the cloud for this owner. Continue?",
            "Wipe Cloud Clipboard");
        if (confirmation != DialogResult.Yes)
        {
            return;
        }

        try
        {
            await _client.DeleteOwnerAsync(ownerId, CancellationToken.None).ConfigureAwait(false);
            _historyCache.Update(Array.Empty<ClipboardItemDto>());
            _manualUploadStore.Clear();
            _pinnedStore.Clear();
            ShowBalloon("Cloud Clipboard", "Remote clipboard history deleted.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to wipe clipboard history for {OwnerId}", ownerId);
            ShowBalloon("Cloud Clipboard", "Failed to wipe remote history. Check logs.");
        }
    }

    private void OpenSettings()
    {
        InvokeOnUi(() =>
        {
            if (_settingsForm is { IsDisposed: false })
            {
                _settingsForm.Show();
                _settingsForm.Activate();
                return;
            }

            try
            {
                _settingsForm = new SettingsForm(_settingsStore);
                _settingsForm.FormClosed += (_, _) => _settingsForm = null;
                _settingsForm.Show();
                _settingsForm.Activate();
            }
            catch (Exception ex)
            {
                _settingsForm = null;
                _logger.LogError(ex, "Failed to open settings window");
                MessageBox.Show("Unable to open the settings window. Check the logs for details.", "Cloud Clipboard", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        });
    }

    private void ShowDiagnostics()
    {
        InvokeOnUi(() =>
        {
            if (_diagnosticsForm is null || _diagnosticsForm.IsDisposed)
            {
                _diagnosticsForm = new DiagnosticsForm(_diagnostics, _ownerStateCache, _historyCache, _manualUploadStore, _pinnedStore);
                _diagnosticsForm.FormClosed += (_, _) => _diagnosticsForm = null;
            }

            _diagnosticsForm.Show();
            _diagnosticsForm.Activate();
        });
    }

    private void ShowBalloon(string title, string message)
        => InvokeOnUi(() => _notifyIcon?.ShowBalloonTip(2000, title, message, ToolTipIcon.Info));

    private bool EnsureUploadsEnabled(string action)
    {
        if (_options.CurrentValue.IsUploadEnabled)
        {
            return true;
        }

        ShowBalloon("Cloud Clipboard", $"Cannot {action} because sync direction is set to 'Only Paste'.");
        return false;
    }

    private bool EnsureDownloadsEnabled(string action)
    {
        if (_options.CurrentValue.IsDownloadEnabled)
        {
            return true;
        }

        ShowBalloon("Cloud Clipboard", $"Cannot {action} because sync direction is set to 'Only Cut'.");
        return false;
    }

    private Task<DialogResult> ShowConfirmationAsync(string message, string caption)
    {
        var tcs = new TaskCompletionSource<DialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        InvokeOnUi(() =>
        {
            var result = MessageBox.Show(message, caption, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            tcs.TrySetResult(result);
        });
        return tcs.Task;
    }

    public override void Dispose()
    {
        _historyCache.HistoryChanged -= OnHistoryChanged;
        _ownerStateCache.StateChanged -= OnOwnerStateChanged;
        _manualUploadStore.PendingChanged -= OnManualUploadPendingChanged;
        _pinnedStore.Changed -= OnPinnedItemsChanged;
        _diagnostics.Changed -= OnDiagnosticsChanged;
        _optionsSubscription?.Dispose();
        _manualUploadHotKey?.Dispose();
        _manualUploadHotKey = null;
        _manualDownloadHotKey?.Dispose();
        _manualDownloadHotKey = null;
        InvokeOnUi(() =>
        {
            if (_diagnosticsForm is not null)
            {
                _diagnosticsForm.Close();
                _diagnosticsForm.Dispose();
                _diagnosticsForm = null;
            }

            if (_settingsForm is not null)
            {
                _settingsForm.Close();
                _settingsForm.Dispose();
                _settingsForm = null;
            }

            if (_notifyIcon is not null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }

            Application.ExitThread();
        });

        if (_scissorsIconHandle != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_scissorsIconHandle);
            _scissorsIconHandle = IntPtr.Zero;
            _scissorsIcon = null;
        }

        base.Dispose();
    }

    private void InvokeOnUi(Action action)
    {
        if (_uiContext is null)
        {
            return;
        }

        _uiContext.Post(_ => action(), null);
    }

    private async Task TogglePauseAsync()
    {
        var ownerId = _options.CurrentValue.OwnerId;
        if (string.IsNullOrWhiteSpace(ownerId))
        {
            ShowBalloon("Cloud Clipboard", "OwnerId is not configured.");
            return;
        }

        InvokeOnUi(() =>
        {
            if (_pauseMenuItem is not null)
            {
                _pauseMenuItem.Enabled = false;
            }
        });

        var target = !_ownerStateCache.IsPaused;
        try
        {
            var state = await _client.SetStateAsync(ownerId, target, CancellationToken.None);
            _ownerStateCache.Update(state);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to toggle pause state");
            ShowBalloon("Cloud Clipboard", "Unable to update pause state.");
        }
        finally
        {
            InvokeOnUi(() =>
            {
                if (_pauseMenuItem is not null)
                {
                    _pauseMenuItem.Enabled = true;
                }
            });
        }
    }

    private async Task SetUploadModeAsync(ClipboardUploadMode targetMode)
    {
        var currentMode = _options.CurrentValue.UploadMode;
        if (currentMode == targetMode)
        {
            return;
        }

        try
        {
            var options = _settingsStore.Load();
            options.UploadMode = targetMode;
            _settingsStore.Save(options);
            UpdateUploadModeMenuState(targetMode);

            if (targetMode == ClipboardUploadMode.Auto && _manualUploadStore.TryTake(out var pending) && pending is not null)
            {
                await _uploadQueue.EnqueueAsync(pending, CancellationToken.None).ConfigureAwait(false);
                _logger.LogInformation("Flushed cached clipboard payload after returning to auto mode");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set upload mode to {Mode}", targetMode);
            ShowBalloon("Cloud Clipboard", "Unable to update upload mode. Check logs.");
        }
    }

    private void UpdateUploadModeMenuState(ClipboardUploadMode mode)
    {
        InvokeOnUi(() =>
        {
            if (_autoModeMenuItem is not null)
            {
                _autoModeMenuItem.Checked = mode == ClipboardUploadMode.Auto;
            }

            if (_manualModeMenuItem is not null)
            {
                var hotkey = GetUploadHotkey();
                _manualModeMenuItem.Checked = mode == ClipboardUploadMode.Manual;
                _manualModeMenuItem.Text = $"Manual ({hotkey})";
            }
        });

        UpdateManualUploadMenuState();
        UpdateManualDownloadMenuState();
        RefreshManualUploadHotKey();
        RefreshManualDownloadHotKey();
        UpdateTrayText();
    }

    private void UpdateManualUploadMenuState()
    {
        InvokeOnUi(() =>
        {
            if (_manualUploadMenuItem is null)
            {
                return;
            }

            var mode = _options.CurrentValue.UploadMode;
            var paused = _ownerStateCache.IsPaused;
            var hasPending = _manualUploadStore.HasPending;
            var hotkey = GetUploadHotkey();
            var uploadsAllowed = _options.CurrentValue.IsUploadEnabled;

            if (!uploadsAllowed)
            {
                _manualUploadMenuItem.Enabled = false;
                _manualUploadMenuItem.Text = $"Upload Cached Clipboard ({hotkey})";
                _manualUploadMenuItem.ToolTipText = "Uploads are disabled by the current sync direction (Only Paste).";
                return;
            }

            _manualUploadMenuItem.Enabled = mode == ClipboardUploadMode.Manual && hasPending && !paused;
            _manualUploadMenuItem.Text = mode == ClipboardUploadMode.Manual
                ? (hasPending ? $"Upload Cached Clipboard ({hotkey})" : $"Waiting for Clipboard Change ({hotkey})")
                : $"Upload Cached Clipboard ({hotkey})";

            _manualUploadMenuItem.ToolTipText = mode == ClipboardUploadMode.Manual
                ? "Uploads the staged clipboard snapshot when one is available."
                : "Switch to manual upload mode to stage clipboard snapshots.";
        });
    }

    private void UpdateManualDownloadMenuState()
        => InvokeOnUi(UpdateManualDownloadMenuStateCore);

    private void UpdateManualDownloadMenuStateCore()
    {
        if (_manualDownloadMenuItem is null)
        {
            return;
        }

        var ownerId = _options.CurrentValue.OwnerId;
        var hotkey = GetDownloadHotkey();
        var latest = _historyCache.Snapshot.FirstOrDefault();
        var downloadsAllowed = _options.CurrentValue.IsDownloadEnabled;

        if (!downloadsAllowed)
        {
            _manualDownloadMenuItem.Enabled = false;
            _manualDownloadMenuItem.Text = $"Download Latest Clipboard ({hotkey})";
            _manualDownloadMenuItem.ToolTipText = "Downloads are disabled by the current sync direction (Only Cut).";
            return;
        }

        _manualDownloadMenuItem.Enabled = !string.IsNullOrWhiteSpace(ownerId);
        _manualDownloadMenuItem.Text = $"Download Latest Clipboard ({hotkey})";
        _manualDownloadMenuItem.ToolTipText = latest is null
            ? "Fetches the newest cloud clipboard entry when one exists."
            : $"Downloads the most recent cloud clipboard entry ({BuildHistoryLabel(latest)}).";
    }

    private static string DescribeClipboardItem(ClipboardItemDto item)
        => item.PayloadType switch
        {
            ClipboardPayloadType.Text => "Text",
            ClipboardPayloadType.Image => "Image",
            ClipboardPayloadType.FileSet => "File(s)",
            _ => item.PayloadType.ToString()
        };

    private static string BuildHistoryLabel(ClipboardItemDto item)
    {
        const string Separator = " \u2022 ";
        var type = DescribeClipboardItem(item);
        var size = FormatSize(item.ContentLength);
        var device = string.IsNullOrWhiteSpace(item.Device) ? "unknown device" : item.Device;
        var created = item.CreatedUtc.ToLocalTime().ToString("t");
        return string.Concat(type, Separator, size, Separator, device, Separator, created);
    }

    private static string FormatSize(long bytes)
    {
        const long KB = 1024;
        const long MB = KB * 1024;
        if (bytes >= MB)
        {
            return $"{bytes / (double)MB:F1} MB";
        }

        if (bytes >= KB)
        {
            return $"{bytes / (double)KB:F1} KB";
        }

        return $"{bytes} B";
    }

    private void PinItem(ClipboardItemDto item)
    {
        if (_pinnedStore.TryGet(item.Id, out _))
        {
            if (_options.CurrentValue.ShowNotifications)
            {
                ShowBalloon("Cloud Clipboard", "Item is already pinned.");
            }

            return;
        }

        _pinnedStore.Pin(item, BuildHistoryLabel(item));
        UpdatePinnedMenu();
        if (_options.CurrentValue.ShowNotifications)
        {
            ShowBalloon("Cloud Clipboard", "Pinned clipboard item for quick access.");
        }
    }

    private void UnpinItem(string itemId)
    {
        _pinnedStore.Unpin(itemId);
        UpdatePinnedMenu();
        if (_options.CurrentValue.ShowNotifications)
        {
            ShowBalloon("Cloud Clipboard", "Removed pinned clipboard item.");
        }
    }

    private async Task PinAndDownloadAsync(ClipboardItemDto item)
    {
        if (!EnsureDownloadsEnabled("download clipboard items"))
        {
            return;
        }

        PinItem(item);
        await PasteItemAsync(item);
    }

    private void UpdatePinnedMenu()
    {
        InvokeOnUi(() =>
        {
            if (_pinnedMenuItem is null)
            {
                return;
            }

            _pinnedMenuItem.DropDownItems.Clear();
            var pinnedItems = _pinnedStore.Snapshot;
            if (pinnedItems.Count == 0)
            {
                _pinnedMenuItem.DropDownItems.Add(new ToolStripMenuItem("No pinned items") { Enabled = false });
                return;
            }

            foreach (var pinned in pinnedItems)
            {
                var current = pinned;
                var label = current.DisplayLabel;
                var entry = new ToolStripMenuItem(label) { Tag = current };
                entry.Click += async (_, _) => await PastePinnedAsync(current);
                var unpin = new ToolStripMenuItem("Remove Pin", null, (_, _) => UnpinItem(current.Id));
                entry.DropDownItems.Add(unpin);
                _pinnedMenuItem.DropDownItems.Add(entry);
            }
        });
    }

    private async Task PastePinnedAsync(PinnedClipboardItem pinned)
    {
        if (!EnsureDownloadsEnabled("download pinned items"))
        {
            return;
        }

        var ownerId = _options.CurrentValue.OwnerId;
        if (string.IsNullOrWhiteSpace(ownerId))
        {
            ShowBalloon("Cloud Clipboard", "OwnerId is not configured.");
            return;
        }

        try
        {
            var dto = await _client.DownloadAsync(ownerId, pinned.Id, CancellationToken.None).ConfigureAwait(false);
            if (dto is null)
            {
                ShowBalloon("Cloud Clipboard", "Pinned item no longer exists in the cloud.");
                return;
            }

            await _pasteService.PasteAsync(dto).ConfigureAwait(false);
            _diagnostics.RecordManualDownload(DateTimeOffset.UtcNow);
            if (_options.CurrentValue.ShowNotifications)
            {
                ShowBalloon("Cloud Clipboard", "Pinned clipboard item downloaded.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download pinned clipboard item {ItemId}", pinned.Id);
            ShowBalloon("Cloud Clipboard", "Unable to download pinned item. Check logs.");
        }
    }

    private async Task TriggerManualUploadAsync(string source)
    {
        if (!EnsureUploadsEnabled("upload clipboard items"))
        {
            return;
        }

        if (_options.CurrentValue.UploadMode != ClipboardUploadMode.Manual)
        {
            ShowBalloon("Cloud Clipboard", "Switch to manual upload mode to trigger uploads manually.");
            return;
        }

        if (!_manualUploadStore.TryTake(out var request) || request is null)
        {
            ShowBalloon("Cloud Clipboard", "No staged clipboard content is available yet.");
            return;
        }

        try
        {
            _diagnostics.IncrementPendingUploads();
            await _uploadQueue.EnqueueAsync(request, CancellationToken.None).ConfigureAwait(false);
            _logger.LogInformation("Manual upload queued via {Source}", source);
            if (_options.CurrentValue.ShowNotifications)
            {
                ShowBalloon("Cloud Clipboard", "Clipboard upload queued.");
            }
        }
        catch (Exception ex)
        {
            _diagnostics.DecrementPendingUploads();
            _logger.LogError(ex, "Failed to enqueue manual upload request");
            _manualUploadStore.Store(request);
            ShowBalloon("Cloud Clipboard", "Unable to queue manual upload. Check logs.");
        }
    }

    private async Task TriggerManualDownloadAsync(string source)
    {
        if (!EnsureDownloadsEnabled("download clipboard items"))
        {
            return;
        }

        var ownerId = _options.CurrentValue.OwnerId;
        if (string.IsNullOrWhiteSpace(ownerId))
        {
            ShowBalloon("Cloud Clipboard", "OwnerId is not configured.");
            return;
        }

        try
        {
            var latest = _historyCache.Snapshot.FirstOrDefault();
            if (latest is null)
            {
                var items = await _client.ListAsync(ownerId, _options.CurrentValue.HistoryLength, CancellationToken.None).ConfigureAwait(false);
                _historyCache.Update(items);
                latest = items.FirstOrDefault();
                UpdateManualDownloadMenuState();
            }

            if (latest is null)
            {
                ShowBalloon("Cloud Clipboard", "No remote clipboard entries are available yet.");
                return;
            }

            var fullItem = latest.ContentBase64 is not null
                ? latest
                : await _client.DownloadAsync(ownerId, latest.Id, CancellationToken.None).ConfigureAwait(false);

            if (fullItem is null)
            {
                ShowBalloon("Cloud Clipboard", "Clipboard item was deleted or expired.");
                return;
            }

            await _pasteService.PasteAsync(fullItem).ConfigureAwait(false);
            _logger.LogInformation("Manual download completed via {Source}", source);
            _diagnostics.RecordManualDownload(DateTimeOffset.UtcNow);
            if (_options.CurrentValue.ShowNotifications)
            {
                ShowBalloon("Cloud Clipboard", "Latest clipboard item downloaded.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download the latest clipboard item manually");
            ShowBalloon("Cloud Clipboard", "Manual download failed. Check logs.");
        }
    }

    private void RefreshTrayPresentation()
    {
        UpdateUploadModeMenuState(_options.CurrentValue.UploadMode);
        UpdateManualDownloadMenuState();
        UpdatePinnedMenu();
        RefreshManualDownloadHotKey();
    }

    private void RefreshManualUploadHotKey()
    {
        InvokeOnUi(() =>
        {
            _manualUploadHotKey?.Dispose();
            _manualUploadHotKey = null;

            if (_options.CurrentValue.UploadMode != ClipboardUploadMode.Manual)
            {
                return;
            }

            if (!_options.CurrentValue.IsUploadEnabled)
            {
                return;
            }

            if (!HotKeyBinding.TryParse(GetUploadHotkey(), out var binding))
            {
                _logger.LogWarning("Manual upload hotkey '{Hotkey}' is invalid", _options.CurrentValue.ManualUploadHotkey);
                return;
            }

            try
            {
                _manualUploadHotKey = new GlobalHotKeyRegistration(binding, () => _ = TriggerManualUploadAsync("hotkey"), _logger);
                _logger.LogInformation("Manual upload hotkey registered: {Hotkey}", binding.DisplayText);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to register manual upload hotkey {Hotkey}", binding.DisplayText);
                ShowBalloon("Cloud Clipboard", "Failed to register manual upload hotkey.");
            }
        });
    }

    private void RefreshManualDownloadHotKey()
    {
        InvokeOnUi(() =>
        {
            _manualDownloadHotKey?.Dispose();
            _manualDownloadHotKey = null;

            if (!HotKeyBinding.TryParse(GetDownloadHotkey(), out var binding))
            {
                _logger.LogWarning("Manual download hotkey '{Hotkey}' is invalid", _options.CurrentValue.ManualDownloadHotkey);
                return;
            }

            if (!_options.CurrentValue.IsDownloadEnabled)
            {
                return;
            }

            try
            {
                _manualDownloadHotKey = new GlobalHotKeyRegistration(binding, () => _ = TriggerManualDownloadAsync("hotkey"), _logger);
                _logger.LogInformation("Manual download hotkey registered: {Hotkey}", binding.DisplayText);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to register manual download hotkey {Hotkey}", binding.DisplayText);
                ShowBalloon("Cloud Clipboard", "Failed to register manual download hotkey.");
            }
        });
    }

    private void UpdateTrayText()
    {
        InvokeOnUi(() =>
        {
            if (_notifyIcon is null)
            {
                return;
            }

            var mode = _options.CurrentValue.UploadMode == ClipboardUploadMode.Manual ? "Manual" : "Auto";
            var state = _ownerStateCache.IsPaused ? "Paused" : "Active";
            var pending = _diagnostics.PendingUploadCount;
            var queueSuffix = pending > 0 ? $", q:{pending}" : string.Empty;
            _notifyIcon.Text = $"Cloud Clipboard ({mode}/{state}{queueSuffix})";
        });
    }

    private void OnManualUploadPendingChanged(object? sender, EventArgs e)
        => UpdateManualUploadMenuState();

    private void OnPinnedItemsChanged(object? sender, IReadOnlyList<PinnedClipboardItem> _) => UpdatePinnedMenu();

    private void OnDiagnosticsChanged(object? sender, EventArgs e)
        => UpdateTrayText();

    private string GetUploadHotkey()
    {
        var configured = _options.CurrentValue.ManualUploadHotkey;
        return string.IsNullOrWhiteSpace(configured) ? "Ctrl+Shift+U" : configured;
    }

    private string GetDownloadHotkey()
    {
        var configured = _options.CurrentValue.ManualDownloadHotkey;
        return string.IsNullOrWhiteSpace(configured) ? "Ctrl+Shift+D" : configured;
    }

    private void OnOwnerStateChanged(object? sender, ClipboardOwnerState state)
    {
        UpdatePauseMenuText(state);
        UpdateManualUploadMenuState();
        UpdateTrayText();
        if (_stateInitialized && _options.CurrentValue.ShowNotifications)
        {
            var message = state.IsPaused
                ? "Uploads paused for this owner."
                : "Uploads resumed for this owner.";
            ShowBalloon("Cloud Clipboard", message);
        }

        _stateInitialized = true;
    }

    private void UpdatePauseMenuText(ClipboardOwnerState state)
    {
        InvokeOnUi(() =>
        {
            if (_pauseMenuItem is null)
            {
                return;
            }

            _pauseMenuItem.Checked = state.IsPaused;
            _pauseMenuItem.Text = state.IsPaused ? "Resume Uploading" : "Pause Uploading";
        });
    }

    private readonly record struct HotKeyBinding(uint Modifiers, uint VirtualKey, string DisplayText)
    {
        public static bool TryParse(string? text, out HotKeyBinding binding)
        {
            binding = default;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            try
            {
                var converter = new KeysConverter();
                if (converter.ConvertFromInvariantString(text) is not Keys keys)
                {
                    return false;
                }

                var modifiersPart = keys & Keys.Modifiers;
                var modifiers = NativeMethods.ModNoRepeat;
                if ((modifiersPart & Keys.Control) == Keys.Control)
                {
                    modifiers |= NativeMethods.ModControl;
                }

                if ((modifiersPart & Keys.Shift) == Keys.Shift)
                {
                    modifiers |= NativeMethods.ModShift;
                }

                if ((modifiersPart & Keys.Alt) == Keys.Alt)
                {
                    modifiers |= NativeMethods.ModAlt;
                }

                if ((modifiersPart & Keys.LWin) == Keys.LWin || (modifiersPart & Keys.RWin) == Keys.RWin)
                {
                    modifiers |= NativeMethods.ModWin;
                }

                if (modifiers == NativeMethods.ModNoRepeat)
                {
                    return false;
                }

                var keyCode = keys & Keys.KeyCode;
                if (keyCode == Keys.None)
                {
                    return false;
                }

                binding = new HotKeyBinding(modifiers, (uint)keyCode, text);
                return true;
            }
            catch
            {
                binding = default;
                return false;
            }
        }
    }

    private sealed class GlobalHotKeyRegistration : NativeWindow, IDisposable
    {
        private readonly int _registrationId;
        private readonly Action _callback;
        private readonly ILogger _logger;
        private readonly HotKeyBinding _binding;

        public GlobalHotKeyRegistration(HotKeyBinding binding, Action callback, ILogger logger)
        {
            _binding = binding;
            _callback = callback;
            _logger = logger;
            _registrationId = Interlocked.Increment(ref _hotKeyCounter);
            CreateHandle(new CreateParams());
            if (!NativeMethods.RegisterHotKey(Handle, _registrationId, binding.Modifiers, binding.VirtualKey))
            {
                var error = Marshal.GetLastWin32Error();
                DestroyHandle();
                throw new InvalidOperationException($"Unable to register global hotkey {binding.DisplayText} (error {error}).");
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeMethods.WmHotKey && m.WParam.ToInt32() == _registrationId)
            {
                _callback();
                return;
            }

            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                NativeMethods.UnregisterHotKey(Handle, _registrationId);
                DestroyHandle();
            }
        }
    }

    private static class NativeMethods
    {
        public const int WmHotKey = 0x0312;
        public const uint ModAlt = 0x0001;
        public const uint ModControl = 0x0002;
        public const uint ModShift = 0x0004;
        public const uint ModWin = 0x0008;
        public const uint ModNoRepeat = 0x4000;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr hIcon);
    }
}
