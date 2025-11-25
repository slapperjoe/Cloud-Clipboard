using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CloudClipboard.Agent.Windows.Configuration;
using CloudClipboard.Agent.Windows.Options;
using CloudClipboard.Agent.Windows.Services;

namespace CloudClipboard.Agent.Windows.UI;

public sealed class DeployFunctionsForm : Form
{
    private readonly IAgentSettingsStore _settingsStore;
    private readonly IFunctionsDeploymentService _deploymentService;
    private AgentOptions _options;

    private readonly TabControl _tabControl;
    private readonly TabPage _activityTab;
    private readonly TextBox _functionAppText;
    private readonly TextBox _resourceGroupText;
    private readonly TextBox _subscriptionText;
    private readonly TextBox _packagePathText;
    private readonly TextBox _functionKeyText;
    private readonly CheckBox _updateFunctionKeyCheckBox;
    private readonly Button _deployButton;
    private readonly Button _browseButton;
    private readonly Button _loginButton;
    private readonly TextBox _logText;
    private readonly CancellationTokenSource _cts = new();
    private bool _isDeploying;

    public DeployFunctionsForm(IAgentSettingsStore settingsStore, IFunctionsDeploymentService deploymentService)
    {
        _settingsStore = settingsStore;
        _deploymentService = deploymentService;
        _options = CloneOptions(settingsStore.Load());

        Text = "Deploy Azure Functions";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1100, 760);
        MinimumSize = new Size(1000, 720);
        AutoScaleMode = AutoScaleMode.Dpi;

        _tabControl = new TabControl
        {
            Dock = DockStyle.Fill
        };

        var settingsTab = new TabPage("Settings")
        {
            Padding = new Padding(0)
        };

