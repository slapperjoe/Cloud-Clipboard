using Avalonia.Threading;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CloudClipboard.Agent.Options;
using CloudClipboard.Agent.Services;

namespace CloudClipboard.Agent.UI;

/// <summary>
/// Cross-platform first-run provisioning dialog (Avalonia code-only).
/// </summary>
public sealed class AvaloniaProvisionDialog
{
    private static TextBlock? _progressLabel;
    private static ProgressBar? _progressBar;
    private static TextBlock? _errorLabel;
    private static Button? _provisionBtn;

    public static Task<BackendProvisioningResult?> ShowAsync(
        ProvisionBackendDialogOptions options,
        ProvisionBackendDialogDependencies dependencies,
        CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<BackendProvisioningResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new Progress<ProvisioningProgressUpdate>();
        progress.ProgressChanged += (s, e) =>
        {
            Dispatcher.UIThread.InvokeAsync(() => UpdateProgress(e));
        };

        Dispatcher.UIThread.InvokeAsync(() =>
        {
            var content = CreateContent(options, dependencies, tcs, progress, cancellationToken);
            App.ShowContent(content, 720, 600);
        });

        return tcs.Task;
    }

    private static void UpdateProgress(ProvisioningProgressUpdate update)
    {
        if (_progressLabel != null)
        {
            _progressLabel.Text = update.Message;
        }
        if (_progressBar != null && update.PercentComplete.HasValue)
        {
            _progressBar.Value = update.PercentComplete.Value;
        }
    }

    private static Control CreateContent(
        ProvisionBackendDialogOptions options,
        ProvisionBackendDialogDependencies dependencies,
        TaskCompletionSource<BackendProvisioningResult?> tcs,
        IProgress<ProvisioningProgressUpdate> progress,
        CancellationToken cancellationToken)
    {
        var root = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12,
        };

        // Subscription selector
        var subscriptionCombo = new ComboBox
        {
            ItemsSource = OptionsToSubscriptionList(options),
        };
        if (options.Subscriptions.Count > 0)
        {
            var defaultSub = System.Linq.Enumerable.FirstOrDefault(options.Subscriptions, s => s.IsDefault);
            subscriptionCombo.SelectedIndex = defaultSub != null
                ? System.Linq.Enumerable.ToList(options.Subscriptions).FindIndex(s => System.Object.ReferenceEquals(s, defaultSub))
                : 0;
            if (subscriptionCombo.SelectedIndex < 0)
                subscriptionCombo.SelectedIndex = 0;
        }

        // Location selector
        var locationCombo = new ComboBox
        {
            ItemsSource = OptionsToLocationList(options),
            SelectedIndex = 0,
        };

        // Resource Group
        var rgTextBox = new TextBox { Text = options.Defaults.ResourceGroup };

        // Auto-generated name fields
        var fnTextBox = new TextBox
        {
            Text = options.Defaults.FunctionAppName,
            IsReadOnly = true,
        };
        var stTextBox = new TextBox
        {
            Text = options.Defaults.StorageAccountName,
            IsReadOnly = true,
        };
        var planTextBox = new TextBox
        {
            Text = options.Defaults.PlanName,
            IsReadOnly = true,
        };
        var containerTextBox = new TextBox
        {
            Text = options.Defaults.PayloadContainer,
        };
        var tableTextBox = new TextBox
        {
            Text = options.Defaults.MetadataTable,
        };

        // Package path
        var packagePathTextBox = new TextBox
        {
            Text = options.Defaults.PackagePath ?? "../artifacts/CloudClipboard.Functions.zip",
        };

        // Progress
        _progressLabel = new TextBlock { Text = "Ready to provision." };
        _progressBar = new ProgressBar { Value = 0, Maximum = 100 };
        _errorLabel = new TextBlock { Text = "", Foreground = Brushes.Red };

        // Buttons
        _provisionBtn = new Button { Content = "Provision", Width = 120 };
        var cancelBtn = new Button { Content = "Cancel", Width = 120 };

        // Button row
        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttonPanel.Children.Add(_provisionBtn);
        buttonPanel.Children.Add(cancelBtn);

        // Build layout
        root.Children.Add(new TextBlock { Text = "Subscription:" });
        root.Children.Add(subscriptionCombo);
        root.Children.Add(new TextBlock { Text = "Location:" });
        root.Children.Add(locationCombo);
        root.Children.Add(new TextBlock { Text = "Resource Group:" });
        root.Children.Add(rgTextBox);
        root.Children.Add(new TextBlock { Text = "Generated Names:", FontWeight = FontWeight.Bold });
        root.Children.Add(new TextBlock { Text = "Function App:" });
        root.Children.Add(fnTextBox);
        root.Children.Add(new TextBlock { Text = "Storage Account:" });
        root.Children.Add(stTextBox);
        root.Children.Add(new TextBlock { Text = "App Service Plan:" });
        root.Children.Add(planTextBox);
        root.Children.Add(new TextBlock { Text = "Payload Container:" });
        root.Children.Add(containerTextBox);
        root.Children.Add(new TextBlock { Text = "Metadata Table:" });
        root.Children.Add(tableTextBox);
        root.Children.Add(new TextBlock { Text = "Package Path:" });
        root.Children.Add(packagePathTextBox);
        root.Children.Add(_progressLabel);
        root.Children.Add(_progressBar);
        root.Children.Add(_errorLabel);
        root.Children.Add(buttonPanel);

