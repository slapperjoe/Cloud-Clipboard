using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CloudClipboard.Agent.Windows.UI;

namespace CloudClipboard.Agent.Windows.Services;

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