        var fields = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 3,
            AutoSize = true,
            Padding = new Padding(16)
        };
        fields.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));

        var introLabel = new Label
        {
            Text = "Push the packaged Azure Functions zip using the Azure CLI (az). Make sure you have permissions to deploy to the selected Function App.",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 12)
        };
        fields.Controls.Add(introLabel, 0, fields.RowCount);
        fields.SetColumnSpan(introLabel, 3);
        fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fields.RowCount++;

        _functionAppText = CreateTextBox();
        AddRow(fields, "Function App", _functionAppText);

        _resourceGroupText = CreateTextBox();
        AddRow(fields, "Resource Group", _resourceGroupText);

        _subscriptionText = CreateTextBox();
        AddRow(fields, "Subscription Id", _subscriptionText);

        _packagePathText = CreateTextBox();
        _browseButton = new Button { Text = "Browse", AutoSize = true };
        _browseButton.Click += (_, _) => BrowseForPackage();
        AddRow(fields, "Package Path", _packagePathText, _browseButton);

        var packageHint = new Label
        {
            Text = "Defaults to the CloudClipboard.Functions.zip generated during dotnet build.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 6)
        };
        fields.Controls.Add(packageHint, 1, fields.RowCount);
        fields.SetColumnSpan(packageHint, 2);
        fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fields.RowCount++;

        _functionKeyText = CreateTextBox();
        _functionKeyText.UseSystemPasswordChar = true;
        AddRow(fields, "Function Key", _functionKeyText);

        _updateFunctionKeyCheckBox = new CheckBox
        {
            Text = "Update agent settings with this key",
            AutoSize = true,
            Checked = true,
            Margin = new Padding(0, 4, 0, 4)
        };
        fields.Controls.Add(_updateFunctionKeyCheckBox, 1, fields.RowCount);
        fields.SetColumnSpan(_updateFunctionKeyCheckBox, 2);
        fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fields.RowCount++;

        var buttonPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.None,
            AutoSize = true,
            Padding = new Padding(10, 5, 10, 0)
        };
        buttonPanel.Anchor = AnchorStyles.Right;

        _deployButton = new Button { Text = "Deploy", AutoSize = true };
        _deployButton.Click += async (_, _) => await DeployAsync();
        buttonPanel.Controls.Add(_deployButton);

        var cancelButton = new Button { Text = "Close", AutoSize = true };
        cancelButton.Click += (_, _) => Close();
        buttonPanel.Controls.Add(cancelButton);

        _loginButton = new Button { Text = "Sign In", AutoSize = true };
        _loginButton.Click += (_, _) => LaunchLoginAsync();
        buttonPanel.Controls.Add(_loginButton);

        fields.Controls.Add(buttonPanel, 0, fields.RowCount);
        fields.SetColumnSpan(buttonPanel, 3);
        fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fields.RowCount++;

        _logText = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            Dock = DockStyle.Fill,
            ScrollBars = ScrollBars.Both,
            Font = new Font("Consolas", 9F),
            BackColor = Color.Black,
            ForeColor = Color.LimeGreen,
            BorderStyle = BorderStyle.FixedSingle
        };

        var logGroup = new GroupBox
        {
            Text = "Activity Log",
            Dock = DockStyle.Fill,
            Padding = new Padding(10)
        };
        logGroup.Controls.Add(_logText);

        var fieldsHost = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true
        };
        fieldsHost.Controls.Add(fields);
        fieldsHost.Resize += (_, _) => fields.Width = Math.Max(fieldsHost.ClientSize.Width - fields.Margin.Horizontal, fields.PreferredSize.Width);

        settingsTab.Controls.Add(fieldsHost);
        _tabControl.TabPages.Add(settingsTab);

        _activityTab = new TabPage("Activity")
        {
            Padding = new Padding(10)
        };
        _activityTab.Controls.Add(logGroup);
        _tabControl.TabPages.Add(_activityTab);

        Controls.Add(_tabControl);

        ApplyOptions();
    }

    private static AgentOptions CloneOptions(AgentOptions source)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(source);
        return System.Text.Json.JsonSerializer.Deserialize<AgentOptions>(json)!;
    }

    private static TextBox CreateTextBox()
        => new()
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 4, 8, 4)
        };

    private void AddRow(TableLayoutPanel table, string labelText, Control control, Control? trailing = null)
    {
        var label = new Label
        {
            Text = labelText,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 4, 8, 4)
        };
        var row = table.RowCount;
        table.Controls.Add(label, 0, row);
        table.Controls.Add(control, 1, row);
        if (trailing is not null)
        {
            table.Controls.Add(trailing, 2, row);
        }
        else
        {
            table.Controls.Add(new Panel { Dock = DockStyle.Fill }, 2, row);
        }
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowCount++;
    }

    private void ApplyOptions()
    {
        var deploy = _options.FunctionsDeployment ?? FunctionsDeploymentOptions.CreateDefault();
        _functionAppText.Text = deploy.FunctionAppName;
        _resourceGroupText.Text = deploy.ResourceGroup;
        _subscriptionText.Text = deploy.SubscriptionId;
        _packagePathText.Text = deploy.PackagePath;
        _functionKeyText.Text = _options.FunctionKey ?? string.Empty;
    }

    private void BrowseForPackage()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Zip files (*.zip)|*.zip|All files (*.*)|*.*",
            Title = "Select Functions Package",
            FileName = _packagePathText.Text
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _packagePathText.Text = dialog.FileName;
        }
    }

    private async Task DeployAsync()
    {
        if (_isDeploying)
        {
            return;
        }

        ShowActivityTab();

        if (!TryResolveAz(out _))
        {
            return;
        }

        _isDeploying = true;
        ToggleInputs(false);
        _logText.Clear();
        var progress = new Progress<string>(line => AppendLog(line));

        var request = new FunctionsDeploymentRequest(
            _functionAppText.Text.Trim(),
            _resourceGroupText.Text.Trim(),
            _subscriptionText.Text.Trim(),
            ResolvePackagePath());

        var result = await Task.Run(() => _deploymentService.DeployAsync(request, progress, _cts.Token));

        if (result.Succeeded)
        {
            AppendLog("Deployment succeeded.");
            var packageHash = FunctionsDeploymentUtilities.TryComputePackageHash(request.PackagePath);
            SaveDeploymentSettings(packageHash);
        }
        else
        {
            AppendLog($"Deployment failed: {result.ErrorMessage}");
        }

        _isDeploying = false;
        ToggleInputs(true);
    }

    private string ResolvePackagePath()
    {
        var path = _packagePathText.Text.Trim();
        return FunctionsDeploymentUtilities.ResolvePackagePath(path);
    }

    private void AppendLog(string line)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => AppendLog(line));
            return;
        }

        _logText.AppendText(line + Environment.NewLine);
    }

    private void ToggleInputs(bool enabled)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => ToggleInputs(enabled));
            return;
        }

        _functionAppText.Enabled = enabled;
        _resourceGroupText.Enabled = enabled;
        _subscriptionText.Enabled = enabled;
        _packagePathText.Enabled = enabled;
        _browseButton.Enabled = enabled;
        _functionKeyText.Enabled = enabled;
        _updateFunctionKeyCheckBox.Enabled = enabled;
        _deployButton.Enabled = enabled;
        _loginButton.Enabled = enabled;
    }

    private void LaunchLoginAsync()
    {
        if (!TryResolveAz(out var azPath))
        {
            return;
        }

        ShowActivityTab();
        AppendLog("Launching Azure CLI device login...");
        _ = Task.Run(async () =>
        {
            var progress = new Progress<string>(AppendLog);
            await EnsureLoginAsync(azPath, progress, _cts.Token).ConfigureAwait(false);
        });
    }

    private void SaveDeploymentSettings(string? packageHash)
    {
        _options.FunctionsDeployment ??= FunctionsDeploymentOptions.CreateDefault();
        _options.FunctionsDeployment.FunctionAppName = _functionAppText.Text.Trim();
        _options.FunctionsDeployment.ResourceGroup = _resourceGroupText.Text.Trim();
        _options.FunctionsDeployment.SubscriptionId = _subscriptionText.Text.Trim();
        _options.FunctionsDeployment.PackagePath = _packagePathText.Text.Trim();

        _options.FunctionsDeployment.LastDeployedUtc = DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(packageHash))
        {
            _options.FunctionsDeployment.LastPackageHash = packageHash;
        }

        if (_updateFunctionKeyCheckBox.Checked)
        {
            _options.FunctionKey = string.IsNullOrWhiteSpace(_functionKeyText.Text) ? null : _functionKeyText.Text.Trim();
        }

        _settingsStore.Save(_options);
    }

    private void ShowActivityTab()
    {
        if (_tabControl.SelectedTab != _activityTab)
        {
            _tabControl.SelectedTab = _activityTab;
        }
    }

    private bool TryResolveAz(out string azPath)
    {
        if (AzureCliLocator.TryResolveExecutable(out azPath, out var error))
        {
            return true;
        }

        var message = error ?? "Azure CLI (az) could not be located. Install it and ensure it is on PATH.";
        AppendLog(message);
        MessageBox.Show(this, message, "Azure CLI Required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        _cts.Cancel();
        _cts.Dispose();
    }

    private async Task EnsureLoginAsync(string azPath, IProgress<string> progress, CancellationToken token)
    {
        try
        {
            const string checkArgs = "account show";
            progress.Report("Checking Azure CLI authentication...");
            progress.Report($"> az {checkArgs}");
            var checkCode = await RunAzAsync(azPath, checkArgs, progress, token).ConfigureAwait(false);
            if (checkCode == 0)
            {
                progress.Report("Already signed in.");
                return;
            }
        }
        catch
        {
            // ignore and attempt login
        }

        const string loginArgs = "login --use-device-code";
        progress.Report("Starting az login device flow...");
        progress.Report($"> az {loginArgs}");
        await RunAzAsync(azPath, loginArgs, progress, token).ConfigureAwait(false);
    }

    private static Task<int> RunAzAsync(string azPath, string arguments, IProgress<string>? progress, CancellationToken token)
        => RunProcessAsync(azPath, arguments, progress, token);

    private static async Task<int> RunProcessAsync(string fileName, string arguments, IProgress<string>? progress, CancellationToken token)
    {
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                progress?.Report(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                progress?.Report(e.Data);
            }
        };
        process.Exited += (_, _) =>
        {
            tcs.TrySetResult(process.ExitCode);
            process.Dispose();
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await using var _ = token.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
            }
            catch
            {
            }
        });

        return await tcs.Task.ConfigureAwait(false);
    }
}
