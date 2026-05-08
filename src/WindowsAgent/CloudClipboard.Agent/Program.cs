using System.Diagnostics;
using System.Threading;
using CloudClipboard.Agent;
using CloudClipboard.Agent.Configuration;
using CloudClipboard.Agent.Options;
using CloudClipboard.Agent.Services;
using CloudClipboard.Agent.UI;
using CloudClipboard.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Platform detection
var isWindows = PlatformDetector.IsWindows;

// Single-instance check
string singleInstanceMutexName;
if (isWindows)
{
    singleInstanceMutexName = "Global\\CloudClipboardAgent";
}
else
{
    singleInstanceMutexName = "CloudClipboardAgent";
}

using var singleInstanceMutex = new Mutex(initiallyOwned: true, singleInstanceMutexName, out var createdNew);
if (!createdNew)
{
#if WINDOWS
    System.Windows.Forms.MessageBox.Show(
        "Cloud Clipboard Agent is already running.",
        "Cloud Clipboard",
        System.Windows.Forms.MessageBoxButtons.OK,
        System.Windows.Forms.MessageBoxIcon.Information);
#else
    Console.WriteLine("Cloud Clipboard Agent is already running (another instance detected).");
#endif
    return;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff ";
    options.SingleLine = true;
});

var userSettingsPath = AgentSettingsPathProvider.GetSettingsPath();
AgentSettingsBackup.TryCreateBackup(userSettingsPath, BackupScope.Startup);
var settingsDirectory = Path.GetDirectoryName(userSettingsPath)!;
var settingsFile = Path.GetFileName(userSettingsPath);
builder.Configuration.AddJsonFile(
    new PhysicalFileProvider(settingsDirectory),
    settingsFile,
    optional: true,
    reloadOnChange: true);

builder.Services.AddSingleton<IAgentSettingsStore, JsonAgentSettingsStore>();
builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection("Agent"));
builder.Services.AddSingleton<IClipboardUploadQueue, ClipboardUploadQueue>();
builder.Services.AddSingleton<IClipboardHistoryCache, ClipboardHistoryCache>();
builder.Services.AddSingleton<IOwnerStateCache, OwnerStateCache>();
builder.Services.AddSingleton<IManualUploadStore, ManualUploadStore>();
builder.Services.AddSingleton<IPinnedClipboardStore, PinnedClipboardStore>();
builder.Services.AddSingleton<IAgentDiagnostics, AgentDiagnostics>();
builder.Services.AddSingleton<IAppIconProvider, AppIconProvider>();
builder.Services.AddSingleton<ILocalUploadTracker, LocalUploadTracker>();
builder.Services.AddSingleton<ISetupActivityMonitor, SetupActivityMonitor>();
builder.Services.AddSingleton<IFunctionsDeploymentService, FunctionsDeploymentService>();
builder.Services.AddSingleton<IBackendProvisioningService, BackendProvisioningService>();
builder.Services.AddSingleton<IAzureCliInstaller, AzureCliInstaller>();
builder.Services.AddSingleton<IAzureCliDeviceLoginPrompt, AzureCliDeviceLoginPrompt>();
builder.Services.AddSingleton<IAzureCliAuthenticator, AzureCliAuthenticator>();
builder.Services.AddSingleton<IAzureCliMetadataProvider, AzureCliMetadataProvider>();
builder.Services.AddSingleton<ClipboardPayloadSerializer>();
builder.Services.AddSingleton<ClipboardPasteService>();
builder.Services.AddHttpClient<HttpCloudClipboardClient>();
builder.Services.AddSingleton<ICloudClipboardClient>(sp =>
{
    var inner = sp.GetRequiredService<HttpCloudClipboardClient>();
    return ActivatorUtilities.CreateInstance<SetupAwareCloudClipboardClient>(sp, inner);
});
builder.Services.AddHttpClient("CloudClipboard.Provisioning");

// Register platform-specific clipboard access
if (isWindows)
{
    builder.Services.AddSingleton<IClipboardAccess, WindowsClipboardAccess>();
    builder.Services.AddSingleton<ITrayIcon, WindowsTrayIcon>();
}
else
{
    builder.Services.AddSingleton<IClipboardAccess, LinuxClipboardAccess>();
    builder.Services.AddSingleton<ITrayIcon, LinuxTrayIcon>();
}

builder.Services.AddHostedService<FirstRunConfigurationService>();
builder.Services.AddHostedService<OwnerConfigurationSyncService>();
builder.Services.AddHostedService<ClipboardCaptureService>();
builder.Services.AddHostedService<ClipboardSyncWorker>();
builder.Services.AddHostedService<ClipboardHistoryWorker>();
builder.Services.AddHostedService<OwnerStateWorker>();
builder.Services.AddHostedService<ClipboardPushListener>();
builder.Services.AddHostedService<TrayIconHostedService>();

var host = builder.Build();
await host.RunAsync();
