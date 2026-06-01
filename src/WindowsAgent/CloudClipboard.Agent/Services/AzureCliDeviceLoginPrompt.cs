using System;
using System.Threading;
using System.Threading.Tasks;
using CloudClipboard.Agent.UI;

namespace CloudClipboard.Agent.Services;

#if WINDOWS
using System.Windows.Forms;

public sealed class AzureCliDeviceLoginPrompt : IAzureCliDeviceLoginPrompt
{
    private readonly IAppIconProvider _iconProvider;

    public AzureCliDeviceLoginPrompt(IAppIconProvider iconProvider)
    {
        _iconProvider = iconProvider;
    }

    public Task<bool> PromptAsync(string azExecutablePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(azExecutablePath))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(azExecutablePath));
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                using var dialog = new AzureCliDeviceLoginDialog(azExecutablePath, cancellationToken)
                {
                    Icon = _iconProvider.GetIcon(64)
                };

                using var registration = cancellationToken.Register(() =>
                {
                    try
                    {
                        if (dialog.IsHandleCreated)
                        {
                            dialog.BeginInvoke(new Action(dialog.Close));
                        }
                        else
                        {
                            dialog.Close();
                        }
                    }
                    catch
                    {
                        // best effort cancellation
                    }
                });

                var result = dialog.ShowDialog();
                tcs.TrySetResult(result == DialogResult.OK);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        {
            IsBackground = false,
            Name = "CloudClipboard.AzureCliDeviceLogin"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return tcs.Task;
    }
}
#else
    public sealed class AzureCliDeviceLoginPrompt : IAzureCliDeviceLoginPrompt
    {
        public async Task<bool> PromptAsync(string azExecutablePath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(azExecutablePath))
            {
                throw new ArgumentException("Value cannot be null or whitespace.", nameof(azExecutablePath));
            }

            Console.WriteLine("=== Azure CLI Device Login ===");
            Console.WriteLine("You will need to open a browser on any device and enter the code displayed below.");
            Console.WriteLine();

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Run `az login --use-device-code` and forward output to console
            var onLine = new Action<string, bool>((line, isErr) =>
            {
                if (isErr)
                    Console.Error.WriteLine($"[az] {line}");
                else
                    Console.Out.WriteLine($"[az] {line}");
            });

            var result = await AzureCliProcessRunner.RunAsync(
                azExecutablePath,
                "login --use-device-code",
                captureOutput: true,
                onLine: onLine,
                cancellationToken: cancellationToken
            ).ConfigureAwait(false);

            Console.WriteLine();
            if (result.ExitCode == 0)
            {
                Console.WriteLine("Azure CLI login succeeded!");
                tcs.TrySetResult(true);
            }
            else
            {
                Console.WriteLine($"Azure CLI login failed (exit code: {result.ExitCode}).");
                if (!string.IsNullOrEmpty(result.StandardError))
                    Console.WriteLine($"Error: {result.StandardError}");
                tcs.TrySetResult(false);
            }

            return await tcs.Task;
        }
    }
#endif
