using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CloudClipboard.Agent.Windows.Services;

namespace CloudClipboard.Agent.Windows.UI;

public sealed class AzureCliInstallDialog : Form
{
    private static readonly Uri DownloadUri32Bit = BuildHttpsUri("aka.ms", "installazurecliwindows");
    private static readonly Uri DownloadUri64Bit = BuildHttpsUri("aka.ms", "installazurecliwindowsx64");
    private static readonly Uri DocumentationUri = BuildHttpsUri("learn.microsoft.com", "/cli/azure/install-azure-cli");

    private readonly HttpClient _httpClient;
    private readonly CancellationTokenSource _installCts = new();
    private readonly Uri _downloadUri;

    private readonly Label _statusLabel;
    private readonly TextBox _logTextBox;
    private readonly ProgressBar _progressBar;
    private readonly Button _installButton;
    private readonly Button _checkButton;
    private readonly Button _closeButton;
    private bool _operationRunning;
    private DateTime _lastDownloadLogUtc = DateTime.MinValue;
    private double _lastDownloadPercentLogged = -10d;

    public AzureCliInstallDialog(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _downloadUri = Environment.Is64BitOperatingSystem ? DownloadUri64Bit : DownloadUri32Bit;
        Text = "Azure CLI Required";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
            AutoScroll = true;
        ClientSize = new Size(1060, 520);
        MinimumSize = new Size(1020, 500);
        MinimizeBox = false;
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            RowCount = 6,
            ColumnCount = 1
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var introLabel = new Label
        {
            Text = "Cloud Clipboard uses the Azure CLI (az) for provisioning and deployments. Install it now to continue.",
            AutoSize = true,
            Dock = DockStyle.Fill
        };
        layout.Controls.Add(introLabel, 0, 0);

        _statusLabel = new Label
        {
            Text = "Azure CLI not detected.",
            AutoSize = true,
            Dock = DockStyle.Fill,
            Font = new Font(Font.FontFamily, 9.5F, FontStyle.Bold),
            Margin = new Padding(0, 12, 0, 4)
        };
        layout.Controls.Add(_statusLabel, 0, 1);

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
        layout.Controls.Add(_logTextBox, 0, 2);

        _progressBar = new ProgressBar
        {
            Dock = DockStyle.Fill,
            Height = 22,
            Style = ProgressBarStyle.Continuous,
            Margin = new Padding(0, 10, 0, 4)
        };
        layout.Controls.Add(_progressBar, 0, 3);

        var docsLink = new LinkLabel
        {
            Text = "Need help? Open installation guide",
            AutoSize = true,
            LinkBehavior = LinkBehavior.HoverUnderline,
            Margin = new Padding(0, 8, 0, 0)
        };
        docsLink.LinkClicked += (_, _) => OpenDocumentation();
        layout.Controls.Add(docsLink, 0, 4);

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(0, 6, 0, 0)
        };

        _closeButton = new Button { Text = "Close", AutoSize = true };
        _closeButton.Click += (_, _) => Close();
        buttonPanel.Controls.Add(_closeButton);

        _checkButton = new Button { Text = "Check Again", AutoSize = true };
        _checkButton.Click += (_, _) => CheckExistingCli();
        buttonPanel.Controls.Add(_checkButton);

        _installButton = new Button { Text = "Install Automatically", AutoSize = true };
        _installButton.Click += async (_, _) => await InstallAsync();
        buttonPanel.Controls.Add(_installButton);

        layout.Controls.Add(buttonPanel, 0, 5);

