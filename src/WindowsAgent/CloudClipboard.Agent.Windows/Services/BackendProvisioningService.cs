using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using CloudClipboard.Agent.Windows.Options;
using Microsoft.Extensions.Logging;

namespace CloudClipboard.Agent.Windows.Services;

public interface IBackendProvisioningService
{
    Task<BackendProvisioningResult> ProvisionAsync(BackendProvisioningRequest request, IProgress<ProvisioningProgressUpdate>? progress, CancellationToken cancellationToken);
}

public sealed record BackendProvisioningRequest(string OwnerId, FunctionsDeploymentOptions DeploymentOptions);

public sealed record BackendProvisioningResult(bool Succeeded, AgentOptions? RemoteOptions, string? ErrorMessage);

public sealed class BackendProvisioningService : IBackendProvisioningService
{
    private readonly ILogger<BackendProvisioningService> _logger;
    private readonly IFunctionsDeploymentService _deploymentService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISetupActivityMonitor _activityMonitor;

    private const double CliCheckPercent = 5;
    private const double PackageValidatedPercent = 10;
    private const double LoginVerifiedPercent = 20;
    private const double ResourceGroupPercent = 35;
    private const double AppInsightsPercent = 40;
    private const double WebPubSubPercent = 42;
    private const double StorageAccountPercent = 45;
    private const double ConnectionStringPercent = 50;
    private const double FunctionAppPercent = 65;
    private const double SettingsPercent = 75;
    private const double DeploymentStartedPercent = 85;
    private const double DeploymentCompletePercent = 90;
    private const double KeysPercent = 95;
    private const double ConfigurationSeedPercent = 100;

    private static readonly JsonSerializerOptions OwnerConfigSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private sealed record OwnerConfigurationPayload(string ConfigurationJson);
    private sealed record AppInsightsDetails(string? ConnectionString, string? InstrumentationKey);
    private sealed record FunctionAppConfigurationInputs(
        string StorageConnectionString,
        string? AppInsightsConnectionString,
        string? AppInsightsInstrumentationKey,
        string? WebPubSubConnectionString);

    public BackendProvisioningService(
        ILogger<BackendProvisioningService> logger,
        IFunctionsDeploymentService deploymentService,
        IHttpClientFactory httpClientFactory,
        ISetupActivityMonitor activityMonitor)
    {
        _logger = logger;
        _deploymentService = deploymentService;
        _httpClientFactory = httpClientFactory;
        _activityMonitor = activityMonitor;
    }

    private sealed record ProvisioningContext(string OwnerId, string AzPath, FunctionsDeploymentOptions Deployment, string PackagePath);

    public async Task<BackendProvisioningResult> ProvisionAsync(BackendProvisioningRequest request, IProgress<ProvisioningProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        using var activity = _activityMonitor.BeginActivity("Provisioning");
        var preparation = await PrepareContextAsync(request, progress, cancellationToken).ConfigureAwait(false);
        if (preparation.Failure is not null)
        {
            return preparation.Failure;
        }

        return await ExecuteProvisioningAsync(preparation.Context!, progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(ProvisioningContext? Context, BackendProvisioningResult? Failure)> PrepareContextAsync(
        BackendProvisioningRequest request,
        IProgress<ProvisioningProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.OwnerId))
        {
            return (null, new(false, null, "OwnerId is required for provisioning."));
        }

        ReportProgress(progress, "Checking Azure CLI availability...", CliCheckPercent);
        if (!AzureCliLocator.TryResolveExecutable(out var azPath, out var azError))
        {
            return (null, new(false, null, azError ?? "Azure CLI (az) is required."));
        }

        var deployment = CloneDeployment(request.DeploymentOptions);
        AgentOptionsJson.Normalize(new AgentOptions { FunctionsDeployment = deployment });
        ReportProgress(progress, "Validating deployment package...", PackageValidatedPercent);
        var packagePath = FunctionsDeploymentUtilities.ResolvePackagePath(deployment.PackagePath);
        if (!File.Exists(packagePath))
        {
            return (null, new(false, null, $"Deployment package not found at {packagePath}."));
        }

        ReportProgress(progress, "Ensuring Azure CLI login and subscription access...", LoginVerifiedPercent);
        var resolvedSubscriptionId = await EnsureLoginAsync(azPath, deployment.SubscriptionId, progress, cancellationToken).ConfigureAwait(false);
        var effectiveSubscriptionId = string.IsNullOrWhiteSpace(deployment.SubscriptionId) ? resolvedSubscriptionId : deployment.SubscriptionId;
        if (string.IsNullOrWhiteSpace(effectiveSubscriptionId))
        {
            return (null, new(false, null, "Azure CLI login failed or subscription could not be resolved."));
        }

        deployment.SubscriptionId = effectiveSubscriptionId;
        await SetSubscriptionAsync(azPath, deployment.SubscriptionId, progress, cancellationToken).ConfigureAwait(false);

        var ownerId = request.OwnerId;
        deployment.FunctionAppName = string.IsNullOrWhiteSpace(deployment.FunctionAppName)
            ? ProvisioningNameGenerator.CreateFunctionAppName(ownerId)
            : deployment.FunctionAppName;
        deployment.ResourceGroup = string.IsNullOrWhiteSpace(deployment.ResourceGroup)
            ? ProvisioningNameGenerator.CreateResourceGroupName(ownerId)
            : deployment.ResourceGroup;
        deployment.StorageAccountName = string.IsNullOrWhiteSpace(deployment.StorageAccountName)
            ? ProvisioningNameGenerator.CreateStorageAccountName(ownerId)
            : deployment.StorageAccountName.ToLowerInvariant();
        deployment.PlanName = string.IsNullOrWhiteSpace(deployment.PlanName)
            ? ProvisioningNameGenerator.CreatePlanName(ownerId)
            : deployment.PlanName;
        deployment.AppInsightsName = string.IsNullOrWhiteSpace(deployment.AppInsightsName)
            ? CreateAppInsightsName(ownerId)
            : deployment.AppInsightsName;
            deployment.WebPubSubName = string.IsNullOrWhiteSpace(deployment.WebPubSubName)
                ? CreateWebPubSubName(ownerId)
                : deployment.WebPubSubName;
        deployment.Location = string.IsNullOrWhiteSpace(deployment.Location) ? "eastus" : deployment.Location;

        var context = new ProvisioningContext(ownerId, azPath, deployment, packagePath);
        return (context, null);
    }

