using System;
using System.Drawing;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;
using CloudClipboard.Agent.Windows.Configuration;
using CloudClipboard.Agent.Windows.Options;
using CloudClipboard.Agent.Windows.Services;
using CloudClipboard.Core.Models;

namespace CloudClipboard.Agent.Windows.UI;

public sealed class SettingsForm : Form
{
    private readonly IAgentSettingsStore _settingsStore;
    private AgentOptions _workingCopy;
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly ToolTip _toolTip = new();
    private readonly TextBox _jsonPreview;
    private readonly TextBox _ownerIdText;
    private readonly TextBox _deviceNameText;
    private readonly TextBox _apiBaseUrlText;
    private readonly TextBox _functionKeyText;
    private readonly CheckBox _autoPasteCheckbox;
    private readonly CheckBox _showNotificationsCheckbox;
    private readonly ComboBox _uploadModeCombo;
    private readonly ComboBox _notificationTransportCombo;
    private readonly ComboBox _syncDirectionCombo;
    private readonly TextBox _manualUploadHotkeyText;
    private readonly TextBox _manualDownloadHotkeyText;
    private readonly NumericUpDown _pollIntervalNumeric;
    private readonly NumericUpDown _historyLengthNumeric;
    private readonly NumericUpDown _historyPollNumeric;
    private readonly NumericUpDown _ownerStatePollNumeric;
    private readonly CheckBox _pushEnabledCheckbox;
    private readonly NumericUpDown _pushReconnectNumeric;
    private readonly TextBox _deployFunctionAppText;
    private readonly TextBox _deployResourceGroupText;
    private readonly TextBox _deploySubscriptionText;
    private readonly TextBox _deployPackagePathText;
    private readonly TextBox _deployLocationText;
    private readonly TextBox _deployStorageAccountText;
    private readonly TextBox _deployPlanText;
    private readonly TextBox _deployContainerText;
    private readonly TextBox _deployTableText;
    private readonly Label _statusLabel;
    private bool _initializing;
    private readonly IAppIconProvider _iconProvider;

