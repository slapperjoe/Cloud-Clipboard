using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CloudClipboard.Agent.Windows.Options;
using Microsoft.Extensions.Logging;

namespace CloudClipboard.Agent.Windows.Services;

public interface IBackendProvisioningService
{
    Task<BackendProvisioningResult> ProvisionAsync(BackendProvisioningRequest request, IProgress<string>? progress, CancellationToken cancellationToken);
}

public sealed record BackendProvisioningRequest(string OwnerId, FunctionsDeploymentOptions DeploymentOptions);

public sealed record BackendProvisioningResult(bool Succeeded, AgentOptions? RemoteOptions, string? ErrorMessage);

public sealed class BackendProvisioningService : IBackendProvisioningService
{
    private readonly ILogger<BackendProvisioningService> _logger;
    private readonly IFunctionsDeploymentService _deploymentService;
    private readonly IHttpClientFactory _httpClientFactory;

    public BackendProvisioningService(
        ILogger<BackendProvisioningService> logger,
        IFunctionsDeploymentService deploymentService,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _deploymentService = deploymentService;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<BackendProvisioningResult> ProvisionAsync(BackendProvisioningRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.OwnerId))
        {
            return new(false, null, "OwnerId is required for provisioning.");
        }

        progress?.Report("Checking Azure CLI availability...");
        if (!AzureCliLocator.TryResolveExecutable(out var azPath, out var azError))
        {
            return new(false, null, azError ?? "Azure CLI (az) is required.");
        }

        var deployment = CloneDeployment(request.DeploymentOptions);
        AgentOptionsJson.Normalize(new AgentOptions { FunctionsDeployment = deployment });
        progress?.Report("Validating deployment package...");
        var packagePath = FunctionsDeploymentUtilities.ResolvePackagePath(deployment.PackagePath);
        if (!File.Exists(packagePath))
        {
            return new(false, null, $"Deployment package not found at {packagePath}.");
        }

        progress?.Report("Ensuring Azure CLI login and subscription access...");
        var resolvedSubscriptionId = await EnsureLoginAsync(azPath, deployment.SubscriptionId, cancellationToken).ConfigureAwait(false);
        var effectiveSubscriptionId = string.IsNullOrWhiteSpace(deployment.SubscriptionId) ? resolvedSubscriptionId : deployment.SubscriptionId;
        if (string.IsNullOrWhiteSpace(effectiveSubscriptionId))
        {
            return new(false, null, "Azure CLI login failed or subscription could not be resolved.");
        }

        deployment.SubscriptionId = effectiveSubscriptionId;
        await SetSubscriptionAsync(azPath, deployment.SubscriptionId, cancellationToken).ConfigureAwait(false);
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
        deployment.Location = string.IsNullOrWhiteSpace(deployment.Location) ? "eastus" : deployment.Location;

        try
        {
            progress?.Report($"Creating resource group '{deployment.ResourceGroup}' in {deployment.Location}...");
            await EnsureResourceGroupAsync(azPath, deployment.ResourceGroup, deployment.Location, cancellationToken).ConfigureAwait(false);
            progress?.Report($"Creating storage account '{deployment.StorageAccountName}'...");
            await EnsureStorageAccountAsync(azPath, deployment.ResourceGroup, deployment.StorageAccountName, deployment.Location, progress, cancellationToken).ConfigureAwait(false);
            progress?.Report("Retrieving storage connection string...");
            var connectionString = await GetStorageConnectionStringAsync(azPath, deployment.ResourceGroup, deployment.StorageAccountName, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return new(false, null, "Failed to obtain storage account connection string.");
            }

            progress?.Report($"Creating Function App '{deployment.FunctionAppName}' (Consumption plan)...");
            await EnsureFunctionAppAsync(azPath, deployment, progress, cancellationToken).ConfigureAwait(false);
            progress?.Report("Configuring Function App settings...");
            await ConfigureFunctionAppAsync(azPath, deployment, connectionString, progress, cancellationToken).ConfigureAwait(false);

            var deployRequest = new FunctionsDeploymentRequest(
                deployment.FunctionAppName,
                deployment.ResourceGroup,
                deployment.SubscriptionId,
                packagePath);

            progress?.Report("Deploying Functions package (this may take a few minutes)...");
            var deployProgress = new Progress<string>(line =>
            {
                _logger.LogInformation("Deploy: {Line}", line);
                progress?.Report(line);
            });
            var deployResult = await _deploymentService.DeployAsync(deployRequest, deployProgress, cancellationToken).ConfigureAwait(false);
            if (!deployResult.Succeeded)
            {
                return new(false, null, deployResult.ErrorMessage ?? "Functions deployment failed.");
            }

            progress?.Report("Retrieving function keys...");
            var functionKey = await GetFunctionKeyAsync(azPath, deployment.ResourceGroup, deployment.FunctionAppName, cancellationToken).ConfigureAwait(false);
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
            progress?.Report("Seeding owner configuration in Functions backend...");
            var seedSucceeded = await SeedRemoteConfigurationAsync(apiBaseUrl, functionKey, ownerId, serialized, cancellationToken).ConfigureAwait(false);
            if (!seedSucceeded)
            {
                return new(false, null, "Failed to seed owner configuration in the Functions backend.");
            }

            progress?.Report($"✓ Provisioning completed! Function App: {deployment.FunctionAppName}");
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
        clone.PayloadContainer = source.PayloadContainer;
        clone.MetadataTable = source.MetadataTable;
        clone.LastPackageHash = source.LastPackageHash;
        clone.LastDeployedUtc = source.LastDeployedUtc;
        return clone;
    }

