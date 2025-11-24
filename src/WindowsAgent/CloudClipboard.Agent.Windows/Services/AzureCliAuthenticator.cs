using System;
using System.Diagnostics;
using System.Text;
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

    public AzureCliAuthenticator(ILogger<AzureCliAuthenticator> logger)
    {
        _logger = logger;
    }

    public async Task<AzureCliPrincipal?> EnsureLoginAsync(CancellationToken cancellationToken)
    {
        if (!AzureCliLocator.TryResolveExecutable(out var azPath, out var error))
        {
            _logger.LogWarning("Azure CLI not found. Install Azure CLI and sign in before running the agent. {Error}", error);
            return null;
        }

        var account = await TryGetAccountAsync(azPath, cancellationToken).ConfigureAwait(false);
        if (account is not null)
        {
            return account;
        }

        _logger.LogInformation("Azure CLI login required. Launching device code flow...");
        var loginResult = await RunProcessAsync(azPath, "login --use-device-code --output json", captureOutput: true, cancellationToken).ConfigureAwait(false);
        LogProcessOutput("az login", loginResult);
        if (loginResult.ExitCode != 0)
        {
            _logger.LogWarning("Azure CLI login failed with exit code {ExitCode}.", loginResult.ExitCode);
            return null;
        }

        return await TryGetAccountAsync(azPath, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AzureCliPrincipal?> TryGetAccountAsync(string azPath, CancellationToken cancellationToken)
    {
        var result = await RunProcessAsync(azPath, "account show --output json", captureOutput: true, cancellationToken).ConfigureAwait(false);
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

    private void LogProcessOutput(string command, ProcessResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            foreach (var line in result.StandardOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                _logger.LogInformation("{Command}: {Line}", command, line);
            }
        }

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            foreach (var line in result.StandardError.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                _logger.LogInformation("{Command} (stderr): {Line}", command, line);
            }
        }
    }

    private static async Task<ProcessResult> RunProcessAsync(string fileName, string arguments, bool captureOutput, CancellationToken cancellationToken)
    {
        var output = captureOutput ? new StringBuilder() : null;
        var error = captureOutput ? new StringBuilder() : null;

        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = captureOutput,
                RedirectStandardError = captureOutput,
                UseShellExecute = !captureOutput,
                CreateNoWindow = captureOutput
            },
            EnableRaisingEvents = true
        };

        if (captureOutput)
        {
            process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    output!.AppendLine(e.Data);
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    error!.AppendLine(e.Data);
                }
            };
        }

        process.Exited += (_, _) =>
        {
            tcs.TrySetResult(process.ExitCode);
            process.Dispose();
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Unable to start process '{fileName}'.");
        }

        if (captureOutput)
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

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
                // ignore cleanup failures
            }
        });

        var exitCode = await tcs.Task.ConfigureAwait(false);
        return new ProcessResult(exitCode, output?.ToString(), error?.ToString());
    }

    private sealed record ProcessResult(int ExitCode, string? StandardOutput, string? StandardError);
}