    private async Task<BackendProvisioningResult> ExecuteProvisioningAsync(
        ProvisioningContext context,
        IProgress<ProvisioningProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        var deployment = context.Deployment;
        var azPath = context.AzPath;
        var ownerId = context.OwnerId;
        var packagePath = context.PackagePath;

        try
        {
            ReportProgress(progress, $"Creating resource group '{deployment.ResourceGroup}' in {deployment.Location}...", ResourceGroupPercent);
            await EnsureResourceGroupAsync(azPath, deployment.ResourceGroup, deployment.Location, progress, cancellationToken).ConfigureAwait(false);
            await EnsureAzExtensionAsync(azPath, "application-insights", progress, cancellationToken).ConfigureAwait(false);
            ReportProgress(progress, $"Ensuring Application Insights '{deployment.AppInsightsName}'...", AppInsightsPercent);
            var appInsightsDetails = await EnsureApplicationInsightsAsync(azPath, deployment.ResourceGroup, deployment.AppInsightsName, deployment.Location, progress, cancellationToken).ConfigureAwait(false);
            await EnsureAzExtensionAsync(azPath, "webpubsub", progress, cancellationToken).ConfigureAwait(false);
            ReportProgress(progress, $"Ensuring Web PubSub service '{deployment.WebPubSubName}'...", WebPubSubPercent);
            var webPubSubConnectionString = await EnsureWebPubSubAsync(azPath, deployment.ResourceGroup, deployment.WebPubSubName, deployment.Location, progress, cancellationToken).ConfigureAwait(false);
            ReportProgress(progress, $"Creating storage account '{deployment.StorageAccountName}'...", StorageAccountPercent);
            await EnsureStorageAccountAsync(azPath, deployment.ResourceGroup, deployment.StorageAccountName, deployment.Location, progress, cancellationToken).ConfigureAwait(false);
            ReportProgress(progress, "Retrieving storage connection string...", ConnectionStringPercent);
            var connectionString = await GetStorageConnectionStringAsync(azPath, deployment.ResourceGroup, deployment.StorageAccountName, progress, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return new(false, null, "Failed to obtain storage account connection string.");
            }

            ReportProgress(progress, $"Creating Function App '{deployment.FunctionAppName}' (Consumption plan)...", FunctionAppPercent);
            await EnsureFunctionAppAsync(azPath, deployment, progress, cancellationToken).ConfigureAwait(false);
            ReportProgress(progress, "Configuring Function App settings...", SettingsPercent);
            var appConfig = new FunctionAppConfigurationInputs(
                connectionString,
                appInsightsDetails?.ConnectionString,
                appInsightsDetails?.InstrumentationKey,
                webPubSubConnectionString);
            await ConfigureFunctionAppAsync(azPath, deployment, appConfig, progress, cancellationToken).ConfigureAwait(false);

            var deployRequest = new FunctionsDeploymentRequest(
                deployment.FunctionAppName,
                deployment.ResourceGroup,
                deployment.SubscriptionId,
                packagePath);

            ReportProgress(progress, "Deploying Functions package (this may take a few minutes)...", DeploymentStartedPercent);
            var deployProgress = new Progress<string>(line =>
            {
                _logger.LogInformation("Deploy: {Line}", line);
                ReportProgress(progress, line, DeploymentStartedPercent, verbose: true);
            });
            var deployResult = await _deploymentService.DeployAsync(deployRequest, deployProgress, cancellationToken).ConfigureAwait(false);
            if (!deployResult.Succeeded)
            {
                return new(false, null, deployResult.ErrorMessage ?? "Functions deployment failed.");
            }

            ReportProgress(progress, "Deployment completed successfully.", DeploymentCompletePercent);

            ReportProgress(progress, "Retrieving function keys...", KeysPercent);
            var functionKey = await GetFunctionKeyAsync(azPath, deployment.ResourceGroup, deployment.FunctionAppName, progress, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(functionKey))
            {
                return new(false, null, "Unable to retrieve the default function key.");
            }

            var apiBaseUrl = $"https://{deployment.FunctionAppName}.azurewebsites.net/api";
            var remoteOptions = new AgentOptions
            {
                OwnerId = ownerId,
                ApiBaseUrl = apiBaseUrl,
                FunctionKey = functionKey,
                FunctionsDeployment = deployment
            };
            AgentOptionsJson.Normalize(remoteOptions);

            var serialized = AgentOptionsJson.Serialize(remoteOptions);
            ReportProgress(progress, "Seeding owner configuration in Functions backend...", ConfigurationSeedPercent);
            var seedSucceeded = await SeedRemoteConfigurationAsync(apiBaseUrl, functionKey, ownerId, serialized, progress, cancellationToken).ConfigureAwait(false);
            if (!seedSucceeded)
            {
                return new(false, null, "Failed to seed owner configuration in the Functions backend.");
            }

            ReportProgress(progress, $"✓ Provisioning completed! Function App: {deployment.FunctionAppName}", ConfigurationSeedPercent);
            _logger.LogInformation("Provisioned backend for owner {OwnerId} using Function App {FunctionApp}.", ownerId, deployment.FunctionAppName);
            return new(true, remoteOptions, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Automated backend provisioning failed.");
            return new(false, null, ex.Message);
        }
    }

    private static FunctionsDeploymentOptions CloneDeployment(FunctionsDeploymentOptions? source)
    {
        var clone = new FunctionsDeploymentOptions();
        if (source is null)
        {
            return clone;
        }

        clone.FunctionAppName = source.FunctionAppName;
        clone.ResourceGroup = source.ResourceGroup;
        clone.SubscriptionId = source.SubscriptionId;
        clone.PackagePath = source.PackagePath;
        clone.Location = source.Location;
        clone.StorageAccountName = source.StorageAccountName;
        clone.PlanName = source.PlanName;
        clone.WebPubSubName = source.WebPubSubName;
        clone.AppInsightsName = source.AppInsightsName;
        clone.PayloadContainer = source.PayloadContainer;
        clone.MetadataTable = source.MetadataTable;
        clone.LastPackageHash = source.LastPackageHash;
        clone.LastDeployedUtc = source.LastDeployedUtc;
        return clone;
    }

    private async Task<string?> EnsureLoginAsync(string azPath, string subscriptionId, IProgress<ProvisioningProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        var streamLogger = CreateStreamLogger(progress);
        var accountArgs = BuildSubscriptionAwareArgs("account show", subscriptionId);
        var accountResult = await RunAzAsync(azPath, accountArgs, cancellationToken, streamLogger: streamLogger).ConfigureAwait(false);
        if (accountResult.ExitCode != 0)
        {
            _logger.LogInformation("Azure CLI login required. Starting device login flow...");
            var loginResult = await RunAzAsync(azPath, "login --use-device-code --output none", cancellationToken, streamLogger: streamLogger).ConfigureAwait(false);
            if (loginResult.ExitCode != 0)
            {
                return null;
            }

            accountResult = await RunAzAsync(azPath, accountArgs, cancellationToken, streamLogger: streamLogger).ConfigureAwait(false);
            if (accountResult.ExitCode != 0)
            {
                return null;
            }
        }

        if (!string.IsNullOrWhiteSpace(subscriptionId))
        {
            return subscriptionId;
        }

        if (string.IsNullOrWhiteSpace(accountResult.StandardOutput))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(accountResult.StandardOutput);
            var root = document.RootElement;
            if (root.TryGetProperty("id", out var idProperty))
            {
                return idProperty.GetString();
            }
        }
        catch (JsonException)
        {
            // ignore and fall back to null
        }

        return null;
    }