        Controls.Add(layout);
        FormClosing += OnFormClosing;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        CheckExistingCli();
    }

    private async Task InstallAsync()
    {
        if (_operationRunning)
        {
            return;
        }

        _operationRunning = true;
        ToggleBusyState(true, "Downloading Azure CLI installer...");
        string? tempFile = null;
        try
        {
            tempFile = await DownloadInstallerAsync(_downloadUri, _installCts.Token).ConfigureAwait(true);
            AppendLog($"Downloaded installer to {tempFile}.");

            ToggleBusyState(true, "Launching installer (you may be prompted for admin approval)...");
            var exitCode = await RunInstallerAsync(tempFile, _installCts.Token).ConfigureAwait(true);
            AppendLog($"Installer exited with code {exitCode}.");

            if (exitCode == 0)
            {
                ToggleBusyState(true, "Checking for Azure CLI...");
                await Task.Delay(1500, _installCts.Token).ConfigureAwait(true);
                if (AzureCliLocator.TryResolveExecutable(out var azPath, out _))
                {
                    AppendLog($"Azure CLI detected at {azPath}.");
                    _statusLabel.Text = "Azure CLI installed successfully.";
                    DialogResult = DialogResult.OK;
                    Close();
                    return;
                }
            }

            _statusLabel.Text = "Azure CLI not detected yet. Click 'Check Again' after completing setup.";
            MessageBox.Show(this, "Azure CLI was not detected yet. Complete the installer or check PATH, then click 'Check Again'.", "Azure CLI", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException)
        {
            AppendLog("Installation cancelled.");
            _statusLabel.Text = "Installation cancelled.";
        }
        catch (Exception ex)
        {
            AppendLog($"Error: {ex.Message}");
            _statusLabel.Text = "Azure CLI installation failed.";
            MessageBox.Show(this, ex.Message, "Azure CLI Installation Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _operationRunning = false;
            ToggleBusyState(false, string.Empty);
            if (!string.IsNullOrWhiteSpace(tempFile))
            {
                TryDelete(tempFile);
            }
        }
    }

    private void CheckExistingCli()
    {
        if (AzureCliLocator.TryResolveExecutable(out var path, out var error))
        {
            AppendLog($"Azure CLI detected at {path}.");
            _statusLabel.Text = "Azure CLI detected. You're ready to continue.";
            DialogResult = DialogResult.OK;
            Close();
        }
        else
        {
            AppendLog(error ?? "Azure CLI still not found on PATH.");
            _statusLabel.Text = "Azure CLI still missing.";
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignore cleanup failures
        }
    }

    private async Task<string> DownloadInstallerAsync(Uri downloadUri, CancellationToken cancellationToken)
    {
        _lastDownloadPercentLogged = -10d;
        _lastDownloadLogUtc = DateTime.MinValue;

        using var response = await _httpClient.GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        var tempPath = Path.Combine(Path.GetTempPath(), $"AzureCLI_{Guid.NewGuid():N}.msi");
        await using var httpStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var fileStream = File.Create(tempPath);

        ReportDownloadProgress(0, totalBytes);
        await CopyWithProgressAsync(httpStream, fileStream, totalBytes, cancellationToken).ConfigureAwait(false);
        return tempPath;
    }

    private static async Task<int> RunInstallerAsync(string installerPath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("msiexec.exe")
        {
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("/i");
        startInfo.ArgumentList.Add(installerPath);
        startInfo.ArgumentList.Add("/passive");
        startInfo.ArgumentList.Add("/norestart");

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to launch msiexec.exe.");
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(true);
        return process.ExitCode;
    }

    private async Task CopyWithProgressAsync(Stream source, Stream destination, long? totalBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long written = 0;

        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            written += read;
            ReportDownloadProgress(written, totalBytes);
        }

        ReportDownloadProgress(written, totalBytes);
    }

    private void OpenDocumentation()
    {
        try
        {
            Process.Start(new ProcessStartInfo(DocumentationUri.ToString()) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Unable to open browser", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void AppendLog(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => AppendLog(message)));
            return;
        }

        if (_logTextBox.TextLength > 0)
        {
            _logTextBox.AppendText(Environment.NewLine);
        }

        _logTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}");
        _logTextBox.SelectionStart = _logTextBox.TextLength;
        _logTextBox.ScrollToCaret();
    }

    private void ToggleBusyState(bool busy, string status)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => ToggleBusyState(busy, status)));
            return;
        }

        _installButton.Enabled = !busy;
        _checkButton.Enabled = !busy;
        _closeButton.Enabled = !busy;
        _progressBar.Style = busy ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
        _progressBar.MarqueeAnimationSpeed = busy ? 30 : 0;
        if (!string.IsNullOrWhiteSpace(status))
        {
            _statusLabel.Text = status;
        }
    }

    private void ReportDownloadProgress(long bytesTransferred, long? totalBytes)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => ReportDownloadProgress(bytesTransferred, totalBytes)));
            return;
        }

        var now = DateTime.UtcNow;
        string progressText;

        if (totalBytes.HasValue && totalBytes.Value > 0)
        {
            var percent = (double)bytesTransferred / totalBytes.Value * 100d;
            var rounded = (int)Math.Clamp(Math.Round(percent), 0, 100);
            _progressBar.Style = ProgressBarStyle.Continuous;
            _progressBar.MarqueeAnimationSpeed = 0;
            _progressBar.Value = Math.Min(_progressBar.Maximum, Math.Max(_progressBar.Minimum, rounded));
            progressText = $"{percent:0.0}% ({FormatBytes(bytesTransferred)} of {FormatBytes(totalBytes.Value)})";

            if (percent - _lastDownloadPercentLogged >= 5 || percent >= 99.9)
            {
                AppendLog($"Download progress: {progressText}");
                _lastDownloadPercentLogged = percent;
                _lastDownloadLogUtc = now;
            }
        }
        else
        {
            _progressBar.Style = ProgressBarStyle.Marquee;
            _progressBar.MarqueeAnimationSpeed = 30;
            progressText = $"{FormatBytes(bytesTransferred)} downloaded";

            if (now - _lastDownloadLogUtc >= TimeSpan.FromSeconds(2) || bytesTransferred == 0)
            {
                AppendLog($"Download progress: {progressText}");
                _lastDownloadLogUtc = now;
            }
        }

        _statusLabel.Text = $"Downloading Azure CLI installer... {progressText}";
    }

    private static string FormatBytes(long bytes)
    {
        var size = Math.Abs(bytes);
        string[] suffixes = ["B", "KB", "MB", "GB"];
        var index = 0;
        double value = size;

        while (value >= 1024 && index < suffixes.Length - 1)
        {
            value /= 1024;
            index++;
        }

        if (bytes == 0)
        {
            return "0 B";
        }

        return $"{value:0.##} {suffixes[index]}";
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_operationRunning && e.CloseReason == CloseReason.UserClosing)
        {
            var result = MessageBox.Show(
                this,
                "The Azure CLI installer is still running. Cancel installation and close this window?",
                "Cancel Installation",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (result != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            _installCts.Cancel();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _installCts.Cancel();
            _installCts.Dispose();
        }

        base.Dispose(disposing);
    }

    private static Uri BuildHttpsUri(string host, string path)
    {
        var builder = new UriBuilder
        {
            Scheme = Uri.UriSchemeHttps,
            Host = host,
                Path = path.StartsWith("/", StringComparison.Ordinal) ? path : $"/{path.TrimStart('/')}"
        };

        return builder.Uri;
    }
}
