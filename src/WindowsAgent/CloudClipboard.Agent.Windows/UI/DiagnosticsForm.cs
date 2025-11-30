using System;
using System.Drawing;
using System.Windows.Forms;
using CloudClipboard.Agent.Windows.Services;

namespace CloudClipboard.Agent.Windows.UI;

public sealed class DiagnosticsForm : Form
{
    private readonly IAgentDiagnostics _diagnostics;
    private readonly IOwnerStateCache _ownerStateCache;
    private readonly IClipboardHistoryCache _historyCache;
    private readonly IManualUploadStore _manualUploadStore;
    private readonly IPinnedClipboardStore _pinnedStore;
    private readonly Label _pendingUploads;
    private readonly Label _lastCapture;
    private readonly Label _lastUpload;
    private readonly Label _lastFailure;
    private readonly Label _lastManualDownload;
    private readonly Label _historyCount;
    private readonly Label _ownerState;
    private readonly Label _pinnedCount;
    private readonly Label _manualStage;
    private readonly System.Windows.Forms.Timer _timer;

    public DiagnosticsForm(
        IAgentDiagnostics diagnostics,
        IOwnerStateCache ownerStateCache,
        IClipboardHistoryCache historyCache,
        IManualUploadStore manualUploadStore,
        IPinnedClipboardStore pinnedStore)
    {
        _diagnostics = diagnostics;
        _ownerStateCache = ownerStateCache;
        _historyCache = historyCache;
        _manualUploadStore = manualUploadStore;
        _pinnedStore = pinnedStore;

        Text = "Cloud Clipboard Diagnostics";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScroll = true;
        ClientSize = new Size(900, 700);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 0,
            Padding = new Padding(10),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink
        };

        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));

        _pendingUploads = AddRow(layout, "Pending uploads:");
        _lastCapture = AddRow(layout, "Last capture:");
        _lastUpload = AddRow(layout, "Last upload:");
        _lastFailure = AddRow(layout, "Last upload failure:");
        _lastManualDownload = AddRow(layout, "Last manual download:");
        _historyCount = AddRow(layout, "Cached history items:");
        _pinnedCount = AddRow(layout, "Pinned items:");
        _manualStage = AddRow(layout, "Manual staging:");
        _ownerState = AddRow(layout, "Owner state:");

        Controls.Add(layout);

        _timer = new System.Windows.Forms.Timer { Interval = 1000, Enabled = true };
        _timer.Tick += (_, _) => RefreshMetrics();
        FormClosed += (_, _) => _timer.Dispose();

        RefreshMetrics();
    }

    private static Label AddRow(TableLayoutPanel layout, string label)
    {
        var caption = new Label
        {
            Text = label,
            AutoSize = true,
            Font = new Font(FontFamily.GenericSansSerif, 9, FontStyle.Bold)
        };
        var value = new Label
        {
            Text = "-",
            AutoSize = true
        };

        var rowIndex = layout.RowCount;
        layout.Controls.Add(caption, 0, rowIndex);
        layout.Controls.Add(value, 1, rowIndex);
        layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        return value;
    }

    private void RefreshMetrics()
    {
        _pendingUploads.Text = _diagnostics.PendingUploadCount.ToString();
        _lastCapture.Text = FormatTimestamp(_diagnostics.LastCaptureUtc);
        _lastUpload.Text = FormatTimestamp(_diagnostics.LastUploadUtc, _diagnostics.LastUploadDuration);
        _lastFailure.Text = FormatTimestamp(_diagnostics.LastUploadFailureUtc);
        _lastManualDownload.Text = FormatTimestamp(_diagnostics.LastManualDownloadUtc);
        _historyCount.Text = _historyCache.Snapshot.Count.ToString();
        _pinnedCount.Text = _pinnedStore.Snapshot.Count.ToString();
        _manualStage.Text = _manualUploadStore.HasPending ? "Item staged" : "Empty";
        _ownerState.Text = _ownerStateCache.IsPaused ? "Paused" : "Active";
    }

    private static string FormatTimestamp(DateTimeOffset? timestamp, TimeSpan? duration = null)
    {
        if (timestamp is null)
        {
            return "(never)";
        }

        var text = timestamp.Value.ToLocalTime().ToString("g");
        if (duration is not null)
        {
            text += $" ({duration.Value.TotalMilliseconds:F0} ms)";
        }

        return text;
    }
}