    private async Task EnsureResourceGroupAsync(string azPath, string resourceGroup, string location, IProgress<ProvisioningProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        var args = $"group create --name \"{resourceGroup}\" --location \"{location}\" --only-show-errors --output none";
        await RunAzAsync(azPath, args, cancellationToken, streamLogger: null).ConfigureAwait(false);
        ReportProgress(progress, $"Resource group '{resourceGroup}' is ready.");
    }

    private async Task<AppInsightsDetails?> EnsureApplicationInsightsAsync(
        string azPath,
        string resourceGroup,
        string appInsightsName,
        string location,
        IProgress<ProvisioningProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(appInsightsName))
        {
            return null;
        }

        var showArgs = $"monitor app-insights component show --app \"{appInsightsName}\" --resource-group \"{resourceGroup}\" --only-show-errors --output json";
        var showResult = await RunAzAsync(azPath, showArgs, cancellationToken, streamLogger: null).ConfigureAwait(false);
        if (showResult.ExitCode == 0 && TryParseAppInsightsDetails(showResult.StandardOutput, out var existing) && existing is not null)
        {
            ReportProgress(progress, $"Application Insights '{appInsightsName}' already exists in resource group.", verbose: true);
            return existing;
        }

        var createArgs = $"monitor app-insights component create --app \"{appInsightsName}\" --location \"{location}\" --resource-group \"{resourceGroup}\" --application-type web --kind web --only-show-errors --output json";
        var createResult = await RunAzAsync(azPath, createArgs, cancellationToken, streamLogger: null).ConfigureAwait(false);
        if (createResult.ExitCode != 0)
        {
            var errorMessage = !string.IsNullOrWhiteSpace(createResult.StandardError)
                ? createResult.StandardError.Trim()
                : "Failed to create Application Insights resource.";
            ReportProgress(progress, $"ERROR: {errorMessage}");
            throw new InvalidOperationException(errorMessage);
        }

