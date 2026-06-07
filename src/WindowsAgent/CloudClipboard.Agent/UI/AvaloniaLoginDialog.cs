using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CloudClipboard.Agent.Services;

namespace CloudClipboard.Agent.UI;

/// <summary>
/// Cross-platform Azure CLI device login dialog (Avalonia, code-only).
/// Detects multi-tenant failures and offers tenant selection for retry.
/// </summary>
public sealed class AvaloniaLoginDialog
{
    private static TextBlock? _statusLabel;
    private static TextBox? _codeTextBox;
    private static Button? _copyCodeButton;
    private static Button? _openBrowserButton;
    private static TextBox? _logTextBox;
    private static ProgressBar? _progressBar;
    private static Button? _cancelButton;
    private static Button? _closeButton;
    private static Button? _retryButton;
    private static ComboBox? _tenantCombo;
    private static StackPanel? _tenantPanel;

    private static readonly Uri DeviceLoginUri = new("https://microsoft.com/devicelogin");
    private static readonly Regex DeviceCodeRegex = new(
        "([A-Z0-9]{4}-[A-Z0-9]{4})|([A-Z0-9]{8,12})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TenantLineRegex = new(
        @"^([0-9a-fA-F\-]{36})\s+'([^']+)'",
        RegexOptions.Compiled);

    private static bool _multiTenant;

    public static Task<bool> ShowAsync(string azExecutablePath, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        Dispatcher.UIThread.InvokeAsync(() =>
        {
            var content = CreateContent(azExecutablePath, tcs, linkedCts);
            App.ShowContent(content, 640, 560);
        });

        return tcs.Task;
    }

    private static Control CreateContent(string azExecutablePath, TaskCompletionSource<bool> tcs, CancellationTokenSource linkedCts)
    {
        var root = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 10,
        };

        root.Children.Add(new TextBlock
        {
            Text = $"Sign in to Azure by entering the device code shown below at {DeviceLoginUri}.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 5),
        });

