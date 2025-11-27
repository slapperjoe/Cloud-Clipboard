using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CloudClipboard.Agent.Windows.Configuration;
using CloudClipboard.Agent.Windows.Options;
using CloudClipboard.Agent.Windows.UI;
using CloudClipboard.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;


namespace CloudClipboard.Agent.Windows.Services;

public sealed class FirstRunConfigurationService : BackgroundService
{
    private readonly ILogger<FirstRunConfigurationService> _logger;
    private readonly IAgentSettingsStore _settingsStore;
    private readonly ICloudClipboardClient _client;
    private readonly IAzureCliAuthenticator _authenticator;
    private readonly IAzureCliMetadataProvider _metadataProvider;
    private readonly IBackendProvisioningService _provisioningService;
    private readonly IOptionsMonitor<AgentOptions> _options;
    private readonly IAppIconProvider _iconProvider;

    private sealed record ProvisioningMetadata(
        IReadOnlyList<AzureSubscriptionInfo> Subscriptions,
        IReadOnlyList<AzureLocationInfo> Locations,
        string? SubscriptionId);

    public FirstRunConfigurationService(
        ILogger<FirstRunConfigurationService> logger,
        IAgentSettingsStore settingsStore,
        ICloudClipboardClient client,
        IAzureCliAuthenticator authenticator,
        IAzureCliMetadataProvider metadataProvider,
        IBackendProvisioningService provisioningService,
        IOptionsMonitor<AgentOptions> options,
        IAppIconProvider iconProvider)
    {
        _logger = logger;
        _settingsStore = settingsStore;
        _client = client;
        _authenticator = authenticator;
        _metadataProvider = metadataProvider;
        _provisioningService = provisioningService;
        _options = options;
        _iconProvider = iconProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await BootstrapAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "First-run configuration bootstrap failed.");
        }
    }

    private async Task BootstrapAsync(CancellationToken cancellationToken)
    {
        var settingsPath = AgentSettingsPathProvider.GetSettingsPath();
        if (File.Exists(settingsPath))
        {
            _logger.LogDebug("Agent settings already exist at {SettingsPath}; bootstrap skipped.", settingsPath);
            return;
        }

        var principal = await _authenticator.EnsureLoginAsync(cancellationToken).ConfigureAwait(false);
        if (principal is null)
        {
            _logger.LogWarning("Azure login was not completed; initial configuration will not be created.");
            return;
        }

        var ownerId = OwnerIdGenerator.CreateOwnerIdFromLogin(principal.LoginName);
        var remoteOptions = await TryLoadRemoteAsync(ownerId, cancellationToken).ConfigureAwait(false);
        if (remoteOptions is null)
        {
            _logger.LogWarning("No remote configuration exists for owner {OwnerId}. Prompting for provisioning options...", ownerId);
            var provisioningOptions = await PromptForProvisioningOptionsAsync(ownerId, principal.SubscriptionId, cancellationToken).ConfigureAwait(false);
            if (provisioningOptions is null)
            {
                _logger.LogInformation("Provisioning cancelled or dialog dismissed. Agent settings will not be created.");
                return;
            }

            if (string.IsNullOrWhiteSpace(provisioningOptions.SubscriptionId))
            {
                provisioningOptions.SubscriptionId = principal.SubscriptionId ?? provisioningOptions.SubscriptionId;
            }

            var provisioningRequest = new BackendProvisioningRequest(ownerId, provisioningOptions);
            var provisioningResult = await RunProvisioningWithProgressAsync(provisioningRequest, cancellationToken).ConfigureAwait(false);
            if (!provisioningResult.Succeeded || provisioningResult.RemoteOptions is null)
            {
                _logger.LogWarning("Automated provisioning failed for owner {OwnerId}: {Reason}", ownerId, provisioningResult.ErrorMessage ?? "unknown error");
                return;
            }

            remoteOptions = provisioningResult.RemoteOptions;
        }

        remoteOptions.OwnerId = ownerId;
        AgentOptionsJson.Normalize(remoteOptions);
        _settingsStore.Save(remoteOptions, BackupScope.Sync);
        _logger.LogInformation("Imported remote configuration for owner {OwnerId} and created agentsettings.json.", ownerId);
    }

    private async Task<AgentOptions?> TryLoadRemoteAsync(string ownerId, CancellationToken cancellationToken)
    {
        try
        {
            OwnerConfiguration? remote = await _client.GetOwnerConfigurationAsync(ownerId, cancellationToken).ConfigureAwait(false);
            if (remote is null || string.IsNullOrWhiteSpace(remote.ConfigurationJson))
            {
                _logger.LogInformation("No remote configuration found for {OwnerId} during bootstrap.", ownerId);
                return null;
            }

            var parsed = AgentOptionsJson.Deserialize(remote.ConfigurationJson);
            if (parsed is null)
            {
                _logger.LogWarning("Remote configuration for {OwnerId} is invalid JSON during bootstrap.", ownerId);
                return null;
            }

            AgentOptionsJson.Normalize(parsed);
            parsed.OwnerId = ownerId;
            _logger.LogInformation("Downloaded remote configuration for {OwnerId} during bootstrap.", ownerId);
            return parsed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download remote configuration for {OwnerId}.", ownerId);
            return null;
        }
    }

    private async Task<FunctionsDeploymentOptions?> PromptForProvisioningOptionsAsync(string ownerId, string? subscriptionId, CancellationToken cancellationToken)
    {
        var baseline = AgentOptionsJson.Clone(_options.CurrentValue);
        AgentOptionsJson.Normalize(baseline);
        baseline.FunctionsDeployment ??= FunctionsDeploymentOptions.CreateDefault();
        var defaults = CloneDeploymentOptions(baseline.FunctionsDeployment);
        var initialSubscriptionId = subscriptionId;
        if (string.IsNullOrWhiteSpace(initialSubscriptionId))
        {
            initialSubscriptionId = defaults.SubscriptionId;
        }

        IReadOnlyList<AzureSubscriptionInfo> subscriptions = Array.Empty<AzureSubscriptionInfo>();
        IReadOnlyList<AzureLocationInfo> locations = Array.Empty<AzureLocationInfo>();
        var indicatorIcon = _iconProvider.GetIcon(32) ?? SystemIcons.Application;
        try
        {
            var metadata = await MetadataLoadingDialog.RunAsync(async (dialog, token) =>
            {
                dialog.UpdateStatus("Loading Azure subscriptions via Azure CLI...");
                var loadedSubscriptions = await _metadataProvider.GetSubscriptionsAsync(token).ConfigureAwait(true);
                var resolvedSubscriptionId = initialSubscriptionId;
                if (string.IsNullOrWhiteSpace(resolvedSubscriptionId) && loadedSubscriptions.Count > 0)
                {
                    resolvedSubscriptionId = loadedSubscriptions.FirstOrDefault(s => s.IsDefault)?.Id ?? loadedSubscriptions[0].Id;
                }

                dialog.UpdateStatus("Loading Azure regions for provisioning...");
                IReadOnlyList<AzureLocationInfo> loadedLocations = Array.Empty<AzureLocationInfo>();
                if (!string.IsNullOrWhiteSpace(resolvedSubscriptionId))
                {
                    loadedLocations = await _metadataProvider.GetLocationsAsync(resolvedSubscriptionId, token).ConfigureAwait(true);
                }

                return new ProvisioningMetadata(loadedSubscriptions, loadedLocations, resolvedSubscriptionId);
            }, indicatorIcon, cancellationToken).ConfigureAwait(false);

            subscriptions = metadata.Subscriptions;
            locations = metadata.Locations;
            initialSubscriptionId = metadata.SubscriptionId;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to load Azure metadata for provisioning dialog.");
        }

        try
        {
            return await ProvisionBackendDialog.ShowAsync(ownerId, initialSubscriptionId, defaults, subscriptions, locations, _metadataProvider, _iconProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<BackendProvisioningResult> RunProvisioningWithProgressAsync(BackendProvisioningRequest request, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<BackendProvisioningResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                using var dialog = new ProvisioningProgressDialog();
                dialog.SetIcon(_iconProvider.GetIcon(32));

                var combinedProgress = new Progress<ProvisioningProgressUpdate>(update =>
                {
                    dialog.ApplyProgress(update);
                });

                using var registration = cancellationToken.Register(() =>
                {
                    if (dialog.IsHandleCreated)
                    {
                        dialog.BeginInvoke(new Action(dialog.Close));
                    }
                });

                dialog.Load += async (_, _) =>
                {
                    try
                    {
                        dialog.SetBusyState(true, "Provisioning backend...");
                        var result = await _provisioningService.ProvisionAsync(request, combinedProgress, cancellationToken).ConfigureAwait(true);
                        dialog.MarkComplete(result.Succeeded, result.ErrorMessage);
                        tcs.TrySetResult(result);
                    }
                    catch (Exception ex)
                    {
                        dialog.MarkComplete(false, $"Exception: {ex.Message}");
                        tcs.TrySetException(ex);
                    }
                };

                Application.Run(dialog);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        {
            IsBackground = false,
            Name = "CloudClipboard.ProvisionProgress"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return await tcs.Task.ConfigureAwait(false);
    }

    private static FunctionsDeploymentOptions CloneDeploymentOptions(FunctionsDeploymentOptions source)
    {
        if (source is null)
        {
            return FunctionsDeploymentOptions.CreateDefault();
        }

        return new FunctionsDeploymentOptions
        {
            FunctionAppName = source.FunctionAppName,
            ResourceGroup = source.ResourceGroup,
            SubscriptionId = source.SubscriptionId,
            PackagePath = source.PackagePath,
            Location = source.Location,
            StorageAccountName = source.StorageAccountName,
            PlanName = source.PlanName,
            PayloadContainer = source.PayloadContainer,
            MetadataTable = source.MetadataTable,
            LastDeployedUtc = source.LastDeployedUtc,
            LastPackageHash = source.LastPackageHash
        };
    }
}