        // Wire up events
        subscriptionCombo.SelectionChanged += (s, e) =>
        {
            _ = ReloadLocationsAsync(subscriptionCombo, locationCombo, dependencies.MetadataProvider, cancellationToken);
        };

        rgTextBox.TextChanged += (s, e) => UpdateNames();

        _provisionBtn.Click += (s, e) =>
        {
            _provisionBtn.IsEnabled = false;
            _ = ProvisionAsync(options, rgTextBox, fnTextBox, stTextBox, planTextBox,
                containerTextBox, tableTextBox, packagePathTextBox,
                subscriptionCombo, locationCombo, dependencies, tcs, progress, cancellationToken);
        };

        cancelBtn.Click += (s, e) =>
        {
            tcs.TrySetResult(null);
            App.HideContent();
        };

        UpdateNames();
        return root;

        void UpdateNames()
        {
            var rg = rgTextBox.Text.Trim();
            fnTextBox.Text = $"clipfn{rg}";
            stTextBox.Text = $"clipst{rg.GetHashCode():x8}";
            planTextBox.Text = $"plan{rg}";
        }
    }

    private static async Task ReloadLocationsAsync(
        ComboBox subscriptionCombo,
        ComboBox locationCombo,
        IAzureCliMetadataProvider metadataProvider,
        CancellationToken cancellationToken)
    {
        try
        {
            var selectedIndex = subscriptionCombo.SelectedIndex;
            if (selectedIndex >= 0)
            {
                var subId = (subscriptionCombo.SelectedItem as AzureSubscriptionInfo)?.Id ?? "";
                var locations = await metadataProvider.GetLocationsAsync(subId, cancellationToken);
                locationCombo.ItemsSource = locations.Select(l => l.Name).ToList();
                if (locations.Count > 0)
                {
                    locationCombo.SelectedIndex = 0;
                }
            }
        }
        catch (Exception ex)
        {
            if (_errorLabel != null)
                _errorLabel.Text = ex.Message;
        }
    }

    private static async Task ProvisionAsync(
        ProvisionBackendDialogOptions options,
        TextBox rgTextBox, TextBox fnTextBox, TextBox stTextBox, TextBox planTextBox,
        TextBox containerTextBox, TextBox tableTextBox, TextBox packagePathTextBox,
        ComboBox subscriptionCombo, ComboBox locationCombo,
        ProvisionBackendDialogDependencies dependencies,
        TaskCompletionSource<BackendProvisioningResult?> tcs,
        IProgress<ProvisioningProgressUpdate> progress,
        CancellationToken cancellationToken)
    {
        try
        {
            var subscriptionId = (subscriptionCombo.SelectedItem as AzureSubscriptionInfo)?.Id;
            var location = locationCombo.SelectedItem?.ToString();

            var deployment = new FunctionsDeploymentOptions
            {
                FunctionAppName = fnTextBox.Text.Trim(),
                ResourceGroup = rgTextBox.Text.Trim(),
                SubscriptionId = subscriptionId,
                PackagePath = packagePathTextBox.Text.Trim(),
                Location = location,
                StorageAccountName = stTextBox.Text.Trim(),
                PlanName = planTextBox.Text.Trim(),
                PayloadContainer = containerTextBox.Text.Trim(),
                MetadataTable = tableTextBox.Text.Trim(),
            };

            var request = new BackendProvisioningRequest(options.OwnerId, deployment);
            var result = await dependencies.ProvisioningService.ProvisionAsync(request, progress, cancellationToken);

            if (result.Succeeded)
            {
                tcs.TrySetResult(result);
                App.HideContent();
            }
            else
            {
                if (_errorLabel != null)
                    _errorLabel.Text = result.ErrorMessage ?? "Provisioning failed.";
                if (_provisionBtn != null)
                    _provisionBtn.IsEnabled = true;
            }
        }
        catch (OperationCanceledException)
        {
            tcs.TrySetResult(null);
        }
        catch (Exception ex)
        {
            if (_errorLabel != null)
                _errorLabel.Text = $"Error: {ex.Message}";
            if (_provisionBtn != null)
                _provisionBtn.IsEnabled = true;
        }
    }

    private static List<AzureSubscriptionInfo> OptionsToSubscriptionList(ProvisionBackendDialogOptions options)
    {
        return options.Subscriptions.ToList();
    }

    private static List<string> OptionsToLocationList(ProvisionBackendDialogOptions options)
    {
        return options.Locations.Select(l => l.Name).ToList();
    }
}
