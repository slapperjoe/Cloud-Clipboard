using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CloudClipboard.Agent.Windows.Options;
using CloudClipboard.Agent.Windows.Services;

namespace CloudClipboard.Agent.Windows.UI;

public sealed class ProvisionBackendDialog : Form
{
    private readonly TextBox _ownerIdText;
    private readonly ComboBox _subscriptionCombo;
    private readonly ComboBox _locationCombo;
    private readonly TextBox _resourceGroupText;
    private readonly TextBox _functionAppText;
    private readonly TextBox _storageAccountText;
    private readonly TextBox _planText;
    private readonly TextBox _containerText;
    private readonly TextBox _tableText;
    private readonly TextBox _packagePathText;
    private readonly Button _browseButton;
    private readonly Button _provisionButton;
    private readonly Label _errorLabel;
    private readonly IAzureCliMetadataProvider _metadataProvider;
    private readonly CancellationToken _cancellationToken;
    private readonly System.Windows.Forms.Timer _locationRefreshTimer;
    private CancellationTokenSource? _locationRefreshCts;
    private readonly IAppIconProvider _iconProvider;

    private ProvisionBackendDialog(
        string ownerId,
        string? subscriptionId,
        FunctionsDeploymentOptions defaults,
        IReadOnlyList<AzureSubscriptionInfo> subscriptions,
        IReadOnlyList<AzureLocationInfo> locations,
        IAzureCliMetadataProvider metadataProvider,
        IAppIconProvider iconProvider,
        CancellationToken cancellationToken)
    {
        _metadataProvider = metadataProvider ?? throw new ArgumentNullException(nameof(metadataProvider));
        _iconProvider = iconProvider ?? throw new ArgumentNullException(nameof(iconProvider));
        _cancellationToken = cancellationToken;
        _locationRefreshTimer = new System.Windows.Forms.Timer { Interval = 600 };
        _locationRefreshTimer.Tick += (_, _) =>
        {
            _locationRefreshTimer.Stop();
            _ = RefreshLocationsForSubscriptionAsync();
        };

        Text = "Provision Cloud Clipboard Backend";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Width = 640;
        Height = 720;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowIcon = true;
        Icon = _iconProvider.GetIcon(32);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 0,
            Padding = new Padding(16),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var intro = new Label
        {
            Text = "Sign in via Azure CLI, then choose where to create the Functions app and storage.\n\nNote: Creates Consumption plan resources (lowest cost, pay-per-use).",
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            Margin = new Padding(0, 0, 0, 12)
        };
        layout.Controls.Add(intro, 0, layout.RowCount);
        layout.SetColumnSpan(intro, 3);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowCount++;

        _ownerIdText = CreateTextBox(ownerId, readOnly: true);
        AddRow(layout, "Owner Id", _ownerIdText);

        _subscriptionCombo = CreateComboBox();
        BindSubscriptions(_subscriptionCombo, subscriptions, subscriptionId ?? defaults.SubscriptionId);
        AddRow(layout, "Subscription", _subscriptionCombo);
        HookValidation(_subscriptionCombo);

        _locationCombo = CreateComboBox();
        BindLocations(_locationCombo, locations, string.IsNullOrWhiteSpace(defaults.Location) ? "eastus" : defaults.Location);
        AddRow(layout, "Location", _locationCombo);
        HookValidation(_locationCombo);

        _resourceGroupText = CreateTextBox(string.IsNullOrWhiteSpace(defaults.ResourceGroup)
            ? ProvisioningNameGenerator.CreateResourceGroupName(ownerId)
            : defaults.ResourceGroup);
        AddRow(layout, "Resource Group", _resourceGroupText);
        HookValidation(_resourceGroupText);

        _functionAppText = CreateTextBox(string.IsNullOrWhiteSpace(defaults.FunctionAppName)
            ? ProvisioningNameGenerator.CreateFunctionAppName(ownerId)
            : defaults.FunctionAppName);
        AddRow(layout, "Function App Name", _functionAppText);
        HookValidation(_functionAppText);

        _storageAccountText = CreateTextBox(string.IsNullOrWhiteSpace(defaults.StorageAccountName)
            ? ProvisioningNameGenerator.CreateStorageAccountName(ownerId)
            : defaults.StorageAccountName);
        AddRow(layout, "Storage Account", _storageAccountText);
        HookValidation(_storageAccountText);

        _planText = CreateTextBox(string.IsNullOrWhiteSpace(defaults.PlanName)
            ? ProvisioningNameGenerator.CreatePlanName(ownerId)
            : defaults.PlanName);
        AddRow(layout, "Hosting Plan", _planText);
        HookValidation(_planText);

        _containerText = CreateTextBox(string.IsNullOrWhiteSpace(defaults.PayloadContainer)
            ? "clipboardpayloads"
            : defaults.PayloadContainer);
        AddRow(layout, "Blob Container", _containerText);
        HookValidation(_containerText);

        _tableText = CreateTextBox(string.IsNullOrWhiteSpace(defaults.MetadataTable)
            ? "ClipboardMetadata"
            : defaults.MetadataTable);
        AddRow(layout, "Table Name", _tableText);
        HookValidation(_tableText);

        _packagePathText = CreateTextBox(string.IsNullOrWhiteSpace(defaults.PackagePath)
            ? FunctionsDeploymentOptions.CreateDefault().PackagePath
            : defaults.PackagePath);
        _browseButton = new Button { Text = "Browse", AutoSize = true };
        _browseButton.Click += (_, _) => BrowseForPackage();
        AddRow(layout, "Package Path", _packagePathText, _browseButton);
        HookValidation(_packagePathText);

        _errorLabel = new Label
        {
            ForeColor = Color.Firebrick,
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 8)
        };
        layout.Controls.Add(_errorLabel, 0, layout.RowCount);
        layout.SetColumnSpan(_errorLabel, 3);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowCount++;

