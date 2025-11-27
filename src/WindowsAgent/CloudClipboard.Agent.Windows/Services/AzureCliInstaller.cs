using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CloudClipboard.Agent.Windows.UI;
using Microsoft.Extensions.Logging;
using System.Net.Http;

namespace CloudClipboard.Agent.Windows.Services;

public interface IAzureCliInstaller
{
    Task<bool> EnsureInstalledAsync(CancellationToken cancellationToken);
}

public sealed class AzureCliInstaller : IAzureCliInstaller
{
    private readonly ILogger<AzureCliInstaller> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAppIconProvider _iconProvider;
    private readonly ISetupActivityMonitor _activityMonitor;

    public AzureCliInstaller(
        ILogger<AzureCliInstaller> logger,
        IHttpClientFactory httpClientFactory,
        IAppIconProvider iconProvider,
        ISetupActivityMonitor activityMonitor)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _iconProvider = iconProvider;
        _activityMonitor = activityMonitor;
    }

    public async Task<bool> EnsureInstalledAsync(CancellationToken cancellationToken)
    {
        if (AzureCliLocator.TryResolveExecutable(out _, out _))
        {
            return true;
        }

        _logger.LogInformation("Azure CLI not detected. Prompting user to install the Azure CLI.");
        using var activity = _activityMonitor.BeginActivity("AzureCliInstaller");

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                using var httpClient = _httpClientFactory.CreateClient("CloudClipboard.Provisioning");
                using var dialog = new AzureCliInstallDialog(httpClient)
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
            Name = "CloudClipboard.AzureCliInstaller"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        bool dialogConfirmed;
        try
        {
            dialogConfirmed = await tcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return AzureCliLocator.TryResolveExecutable(out _, out _);
        }

        if (AzureCliLocator.TryResolveExecutable(out var azPath, out _))
        {
            _logger.LogInformation("Azure CLI located at {AzPath} after installer dialog.", azPath);
            return true;
        }

        if (dialogConfirmed)
        {
            _logger.LogWarning("Azure CLI installer flow completed but az.exe is still not on PATH.");
        }
        else
        {
            _logger.LogInformation("Azure CLI installer dialog dismissed; az.exe still missing.");
        }

        return false;
    }
}
