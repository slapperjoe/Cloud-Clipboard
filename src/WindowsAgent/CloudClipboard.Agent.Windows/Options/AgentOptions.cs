using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace CloudClipboard.Agent.Windows.Options;

public sealed class AgentOptions
{
    public string OwnerId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = Environment.MachineName;
    public string ApiBaseUrl { get; set; } = "http://localhost:7071/api";
    public string? FunctionKey { get; set; }
    public int PollIntervalSeconds { get; set; } = 3;
    public int HistoryLength { get; set; } = 20;
    public int HistoryPollSeconds { get; set; } = 5;
    public int OwnerStatePollSeconds { get; set; } = 10;
    public bool AutoPasteLatestOnStartup { get; set; }
    public bool ShowNotifications { get; set; } = true;
    public ClipboardUploadMode UploadMode { get; set; } = ClipboardUploadMode.Auto;
    public string ManualUploadHotkey { get; set; } = "Ctrl+Shift+U";
    public string ManualDownloadHotkey { get; set; } = "Ctrl+Shift+D";
    public bool EnablePushNotifications { get; set; } = true;
    public int PushReconnectSeconds { get; set; } = 30;
    public NotificationTransport NotificationTransport { get; set; } = NotificationTransport.PubSub;
    public ClipboardSyncDirection SyncDirection { get; set; } = ClipboardSyncDirection.Full;
    public List<PinnedClipboardItem> PinnedItems { get; set; } = new();

    [JsonIgnore]
    public bool IsUploadEnabled => SyncDirection is ClipboardSyncDirection.Full or ClipboardSyncDirection.OnlyCut;

    [JsonIgnore]
    public bool IsDownloadEnabled => SyncDirection is ClipboardSyncDirection.Full or ClipboardSyncDirection.OnlyPaste;
}

public enum NotificationTransport
{
    Polling = 0,
    PubSub = 1
}

public enum ClipboardSyncDirection
{
    Full = 0,
    OnlyCut = 1,
    OnlyPaste = 2
}
