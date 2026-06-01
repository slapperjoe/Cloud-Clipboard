using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CloudClipboard.Agent.Configuration;
using CloudClipboard.Agent.Options;
using CloudClipboard.Agent.UI;
using CloudClipboard.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

#if WINDOWS
using System.Drawing;
using System.Windows.Forms;

namespace CloudClipboard.Agent.Services;

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
            _logger.LogWarning("No remote configuration exists for owner {OwnerId}. Starting provisioning flow...", ownerId);
            var provisioningResult = await RunProvisioningDialogAsync(ownerId, principal.SubscriptionId, cancellationToken).ConfigureAwait(false);
            if (provisioningResult is null)
            {
                _logger.LogInformation("Provisioning cancelled or dialog dismissed. Agent settings will not be created.");
                return;
            }

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

    private async Task<BackendProvisioningResult?> RunProvisioningDialogAsync(string ownerId, string? subscriptionId, CancellationToken cancellationToken)
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
            var dialogOptions = new ProvisionBackendDialogOptions(ownerId, initialSubscriptionId, defaults, subscriptions, locations);
            var dialogDependencies = new ProvisionBackendDialogDependencies(_metadataProvider, _provisioningService, _iconProvider);
            return await ProvisionBackendDialog.ShowAsync(dialogOptions, dialogDependencies, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
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
#else
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using CloudClipboard.Agent.Configuration;
    using CloudClipboard.Agent.Options;
    using CloudClipboard.Core.Models;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;

    namespace CloudClipboard.Agent.Services;

    public sealed class FirstRunConfigurationService : BackgroundService
    {
        private readonly ILogger<FirstRunConfigurationService> _logger;
        private readonly IAgentSettingsStore _settingsStore;
        private readonly ICloudClipboardClient _client;
        private readonly IAzureCliAuthenticator _authenticator;
        private readonly IAzureCliMetadataProvider _metadataProvider;
        private readonly IBackendProvisioningService _provisioningService;
        private readonly IOptionsMonitor<AgentOptions> _options;

        public FirstRunConfigurationService(
            ILogger<FirstRunConfigurationService> logger,
            IAgentSettingsStore settingsStore,
            ICloudClipboardClient client,
            IAzureCliAuthenticator authenticator,
            IAzureCliMetadataProvider metadataProvider,
            IBackendProvisioningService provisioningService,
            IOptionsMonitor<AgentOptions> options)
        {
            _logger = logger;
            _settingsStore = settingsStore;
            _client = client;
            _authenticator = authenticator;
            _metadataProvider = metadataProvider;
            _provisioningService = provisioningService;
            _options = options;
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
                _logger.LogWarning("No remote configuration exists for owner {OwnerId}. Starting provisioning flow...", ownerId);
                var provisioningResult = await RunProvisioningConsoleAsync(ownerId, principal.SubscriptionId, cancellationToken).ConfigureAwait(false);
                if (provisioningResult is null)
                {
                    _logger.LogInformation("Provisioning cancelled. Agent settings will not be created.");
                    return;
                }

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
            _logger.LogInformation("Created agentsettings.json for owner {OwnerId}.", ownerId);
        }

        private async Task<AgentOptions?> TryLoadRemoteAsync(string ownerId, CancellationToken cancellationToken)
        {
            try
            {
                var remote = await _client.GetOwnerConfigurationAsync(ownerId, cancellationToken).ConfigureAwait(false);
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

        private async Task<BackendProvisioningResult?> RunProvisioningConsoleAsync(string ownerId, string? subscriptionId, CancellationToken cancellationToken)
        {
            Console.WriteLine("=== Cloud Clipboard - First Run Setup ===");
            Console.WriteLine();
            Console.WriteLine("Welcome to Cloud Clipboard! Let's get your environment set up.");
            Console.WriteLine();

            // Get subscriptions
            IReadOnlyList<AzureSubscriptionInfo> subscriptions = Array.Empty<AzureSubscriptionInfo>();
            try
            {
                Console.Write("Loading Azure subscriptions... ");
                subscriptions = await _metadataProvider.GetSubscriptionsAsync(cancellationToken).ConfigureAwait(false);
                if (subscriptions.Count == 0)
                {
                    Console.WriteLine("No subscriptions found. Please ensure you have access to at least one Azure subscription.");
                    return null;
                }
                Console.WriteLine($"Found {subscriptions.Count} subscription(s).");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load subscriptions: {ex.Message}");
                return null;
            }

            // Prompt for subscription selection
            string resolvedSubscriptionId = subscriptionId;
            if (string.IsNullOrWhiteSpace(resolvedSubscriptionId))
            {
                Console.WriteLine();
                Console.WriteLine("Select an Azure subscription:");
                for (int i = 0; i < subscriptions.Count; i++)
                {
                    var sub = subscriptions[i];
                    string marker = sub.IsDefault ? " (default)" : "";
                    Console.WriteLine($"  {i + 1}. {sub.Name}{marker} [{sub.Id}]");
                }
                Console.Write("\nSubscription number: ");
                var input = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(input))
                {
                    Console.WriteLine("No selection made. Cancelling setup.");
                    return null;
                }
                int index;
                if (int.TryParse(input, out index) && index > 0 && index <= subscriptions.Count)
                {
                    resolvedSubscriptionId = subscriptions[index - 1].Id;
                }
                else
                {
                    Console.WriteLine("Invalid selection. Cancelling setup.");
                    return null;
                }
            }

            // Get locations
            IReadOnlyList<AzureLocationInfo> locations = Array.Empty<AzureLocationInfo>();
            try
            {
                Console.Write("Loading Azure locations... ");
                locations = await _metadataProvider.GetLocationsAsync(resolvedSubscriptionId, cancellationToken).ConfigureAwait(false);
                Console.WriteLine("Done.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load locations: {ex.Message}");
                return null;
            }

            // Collect provisioning inputs
            Console.WriteLine();
            Console.WriteLine("Enter details for Azure resource provisioning:");
            Console.Write("Resource group name [cloud-clipboard]: ");
            string resourceGroup = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(resourceGroup))
                resourceGroup = "cloud-clipboard";

            // Select location
            string resolvedLocation = "eastus"; // default
            if (locations.Count > 0)
            {
                Console.WriteLine("\nSelect an Azure location:");
                for (int i = 0; i < locations.Count; i++)
                {
                    Console.WriteLine($"  {i + 1}. {locations[i].DisplayName} ({locations[i].Name})");
                }
                Console.Write("\nLocation number: ");
                var locInput = Console.ReadLine();
                int locIndex;
                if (int.TryParse(locInput, out locIndex) && locIndex > 0 && locIndex <= locations.Count)
                {
                    resolvedLocation = locations[locIndex - 1].Name;
                }
                else
                {
                    Console.WriteLine("Invalid selection. Cancelling setup.");
                    return null;
                }
            }

            // Build deployment options
            var deployment = new FunctionsDeploymentOptions
            {
                SubscriptionId = resolvedSubscriptionId,
                ResourceGroup = resourceGroup,
                FunctionAppName = resourceGroup + "-func",
                StorageAccountName = resourceGroup.Replace("-", "").ToLower() + "sa",
                Location = resolvedLocation,
                PackagePath = Path.GetFullPath(Path.Combine("..", "artifacts", "CloudClipboard.Functions.zip")),
                PlanName = resourceGroup + "-plan",
                PayloadContainer = "payloads",
                MetadataTable = "ClipboardMetadata"
            };

            var request = new BackendProvisioningRequest(ownerId, deployment);
            Console.WriteLine();
            Console.WriteLine($"Provisioning Azure resources in subscription {resolvedSubscriptionId}...");
            Console.WriteLine("This may take a few minutes. Please wait...");
            var result = await _provisioningService.ProvisionAsync(request, null, cancellationToken).ConfigureAwait(false);

            if (result.Succeeded && result.RemoteOptions != null)
            {
                Console.WriteLine("Provisioning completed successfully!");
                Console.WriteLine();
                Console.WriteLine("Your Cloud Clipboard backend has been set up.");
                Console.WriteLine("Starting the agent...");
            }
            else
            {
                Console.WriteLine($"Provisioning failed: {result.ErrorMessage ?? "unknown error"}");
            }

            return result;
        }
    }
#endif
