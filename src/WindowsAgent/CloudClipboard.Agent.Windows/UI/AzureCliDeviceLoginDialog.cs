using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CloudClipboard.Agent.Windows.Services;

namespace CloudClipboard.Agent.Windows.UI;

public sealed class AzureCliDeviceLoginDialog : Form
{
    private static readonly Uri DeviceLoginUri = new("https://microsoft.com/devicelogin");
    private static readonly Regex DeviceCodeRegex = new(
        "([A-Z0-9]{4}-[A-Z0-9]{4})|([A-Z0-9]{8,12})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TenantLineRegex = new(
        "(?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\\s*'?(?<name>[^']*)'?",
        RegexOptions.Compiled);

    private readonly string _azExecutablePath;
    private readonly CancellationTokenSource _dialogCts = new();
    private readonly CancellationTokenSource _linkedCts;
    private readonly List<TenantOption> _tenantOptions = new();

    private readonly Label _statusLabel;
    private readonly TextBox _codeTextBox;
    private readonly Button _copyCodeButton;
    private readonly Button _openBrowserButton;
    private readonly TextBox _logTextBox;
    private readonly ProgressBar _progressBar;
    private readonly Button _cancelButton;
    private readonly Button _closeButton;
    private readonly LinkLabel _loginLinkLabel;
    private readonly GroupBox _tenantGroupBox;
    private readonly FlowLayoutPanel _tenantRadioPanel;
    private readonly Button _tenantLoginButton;

    private string? _deviceCode;
    private bool _loginCompleted;
    private bool _noSubscriptionsDetected;

    public AzureCliDeviceLoginDialog(string azExecutablePath, CancellationToken cancellationToken)
    {
        _azExecutablePath = azExecutablePath ?? throw new ArgumentNullException(nameof(azExecutablePath));
        _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_dialogCts.Token, cancellationToken);

        Text = "Azure CLI Sign-In";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScroll = true;
        ClientSize = new Size(960, 480);
        MinimumSize = new Size(920, 460);
        MaximizeBox = false;
        MinimizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            RowCount = 7,
            ColumnCount = 1
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var introLabel = new Label
        {
            Text = $"Sign in to Azure by entering the device code shown below at {DeviceLoginUri}.",
            AutoSize = true,
            Dock = DockStyle.Fill
        };
        layout.Controls.Add(introLabel, 0, 0);

        _statusLabel = new Label
        {
            Text = "Starting Azure CLI sign-in...",
            AutoSize = true,
            Font = new Font(Font.FontFamily, 9.5F, FontStyle.Bold),
            Margin = new Padding(0, 10, 0, 8)
        };
        layout.Controls.Add(_statusLabel, 0, 1);

        var codePanel = new TableLayoutPanel
        {
            ColumnCount = 4,
            Dock = DockStyle.Fill,
            AutoSize = true
        };
        codePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        codePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        codePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        codePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var codeLabel = new Label
        {
            Text = "Device code:",
            AutoSize = true,
            Anchor = AnchorStyles.Left
        };
        codePanel.Controls.Add(codeLabel, 0, 0);

        _codeTextBox = new TextBox
        {
            ReadOnly = true,
            Font = new Font("Consolas", 14F, FontStyle.Bold),
            Dock = DockStyle.Fill,
            Margin = new Padding(6, 0, 6, 0)
        };
        codePanel.Controls.Add(_codeTextBox, 1, 0);

        _copyCodeButton = new Button
        {
            Text = "Copy",
            Enabled = false,
            AutoSize = true,
            Anchor = AnchorStyles.Right
        };
        _copyCodeButton.Click += (_, _) => CopyCode();
        codePanel.Controls.Add(_copyCodeButton, 2, 0);

        _openBrowserButton = new Button
        {
            Text = "Open Login Page",
            Enabled = false,
            AutoSize = true,
            Anchor = AnchorStyles.Right
        };
        _openBrowserButton.Click += (_, _) => OpenLoginPage();
        codePanel.Controls.Add(_openBrowserButton, 3, 0);

        layout.Controls.Add(codePanel, 0, 2);

        _progressBar = new ProgressBar
        {
            Dock = DockStyle.Fill,
            Style = ProgressBarStyle.Marquee,
            Height = 22,
            Margin = new Padding(0, 12, 0, 8)
        };
        layout.Controls.Add(_progressBar, 0, 3);

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
        layout.Controls.Add(_logTextBox, 0, 4);

        _tenantGroupBox = new GroupBox
        {
            Text = "Select a tenant to continue",
            Dock = DockStyle.Fill,
            Visible = false,
            Padding = new Padding(12)
        };