    public SettingsForm(IAgentSettingsStore settingsStore, IAppIconProvider iconProvider)
    {
        _settingsStore = settingsStore;
        _iconProvider = iconProvider;
        _workingCopy = CloneOptions(settingsStore.Load());
        _initializing = true;

        Text = "Cloud Clipboard Settings";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 600);
        AutoScaleMode = AutoScaleMode.Dpi;
            AutoScroll = true;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        Icon = _iconProvider.GetIcon(128);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(10)
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));

        var fieldsTable = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink
        };
        fieldsTable.ColumnStyles.Clear();
        fieldsTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        fieldsTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fieldsTable.GrowStyle = TableLayoutPanelGrowStyle.AddRows;

        _ownerIdText = CreateTextBox();
        AddLabeledControl(fieldsTable, "Owner ID", "Unique identifier shared across your devices.", _ownerIdText);

        _deviceNameText = CreateTextBox();
        AddLabeledControl(fieldsTable, "Device Name", "Friendly name stamped on uploads.", _deviceNameText);

        _apiBaseUrlText = CreateTextBox();
        AddLabeledControl(fieldsTable, "API Base URL", "Base Functions URL, include /api.", _apiBaseUrlText);

        _functionKeyText = CreateTextBox();
        AddLabeledControl(fieldsTable, "Function Key", "x-functions-key or ?code value for authentication.", _functionKeyText);

        _autoPasteCheckbox = new CheckBox { AutoSize = true };
        AddLabeledControl(fieldsTable, "Auto Paste on Startup", "Download and paste the latest cloud item when the agent starts.", _autoPasteCheckbox);

        _showNotificationsCheckbox = new CheckBox { AutoSize = true };
        AddLabeledControl(fieldsTable, "Show Notifications", "Display balloon notifications for key events.", _showNotificationsCheckbox);

        _uploadModeCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock = DockStyle.Fill,
            DataSource = Enum.GetValues(typeof(ClipboardUploadMode))
        };
        AddLabeledControl(fieldsTable, "Upload Mode", "Automatic uploads immediately; Manual queues until you trigger it.", _uploadModeCombo);

        _notificationTransportCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock = DockStyle.Fill,
            DataSource = Enum.GetValues(typeof(NotificationTransport))
        };
        AddLabeledControl(fieldsTable, "Notification Transport", "Choose PubSub for Azure Web PubSub (default) or Polling as a fallback.", _notificationTransportCombo);

        _syncDirectionCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock = DockStyle.Fill,
            DataSource = Enum.GetValues(typeof(ClipboardSyncDirection))
        };
        AddLabeledControl(fieldsTable, "Sync Direction", "Full sync uploads and downloads, Only Cut uploads without downloading, Only Paste downloads without uploading.", _syncDirectionCombo);

        _manualUploadHotkeyText = CreateTextBox();
        AddLabeledControl(fieldsTable, "Manual Upload Hotkey", "Global hotkey used to upload the staged clipboard in Manual mode.", _manualUploadHotkeyText);

        _manualDownloadHotkeyText = CreateTextBox();
        AddLabeledControl(fieldsTable, "Manual Download Hotkey", "Global hotkey used to download the most recent cloud clipboard entry.", _manualDownloadHotkeyText);

        _pollIntervalNumeric = CreateNumericUpDown(1, 60);
        AddLabeledControl(fieldsTable, "Clipboard Poll Seconds", "Interval between local clipboard snapshots.", _pollIntervalNumeric);

        _historyLengthNumeric = CreateNumericUpDown(1, 100);
        AddLabeledControl(fieldsTable, "History Length", "Number of history items to request from the service.", _historyLengthNumeric);

        _historyPollNumeric = CreateNumericUpDown(5, 120);
        AddLabeledControl(fieldsTable, "History Refresh Seconds", "Interval between history refresh calls.", _historyPollNumeric);

        _ownerStatePollNumeric = CreateNumericUpDown(5, 300);
        AddLabeledControl(fieldsTable, "Owner State Poll Seconds", "Interval between pause-state checks.", _ownerStatePollNumeric);

        _pushEnabledCheckbox = new CheckBox { AutoSize = true };
        AddLabeledControl(fieldsTable, "Enable Push Notifications", "Use long-polling notifications to refresh quickly.", _pushEnabledCheckbox);

        _pushReconnectNumeric = CreateNumericUpDown(5, 300);
        AddLabeledControl(fieldsTable, "Push Reconnect Seconds", "Delay before reconnecting when the notification poll returns.", _pushReconnectNumeric);

        var deployHeader = new Label
        {
            Text = "Deployment Defaults",
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Padding = new Padding(0, 15, 0, 5)
        };
        fieldsTable.Controls.Add(deployHeader);
        fieldsTable.SetColumnSpan(deployHeader, 2);

        _deployFunctionAppText = CreateTextBox();
        AddLabeledControl(fieldsTable, "Function App Name", "Name of the Azure Functions app to deploy to.", _deployFunctionAppText);

        _deployResourceGroupText = CreateTextBox();
        AddLabeledControl(fieldsTable, "Resource Group", "Azure resource group hosting the Function App.", _deployResourceGroupText);

        _deploySubscriptionText = CreateTextBox();
        AddLabeledControl(fieldsTable, "Subscription Id", "Azure subscription ID that contains the Function App.", _deploySubscriptionText);

        _deployLocationText = CreateTextBox();
        AddLabeledControl(fieldsTable, "Location", "Azure region for provisioning (e.g., eastus).", _deployLocationText);

        _deployStorageAccountText = CreateTextBox();
        AddLabeledControl(fieldsTable, "Storage Account", "Name of the storage account used for payloads and metadata.", _deployStorageAccountText);

        _deployPlanText = CreateTextBox();
        AddLabeledControl(fieldsTable, "Hosting Plan", "Consumption plan name for the Function App.", _deployPlanText);

        _deployContainerText = CreateTextBox();
        AddLabeledControl(fieldsTable, "Blob Container", "Container name for clipboard payloads.", _deployContainerText);

        _deployTableText = CreateTextBox();
        AddLabeledControl(fieldsTable, "Metadata Table", "Table name for clipboard metadata.", _deployTableText);

        _deployPackagePathText = CreateTextBox();
        AddLabeledControl(fieldsTable, "Package Path", "Path to the packaged function zip (defaults next to the agent).", _deployPackagePathText);

        var fieldsPanel = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(0, 0, 10, 0)
        };
        fieldsPanel.Controls.Add(fieldsTable);
        content.Controls.Add(fieldsPanel, 0, 0);

        var previewPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1
        };
        previewPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        previewPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var previewLabel = new Label
        {
            Text = "Generated JSON",
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold)
        };
        previewPanel.Controls.Add(previewLabel, 0, 0);

        _jsonPreview = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            Font = new Font(FontFamily.GenericMonospace, 9),
            Dock = DockStyle.Fill
        };
        previewPanel.Controls.Add(_jsonPreview, 0, 1);

        content.Controls.Add(previewPanel, 1, 0);

        root.Controls.Add(content, 0, 0);

        var buttonPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(10)
        };
        buttonPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        buttonPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        _statusLabel = new Label
        {
            Text = string.Empty,
            AutoSize = true,
            Dock = DockStyle.Left
        };
        buttonPanel.Controls.Add(_statusLabel, 0, 0);

        var buttonsFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Anchor = AnchorStyles.Right
        };

        var closeButton = new Button { Text = "Close", AutoSize = true };
        closeButton.Click += (_, _) => Close();
        buttonsFlow.Controls.Add(closeButton);

        var saveButton = new Button { Text = "Save", AutoSize = true };
        saveButton.Click += (_, _) => SaveSettings();
        buttonsFlow.Controls.Add(saveButton);

        var restoreCheckpointButton = new Button { Text = "Restore Checkpoint", AutoSize = true };
        restoreCheckpointButton.Click += (_, _) => RestoreCheckpoint();
        buttonsFlow.Controls.Add(restoreCheckpointButton);

        var saveCheckpointButton = new Button { Text = "Save Checkpoint", AutoSize = true };
        saveCheckpointButton.Click += (_, _) => SaveCheckpoint();
        buttonsFlow.Controls.Add(saveCheckpointButton);

        buttonPanel.Controls.Add(buttonsFlow, 1, 0);
        root.Controls.Add(buttonPanel, 0, 1);

        Controls.Add(root);

        ApplyOptionsToControls();
        HookEvents();
        _initializing = false;
        RefreshJsonPreview();
    }

    private void HookEvents()
    {
        _ownerIdText.TextChanged += (_, _) => UpdateOption(() => _workingCopy.OwnerId = _ownerIdText.Text.Trim());
        _deviceNameText.TextChanged += (_, _) => UpdateOption(() => _workingCopy.DeviceName = _deviceNameText.Text.Trim());
        _apiBaseUrlText.TextChanged += (_, _) => UpdateOption(() => _workingCopy.ApiBaseUrl = _apiBaseUrlText.Text.Trim());
        _functionKeyText.TextChanged += (_, _) => UpdateOption(() => _workingCopy.FunctionKey = string.IsNullOrWhiteSpace(_functionKeyText.Text) ? null : _functionKeyText.Text.Trim());
        _autoPasteCheckbox.CheckedChanged += (_, _) => UpdateOption(() => _workingCopy.AutoPasteLatestOnStartup = _autoPasteCheckbox.Checked);
        _showNotificationsCheckbox.CheckedChanged += (_, _) => UpdateOption(() => _workingCopy.ShowNotifications = _showNotificationsCheckbox.Checked);
        _uploadModeCombo.SelectedValueChanged += (_, _) => UpdateOption(() =>
        {
            if (_uploadModeCombo.SelectedItem is ClipboardUploadMode mode)
            {
                _workingCopy.UploadMode = mode;
            }
        });
        _notificationTransportCombo.SelectedValueChanged += (_, _) => UpdateOption(() =>
        {
            if (_notificationTransportCombo.SelectedItem is NotificationTransport transport)
            {
                _workingCopy.NotificationTransport = transport;
            }
        });
        _syncDirectionCombo.SelectedValueChanged += (_, _) => UpdateOption(() =>
        {
            if (_syncDirectionCombo.SelectedItem is ClipboardSyncDirection direction)
            {
                _workingCopy.SyncDirection = direction;
            }
        });
        _manualUploadHotkeyText.TextChanged += (_, _) => UpdateOption(() => _workingCopy.ManualUploadHotkey = _manualUploadHotkeyText.Text.Trim());
        _manualDownloadHotkeyText.TextChanged += (_, _) => UpdateOption(() => _workingCopy.ManualDownloadHotkey = _manualDownloadHotkeyText.Text.Trim());
        _pollIntervalNumeric.ValueChanged += (_, _) => UpdateOption(() => _workingCopy.PollIntervalSeconds = (int)_pollIntervalNumeric.Value);
        _historyLengthNumeric.ValueChanged += (_, _) => UpdateOption(() => _workingCopy.HistoryLength = (int)_historyLengthNumeric.Value);
        _historyPollNumeric.ValueChanged += (_, _) => UpdateOption(() => _workingCopy.HistoryPollSeconds = (int)_historyPollNumeric.Value);
        _ownerStatePollNumeric.ValueChanged += (_, _) => UpdateOption(() => _workingCopy.OwnerStatePollSeconds = (int)_ownerStatePollNumeric.Value);
        _pushEnabledCheckbox.CheckedChanged += (_, _) => UpdateOption(() => _workingCopy.EnablePushNotifications = _pushEnabledCheckbox.Checked);
        _pushReconnectNumeric.ValueChanged += (_, _) => UpdateOption(() => _workingCopy.PushReconnectSeconds = (int)_pushReconnectNumeric.Value);
        _deployFunctionAppText.TextChanged += (_, _) => UpdateOption(() => EnsureDeploymentOptions().FunctionAppName = _deployFunctionAppText.Text.Trim());
        _deployResourceGroupText.TextChanged += (_, _) => UpdateOption(() => EnsureDeploymentOptions().ResourceGroup = _deployResourceGroupText.Text.Trim());
        _deploySubscriptionText.TextChanged += (_, _) => UpdateOption(() => EnsureDeploymentOptions().SubscriptionId = _deploySubscriptionText.Text.Trim());
        _deployLocationText.TextChanged += (_, _) => UpdateOption(() => EnsureDeploymentOptions().Location = _deployLocationText.Text.Trim());
        _deployStorageAccountText.TextChanged += (_, _) => UpdateOption(() => EnsureDeploymentOptions().StorageAccountName = _deployStorageAccountText.Text.Trim());
        _deployPlanText.TextChanged += (_, _) => UpdateOption(() => EnsureDeploymentOptions().PlanName = _deployPlanText.Text.Trim());
        _deployContainerText.TextChanged += (_, _) => UpdateOption(() => EnsureDeploymentOptions().PayloadContainer = _deployContainerText.Text.Trim());
        _deployTableText.TextChanged += (_, _) => UpdateOption(() => EnsureDeploymentOptions().MetadataTable = _deployTableText.Text.Trim());
        _deployPackagePathText.TextChanged += (_, _) => UpdateOption(() => EnsureDeploymentOptions().PackagePath = _deployPackagePathText.Text.Trim());
    }

    private void ApplyOptionsToControls()
    {
        _ownerIdText.Text = _workingCopy.OwnerId;
        _deviceNameText.Text = _workingCopy.DeviceName;
        _apiBaseUrlText.Text = _workingCopy.ApiBaseUrl;
        _functionKeyText.Text = _workingCopy.FunctionKey ?? string.Empty;
        _autoPasteCheckbox.Checked = _workingCopy.AutoPasteLatestOnStartup;
        _showNotificationsCheckbox.Checked = _workingCopy.ShowNotifications;
        _uploadModeCombo.SelectedItem = _workingCopy.UploadMode;
        _notificationTransportCombo.SelectedItem = _workingCopy.NotificationTransport;
        _syncDirectionCombo.SelectedItem = _workingCopy.SyncDirection;
        _manualUploadHotkeyText.Text = _workingCopy.ManualUploadHotkey;
        _manualDownloadHotkeyText.Text = _workingCopy.ManualDownloadHotkey;
        _pollIntervalNumeric.Value = ClampNumeric(_pollIntervalNumeric, _workingCopy.PollIntervalSeconds);
        _historyLengthNumeric.Value = ClampNumeric(_historyLengthNumeric, _workingCopy.HistoryLength);
        _historyPollNumeric.Value = ClampNumeric(_historyPollNumeric, _workingCopy.HistoryPollSeconds);
        _ownerStatePollNumeric.Value = ClampNumeric(_ownerStatePollNumeric, _workingCopy.OwnerStatePollSeconds);
        _pushEnabledCheckbox.Checked = _workingCopy.EnablePushNotifications;
        _pushReconnectNumeric.Value = ClampNumeric(_pushReconnectNumeric, _workingCopy.PushReconnectSeconds);
        var deployment = _workingCopy.FunctionsDeployment ?? FunctionsDeploymentOptions.CreateDefault();
        _deployFunctionAppText.Text = deployment.FunctionAppName ?? string.Empty;
        _deployResourceGroupText.Text = deployment.ResourceGroup ?? string.Empty;
        _deploySubscriptionText.Text = deployment.SubscriptionId ?? string.Empty;
        _deployLocationText.Text = deployment.Location ?? string.Empty;
        _deployStorageAccountText.Text = deployment.StorageAccountName ?? string.Empty;
        _deployPlanText.Text = deployment.PlanName ?? string.Empty;
        _deployContainerText.Text = deployment.PayloadContainer ?? string.Empty;
        _deployTableText.Text = deployment.MetadataTable ?? string.Empty;
        _deployPackagePathText.Text = deployment.PackagePath ?? string.Empty;
    }

    private void SaveSettings()
    {
        try
        {
            _workingCopy.PinnedItems ??= new();
            _settingsStore.Save(_workingCopy);
            _statusLabel.Text = $"Saved at {DateTime.Now:T}. Changes apply within a few seconds.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to save settings: {ex.Message}", "Cloud Clipboard", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SaveCheckpoint()
    {
        try
        {
            AgentSettingsCheckpointStore.Save(_workingCopy);
            _statusLabel.Text = $"Checkpoint saved at {DateTime.Now:T}.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to save checkpoint: {ex.Message}", "Cloud Clipboard", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RestoreCheckpoint()
    {
        try
        {
            var restored = AgentSettingsCheckpointStore.Load();
            if (restored is null)
            {
                MessageBox.Show(this, "No checkpoint file found.", "Cloud Clipboard", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _initializing = true;
            _workingCopy = CloneOptions(restored);
            ApplyOptionsToControls();
            _initializing = false;
            RefreshJsonPreview();
            _settingsStore.Save(_workingCopy);
            _statusLabel.Text = $"Checkpoint restored and saved at {DateTime.Now:T}.";
        }
        catch (Exception ex)
        {
            _initializing = false;
            MessageBox.Show(this, $"Failed to restore checkpoint: {ex.Message}", "Cloud Clipboard", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void UpdateOption(Action updater)
    {
        if (_initializing)
        {
            return;
        }

        updater();
        RefreshJsonPreview();
    }

    private void RefreshJsonPreview()
    {
        var document = new { Agent = _workingCopy };
        var json = JsonSerializer.Serialize(document, _serializerOptions);
        _jsonPreview.Text = json;
    }

    private static AgentOptions CloneOptions(AgentOptions source)
    {
        var json = JsonSerializer.Serialize(source);
        return JsonSerializer.Deserialize<AgentOptions>(json)!;
    }

    private FunctionsDeploymentOptions EnsureDeploymentOptions()
    {
        if (_workingCopy.FunctionsDeployment is null)
        {
            _workingCopy.FunctionsDeployment = FunctionsDeploymentOptions.CreateDefault();
        }

        return _workingCopy.FunctionsDeployment;
    }

    private TextBox CreateTextBox()
        => new() { Dock = DockStyle.Fill };

    private NumericUpDown CreateNumericUpDown(int min, int max)
        => new()
        {
            Minimum = min,
            Maximum = max,
            DecimalPlaces = 0,
            Increment = 1,
            Dock = DockStyle.Fill
        };

    private static decimal ClampNumeric(NumericUpDown control, int value)
    {
        if (value < control.Minimum)
        {
            return control.Minimum;
        }

        if (value > control.Maximum)
        {
            return control.Maximum;
        }

        return value;
    }

    private void AddLabeledControl(TableLayoutPanel table, string labelText, string description, Control control)
    {
        var label = new Label
        {
            Text = labelText,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 4, 8, 4)
        };

        control.Margin = new Padding(0, 4, 0, 4);
        control.Anchor = AnchorStyles.Left | AnchorStyles.Right;

        var rowIndex = table.RowCount;
        table.Controls.Add(label, 0, rowIndex);
        table.Controls.Add(control, 1, rowIndex);
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowCount++;
        _toolTip.SetToolTip(label, description);
        _toolTip.SetToolTip(control, description);
    }
}