    private async Task<string?> EnsureLoginAsync(string azPath, string subscriptionId, CancellationToken cancellationToken)
    {
        var accountArgs = BuildSubscriptionAwareArgs("account show", subscriptionId);
        var accountResult = await RunAzAsync(azPath, accountArgs, captureOutput: true, cancellationToken).ConfigureAwait(false);
        if (accountResult.ExitCode != 0)
        {
            _logger.LogInformation("Azure CLI login required. Starting device login flow...");
            var loginResult = await RunAzAsync(azPath, "login --use-device-code --output none", captureOutput: false, cancellationToken).ConfigureAwait(false);
            if (loginResult.ExitCode != 0)
            {
                return null;
            }

            accountResult = await RunAzAsync(azPath, accountArgs, captureOutput: true, cancellationToken).ConfigureAwait(false);
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

    private async Task EnsureResourceGroupAsync(string azPath, string resourceGroup, string location, CancellationToken cancellationToken)
    {
        var args = $"group create --name \"{resourceGroup}\" --location \"{location}\"";
        await RunAzAsync(azPath, args, captureOutput: false, cancellationToken).ConfigureAwait(false);
    }

    private async Task SetSubscriptionAsync(string azPath, string subscriptionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(subscriptionId))
        {
            return;
        }

        var args = $"account set --subscription \"{subscriptionId}\"";
        var result = await RunAzAsync(azPath, args, captureOutput: false, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Failed to set Azure subscription {subscriptionId}.");
        }
    }

    private async Task EnsureStorageAccountAsync(string azPath, string resourceGroup, string storageAccount, string location, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        // First check if it already exists in our resource group
        var showArgs = $"storage account show --name {storageAccount} --resource-group \"{resourceGroup}\"";
        var showResult = await RunAzAsync(azPath, showArgs, captureOutput: false, cancellationToken).ConfigureAwait(false);
        if (showResult.ExitCode == 0)
        {
            progress?.Report($"Storage account '{storageAccount}' already exists in resource group.");
            return;
        }

        // Check global name availability before attempting to create
        var checkArgs = $"storage account check-name --name {storageAccount} --query nameAvailable -o tsv";
        var checkResult = await RunAzAsync(azPath, checkArgs, captureOutput: true, cancellationToken).ConfigureAwait(false);
        if (checkResult.ExitCode == 0 && checkResult.StandardOutput.Trim().Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            var errorMessage = $"The storage account name '{storageAccount}' is already taken globally. Please choose a different name.";
            progress?.Report($"ERROR: {errorMessage}");
            throw new InvalidOperationException(errorMessage);
        }

        var createArgs = $"storage account create --name {storageAccount} --resource-group \"{resourceGroup}\" --location \"{location}\" --sku Standard_LRS";
        var createResult = await RunAzAsync(azPath, createArgs, captureOutput: false, cancellationToken).ConfigureAwait(false);
        if (createResult.ExitCode != 0)
        {
            var errorMessage = !string.IsNullOrWhiteSpace(createResult.StandardError)
                ? createResult.StandardError.Trim()
                : "Failed to create storage account.";
            progress?.Report($"ERROR: {errorMessage}");
            throw new InvalidOperationException(errorMessage);
        }
    }

    private async Task<string?> GetStorageConnectionStringAsync(string azPath, string resourceGroup, string storageAccount, CancellationToken cancellationToken)
    {
        var args = $"storage account show-connection-string --name {storageAccount} --resource-group \"{resourceGroup}\" --query connectionString -o tsv";
        var result = await RunAzAsync(azPath, args, captureOutput: true, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            return null;
        }

        return result.StandardOutput.Trim();
    }

    private async Task EnsureFunctionAppAsync(string azPath, FunctionsDeploymentOptions deployment, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        // First check if it already exists in our resource group
        var showArgs = $"functionapp show --name \"{deployment.FunctionAppName}\" --resource-group \"{deployment.ResourceGroup}\"";
        var showResult = await RunAzAsync(azPath, showArgs, captureOutput: false, cancellationToken).ConfigureAwait(false);
        if (showResult.ExitCode == 0)
        {
            progress?.Report($"Function App '{deployment.FunctionAppName}' already exists in resource group.");
            return;
        }

        // Check global name availability before attempting to create
        var checkArgs = $"webapp list --query \"[?name=='{deployment.FunctionAppName}'].name\" -o tsv";
        var checkResult = await RunAzAsync(azPath, checkArgs, captureOutput: true, cancellationToken).ConfigureAwait(false);
        if (checkResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(checkResult.StandardOutput.Trim()))
        {
            var errorMessage = $"The function app name '{deployment.FunctionAppName}' is already taken globally. Please choose a different name.";
            progress?.Report($"ERROR: {errorMessage}");
            throw new InvalidOperationException(errorMessage);
        }

        var createArgs = $"functionapp create --name \"{deployment.FunctionAppName}\" --resource-group \"{deployment.ResourceGroup}\" --storage-account {deployment.StorageAccountName} --consumption-plan-location \"{deployment.Location}\" --functions-version 4 --runtime dotnet-isolated --os-type Windows";
        var createResult = await RunAzAsync(azPath, createArgs, captureOutput: false, cancellationToken).ConfigureAwait(false);
        if (createResult.ExitCode != 0)
        {
            var errorMessage = !string.IsNullOrWhiteSpace(createResult.StandardError)
                ? createResult.StandardError.Trim()
                : "Failed to create Azure Function App.";
            progress?.Report($"ERROR: {errorMessage}");
            throw new InvalidOperationException(errorMessage);
        }
    }

    private async Task ConfigureFunctionAppAsync(string azPath, FunctionsDeploymentOptions deployment, string connectionString, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var payloadContainer = deployment.PayloadContainer;
        var metadataTable = deployment.MetadataTable;
        var settingsArgs = $"functionapp config appsettings set --name \"{deployment.FunctionAppName}\" --resource-group \"{deployment.ResourceGroup}\" --settings \"Storage:BlobConnectionString={EscapeSetting(connectionString)}\" \"Storage:TableConnectionString={EscapeSetting(connectionString)}\" \"Storage:PayloadContainer={payloadContainer}\" \"Storage:MetadataTable={metadataTable}\"";
        var display = $"functionapp config appsettings set --name {deployment.FunctionAppName} --resource-group {deployment.ResourceGroup} --settings <redacted>";
        var settingsResult = await RunAzAsync(azPath, settingsArgs, captureOutput: false, cancellationToken, display).ConfigureAwait(false);
        if (settingsResult.ExitCode != 0)
        {
            var errorMessage = !string.IsNullOrWhiteSpace(settingsResult.StandardError)
                ? settingsResult.StandardError.Trim()
                : "Failed to configure Function App settings.";
            progress?.Report($"ERROR: {errorMessage}");
            throw new InvalidOperationException(errorMessage);
        }
    }

    private static string EscapeSetting(string value)
        => value.Replace("\"", "\\\"", StringComparison.Ordinal);

    private async Task<string?> GetFunctionKeyAsync(string azPath, string resourceGroup, string functionApp, CancellationToken cancellationToken)
    {
        var args = $"functionapp keys list --name \"{functionApp}\" --resource-group \"{resourceGroup}\" --query functionKeys.default -o tsv";
        var result = await RunAzAsync(azPath, args, captureOutput: true, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            return null;
        }

        return result.StandardOutput.Trim();
    }

    private async Task<bool> SeedRemoteConfigurationAsync(string apiBaseUrl, string functionKey, string ownerId, string configJson, CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient("CloudClipboard.Provisioning");
        var endpoint = $"{apiBaseUrl.TrimEnd('/')}/clipboard/owners/{Uri.EscapeDataString(ownerId)}/configuration?code={Uri.EscapeDataString(functionKey)}";
        try
        {
            var response = await client.PostAsJsonAsync(endpoint, new { ConfigurationJson = configJson }, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogWarning("Failed to seed owner configuration: {Status} {Body}", response.StatusCode, body);
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "HTTP request for configuration seeding failed");
            return false;
        }
    }

    private static string BuildSubscriptionAwareArgs(string baseArgs, string subscriptionId)
        => string.IsNullOrWhiteSpace(subscriptionId) ? baseArgs : $"{baseArgs} --subscription \"{subscriptionId}\"";

    private async Task<CommandResult> RunAzAsync(string azPath, string arguments, bool captureOutput, CancellationToken cancellationToken, string? logDisplay = null)
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
                output.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                error.AppendLine(e.Data);
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
        return new CommandResult(exitCode, output.ToString(), error.ToString());
    }

    private sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);
}