        var tenantLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2
        };
        tenantLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        tenantLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var tenantInstructions = new Label
        {
            Text = "Azure CLI reported multiple tenants. Choose one and click 'Sign in'.",
            AutoSize = true,
            Dock = DockStyle.Fill
        };
        tenantLayout.Controls.Add(tenantInstructions, 0, 0);

        _tenantRadioPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0, 8, 0, 8)
        };
        tenantLayout.Controls.Add(_tenantRadioPanel, 0, 1);

        _tenantGroupBox.Controls.Add(tenantLayout);
        layout.Controls.Add(_tenantGroupBox, 0, 5);

        var buttonPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            AutoSize = true,
            Margin = new Padding(0, 10, 0, 0)
        };

        _closeButton = new Button { Text = "Close", AutoSize = true, Enabled = false };
        _closeButton.Click += (_, _) => Close();
        buttonPanel.Controls.Add(_closeButton);

        _cancelButton = new Button { Text = "Cancel Login", AutoSize = true };
        _cancelButton.Click += (_, _) => CancelLogin();
        buttonPanel.Controls.Add(_cancelButton);

        _loginLinkLabel = new LinkLabel
        {
            Text = DeviceLoginUri.ToString(),
            AutoSize = true,
            LinkBehavior = LinkBehavior.HoverUnderline,
            Enabled = false
        };
        _loginLinkLabel.LinkClicked += (_, _) => OpenLoginPage();
        buttonPanel.Controls.Add(_loginLinkLabel);

        _tenantLoginButton = new Button
        {
            Text = "Sign in to Selected Tenant",
            AutoSize = true,
            Enabled = false
        };
        _tenantLoginButton.Click += async (_, _) => await TenantLoginButtonClickedAsync();
        buttonPanel.Controls.Add(_tenantLoginButton);

        layout.Controls.Add(buttonPanel, 0, 6);
        Controls.Add(layout);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _ = RunLoginAsync();
    }

    private async Task RunLoginAsync()
    {
        try
        {
            var result = await AzureCliProcessRunner.RunAsync(
                _azExecutablePath,
                "login --use-device-code --output json",
                captureOutput: true,
                onLine: HandleCliOutput,
                cancellationToken: _linkedCts.Token).ConfigureAwait(true);

            if (result.ExitCode == 0)
            {
                _loginCompleted = true;
                UpdateStatus("Signed in successfully. You can close this window.");
                _progressBar.Style = ProgressBarStyle.Blocks;
                _progressBar.MarqueeAnimationSpeed = 0;
                _progressBar.Value = _progressBar.Maximum;
                DialogResult = DialogResult.OK;
                Close();
            }
            else
            {
                AppendLog(result.StandardError ?? "Azure CLI login failed.", isError: true);
                UpdateStatus("Azure CLI login failed. Review the log output.");
                _closeButton.Enabled = true;

                if (_tenantOptions.Count > 0 || _noSubscriptionsDetected)
                {
                    ShowTenantSelection();
                }
            }
        }
        catch (OperationCanceledException)
        {
            UpdateStatus("Login cancelled.");
            DialogResult = DialogResult.Cancel;
            Close();
        }
        catch (Exception ex)
        {
            AppendLog($"Error: {ex.Message}", isError: true);
            UpdateStatus("Azure CLI login failed.");
            _closeButton.Enabled = true;
        }
        finally
        {
            _cancelButton.Enabled = false;
            _progressBar.MarqueeAnimationSpeed = 0;
        }
    }

    private void HandleCliOutput(string line, bool isError)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<string, bool>(HandleCliOutput), line, isError);
            return;
        }

        AppendLog(line, isError);

        foreach (Match match in DeviceCodeRegex.Matches(line))
        {
            if (!match.Success)
            {
                continue;
            }

            var candidate = match.Value.ToUpperInvariant();
            if (!LooksLikeDeviceCode(candidate))
            {
                continue;
            }

            SetDeviceCode(candidate);
            break;
        }

        if (line.Contains(DeviceLoginUri.Host, StringComparison.OrdinalIgnoreCase))
        {
            _openBrowserButton.Enabled = true;
            _loginLinkLabel.Enabled = true;
        }

        if (line.IndexOf("no subscriptions found", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            _noSubscriptionsDetected = true;
        }

        TryAddTenantFromLine(line);
    }

    private void SetDeviceCode(string code)
    {
        _deviceCode = code;
        _codeTextBox.Text = code;
        _copyCodeButton.Enabled = true;
        _openBrowserButton.Enabled = true;
        _loginLinkLabel.Enabled = true;
        UpdateStatus("Enter the device code in your browser to continue.");
    }

    private void CopyCode()
    {
        if (string.IsNullOrWhiteSpace(_deviceCode))
        {
            return;
        }

        try
        {
            Clipboard.SetText(_deviceCode);
        }
        catch
        {
            // ignore clipboard failures
        }
    }

    private void OpenLoginPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(DeviceLoginUri.ToString()) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendLog($"Unable to open browser: {ex.Message}", isError: true);
        }
    }

    private void AppendLog(string message, bool isError)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        if (_logTextBox.TextLength > 0)
        {
            _logTextBox.AppendText(Environment.NewLine);
        }

        var prefix = isError ? "[stderr]" : "[stdout]";
        _logTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {prefix} {message}");
        _logTextBox.SelectionStart = _logTextBox.TextLength;
        _logTextBox.ScrollToCaret();
    }

    private void UpdateStatus(string status)
    {
        _statusLabel.Text = status;
    }

    private void CancelLogin()
    {
        _cancelButton.Enabled = false;
        UpdateStatus("Cancelling Azure CLI login...");
        _dialogCts.Cancel();
    }

    private static bool LooksLikeDeviceCode(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        if (candidate.Contains('-'))
        {
            return true;
        }

        return candidate.Any(char.IsDigit);
    }

    private void TryAddTenantFromLine(string line)
    {
        foreach (Match match in TenantLineRegex.Matches(line))
        {
            if (!match.Success)
            {
                continue;
            }

            var id = match.Groups["id"].Value;
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            if (_tenantOptions.Any(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var nameGroup = match.Groups["name"];
            var name = nameGroup.Success ? nameGroup.Value.Trim() : null;
            var option = new TenantOption(id, name);
            _tenantOptions.Add(option);

            if (_tenantGroupBox.Visible)
            {
                AddTenantRadio(option);
            }
        }
    }

    private void AddTenantRadio(TenantOption option)
    {
        var radio = new RadioButton
        {
            AutoSize = true,
            Text = option.DisplayText,
            Tag = option
        };
        radio.CheckedChanged += TenantRadioCheckedChanged;
        option.Attach(radio);
        _tenantRadioPanel.Controls.Add(radio);

        if (SelectedTenant is null)
        {
            radio.Checked = true;
        }
        else
        {
            _tenantLoginButton.Enabled = true;
        }
    }

    private void TenantRadioCheckedChanged(object? sender, EventArgs e)
    {
        if (!_tenantGroupBox.Visible)
        {
            return;
        }

        _tenantLoginButton.Enabled = SelectedTenant is not null;
    }

    private void ShowTenantSelection()
    {
        if (_tenantOptions.Count == 0)
        {
            return;
        }

        _tenantRadioPanel.SuspendLayout();
        if (!_tenantGroupBox.Visible)
        {
            _tenantRadioPanel.Controls.Clear();
            foreach (var option in _tenantOptions)
            {
                AddTenantRadio(option);
            }
            _tenantGroupBox.Visible = true;
        }
        _tenantRadioPanel.ResumeLayout();

        _tenantLoginButton.Enabled = SelectedTenant is not null;
        UpdateStatus("Select a tenant and click 'Sign in to Selected Tenant'.");
        _closeButton.Enabled = true;
    }

    private TenantOption? SelectedTenant => _tenantOptions.FirstOrDefault(t => t.RadioButton?.Checked == true);

    private async Task TenantLoginButtonClickedAsync()
    {
        var tenant = SelectedTenant;
        if (tenant is null)
        {
            return;
        }

        await RunTenantLoginAsync(tenant);
    }

    private async Task RunTenantLoginAsync(TenantOption tenant)
    {
        ToggleTenantControls(false);
        _progressBar.Style = ProgressBarStyle.Marquee;
        _progressBar.MarqueeAnimationSpeed = 30;
        UpdateStatus($"Signing into tenant {tenant.DisplayText}...");

        try
        {
            var result = await AzureCliProcessRunner.RunAsync(
                _azExecutablePath,
                $"login --tenant {tenant.Id} --use-device-code --output json",
                captureOutput: true,
                onLine: HandleCliOutput,
                cancellationToken: _linkedCts.Token).ConfigureAwait(true);

            if (result.ExitCode == 0)
            {
                _loginCompleted = true;
                UpdateStatus("Signed in successfully. You can close this window.");
                DialogResult = DialogResult.OK;
                Close();
                return;
            }

            AppendLog(result.StandardError ?? $"Tenant login failed for {tenant.Id}.", isError: true);
            UpdateStatus("Tenant login failed. Select another tenant or retry.");
        }
        catch (OperationCanceledException)
        {
            UpdateStatus("Tenant login cancelled.");
        }
        catch (Exception ex)
        {
            AppendLog($"Tenant login error: {ex.Message}", isError: true);
            UpdateStatus("Tenant login failed.");
        }
        finally
        {
            if (!_loginCompleted)
            {
                ToggleTenantControls(true);
                _progressBar.MarqueeAnimationSpeed = 0;
            }
        }
    }

    private void ToggleTenantControls(bool enabled)
    {
        foreach (var option in _tenantOptions.Where(o => o.RadioButton is not null))
        {
            option.RadioButton!.Enabled = enabled;
        }

        _tenantLoginButton.Enabled = enabled && SelectedTenant is not null;
        _cancelButton.Enabled = enabled;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_loginCompleted && !_dialogCts.IsCancellationRequested)
        {
            _dialogCts.Cancel();
        }

        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (!_dialogCts.IsCancellationRequested)
            {
                _dialogCts.Cancel();
            }

            _dialogCts.Dispose();
            _linkedCts.Dispose();
        }

        base.Dispose(disposing);
    }

    private sealed class TenantOption
    {
        public TenantOption(string id, string? name)
        {
            Id = id;
            Name = string.IsNullOrWhiteSpace(name) ? null : name;
        }

        public string Id { get; }
        public string? Name { get; }
        public RadioButton? RadioButton { get; private set; }
        public string DisplayText => string.IsNullOrWhiteSpace(Name) ? Id : $"{Name} ({Id})";

        public void Attach(RadioButton radio)
        {
            RadioButton = radio;
        }
    }
}