        var buttonsPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(0, 8, 0, 0)
        };
        _provisionButton = new Button { Text = "Provision", AutoSize = true, MinimumSize = new Size(90, 28), Padding = new Padding(8, 4, 8, 4) };
        _provisionButton.Click += (_, _) => OnProvision();
        var cancelButton = new Button { Text = "Cancel", AutoSize = true, MinimumSize = new Size(90, 28), Padding = new Padding(8, 4, 8, 4) };
        cancelButton.Click += (_, _) => CloseWithResult(DialogResult.Cancel);
        buttonsPanel.Controls.Add(_provisionButton);
        buttonsPanel.Controls.Add(cancelButton);

        layout.Controls.Add(buttonsPanel, 0, layout.RowCount);
        layout.SetColumnSpan(buttonsPanel, 3);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowCount++;

        Controls.Add(layout);
        Shown += (_, _) => ValidateInputs();

        _subscriptionCombo.SelectedIndexChanged += (_, _) => ScheduleLocationRefresh();
        _subscriptionCombo.TextChanged += (_, _) => ScheduleLocationRefresh();
    }

    public static Task<FunctionsDeploymentOptions?> ShowAsync(
        string ownerId,
        string? subscriptionId,
        FunctionsDeploymentOptions defaults,
        IReadOnlyList<AzureSubscriptionInfo> subscriptions,
        IReadOnlyList<AzureLocationInfo> locations,
        IAzureCliMetadataProvider metadataProvider,
        IAppIconProvider iconProvider,
        CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<FunctionsDeploymentOptions?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                using var dialog = new ProvisionBackendDialog(ownerId, subscriptionId, defaults, subscriptions, locations, metadataProvider, iconProvider, cancellationToken);
                using var registration = cancellationToken.Register(() =>
                {
                    if (dialog.IsHandleCreated)
                    {
                        dialog.BeginInvoke(new Action(() => dialog.CloseWithResult(DialogResult.Cancel)));
                    }
                });
                var result = dialog.ShowDialog();
                if (result == DialogResult.OK)
                {
                    tcs.TrySetResult(dialog.BuildOptions());
                }
                else if (cancellationToken.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(cancellationToken);
                }
                else
                {
                    tcs.TrySetResult(null);
                }
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "CloudClipboard.ProvisionDialog"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return tcs.Task;
    }

    private void AddRow(TableLayoutPanel layout, string label, Control control, Control? trailing = null)
    {
        var textLabel = new Label
        {
            Text = label,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 6, 8, 6)
        };
        var rowIndex = layout.RowCount;
        layout.Controls.Add(textLabel, 0, rowIndex);
        layout.Controls.Add(control, 1, rowIndex);
        if (trailing is not null)
        {
            trailing.Margin = new Padding(8, 4, 0, 4);
            trailing.Dock = DockStyle.Fill;
            layout.Controls.Add(trailing, 2, rowIndex);
        }
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowCount++;
    }

    private static TextBox CreateTextBox(string value, bool readOnly = false)
        => new()
        {
            Text = value,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 4, 0, 4),
            ReadOnly = readOnly
        };

    private static ComboBox CreateComboBox()
        => new()
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 4, 0, 4),
            DropDownStyle = ComboBoxStyle.DropDown,
            AutoCompleteMode = AutoCompleteMode.SuggestAppend,
            AutoCompleteSource = AutoCompleteSource.ListItems
        };

    private static void BindSubscriptions(ComboBox combo, IReadOnlyList<AzureSubscriptionInfo> subscriptions, string? preferredId)
    {
        combo.DisplayMember = nameof(AzureSubscriptionInfo.Label);
        combo.ValueMember = nameof(AzureSubscriptionInfo.Id);
        combo.Items.Clear();
        if (subscriptions is { Count: > 0 })
        {
            foreach (var subscription in subscriptions)
            {
                combo.Items.Add(subscription);
            }

            var match = FindSubscription(subscriptions, preferredId);
            if (match is not null)
            {
                combo.SelectedItem = match;
            }
            else if (!string.IsNullOrWhiteSpace(preferredId))
            {
                combo.SelectedItem = null;
                combo.Text = preferredId;
            }
            else
            {
                combo.SelectedItem = subscriptions.FirstOrDefault(s => s.IsDefault) ?? subscriptions[0];
            }
        }
        else if (!string.IsNullOrWhiteSpace(preferredId))
        {
            combo.Text = preferredId;
        }

        static AzureSubscriptionInfo? FindSubscription(IReadOnlyList<AzureSubscriptionInfo> items, string? id)
            => string.IsNullOrWhiteSpace(id)
                ? null
                : items.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    private static void BindLocations(ComboBox combo, IReadOnlyList<AzureLocationInfo> locations, string? preferredName)
    {
        combo.DisplayMember = nameof(AzureLocationInfo.Label);
        combo.ValueMember = nameof(AzureLocationInfo.Name);
        combo.Items.Clear();
        if (locations is { Count: > 0 })
        {
            foreach (var location in locations)
            {
                combo.Items.Add(location);
            }

            var match = locations.FirstOrDefault(l => string.Equals(l.Name, preferredName, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                combo.SelectedItem = match;
            }
            else if (!string.IsNullOrWhiteSpace(preferredName))
            {
                combo.SelectedItem = null;
                combo.Text = preferredName;
            }
            else
            {
                combo.SelectedItem = locations[0];
            }
        }
        else
        {
            combo.Text = preferredName ?? string.Empty;
        }
    }

    private void HookValidation(Control control)
    {
        control.TextChanged += (_, _) => ValidateInputs();
        if (control is ComboBox combo)
        {
            combo.SelectedIndexChanged += (_, _) => ValidateInputs();
        }
    }

    private void ScheduleLocationRefresh()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        _locationRefreshTimer.Stop();
        _locationRefreshTimer.Start();
    }

    private async Task RefreshLocationsForSubscriptionAsync()
    {
        if (_cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var subscriptionId = GetSubscriptionText();
        if (string.IsNullOrWhiteSpace(subscriptionId))
        {
            BindLocations(_locationCombo, Array.Empty<AzureLocationInfo>(), GetLocationText());
            return;
        }

        var preferredLocation = GetLocationText();
        _locationRefreshCts?.Cancel();
        _locationRefreshCts?.Dispose();
        _locationRefreshCts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken);
        var token = _locationRefreshCts.Token;

        ToggleLocationLoadingState(true);
        try
        {
            var locations = await _metadataProvider.GetLocationsAsync(subscriptionId, token).ConfigureAwait(true);
            if (token.IsCancellationRequested)
            {
                return;
            }

            BindLocations(_locationCombo, locations, preferredLocation);
            ValidateInputs();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // ignore
        }
        catch
        {
            _errorLabel.Text = "Failed to refresh locations. You can type the region manually.";
        }
        finally
        {
            ToggleLocationLoadingState(false);
        }
    }

    private void ToggleLocationLoadingState(bool isLoading)
    {
        _locationCombo.Enabled = !isLoading;
        UseWaitCursor = isLoading;
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
            ValidateInputs();
        }
    }

    private async void OnProvision()
    {
        if (!ValidateInputs())
        {
            return;
        }

        // Check Azure name availability before proceeding
        _provisionButton.Enabled = false;
        _errorLabel.Text = "Checking name availability...";
        _errorLabel.ForeColor = Color.DarkOrange;

        try
        {
            var storageAccountName = _storageAccountText.Text.Trim().ToLowerInvariant();
            var functionAppName = _functionAppText.Text.Trim();

            var availabilityError = await CheckAzureNameAvailabilityAsync(storageAccountName, functionAppName, _cancellationToken);
            if (!string.IsNullOrEmpty(availabilityError))
            {
                _errorLabel.Text = availabilityError;
                _errorLabel.ForeColor = Color.Firebrick;
                _provisionButton.Enabled = true;
                return;
            }

            _errorLabel.Text = string.Empty;
            CloseWithResult(DialogResult.OK);
        }
        catch (Exception ex)
        {
            _errorLabel.Text = $"Failed to check availability: {ex.Message}";
            _errorLabel.ForeColor = Color.Firebrick;
            _provisionButton.Enabled = true;
        }
    }

    private void CloseWithResult(DialogResult result)
    {
        DialogResult = result;
        Close();
    }

    private bool ValidateInputs()
    {
        var error = string.Empty;
        var subscription = GetSubscriptionText();
        var location = GetLocationText();

        if (string.IsNullOrWhiteSpace(subscription))
        {
            error = "Subscription Id is required.";
        }
        else if (string.IsNullOrWhiteSpace(location))
        {
            error = "Location is required.";
        }
        else if (string.IsNullOrWhiteSpace(_resourceGroupText.Text))
        {
            error = "Resource Group is required.";
        }
        else if (string.IsNullOrWhiteSpace(_functionAppText.Text))
        {
            error = "Function App name is required.";
        }
        else if (!IsValidStorageAccountName(_storageAccountText.Text))
        {
            error = "Storage account name must be 3-24 lowercase letters or digits.";
        }
        else if (string.IsNullOrWhiteSpace(_planText.Text))
        {
            error = "Hosting plan name is required.";
        }
        else if (string.IsNullOrWhiteSpace(_containerText.Text))
        {
            error = "Blob container name is required.";
        }
        else if (string.IsNullOrWhiteSpace(_tableText.Text))
        {
            error = "Table name is required.";
        }
        else if (string.IsNullOrWhiteSpace(_packagePathText.Text))
        {
            error = "Select a Functions package to deploy.";
        }

        _errorLabel.Text = error;
        _provisionButton.Enabled = string.IsNullOrEmpty(error);
        return string.IsNullOrEmpty(error);
    }

    private static bool IsValidStorageAccountName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (value.Length is < 3 or > 24)
        {
            return false;
        }

        foreach (var ch in value)
        {
            if (!char.IsLetterOrDigit(ch))
            {
                return false;
            }

            if (char.IsLetter(ch) && !char.IsLower(ch))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<string?> CheckAzureNameAvailabilityAsync(string storageAccountName, string functionAppName, CancellationToken cancellationToken)
    {
        if (!AzureCliLocator.TryResolveExecutable(out var azPath, out var azError))
        {
            return $"Azure CLI not available: {azError}";
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));
        var token = timeoutCts.Token;

        // Check storage account name availability (simple tsv response)
        var storageCheckResult = await RunAzCommandAsync(azPath, $"storage account check-name --name {storageAccountName} --query nameAvailable -o tsv", token).ConfigureAwait(false);
        if (storageCheckResult.ExitCode != 0)
        {
            return BuildCliError("Storage account availability check failed.", storageCheckResult);
        }

        if (storageCheckResult.StandardOutput.Trim().Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return $"Storage account name '{storageAccountName}' is already taken globally. Please choose a different name.";
        }

        // Check function app name availability (JSON payload)
        var functionCheckResult = await RunAzCommandAsync(azPath, $"functionapp check-name --name \"{functionAppName}\" --output json", token).ConfigureAwait(false);
        if (functionCheckResult.ExitCode != 0 || string.IsNullOrWhiteSpace(functionCheckResult.StandardOutput))
        {
            return BuildCliError("Function App name availability check failed.", functionCheckResult);
        }

        try
        {
            using var doc = JsonDocument.Parse(functionCheckResult.StandardOutput);
            var nameAvailable = doc.RootElement.TryGetProperty("nameAvailable", out var availableProp) && availableProp.GetBoolean();
            if (nameAvailable)
            {
                return null;
            }

            var message = doc.RootElement.TryGetProperty("message", out var messageProp) ? messageProp.GetString() : null;
            var reason = doc.RootElement.TryGetProperty("reason", out var reasonProp) ? reasonProp.GetString() : null;
            var detail = !string.IsNullOrWhiteSpace(message)
                ? message
                : $"Function app name '{functionAppName}' is already taken globally.";
            if (!string.IsNullOrWhiteSpace(reason))
            {
                detail = $"{detail} ({reason})";
            }

            return detail;
        }
        catch (JsonException ex)
        {
            return $"Unable to parse Azure CLI response: {ex.Message}";
        }
    }

    private static string BuildCliError(string prefix, AzCliResult result)
    {
        var detail = !string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardError.Trim()
            : (!string.IsNullOrWhiteSpace(result.StandardOutput) ? result.StandardOutput.Trim() : string.Empty);
        return string.IsNullOrWhiteSpace(detail) ? prefix : $"{prefix} {detail}";
    }

    private async Task<AzCliResult> RunAzCommandAsync(string azPath, string arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = azPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = false };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                stdout.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                stderr.AppendLine(e.Data);
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start Azure CLI process.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // ignored
            }

            throw;
        }

        return new AzCliResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private sealed record AzCliResult(int ExitCode, string StandardOutput, string StandardError);

    private FunctionsDeploymentOptions BuildOptions()
    {
        var subscription = GetSubscriptionText();
        var location = GetLocationText();

        return new FunctionsDeploymentOptions
        {
            SubscriptionId = subscription,
            ResourceGroup = _resourceGroupText.Text.Trim(),
            FunctionAppName = _functionAppText.Text.Trim(),
            StorageAccountName = _storageAccountText.Text.Trim().ToLowerInvariant(),
            PlanName = _planText.Text.Trim(),
            Location = location,
            PackagePath = _packagePathText.Text.Trim(),
            PayloadContainer = _containerText.Text.Trim(),
            MetadataTable = _tableText.Text.Trim()
        };
    }

    private string GetSubscriptionText()
    {
        if (_subscriptionCombo.SelectedItem is AzureSubscriptionInfo subscription)
        {
            return subscription.Id;
        }

        return _subscriptionCombo.Text.Trim();
    }

    private string GetLocationText()
    {
        if (_locationCombo.SelectedItem is AzureLocationInfo location)
        {
            return location.Name;
        }

        return _locationCombo.Text.Trim();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _locationRefreshTimer.Stop();
            _locationRefreshTimer.Dispose();
            if (_locationRefreshCts is not null)
            {
                _locationRefreshCts.Cancel();
                _locationRefreshCts.Dispose();
            }
        }

        base.Dispose(disposing);
    }
}
