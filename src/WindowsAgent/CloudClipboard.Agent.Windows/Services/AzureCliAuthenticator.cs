using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace CloudClipboard.Agent.Windows.Services;

public interface IAzureCliAuthenticator
{
    Task<AzureCliPrincipal?> EnsureLoginAsync(CancellationToken cancellationToken);
}

public sealed record AzureCliPrincipal(string LoginName, string? UserType, string? TenantId, string? SubscriptionId);

public sealed class AzureCliAuthenticator : IAzureCliAuthenticator
{
    private readonly ILogger<AzureCliAuthenticator> _logger;
    private readonly IAzureCliInstaller _installer;
    private readonly IAzureCliDeviceLoginPrompt _loginPrompt;

    public AzureCliAuthenticator(
        ILogger<AzureCliAuthenticator> logger,
        IAzureCliInstaller installer,
        IAzureCliDeviceLoginPrompt loginPrompt)
    {
        _logger = logger;
        _installer = installer;
        _loginPrompt = loginPrompt;
    }

    public async Task<AzureCliPrincipal?> EnsureLoginAsync(CancellationToken cancellationToken)
    {
        if (!AzureCliLocator.TryResolveExecutable(out var azPath, out var error))
        {
            _logger.LogWarning("Azure CLI not found: {Error}", error);
            var installed = await _installer.EnsureInstalledAsync(cancellationToken).ConfigureAwait(false);
            if (!installed || !AzureCliLocator.TryResolveExecutable(out azPath, out error))
            {
                _logger.LogWarning("Azure CLI installation is required before continuing. {Error}", error);
                return null;
            }
        }

        var account = await TryGetAccountAsync(azPath, cancellationToken).ConfigureAwait(false);
        if (account is not null)
        {
            return account;
        }

        _logger.LogInformation("Azure CLI login required. Opening Cloud Clipboard login dialog...");
        var loginCompleted = await _loginPrompt.PromptAsync(azPath, cancellationToken).ConfigureAwait(false);
        if (!loginCompleted)
        {
            _logger.LogWarning("Azure CLI login was cancelled or failed.");
            return null;
        }

        return await TryGetAccountAsync(azPath, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AzureCliPrincipal?> TryGetAccountAsync(string azPath, CancellationToken cancellationToken)
    {
        var result = await AzureCliProcessRunner.RunAsync(azPath, "account show --output json", captureOutput: true, onLine: null, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            _logger.LogDebug("az account show exited with {ExitCode}.", result.ExitCode);
            return null;
        }

        if (string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            _logger.LogWarning("az account show returned empty output.");
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            var root = document.RootElement;
            var loginName = root.TryGetProperty("user", out var userElement) && userElement.ValueKind == JsonValueKind.Object && userElement.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString()
                : null;
            var userType = userElement.ValueKind == JsonValueKind.Object && userElement.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString()
                : null;
            var tenantId = root.TryGetProperty("tenantId", out var tenantElement) ? tenantElement.GetString() : null;
            var subscriptionId = root.TryGetProperty("id", out var subscriptionElement) ? subscriptionElement.GetString() : null;

            if (string.IsNullOrWhiteSpace(loginName))
            {
                _logger.LogWarning("Azure CLI account information did not include a login name.");
                return null;
            }

            _logger.LogInformation("Using Azure CLI account {LoginName} (tenant {TenantId}, subscription {SubscriptionId}).", loginName, tenantId, subscriptionId);
            return new AzureCliPrincipal(loginName, userType, tenantId, subscriptionId);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse Azure CLI account information.");
            return null;
        }
    }

}