 using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace CloudClipboard.Agent.Windows.Services;

public interface IAzureCliMetadataProvider
{
    Task<IReadOnlyList<AzureSubscriptionInfo>> GetSubscriptionsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<AzureLocationInfo>> GetLocationsAsync(string? subscriptionId, CancellationToken cancellationToken);
}

public sealed record class AzureSubscriptionInfo(string Id, string Name, bool IsDefault)
{
    public string Label => string.IsNullOrWhiteSpace(Name) ? Id : $"{Name} ({Id})";
    public override string ToString() => Label;
}

public sealed record class AzureLocationInfo(string Name, string DisplayName)
{
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Name : $"{DisplayName} ({Name})";
    public override string ToString() => Label;
}

public sealed class AzureCliMetadataProvider : IAzureCliMetadataProvider
{
    private readonly ILogger<AzureCliMetadataProvider> _logger;

    public AzureCliMetadataProvider(ILogger<AzureCliMetadataProvider> logger)
    {
        _logger = logger;
    }

    public async Task<IReadOnlyList<AzureSubscriptionInfo>> GetSubscriptionsAsync(CancellationToken cancellationToken)
    {
        if (!AzureCliLocator.TryResolveExecutable(out var azPath, out var error))
        {
            _logger.LogDebug("Azure CLI not available for metadata lookup: {Error}", error);
            return Array.Empty<AzureSubscriptionInfo>();
        }

        var result = await RunAzAsync(azPath, cancellationToken, "account", "list", "--output", "json").ConfigureAwait(false);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            _logger.LogDebug("az account list failed: {Error}", result.StandardError);
            return Array.Empty<AzureSubscriptionInfo>();
        }

        try
        {
            using var doc = JsonDocument.Parse(result.StandardOutput);
            var subscriptions = new List<AzureSubscriptionInfo>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var id = element.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var name = element.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                var isDefault = element.TryGetProperty("isDefault", out var defaultProp) && defaultProp.GetBoolean();
                subscriptions.Add(new AzureSubscriptionInfo(id, name ?? string.Empty, isDefault));
            }

            return subscriptions
                .OrderByDescending(s => s.IsDefault)
                .ThenBy(s => string.IsNullOrWhiteSpace(s.Name) ? s.Id : s.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Unable to parse subscription metadata from Azure CLI.");
            return Array.Empty<AzureSubscriptionInfo>();
        }
    }

    public async Task<IReadOnlyList<AzureLocationInfo>> GetLocationsAsync(string? subscriptionId, CancellationToken cancellationToken)
    {
        if (!AzureCliLocator.TryResolveExecutable(out var azPath, out var error))
        {
            _logger.LogDebug("Azure CLI not available for location lookup: {Error}", error);
            return Array.Empty<AzureLocationInfo>();
        }

        var result = await RunAzAsync(azPath, cancellationToken, "account", "list-locations", "--output", "json").ConfigureAwait(false);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            _logger.LogDebug("az account list-locations failed: {Error}", result.StandardError);
            return Array.Empty<AzureLocationInfo>();
        }

        try
        {
            using var doc = JsonDocument.Parse(result.StandardOutput);
            var locations = new List<AzureLocationInfo>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var name = element.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var displayName = element.TryGetProperty("displayName", out var displayProp) ? displayProp.GetString() : null;
                locations.Add(new AzureLocationInfo(name, displayName ?? string.Empty));
            }

            return locations
                .OrderBy(l => string.IsNullOrWhiteSpace(l.DisplayName) ? l.Name : l.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Unable to parse location metadata from Azure CLI.");
            return Array.Empty<AzureLocationInfo>();
        }
    }

    private static async Task<CommandResult> RunAzAsync(string azPath, CancellationToken cancellationToken, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(azPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var output = new StringBuilder();
        var error = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                output.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                error.AppendLine(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        return new CommandResult(process.ExitCode, output.ToString(), error.ToString());
    }

    private sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);
}
