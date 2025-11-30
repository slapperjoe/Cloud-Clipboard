using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
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
    private readonly string _ownerId;
    private readonly IBackendProvisioningService _provisioningService;
    private BackendProvisioningResult? _provisioningResult;
    private bool _isProvisioning;
    private bool _provisioningCompletedSuccessfully;

    private readonly ComboBox _subscriptionCombo;
    private readonly ComboBox _locationCombo;
    private readonly TextBox _resourceGroupText;
    private readonly TextBox _functionAppText;
    private readonly TextBox _storageAccountText;
    private readonly TextBox _planText;
    private readonly TextBox _containerText;
    private readonly TextBox _tableText;
    private readonly TextBox _packagePathText;
    private readonly RichTextBox _logTextBox;
    private readonly Button _browseButton;
    private readonly Button _provisionButton;
    private readonly Button _cancelButton;
    private readonly Label _errorLabel;
    private readonly Label _statusLabel;
    private readonly ProgressBar _progressBar;
    private readonly IAzureCliMetadataProvider _metadataProvider;
    private readonly CancellationToken _cancellationToken;
    private readonly System.Windows.Forms.Timer _locationRefreshTimer;
    private CancellationTokenSource? _locationRefreshCts;
    private readonly TabControl _tabControl;
    private readonly TabPage _configTab;
    private readonly TabPage _logTab;

    private ProvisionBackendDialog(
        ProvisionBackendDialogOptions options,
        ProvisionBackendDialogDependencies dependencies,
        CancellationToken cancellationToken)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        if (dependencies is null)
        {
            throw new ArgumentNullException(nameof(dependencies));
        }

        _ownerId = string.IsNullOrWhiteSpace(options.OwnerId)
            ? throw new ArgumentException("Owner Id is required.", nameof(options))
            : options.OwnerId;
        _metadataProvider = dependencies.MetadataProvider ?? throw new ArgumentNullException(nameof(dependencies), "MetadataProvider is required.");
        _provisioningService = dependencies.ProvisioningService ?? throw new ArgumentNullException(nameof(dependencies), "ProvisioningService is required.");
        var iconProvider = dependencies.IconProvider ?? throw new ArgumentNullException(nameof(dependencies), "IconProvider is required.");
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
        AutoScroll = true;
        ClientSize = new Size(1120,480);
        MinimumSize = new Size(1024, 500);
        MinimizeBox = false;
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ShowIcon = true;
        Icon = iconProvider.GetIcon(32);

        _tabControl = new TabControl
        {
            Dock = DockStyle.Fill
        };

        var configTab = new TabPage("Configuration")
        {
            Padding = new Padding(0),
            AutoScroll = true
        };
        _configTab = configTab;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 3,
            RowCount = 0,
            Padding = new Padding(16),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        configTab.Controls.Add(layout);
        _tabControl.TabPages.Add(configTab);

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

        var ownerIdTextBox = CreateTextBox(_ownerId, readOnly: true);
        AddRow(layout, "Owner Id", ownerIdTextBox);

        _subscriptionCombo = CreateComboBox();
        BindSubscriptions(_subscriptionCombo, options.Subscriptions, options.SubscriptionId ?? options.Defaults.SubscriptionId);
        AddRow(layout, "Subscription", _subscriptionCombo);
        HookValidation(_subscriptionCombo);

        _locationCombo = CreateComboBox();
        var defaultLocation = string.IsNullOrWhiteSpace(options.Defaults.Location) ? "eastus" : options.Defaults.Location;
        BindLocations(_locationCombo, options.Locations, defaultLocation);
        AddRow(layout, "Location", _locationCombo);
        HookValidation(_locationCombo);

        _resourceGroupText = CreateTextBox(string.IsNullOrWhiteSpace(options.Defaults.ResourceGroup)
            ? ProvisioningNameGenerator.CreateResourceGroupName(_ownerId)
            : options.Defaults.ResourceGroup);
        AddRow(layout, "Resource Group", _resourceGroupText);
        HookValidation(_resourceGroupText);

        _functionAppText = CreateTextBox(string.IsNullOrWhiteSpace(options.Defaults.FunctionAppName)
            ? ProvisioningNameGenerator.CreateFunctionAppName(_ownerId)
            : options.Defaults.FunctionAppName);
        AddRow(layout, "Function App Name", _functionAppText);
        HookValidation(_functionAppText);

        _storageAccountText = CreateTextBox(string.IsNullOrWhiteSpace(options.Defaults.StorageAccountName)
            ? ProvisioningNameGenerator.CreateStorageAccountName(_ownerId)
            : options.Defaults.StorageAccountName);
        AddRow(layout, "Storage Account", _storageAccountText);
        HookValidation(_storageAccountText);

        _planText = CreateTextBox(string.IsNullOrWhiteSpace(options.Defaults.PlanName)
            ? ProvisioningNameGenerator.CreatePlanName(_ownerId)
            : options.Defaults.PlanName);
        AddRow(layout, "Hosting Plan", _planText);
        HookValidation(_planText);

        _containerText = CreateTextBox(string.IsNullOrWhiteSpace(options.Defaults.PayloadContainer)
            ? "clipboardpayloads"
            : options.Defaults.PayloadContainer);
        AddRow(layout, "Blob Container", _containerText);
        HookValidation(_containerText);

        _tableText = CreateTextBox(string.IsNullOrWhiteSpace(options.Defaults.MetadataTable)
            ? "ClipboardMetadata"
            : options.Defaults.MetadataTable);
        AddRow(layout, "Table Name", _tableText);
        HookValidation(_tableText);

        _packagePathText = CreateTextBox(string.IsNullOrWhiteSpace(options.Defaults.PackagePath)
            ? FunctionsDeploymentOptions.CreateDefault().PackagePath
            : options.Defaults.PackagePath);
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

        _statusLabel = new Label
        {
            Text = "Waiting to start provisioning...",
            Dock = DockStyle.Fill,
            AutoSize = false,
            Height = 20,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(Font.FontFamily, 9.5F, FontStyle.Bold)
        };

        _logTextBox = new RichTextBox
        {
            Multiline = true,
            ReadOnly = true,
            DetectUrls = false,
            ShortcutsEnabled = true,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9F),
            BackColor = Color.Black,
            ForeColor = Color.LimeGreen,
            BorderStyle = BorderStyle.FixedSingle,
            ScrollBars = RichTextBoxScrollBars.Both,
            WordWrap = false,
            HideSelection = false
        };

        var logContextMenu = new ContextMenuStrip();
        var copyMenuItem = new ToolStripMenuItem("Copy") { ShortcutKeys = Keys.Control | Keys.C };
        copyMenuItem.Click += (_, _) => CopyLogSelection();
        var selectAllMenuItem = new ToolStripMenuItem("Select All") { ShortcutKeys = Keys.Control | Keys.A };
        selectAllMenuItem.Click += (_, _) => SelectAllLogText();
        logContextMenu.Items.Add(copyMenuItem);
        logContextMenu.Items.Add(selectAllMenuItem);
        _logTextBox.ContextMenuStrip = logContextMenu;

        _progressBar = new ProgressBar
        {
            Dock = DockStyle.Fill,
            Style = ProgressBarStyle.Continuous,
            Height = 22,
            Margin = new Padding(0, 8, 0, 0)
        };

        var logLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(4)
        };
        logLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        logLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        logLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        logLayout.Controls.Add(_statusLabel, 0, 0);
        logLayout.Controls.Add(_logTextBox, 0, 1);
        logLayout.Controls.Add(_progressBar, 0, 2);

        var logGroup = new GroupBox
        {
            Text = string.Empty,
            Dock = DockStyle.Fill,
            Padding = new Padding(4),
            Margin = new Padding(8)
        };
        logGroup.Controls.Add(logLayout);

        _logTab = new TabPage("Azure CLI Output")
        {
            Padding = new Padding(0)
        };
        _logTab.Controls.Add(logGroup);
        _tabControl.TabPages.Add(_logTab);

        var buttonsPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(0, 8, 0, 0)
        };
        _provisionButton = new Button { Text = "Provision", AutoSize = true, MinimumSize = new Size(90, 28), Padding = new Padding(8, 4, 8, 4) };
        _provisionButton.Click += (_, _) => OnProvision();
        _cancelButton = new Button { Text = "Cancel", AutoSize = true, MinimumSize = new Size(90, 28), Padding = new Padding(8, 4, 8, 4) };
        _cancelButton.Click += (_, _) => OnCancelClicked();
        buttonsPanel.Controls.Add(_provisionButton);
        buttonsPanel.Controls.Add(_cancelButton);

        var rootLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(0)
        };
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rootLayout.Controls.Add(_tabControl, 0, 0);
        rootLayout.Controls.Add(buttonsPanel, 0, 1);

        Controls.Add(rootLayout);
        Shown += (_, _) => ValidateInputs();

        _subscriptionCombo.SelectedIndexChanged += (_, _) => ScheduleLocationRefresh();
        _subscriptionCombo.TextChanged += (_, _) => ScheduleLocationRefresh();
    }

    public static Task<BackendProvisioningResult?> ShowAsync(
        ProvisionBackendDialogOptions options,
        ProvisionBackendDialogDependencies dependencies,
        CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<BackendProvisioningResult?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                using var dialog = new ProvisionBackendDialog(options, dependencies, cancellationToken);
                using var registration = cancellationToken.Register(() =>
                {
                    if (dialog.IsHandleCreated)
                    {
                        dialog.BeginInvoke(new Action(() => dialog.CloseWithResult(DialogResult.Cancel)));
                    }
                });
                var result = dialog.ShowDialog();
                if (result == DialogResult.OK && dialog._provisioningResult is not null)
                {
                    tcs.TrySetResult(dialog._provisioningResult);
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
        if (_locationRefreshCts is not null)
        {
            await _locationRefreshCts.CancelAsync();
            _locationRefreshCts.Dispose();
        }

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

    private void ToggleConfigurationInputs(bool enabled)
    {
        _subscriptionCombo.Enabled = enabled;
        _locationCombo.Enabled = enabled;
        _resourceGroupText.ReadOnly = !enabled;
        _functionAppText.ReadOnly = !enabled;
        _storageAccountText.ReadOnly = !enabled;
        _planText.ReadOnly = !enabled;
        _containerText.ReadOnly = !enabled;
        _tableText.ReadOnly = !enabled;
        _packagePathText.ReadOnly = !enabled;
        _browseButton.Enabled = enabled;
    }

    private void OnCancelClicked()
    {
        if (_isProvisioning)
        {
            MessageBox.Show(this, "Provisioning is currently running. Please wait until it completes.", "Provisioning In Progress", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_provisioningCompletedSuccessfully)
        {
            CloseWithResult(DialogResult.OK);
        }
        else
        {
            CloseWithResult(DialogResult.Cancel);
        }
    }

    private async Task StartProvisioningAsync()
    {
        _isProvisioning = true;
        ToggleConfigurationInputs(false);
        _provisionButton.Enabled = false;
        _cancelButton.Enabled = false;
        ShowLogTab();
        SetProgressBusy(true, "Provisioning backend...");

        var request = new BackendProvisioningRequest(_ownerId, BuildOptions());
        var progress = new Progress<ProvisioningProgressUpdate>(ApplyProvisioningProgress);

        BackendProvisioningResult result;
        try
        {
            result = await _provisioningService.ProvisionAsync(request, progress, _cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            result = new BackendProvisioningResult(false, null, ex.Message);
        }

        HandleProvisioningCompletion(result);
    }

    private void HandleProvisioningCompletion(BackendProvisioningResult result)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => HandleProvisioningCompletion(result)));
            return;
        }

        _isProvisioning = false;
        SetProgressBusy(false);

        if (result.Succeeded && result.RemoteOptions is not null)
        {
            _provisioningResult = result;
            _provisioningCompletedSuccessfully = true;
            UpdateStatusLabel("✓ Provisioning completed. Close this window to start using the agent.", Color.DarkGreen);
            AppendLog("Provisioning completed successfully. Close the dialog to apply the configuration.");
            _cancelButton.Text = "Close";
            _cancelButton.Enabled = true;
            _provisionButton.Visible = false;
            MessageBox.Show(
                this,
                "Provisioning finished successfully. Close this window to apply the configuration and let the agent start using it.",
                "Provisioning Complete",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        else
        {
            var message = result.ErrorMessage ?? "Provisioning failed.";
            UpdateStatusLabel(message, Color.DarkRed);
            AppendLog(message, isError: true);
            ToggleConfigurationInputs(true);
            _provisionButton.Enabled = true;
            _cancelButton.Enabled = true;
            ShowConfigurationTab();
        }
    }

    private void ApplyProvisioningProgress(ProvisioningProgressUpdate update)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => ApplyProvisioningProgress(update)));
            return;
        }

        if (!string.IsNullOrWhiteSpace(update.Message))
        {
            AppendLog(update.Message);
            if (!update.IsVerbose)
            {
                UpdateStatusLabel(update.Message);
            }
        }

        if (update.PercentComplete.HasValue)
        {
            var percent = (int)Math.Clamp(Math.Round(update.PercentComplete.Value), 0, 100);
            _progressBar.Style = ProgressBarStyle.Continuous;
            _progressBar.MarqueeAnimationSpeed = 0;
            _progressBar.Value = Math.Min(_progressBar.Maximum, Math.Max(_progressBar.Minimum, percent));
        }
        else if (_progressBar.Style != ProgressBarStyle.Marquee && !_provisioningCompletedSuccessfully)
        {
            _progressBar.Style = ProgressBarStyle.Marquee;
            _progressBar.MarqueeAnimationSpeed = 30;
        }
    }

    private void SetProgressBusy(bool busy, string? status = null)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => SetProgressBusy(busy, status)));
            return;
        }

        if (busy)
        {
            _progressBar.Style = ProgressBarStyle.Marquee;
            _progressBar.MarqueeAnimationSpeed = 30;
            _progressBar.Value = 0;
        }
        else
        {
            _progressBar.Style = ProgressBarStyle.Continuous;
            _progressBar.MarqueeAnimationSpeed = 0;
            _progressBar.Value = _provisioningCompletedSuccessfully ? _progressBar.Maximum : 0;
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            UpdateStatusLabel(status);
        }
    }

    private void UpdateStatusLabel(string message, Color? color = null)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => UpdateStatusLabel(message, color)));
            return;
        }

        _statusLabel.Text = message;
        _statusLabel.ForeColor = color ?? SystemColors.ControlText;
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
        AppendLog("Starting Azure resource name availability checks...");
        ShowLogTab();

        try
        {
            var subscriptionId = GetSubscriptionText();
            if (string.IsNullOrWhiteSpace(subscriptionId))
            {
                _errorLabel.Text = "Subscription Id is required to verify names.";
                _errorLabel.ForeColor = Color.Firebrick;
                _provisionButton.Enabled = true;
                return;
            }

            var storageAccountName = _storageAccountText.Text.Trim().ToLowerInvariant();
            var functionAppName = _functionAppText.Text.Trim();

            var availabilityError = await CheckAzureNameAvailabilityAsync(subscriptionId, storageAccountName, functionAppName, _cancellationToken);
            if (!string.IsNullOrEmpty(availabilityError))
            {
                var guidance = $"{availabilityError} Update the Configuration tab values and try again.";
                _errorLabel.Text = guidance;
                _errorLabel.ForeColor = Color.Firebrick;
                AppendLog(guidance, isError: true);
                ShowConfigurationTab();
                _provisionButton.Enabled = true;
                return;
            }

            _errorLabel.Text = string.Empty;
            AppendLog("Name availability confirmed. Starting provisioning.");
            await StartProvisioningAsync();
        }
        catch (Exception ex)
        {
            _errorLabel.Text = $"Failed to check availability: {ex.Message}";
            _errorLabel.ForeColor = Color.Firebrick;
            AppendLog($"Failed to check availability: {ex.Message}", isError: true);
            _provisionButton.Enabled = true;
        }
    }

    private void CloseWithResult(DialogResult result)
    {
        DialogResult = result;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_isProvisioning && e.CloseReason == CloseReason.UserClosing)
        {
            var confirm = MessageBox.Show(
                this,
                "Provisioning is still running. Are you sure you want to close this window?",
                "Provisioning In Progress",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (confirm != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        if (_provisioningCompletedSuccessfully && DialogResult != DialogResult.OK)
        {
            DialogResult = DialogResult.OK;
        }

        base.OnFormClosing(e);
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

    private async Task<string?> CheckAzureNameAvailabilityAsync(string subscriptionId, string storageAccountName, string functionAppName, CancellationToken cancellationToken)
    {
        if (!AzureCliLocator.TryResolveExecutable(out var azPath, out var azError))
        {
            var message = $"Azure CLI not available: {azError}";
            AppendLog(message, isError: true);
            return message;
        }

        AppendLog($"Using Azure CLI at '{azPath}' to validate names...");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));
        var token = timeoutCts.Token;

        var storageError = await CheckStorageAccountAvailabilityAsync(azPath, storageAccountName, token).ConfigureAwait(false);
        if (storageError is not null)
        {
            return storageError;
        }

        var functionError = await CheckFunctionAppAvailabilityAsync(azPath, subscriptionId, functionAppName, token).ConfigureAwait(false);
        if (functionError is not null)
        {
            return functionError;
        }

        AppendLog("Azure CLI reports both names are available.");
        return null;
    }

    private async Task<string?> CheckStorageAccountAvailabilityAsync(string azPath, string storageAccountName, CancellationToken token)
    {
        AppendLog($"Running storage account availability check for '{storageAccountName}'...");
        var storageCheckResult = await RunAzCommandAsync(
            azPath,
            $"storage account check-name --name {storageAccountName} --query nameAvailable -o tsv --only-show-errors",
            token,
            streamLogger: null).ConfigureAwait(false);
        AppendLog($"storage account check-name exited with code {storageCheckResult.ExitCode}.");
        if (storageCheckResult.ExitCode != 0)
        {
            var error = BuildCliError("Storage account availability check failed.", storageCheckResult);
            AppendLog(error, isError: true);
            return error;
        }

        if (storageCheckResult.StandardOutput.Trim().Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            var message = $"Storage account name '{storageAccountName}' is already taken globally. Please choose a different name.";
            AppendLog(message, isError: true);
            return message;
        }

        AppendLog($"Storage account name '{storageAccountName}' is available.");

        return null;
    }

    private async Task<string?> CheckFunctionAppAvailabilityAsync(string azPath, string subscriptionId, string functionAppName, CancellationToken token)
    {
        AppendLog($"Running Function App availability check for '{functionAppName}'...");
        var requestUri = $"https://management.azure.com/subscriptions/{subscriptionId}/providers/Microsoft.Web/checkNameAvailability?api-version=2022-03-01";
        var requestBody = JsonSerializer.Serialize(new { name = functionAppName, type = "Microsoft.Web/sites" });
        var tempFile = Path.Combine(Path.GetTempPath(), $"cloudclipboard-checkname-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(tempFile, requestBody, Encoding.UTF8, token).ConfigureAwait(false);

        try
        {
            var arguments = $"rest --method post --uri \"{requestUri}\" --headers Content-Type=application/json --body \"@{tempFile}\"";
            var functionCheckResult = await RunAzCommandAsync(azPath, arguments, token, streamLogger: null).ConfigureAwait(false);
            AppendLog($"functionapp check-name (REST) exited with code {functionCheckResult.ExitCode}.");
            if (functionCheckResult.ExitCode != 0 || string.IsNullOrWhiteSpace(functionCheckResult.StandardOutput))
            {
                var error = BuildCliError("Function App name availability check failed.", functionCheckResult);
                AppendLog(error, isError: true);
                return error;
            }

            using var doc = JsonDocument.Parse(functionCheckResult.StandardOutput);
            var nameAvailable = doc.RootElement.TryGetProperty("nameAvailable", out var availableProp) && availableProp.GetBoolean();
            if (nameAvailable)
            {
                AppendLog($"Function App name '{functionAppName}' is available.");
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

            AppendLog(detail, isError: true);
            return detail;
        }
        catch (JsonException ex)
        {
            var message = $"Unable to parse Azure CLI response: {ex.Message}";
            AppendLog(message, isError: true);
            return message;
        }
        finally
        {
            try
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
            catch
            {
                // ignored
            }
        }
    }

    private static string BuildCliError(string prefix, AzCliResult result)
    {
        string detail;
        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            detail = result.StandardError.Trim();
        }
        else if (!string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            detail = result.StandardOutput.Trim();
        }
        else
        {
            detail = string.Empty;
        }

        return string.IsNullOrWhiteSpace(detail) ? prefix : $"{prefix} {detail}";
    }

    private async Task<AzCliResult> RunAzCommandAsync(string azPath, string arguments, CancellationToken cancellationToken, Action<string, bool>? streamLogger = null)
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
                lock (stdout)
                {
                    stdout.AppendLine(e.Data);
                }
                streamLogger?.Invoke(e.Data, false);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                lock (stderr)
                {
                    stderr.AppendLine(e.Data);
                }
                streamLogger?.Invoke(e.Data, true);
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

    private void ShowLogTab()
    {
        if (_tabControl.SelectedTab != _logTab)
        {
            _tabControl.SelectedTab = _logTab;
        }
    }

    private void ShowConfigurationTab()
    {
        if (_tabControl.SelectedTab != _configTab)
        {
            _tabControl.SelectedTab = _configTab;
        }
    }

    private void CopyLogSelection()
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(CopyLogSelection));
            return;
        }

        if (_logTextBox.TextLength == 0)
        {
            return;
        }

        if (_logTextBox.SelectionLength == 0)
        {
            _logTextBox.SelectAll();
        }

        _logTextBox.Copy();
    }

    private void SelectAllLogText()
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(SelectAllLogText));
            return;
        }

        if (_logTextBox.TextLength == 0)
        {
            return;
        }

        _logTextBox.SelectAll();
    }

    private void AppendLog(string message, bool isError = false)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => AppendLog(message, isError)));
            return;
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        if (_logTextBox.TextLength > 0)
        {
            _logTextBox.AppendText(Environment.NewLine);
        }

        var prefix = isError ? "[stderr]" : "[info]";
        _logTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {prefix} {message}");
        _logTextBox.SelectionStart = _logTextBox.TextLength;
        _logTextBox.ScrollToCaret();
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

public sealed record ProvisionBackendDialogOptions(
    string OwnerId,
    string? SubscriptionId,
    FunctionsDeploymentOptions Defaults,
    IReadOnlyList<AzureSubscriptionInfo> Subscriptions,
    IReadOnlyList<AzureLocationInfo> Locations);

public sealed record ProvisionBackendDialogDependencies(
    IAzureCliMetadataProvider MetadataProvider,
    IBackendProvisioningService ProvisioningService,
    IAppIconProvider IconProvider);
