using System;
using System.Drawing;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace CloudClipboard.Agent.Windows.UI;

public sealed class ProvisioningProgressDialog : Form
{
    private readonly TextBox _logTextBox;
    private readonly ProgressBar _progressBar;
    private readonly Label _statusLabel;
    private readonly Button _closeButton;
    private bool _isComplete;

    public ProvisioningProgressDialog()
    {
        Text = "Provisioning Cloud Clipboard Backend";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Width = 700;
        Height = 500;
        MinimizeBox = false;
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ShowIcon = true;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 4,
            ColumnCount = 1,
            Padding = new Padding(16)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _statusLabel = new Label
        {
            Text = "Initializing provisioning...",
            AutoSize = false,
            Height = 24,
            TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.Fill,
            Font = new Font(Font.FontFamily, 9.5F, FontStyle.Bold)
        };
        layout.Controls.Add(_statusLabel, 0, 0);

        _logTextBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9F),
            BackColor = Color.Black,
            ForeColor = Color.LimeGreen,
            BorderStyle = BorderStyle.FixedSingle
        };
        layout.Controls.Add(_logTextBox, 0, 1);

        _progressBar = new ProgressBar
        {
            Dock = DockStyle.Fill,
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 30,
            Height = 24,
            Margin = new Padding(0, 8, 0, 8)
        };
        layout.Controls.Add(_progressBar, 0, 2);

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(0, 8, 0, 0)
        };
        _closeButton = new Button
        {
            Text = "Close",
            Enabled = false,
            MinimumSize = new Size(90, 28),
            Padding = new Padding(8, 4, 8, 4)
        };
        _closeButton.Click += (_, _) => Close();
        buttonPanel.Controls.Add(_closeButton);
        layout.Controls.Add(buttonPanel, 0, 3);

        Controls.Add(layout);
        FormClosing += OnFormClosing;
    }

    public void SetIcon(Icon icon)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => SetIcon(icon)));
            return;
        }

        Icon = icon;
    }

    public void UpdateStatus(string status)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => UpdateStatus(status)));
            return;
        }

        _statusLabel.Text = status;
    }

    public void AppendLog(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => AppendLog(message)));
            return;
        }

        if (_logTextBox.Text.Length > 0)
        {
            _logTextBox.AppendText(Environment.NewLine);
        }

        _logTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}");
        _logTextBox.SelectionStart = _logTextBox.Text.Length;
        _logTextBox.ScrollToCaret();
    }

    public void MarkComplete(bool success, string? finalMessage = null)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => MarkComplete(success, finalMessage)));
            return;
        }

        _isComplete = true;
        _progressBar.Style = ProgressBarStyle.Continuous;
        _progressBar.Value = success ? _progressBar.Maximum : 0;
        _closeButton.Enabled = true;

        if (!string.IsNullOrWhiteSpace(finalMessage))
        {
            _statusLabel.Text = finalMessage;
            _statusLabel.ForeColor = success ? Color.DarkGreen : Color.DarkRed;
        }
        else
        {
            _statusLabel.Text = success ? "✓ Provisioning completed successfully" : "✗ Provisioning failed";
            _statusLabel.ForeColor = success ? Color.DarkGreen : Color.DarkRed;
        }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_isComplete && e.CloseReason == CloseReason.UserClosing)
        {
            var result = MessageBox.Show(
                this,
                "Provisioning is still in progress. Are you sure you want to cancel?",
                "Cancel Provisioning",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (result != DialogResult.Yes)
            {
                e.Cancel = true;
            }
        }
    }
}