        _statusLabel = new TextBlock
        {
            Text = "Starting Azure CLI sign-in...",
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 0, 0, 5),
        };
        root.Children.Add(_statusLabel);

        // Device code panel
        var codePanel = new StackPanel { Spacing = 6 };
        codePanel.Children.Add(new TextBlock { Text = "Device code:" });

        _codeTextBox = new TextBox
        {
            Text = "",
            IsReadOnly = true,
            FontSize = 22,
            FontWeight = FontWeight.Bold,
            Width = 300,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };
        codePanel.Children.Add(_codeTextBox);

        var codeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        _copyCodeButton = new Button { Content = "Copy Code", Width = 100, IsEnabled = false };
        _copyCodeButton.Click += (_, _) => CopyCode();
        codeRow.Children.Add(_copyCodeButton);

        _openBrowserButton = new Button { Content = "Open Login Page", Width = 120, IsEnabled = false };
        _openBrowserButton.Click += (_, _) => OpenLoginPage();
        codeRow.Children.Add(_openBrowserButton);

        codePanel.Children.Add(codeRow);
        root.Children.Add(codePanel);

        // Tenant selector (hidden by default)
        _tenantPanel = new StackPanel { Spacing = 6, IsVisible = false };
        _tenantPanel.Children.Add(new TextBlock { Text = "Select a tenant:" });
        _tenantCombo = new ComboBox { Width = 400 };
        _tenantPanel.Children.Add(_tenantCombo);

        _retryButton = new Button { Content = "Retry with Tenant", Width = 140 };
        _retryButton.IsVisible = false;
        _retryButton.Click += (_, _) =>
        {
            if (_tenantCombo?.SelectedItem is TenantItem ti)
                _ = RunLoginAsync(azExecutablePath, tcs, linkedCts, ti.Id);
        };
        _tenantPanel.Children.Add(_retryButton);
        root.Children.Add(_tenantPanel);

        // Progress bar
        _progressBar = new ProgressBar { Width = double.NaN, Value = 0, Minimum = 0, Maximum = 100, IsIndeterminate = true };
        root.Children.Add(_progressBar);

        // Log output
        _logTextBox = new TextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = "monospace",
            Height = 160,
            Margin = new Thickness(0, 5, 0, 5),
            Text = "",
        };
        root.Children.Add(_logTextBox);

        // Button panel
        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
        };

        _cancelButton = new Button { Content = "Cancel Login", Width = 120 };
        _cancelButton.Click += (_, _) => CancelLogin(tcs);
        buttonPanel.Children.Add(_cancelButton);

        _closeButton = new Button { Content = "Close", Width = 80, IsEnabled = false };
        _closeButton.Click += (_, _) => App.HideContent();
        buttonPanel.Children.Add(_closeButton);

        root.Children.Add(buttonPanel);

        _ = RunLoginAsync(azExecutablePath, tcs, linkedCts);

        return root;
    }

    private static async Task RunLoginAsync(string azExecutablePath, TaskCompletionSource<bool> tcs,
        CancellationTokenSource linkedCts, string? tenantId = null)
    {
        try
        {
            ResetForRetry();

            var args = tenantId is null
                ? "login --use-device-code --output json"
                : $"login --use-device-code --output json --tenant {tenantId}";

            var result = await AzureCliProcessRunner.RunAsync(
                azExecutablePath,
                args,
                captureOutput: true,
                onLine: (line, isError) => Dispatcher.UIThread.InvokeAsync(() => HandleCliOutput(line, isError)),
                cancellationToken: linkedCts.Token).ConfigureAwait(true);

            if (result.ExitCode == 0)
            {
                UpdateStatus("Signed in successfully. You can close this window.");
                _progressBar!.IsIndeterminate = false;
                _progressBar.Value = _progressBar.Maximum;
                _closeButton!.IsEnabled = true;
                tcs.TrySetResult(true);
                App.HideContent();
            }
            else if (_multiTenant)
            {
                // Tenant-specific login also failed; show log
                AppendLog(result.StandardError ?? "Azure CLI login failed.", isError: true);
                UpdateStatus("Login failed with selected tenant. Review the log output.");
                _closeButton!.IsEnabled = true;
            }
            else
            {
                AppendLog(result.StandardError ?? "Azure CLI login failed.", isError: true);
                UpdateStatus("Azure CLI login failed. Review the log output.");
                _closeButton!.IsEnabled = true;
            }
        }
        catch (OperationCanceledException)
        {
            UpdateStatus("Login cancelled.");
            tcs.TrySetResult(false);
        }
        catch (Exception ex)
        {
            AppendLog($"Error: {ex.Message}", isError: true);
            UpdateStatus("Azure CLI login failed.");
            _closeButton!.IsEnabled = true;
        }
    }

    private static readonly List<TenantItem> _tenants = new();

    private static void HandleCliOutput(string line, bool isError)
    {
        AppendLog(line, isError);

        // Detect multi-tenant hint
        if (line.Contains("az login --tenant", StringComparison.OrdinalIgnoreCase))
            _multiTenant = true;

        // Extract device code
        foreach (Match match in DeviceCodeRegex.Matches(line))
        {
            if (!match.Success) continue;
            var candidate = match.Value.ToUpperInvariant();
            if (!LooksLikeDeviceCode(candidate)) continue;
            SetDeviceCode(candidate);
            break;
        }

        // Extract tenant ID + name lines
        var tenantMatch = TenantLineRegex.Match(line);
        if (tenantMatch.Success)
        {
            var id = tenantMatch.Groups[1].Value;
            var name = tenantMatch.Groups[2].Value;
            if (!_tenants.Any(t => t.Id == id))
                _tenants.Add(new TenantItem(id, name));
        }

        if (line.Contains(DeviceLoginUri.Host, StringComparison.OrdinalIgnoreCase))
            _openBrowserButton!.IsEnabled = true;

        // When login completely fails and we have multiple tenants, show selector
        if (_multiTenant && _tenants.Count > 0 && line.Contains("No subscriptions found"))
        {
            ShowTenantSelector();
        }
    }

    private static bool _selectorShown;

    private static void ShowTenantSelector()
    {
        if (_selectorShown) return;
        _selectorShown = true;

        UpdateStatus("Multiple tenants detected. Select one below and retry.");
        if (_tenantCombo is not null)
        {
            _tenantCombo.ItemsSource = _tenants;
            if (_tenants.Count > 0) _tenantCombo.SelectedIndex = 0;
        }
        if (_tenantPanel is not null) _tenantPanel.IsVisible = true;
        if (_retryButton is not null) _retryButton.IsVisible = true;
        _progressBar!.IsIndeterminate = false;
        _progressBar.Value = 0;
    }

    private static void ResetForRetry()
    {
        _multiTenant = false;
        _tenants.Clear();
        _selectorShown = false;
        _codeTextBox!.Text = "";
        _copyCodeButton!.IsEnabled = false;
        _openBrowserButton!.IsEnabled = false;
        if (_tenantPanel is not null) _tenantPanel.IsVisible = false;
        if (_retryButton is not null) _retryButton.IsVisible = false;
        _progressBar!.IsIndeterminate = true;
        UpdateStatus("Retrying Azure CLI sign-in...");
    }

    private static void SetDeviceCode(string code)
    {
        _codeTextBox!.Text = code;
        _copyCodeButton!.IsEnabled = true;
        _openBrowserButton!.IsEnabled = true;
        UpdateStatus("Enter the device code in your browser to continue.");
    }

    private static void CopyCode()
    {
        if (string.IsNullOrWhiteSpace(_codeTextBox?.Text)) return;
        try
        {
            var topLevel = TopLevel.GetTopLevel(_codeTextBox);
            topLevel?.Clipboard?.SetTextAsync(_codeTextBox.Text).Wait();
        }
        catch { }
    }

    private static void OpenLoginPage()
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

    private static void AppendLog(string message, bool isError)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        if (_logTextBox != null)
        {
            if (_logTextBox.Text?.Length > 0)
                _logTextBox.Text += "\n";
            var prefix = isError ? "[stderr]" : "[stdout]";
            _logTextBox.Text += $"[{DateTime.Now:HH:mm:ss}] {prefix} {message}";
        }
    }

    private static void UpdateStatus(string status)
    {
        if (_statusLabel != null) _statusLabel.Text = status;
    }

    private static void CancelLogin(TaskCompletionSource<bool> tcs)
    {
        UpdateStatus("Cancelling Azure CLI login...");
        tcs.TrySetResult(false);
        App.HideContent();
    }

    private static bool LooksLikeDeviceCode(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        if (candidate.Contains('-')) return true;
        return candidate.Any(char.IsDigit);
    }

    private sealed record TenantItem(string Id, string Name)
    {
        public override string ToString() => $"{Name} ({Id})";
    }
}