        if (!TryParseAppInsightsDetails(createResult.StandardOutput, out var created) || created is null)
        {
            throw new InvalidOperationException("Unable to retrieve Application Insights connection details after creation.");
        }

        ReportProgress(progress, $"Application Insights '{appInsightsName}' created.", verbose: true);
        return created;
    }

    private async Task<string?> EnsureWebPubSubAsync(
        string azPath,
        string resourceGroup,
        string serviceName,
        string location,
        IProgress<ProvisioningProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return null;
        }

        var showArgs = $"webpubsub show --name \"{serviceName}\" --resource-group \"{resourceGroup}\" --only-show-errors --output none";
        var showResult = await RunAzAsync(azPath, showArgs, cancellationToken, streamLogger: null).ConfigureAwait(false);
        if (showResult.ExitCode != 0)
        {
            // Newer CLI versions reject --public-network-access here; default behavior already enables it
            var createArgs = $"webpubsub create --name \"{serviceName}\" --resource-group \"{resourceGroup}\" --location \"{location}\" --sku Standard_S1 --unit-count 1 --only-show-errors --output none";
            var createResult = await RunAzAsync(azPath, createArgs, cancellationToken, streamLogger: null).ConfigureAwait(false);
            if (createResult.ExitCode != 0)
            {
                var errorMessage = !string.IsNullOrWhiteSpace(createResult.StandardError)
                    ? createResult.StandardError.Trim()
                    : "Failed to create Azure Web PubSub service.";
                ReportProgress(progress, $"ERROR: {errorMessage}");
                throw new InvalidOperationException(errorMessage);
            }

            ReportProgress(progress, $"Web PubSub service '{serviceName}' created.", verbose: true);
        }
        else
        {
            ReportProgress(progress, $"Web PubSub service '{serviceName}' already exists in resource group.", verbose: true);
        }

        return await GetWebPubSubConnectionStringAsync(azPath, resourceGroup, serviceName, progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureAzExtensionAsync(string azPath, string extensionName, IProgress<ProvisioningProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        var showArgs = $"extension show --name {extensionName} --only-show-errors --output none";
        var showResult = await RunAzAsync(azPath, showArgs, cancellationToken, streamLogger: CreateStreamLogger(progress)).ConfigureAwait(false);
        if (showResult.ExitCode == 0)
        {
            return;
        }

        ReportProgress(progress, $"Azure CLI extension '{extensionName}' not found. Installing...", verbose: true);
        var addArgs = $"extension add --name {extensionName} --yes --only-show-errors --output none";
        var addResult = await RunAzAsync(azPath, addArgs, cancellationToken, streamLogger: CreateStreamLogger(progress)).ConfigureAwait(false);
        if (addResult.ExitCode != 0)
        {
            var message = !string.IsNullOrWhiteSpace(addResult.StandardError)
                ? addResult.StandardError.Trim()
                : $"Failed to install Azure CLI extension '{extensionName}'.";
            ReportProgress(progress, $"ERROR: {message}");
            throw new InvalidOperationException(message);
        }

        ReportProgress(progress, $"Azure CLI extension '{extensionName}' installed.", verbose: true);
    }

    private async Task SetSubscriptionAsync(string azPath, string subscriptionId, IProgress<ProvisioningProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(subscriptionId))
        {
            return;
        }

        var args = $"account set --subscription \"{subscriptionId}\" --only-show-errors --output none";
        var result = await RunAzAsync(azPath, args, cancellationToken, streamLogger: null).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Failed to set Azure subscription {subscriptionId}.");
        }

        ReportProgress(progress, $"Using Azure subscription '{subscriptionId}'.", verbose: true);
    }

    private async Task EnsureStorageAccountAsync(string azPath, string resourceGroup, string storageAccount, string location, IProgress<ProvisioningProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        // First check if it already exists in our resource group
        var showArgs = $"storage account show --name {storageAccount} --resource-group \"{resourceGroup}\" --only-show-errors --output none";
        var showResult = await RunAzAsync(azPath, showArgs, cancellationToken, streamLogger: null).ConfigureAwait(false);
        if (showResult.ExitCode == 0)
        {
            ReportProgress(progress, $"Storage account '{storageAccount}' already exists in resource group.");
            return;
        }

        // Check global name availability before attempting to create
        var checkArgs = $"storage account check-name --name {storageAccount} --query nameAvailable -o tsv --only-show-errors";
        var checkResult = await RunAzAsync(azPath, checkArgs, cancellationToken, streamLogger: null).ConfigureAwait(false);
        var trimmedOutput = checkResult.StandardOutput?.Trim();
        if (checkResult.ExitCode == 0 && string.Equals(trimmedOutput, "false", StringComparison.OrdinalIgnoreCase))
        {
            var errorMessage = $"The storage account name '{storageAccount}' is already taken globally. Please choose a different name.";
            ReportProgress(progress, $"ERROR: {errorMessage}");
            throw new InvalidOperationException(errorMessage);
        }
        else if (checkResult.ExitCode == 0 && string.Equals(trimmedOutput, "true", StringComparison.OrdinalIgnoreCase))
        {
            ReportProgress(progress, $"Storage account name '{storageAccount}' is available.", verbose: true);
        }

        var createArgs = $"storage account create --name {storageAccount} --resource-group \"{resourceGroup}\" --location \"{location}\" --sku Standard_LRS --only-show-errors --output none";
        var createResult = await RunAzAsync(azPath, createArgs, cancellationToken, streamLogger: null).ConfigureAwait(false);
        if (createResult.ExitCode != 0)
        {
            var errorMessage = !string.IsNullOrWhiteSpace(createResult.StandardError)
                ? createResult.StandardError.Trim()
                : "Failed to create storage account.";
            ReportProgress(progress, $"ERROR: {errorMessage}");
            throw new InvalidOperationException(errorMessage);
        }

        ReportProgress(progress, $"Storage account '{storageAccount}' created.", verbose: true);
    }

    private async Task<string?> GetStorageConnectionStringAsync(string azPath, string resourceGroup, string storageAccount, IProgress<ProvisioningProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        var args = $"storage account show-connection-string --name {storageAccount} --resource-group \"{resourceGroup}\" --query connectionString -o tsv";
        var result = await RunAzAsync(azPath, args, cancellationToken, streamLogger: null).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            return null;
        }

        var connectionString = result.StandardOutput?.Trim();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        ReportProgress(progress, "Retrieved storage connection string.", verbose: true);
        return connectionString;
    }

    private async Task EnsureFunctionAppAsync(string azPath, FunctionsDeploymentOptions deployment, IProgress<ProvisioningProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        if (await FunctionAppExistsAsync(azPath, deployment.FunctionAppName, deployment.ResourceGroup, cancellationToken).ConfigureAwait(false))
        {
            ReportProgress(progress, $"Function App '{deployment.FunctionAppName}' already exists in resource group.");
            return;
        }

        await EnsureFunctionAppNameAvailableAsync(azPath, deployment.SubscriptionId, deployment.FunctionAppName, progress, cancellationToken).ConfigureAwait(false);
        await CreateFunctionAppAsync(azPath, deployment, progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> FunctionAppExistsAsync(string azPath, string functionAppName, string resourceGroup, CancellationToken cancellationToken)
    {
        var showArgs = $"functionapp show --name \"{functionAppName}\" --resource-group \"{resourceGroup}\" --only-show-errors --output none";
        var showResult = await RunAzAsync(azPath, showArgs, cancellationToken, streamLogger: null).ConfigureAwait(false);
        return showResult.ExitCode == 0;
    }

    private async Task EnsureFunctionAppNameAvailableAsync(string azPath, string? subscriptionId, string functionAppName, IProgress<ProvisioningProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(subscriptionId))
        {
            throw new InvalidOperationException("Subscription Id is required to validate Function App names.");
        }

        var requestUri = $"https://management.azure.com/subscriptions/{subscriptionId}/providers/Microsoft.Web/checkNameAvailability?api-version=2022-03-01";
        var requestBody = JsonSerializer.Serialize(new { name = functionAppName, type = "Microsoft.Web/sites" });
        var tempFile = Path.Combine(Path.GetTempPath(), $"cloudclipboard-checkname-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(tempFile, requestBody, Encoding.UTF8, cancellationToken).ConfigureAwait(false);

        try
        {
            var checkArgs = $"rest --method post --uri \"{requestUri}\" --headers Content-Type=application/json --body \"@{tempFile}\"";
            var checkResult = await RunAzAsync(azPath, checkArgs, cancellationToken, streamLogger: null).ConfigureAwait(false);
            if (checkResult.ExitCode != 0 || string.IsNullOrWhiteSpace(checkResult.StandardOutput))
            {
                var cliError = !string.IsNullOrWhiteSpace(checkResult.StandardError)
                    ? checkResult.StandardError.Trim()
                    : "Function App name availability check failed.";
                ReportProgress(progress, $"ERROR: {cliError}");
                throw new InvalidOperationException(cliError);
            }

            using var doc = JsonDocument.Parse(checkResult.StandardOutput);
            var nameAvailable = doc.RootElement.TryGetProperty("nameAvailable", out var availableProp) && availableProp.GetBoolean();
            if (nameAvailable)
            {
                return;
            }

            var message = doc.RootElement.TryGetProperty("message", out var messageProp) ? messageProp.GetString() : null;
            var reason = doc.RootElement.TryGetProperty("reason", out var reasonProp) ? reasonProp.GetString() : null;
            var errorMessage = !string.IsNullOrWhiteSpace(message)
                ? message
                : $"The function app name '{functionAppName}' is already taken globally.";
            if (!string.IsNullOrWhiteSpace(reason))
            {
                errorMessage = $"{errorMessage} ({reason})";
            }

            ReportProgress(progress, $"ERROR: {errorMessage}");
            throw new InvalidOperationException(errorMessage);
        }
        catch (JsonException ex)
        {
            var message = $"Unable to parse Azure CLI response: {ex.Message}";
            ReportProgress(progress, $"ERROR: {message}");
            throw new InvalidOperationException(message);
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
                // ignore cleanup errors
            }
        }
    }

    private async Task CreateFunctionAppAsync(string azPath, FunctionsDeploymentOptions deployment, IProgress<ProvisioningProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(
            $"functionapp create --name \"{deployment.FunctionAppName}\" --resource-group \"{deployment.ResourceGroup}\" --storage-account {deployment.StorageAccountName} --consumption-plan-location \"{deployment.Location}\" --functions-version 4 --runtime dotnet-isolated --os-type Windows");

        if (!string.IsNullOrWhiteSpace(deployment.AppInsightsName))
        {
            builder.Append($" --app-insights \"{deployment.AppInsightsName}\"");
        }

        builder.Append(" --only-show-errors --output none");
        var createArgs = builder.ToString();
        var createResult = await RunAzAsync(azPath, createArgs, cancellationToken, streamLogger: null).ConfigureAwait(false);
        if (createResult.ExitCode != 0)
        {
            var errorMessage = !string.IsNullOrWhiteSpace(createResult.StandardError)
                ? createResult.StandardError.Trim()
                : "Failed to create Azure Function App.";
            ReportProgress(progress, $"ERROR: {errorMessage}");
            throw new InvalidOperationException(errorMessage);
        }

        ReportProgress(progress, $"Function App '{deployment.FunctionAppName}' created.", verbose: true);
    }

    private async Task ConfigureFunctionAppAsync(
        string azPath,
        FunctionsDeploymentOptions deployment,
        FunctionAppConfigurationInputs inputs,
        IProgress<ProvisioningProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        var payloadContainer = deployment.PayloadContainer;
        var metadataTable = deployment.MetadataTable;
        var settings = new List<string>
        {
            $"\"Storage:BlobConnectionString={EscapeSetting(inputs.StorageConnectionString)}\"",
            $"\"Storage:TableConnectionString={EscapeSetting(inputs.StorageConnectionString)}\"",
            $"\"Storage:PayloadContainer={payloadContainer}\"",
            $"\"Storage:MetadataTable={metadataTable}\""
        };

        if (!string.IsNullOrWhiteSpace(inputs.AppInsightsConnectionString))
        {
            settings.Add($"\"APPLICATIONINSIGHTS_CONNECTION_STRING={EscapeSetting(inputs.AppInsightsConnectionString)}\"");
        }

        if (!string.IsNullOrWhiteSpace(inputs.AppInsightsInstrumentationKey))
        {
            settings.Add($"\"APPINSIGHTS_INSTRUMENTATIONKEY={EscapeSetting(inputs.AppInsightsInstrumentationKey)}\"");
        }

        if (!string.IsNullOrWhiteSpace(inputs.WebPubSubConnectionString))
        {
            settings.Add($"\"Notifications:PubSub:ConnectionString={EscapeSetting(inputs.WebPubSubConnectionString)}\"");
        }

        var settingsArgs = $"functionapp config appsettings set --name \"{deployment.FunctionAppName}\" --resource-group \"{deployment.ResourceGroup}\" --settings {string.Join(' ', settings)} --only-show-errors --output none";
        var display = $"functionapp config appsettings set --name {deployment.FunctionAppName} --resource-group {deployment.ResourceGroup} --settings <redacted>";
        var settingsResult = await RunAzAsync(azPath, settingsArgs, cancellationToken, display, streamLogger: null).ConfigureAwait(false);
        if (settingsResult.ExitCode != 0)
        {
            var errorMessage = !string.IsNullOrWhiteSpace(settingsResult.StandardError)
                ? settingsResult.StandardError.Trim()
                : "Failed to configure Function App settings.";
            ReportProgress(progress, $"ERROR: {errorMessage}");
            throw new InvalidOperationException(errorMessage);
        }

        ReportProgress(progress, "Function App settings updated.", verbose: true);
    }

    private static string EscapeSetting(string value)
        => value.Replace("\"", "\\\"", StringComparison.Ordinal);

    private async Task<string?> GetFunctionKeyAsync(string azPath, string resourceGroup, string functionApp, IProgress<ProvisioningProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        var args = $"functionapp keys list --name \"{functionApp}\" --resource-group \"{resourceGroup}\" --query functionKeys.default -o tsv";
        var result = await RunAzAsync(azPath, args, cancellationToken, streamLogger: null).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            return null;
        }

        var key = result.StandardOutput?.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        ReportProgress(progress, "Retrieved default function key.", verbose: true);
        return key;
    }

    private async Task<bool> SeedRemoteConfigurationAsync(string apiBaseUrl, string functionKey, string ownerId, string configJson, IProgress<ProvisioningProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient("CloudClipboard.Provisioning");
        var endpoint = $"{apiBaseUrl.TrimEnd('/')}/clipboard/owners/{Uri.EscapeDataString(ownerId)}/configuration?code={Uri.EscapeDataString(functionKey)}";

        if (!await WaitForFunctionAppReadyAsync(client, endpoint, progress, cancellationToken).ConfigureAwait(false))
        {
            ReportProgress(progress, "Function App did not become responsive in time.");
            return false;
        }

        var payloadJson = JsonSerializer.Serialize(new OwnerConfigurationPayload(configJson), OwnerConfigSerializerOptions);
        var payloadSizeBytes = Encoding.UTF8.GetByteCount(payloadJson);
        ReportProgress(progress, $"Owner configuration payload prepared ({payloadSizeBytes} bytes).", verbose: true);

        const int maxAttempts = 5;
        var delay = TimeSpan.FromSeconds(2);
        HttpStatusCode? lastStatus = null;
        string? lastBody = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
                var response = await client.PostAsync(endpoint, content, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }

                lastStatus = response.StatusCode;
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                lastBody = body;
                var message = $"Seed attempt {attempt}/{maxAttempts} failed: {(int)response.StatusCode} {response.StatusCode} {TruncateForLog(body)}";
                ReportProgress(progress, message, verbose: true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastBody = ex.Message;
                ReportProgress(progress, $"Seed attempt {attempt}/{maxAttempts} failed: {ex.Message}", verbose: true);
            }

            if (attempt < maxAttempts)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 15));
            }
        }

        var finalMessage = lastStatus.HasValue
            ? $"Owner configuration seeding failed after {maxAttempts} attempts. Last response: {(int)lastStatus.Value} {lastStatus} {TruncateForLog(lastBody)}"
            : "Owner configuration seeding failed after multiple attempts. No HTTP response body was returned.";
        ReportProgress(progress, finalMessage);
        return false;
    }

    private static async Task<bool> WaitForFunctionAppReadyAsync(HttpClient client, string configurationEndpoint, IProgress<ProvisioningProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        const int maxAttempts = 6;
        var delay = TimeSpan.FromSeconds(5);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, configurationEndpoint);
                var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound)
                {
                    if (attempt > 1)
                    {
                        ReportProgress(progress, "Function App is responsive. Continuing seeding step...", verbose: true);
                    }

                    return true;
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                ReportProgress(progress, $"Function App not ready ({(int)response.StatusCode} {response.StatusCode}). {TruncateForLog(body)}", verbose: true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                ReportProgress(progress, $"Function App readiness attempt {attempt} failed: {ex.Message}", verbose: true);
            }

            if (attempt < maxAttempts)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 1.5, 15));
            }
        }

        return false;
    }

    private static string TruncateForLog(string? value, int maxLength = 200)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "(no body)";
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength] + "...";
    }

    private async Task<string?> GetWebPubSubConnectionStringAsync(
        string azPath,
        string resourceGroup,
        string serviceName,
        IProgress<ProvisioningProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        var keyArgs = $"webpubsub key show --name \"{serviceName}\" --resource-group \"{resourceGroup}\" --query primaryConnectionString -o tsv --only-show-errors";
        var keyResult = await RunAzAsync(azPath, keyArgs, cancellationToken, streamLogger: null).ConfigureAwait(false);
        if (keyResult.ExitCode != 0)
        {
            var message = !string.IsNullOrWhiteSpace(keyResult.StandardError)
                ? keyResult.StandardError.Trim()
                : "Failed to retrieve Web PubSub connection string.";
            ReportProgress(progress, $"ERROR: {message}");
            throw new InvalidOperationException(message);
        }

        var connectionString = keyResult.StandardOutput?.Trim();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Web PubSub connection string was empty.");
        }

        return connectionString;
    }

    private static bool TryParseAppInsightsDetails(string? json, out AppInsightsDetails? details)
    {
        details = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            string? connectionString = null;
            if (root.TryGetProperty("connectionString", out var connectionProp))
            {
                connectionString = connectionProp.GetString();
            }

            string? instrumentationKey = null;
            if (root.TryGetProperty("InstrumentationKey", out var upperProp))
            {
                instrumentationKey = upperProp.GetString();
            }
            else if (root.TryGetProperty("instrumentationKey", out var lowerProp))
            {
                instrumentationKey = lowerProp.GetString();
            }

            details = new(connectionString, instrumentationKey);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string CreateAppInsightsName(string ownerId)
    {
        var baseName = ProvisioningNameGenerator.CreateFunctionAppName(ownerId);
        var candidate = $"{baseName}-ai";
        return candidate.Length <= 60 ? candidate : candidate[..60];
    }

    private static string CreateWebPubSubName(string ownerId)
    {
        var baseName = ProvisioningNameGenerator.CreateFunctionAppName(ownerId);
        var candidate = $"{baseName}-pubsub";
        if (candidate.Length <= 50)
        {
            return candidate;
        }

        return candidate[..50];
    }

    private static string BuildSubscriptionAwareArgs(string baseArgs, string subscriptionId)
        => string.IsNullOrWhiteSpace(subscriptionId) ? baseArgs : $"{baseArgs} --subscription \"{subscriptionId}\"";

    private async Task<CommandResult> RunAzAsync(
        string azPath,
        string arguments,
        CancellationToken cancellationToken,
        string? logDisplay = null,
        Action<string, bool>? streamLogger = null)
    {
        _logger.LogInformation("az {Arguments}", logDisplay ?? arguments);
        var output = new StringBuilder();
        var error = new StringBuilder();
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = azPath,
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
            if (!string.IsNullOrEmpty(e.Data))
            {
                lock (output)
                {
                    output.AppendLine(e.Data);
                }

                streamLogger?.Invoke(e.Data, false);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                lock (error)
                {
                    error.AppendLine(e.Data);
                }

                streamLogger?.Invoke(e.Data, true);
            }
        };

        process.Exited += (_, _) =>
        {
            tcs.TrySetResult(process.ExitCode);
            process.Dispose();
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start Azure CLI process.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var registration = cancellationToken.Register(() =>
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
                // ignored
            }
        });

        var exitCode = await tcs.Task.ConfigureAwait(false);
        string stdOut;
        string stdErr;
        lock (output)
        {
            stdOut = output.ToString();
        }

        lock (error)
        {
            stdErr = error.ToString();
        }

        return new CommandResult(exitCode, stdOut, stdErr);
    }

    private static Action<string, bool>? CreateStreamLogger(IProgress<ProvisioningProgressUpdate>? progress)
    {
        if (progress is null)
        {
            return null;
        }

        return (line, isError) =>
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            var prefix = isError ? "[az][stderr]" : "[az][stdout]";
            ReportProgress(progress, $"{prefix} {line}", verbose: true);
        };
    }

    private static void ReportProgress(IProgress<ProvisioningProgressUpdate>? progress, string message, double? percent = null, bool verbose = false)
    {
        if (progress is null || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        double? clamped = percent.HasValue ? Math.Clamp(percent.Value, 0, 100) : null;
        progress.Report(new ProvisioningProgressUpdate(message, clamped, verbose));
    }

    private sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);
}
