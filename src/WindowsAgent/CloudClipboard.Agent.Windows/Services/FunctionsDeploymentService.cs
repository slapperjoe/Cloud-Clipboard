using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace CloudClipboard.Agent.Windows.Services;

public interface IFunctionsDeploymentService
{
    Task<FunctionsDeploymentResult> DeployAsync(FunctionsDeploymentRequest request, IProgress<string>? progress, CancellationToken cancellationToken);
}

public sealed record FunctionsDeploymentRequest(
    string FunctionAppName,
    string ResourceGroup,
    string SubscriptionId,
    string PackagePath);

public sealed record FunctionsDeploymentResult(bool Succeeded, string? ErrorMessage = null);

public sealed class FunctionsDeploymentService : IFunctionsDeploymentService
{
    private readonly ILogger<FunctionsDeploymentService> _logger;

    public FunctionsDeploymentService(ILogger<FunctionsDeploymentService> logger)
    {
        _logger = logger;
    }

    public async Task<FunctionsDeploymentResult> DeployAsync(FunctionsDeploymentRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.FunctionAppName) || string.IsNullOrWhiteSpace(request.ResourceGroup))
        {
            return new(false, "Function App name and Resource Group are required.");
        }

        if (!File.Exists(request.PackagePath))
        {
            return new(false, $"Package not found: {request.PackagePath}");
        }

        var tempZip = Path.Combine(Path.GetTempPath(), $"CloudClipboardDeploy_{Guid.NewGuid():N}.zip");
        try
        {
            File.Copy(request.PackagePath, tempZip, overwrite: true);
        }
        catch (Exception copyEx)
        {
            _logger.LogError(copyEx, "Failed to copy deployment package");
            return new(false, $"Failed to copy package: {copyEx.Message}");
        }

        if (!AzureCliLocator.TryResolveExecutable(out var azPath, out var azError))
        {
            progress?.Report(azError ?? "Azure CLI not found");
            return new(false, azError);
        }

        try
        {
            progress?.Report("Checking Azure CLI authentication...");
            var accountArgs = $"account show{BuildSubscriptionFlag(request.SubscriptionId)}";
            ReportCommand(progress, azPath, accountArgs);
            var checkLogin = await RunAzAsync(azPath, accountArgs, progress, cancellationToken).ConfigureAwait(false);
            if (checkLogin != 0)
            {
                progress?.Report("Azure CLI requires login. Launching device code flow...");
                const string loginArgs = "login --use-device-code";
                ReportCommand(progress, azPath, loginArgs);
                var loginCode = await RunAzAsync(azPath, loginArgs, progress, cancellationToken).ConfigureAwait(false);
                if (loginCode != 0)
                {
                    return new(false, "Azure CLI login failed.");
                }
            }

            var deployArgs = $"functionapp deployment source config-zip --resource-group \"{request.ResourceGroup}\" --name \"{request.FunctionAppName}\" --src \"{tempZip}\"{BuildSubscriptionFlag(request.SubscriptionId)}";
            progress?.Report("Starting Azure Functions deployment...");
            ReportCommand(progress, azPath, deployArgs);
            var exitCode = await RunAzAsync(azPath, deployArgs, progress, cancellationToken).ConfigureAwait(false);
            if (exitCode != 0)
            {
                progress?.Report($"Azure CLI exited with code {exitCode}.");
                return new(false, "Azure CLI reported a deployment failure.");
            }

            progress?.Report("Deployment completed successfully.");
            return new(true, null);
        }
        catch (OperationCanceledException)
        {
            return new(false, "Deployment was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected deployment failure");
            return new(false, ex.Message);
        }
        finally
        {
            TryDeleteFile(tempZip);
        }
    }

    private static async Task<int> RunAzAsync(string azPath, string arguments, IProgress<string>? progress, CancellationToken cancellationToken)
        => await RunProcessAsync(azPath, arguments, progress, cancellationToken).ConfigureAwait(false);

    private static async Task<int> RunProcessAsync(string fileName, string arguments, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
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
                progress?.Report(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                progress?.Report(e.Data);
            }
        };
        process.Exited += (_, _) =>
        {
            tcs.TrySetResult(process.ExitCode);
            process.Dispose();
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Unable to start process '{fileName}'.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await using var _ = cancellationToken.Register(() =>
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
                // ignore
            }
        });

        return await tcs.Task.ConfigureAwait(false);
    }

    private static string BuildSubscriptionFlag(string subscriptionId)
        => string.IsNullOrWhiteSpace(subscriptionId) ? string.Empty : $" --subscription \"{subscriptionId}\"";

    private static void ReportCommand(IProgress<string>? progress, string azPath, string arguments)
    {
        var display = Path.GetFileName(azPath) ?? "az";
        progress?.Report($"> {display} {arguments}");
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best effort cleanup
        }
    }
}
