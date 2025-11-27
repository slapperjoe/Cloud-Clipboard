using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CloudClipboard.Agent.Windows.Services;

internal static class AzureCliProcessRunner
{
    public sealed record ProcessResult(int ExitCode, string? StandardOutput, string? StandardError);

    public static Task<ProcessResult> RunAsync(
        string fileName,
        string arguments,
        bool captureOutput,
        Action<string, bool>? onLine,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(fileName));
        }

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
                    onLine?.Invoke(e.Data, false);
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    error!.AppendLine(e.Data);
                    onLine?.Invoke(e.Data, true);
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
                // best effort cancellation
            }
        });

        async Task<ProcessResult> AwaitExitAsync()
        {
            var exitCode = await tcs.Task.ConfigureAwait(false);
            return new ProcessResult(exitCode, output?.ToString(), error?.ToString());
        }

        return AwaitExitAsync();
    }
}
