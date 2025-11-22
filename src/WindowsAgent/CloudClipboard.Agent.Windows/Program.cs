using CloudClipboard.Agent.Windows;
using CloudClipboard.Agent.Windows.Configuration;
using CloudClipboard.Agent.Windows.Options;
using CloudClipboard.Agent.Windows.Services;
using CloudClipboard.Agent.Windows.UI;
using CloudClipboard.Core.Services;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

DpiAwareness.Initialize();

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
builder.Services.AddSingleton<IFunctionsDeploymentService, FunctionsDeploymentService>();
builder.Services.AddSingleton<ClipboardPayloadSerializer>();
builder.Services.AddSingleton<ClipboardPasteService>();
builder.Services.AddHttpClient<ICloudClipboardClient, HttpCloudClipboardClient>();
builder.Services.AddHostedService<ClipboardCaptureService>();
builder.Services.AddHostedService<ClipboardSyncWorker>();
builder.Services.AddHostedService<ClipboardHistoryWorker>();
builder.Services.AddHostedService<OwnerStateWorker>();
builder.Services.AddHostedService<ClipboardPushListener>();
builder.Services.AddHostedService<TrayIconHostedService>();

var host = builder.Build();
host.Run();
