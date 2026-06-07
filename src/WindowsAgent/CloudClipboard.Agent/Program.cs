using System.Diagnostics;
using System.IO;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.X11;
using Avalonia.Skia;
using CloudClipboard.Agent;
using CloudClipboard.Agent.Configuration;
using CloudClipboard.Agent.Options;
using CloudClipboard.Agent.Services;
using CloudClipboard.Agent.UI;
using CloudClipboard.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Platform detection
var isWindows = PlatformDetector.IsWindows;

// Single-instance check
string singleInstanceMutexName = isWindows ? "Global\\CloudClipboardAgent" : "CloudClipboardAgent";
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
    // On Linux, just exit silently (no console to show a message)
#endif
    return;
}

// Redirect all logging to a file (no console output for system tray app)
var logPath = Path.Combine(AgentSettingsPathProvider.GetSettingsDirectory(), "agent.log");
var logStream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.Read);

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddProvider(new FileLoggerProvider(logStream));

var userSettingsPath = AgentSettingsPathProvider.GetSettingsPath();
AgentSettingsBackup.TryCreateBackup(userSettingsPath, BackupScope.Startup);
// Ensure settings file exists before host starts so IOptionsMonitor reloadOnChange works.
if (!File.Exists(userSettingsPath))
{
    var defaultDoc = new JsonAgentSettingsStore().Load();
    JsonAgentSettingsStore.WriteDefaults(userSettingsPath, defaultDoc);
}
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

// For non-Windows, start the host in background and show a minimal Avalonia window
if (!isWindows)
{
    var host = builder.Build();
    _ = Task.Run(() => host.RunAsync());

    // Detect HiDPI scaling from the desktop environment on Linux.
    // Avalonia's X11 backend reads Xft.dpi, but modern DEs (GNOME/KDE)
    // signal scaling via env vars instead. Bridge this gap.
    if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AVALONIA_SCREEN_SCALE_FACTORS")))
    {
        var scale = Environment.GetEnvironmentVariable("GDK_SCALE")
                 ?? Environment.GetEnvironmentVariable("QT_SCALE_FACTOR");
        if (!string.IsNullOrEmpty(scale) && double.TryParse(scale,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed) && parsed > 1.0)
        {
            Environment.SetEnvironmentVariable("AVALONIA_SCREEN_SCALE_FACTORS", parsed.ToString("F1",
                System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AVALONIA_SCREEN_SCALE_FACTORS")))
    {
        var xDpi = TryReadXDpi();
        if (xDpi is > 120)
        {
            var inferredScale = Math.Round(xDpi.Value / 96.0, 1);
            Environment.SetEnvironmentVariable("AVALONIA_SCREEN_SCALE_FACTORS",
                inferredScale.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
        }
        else if (xDpi is null)
        {
            // No desktop env scale variable and xdpyinfo returned nothing
            // (e.g. Wayland without XWayland). Default to 2x so dialogs are
            // readable on modern HiDPI displays.
            Environment.SetEnvironmentVariable("AVALONIA_SCREEN_SCALE_FACTORS", "2.0");
        }
    }

    // Launch minimal Avalonia window so the app behaves like a GUI system-tray app
    AppBuilder.Configure<App>()
        .UseX11()
        .UseSkia()
        .Start((app, args) =>
        {
            var window = new Window
            {
                Title = "Cloud Clipboard",
                Width = 1,
                Height = 1,
                ShowInTaskbar = false,
            };
            App.MainWindow = window;
            window.Show();
            app.Run(window);
        }, Array.Empty<string>());
}
else
{
    var host = builder.Build();
    await host.RunAsync();
}

/// <summary>
/// Tries to read the X server DPI via xdpyinfo. Returns null if unavailable.
/// </summary>
static double? TryReadXDpi()
{
    try
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("xdpyinfo")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
            EnableRaisingEvents = true,
        };
        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(TimeSpan.FromSeconds(3));

        // Parse "resolution: 192x192 dots per inch"
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.TrimStart().StartsWith("resolution:", StringComparison.OrdinalIgnoreCase))
            {
                var parts = line.Split(':', 2)[1].Trim().Split('x');
                if (parts.Length >= 1 && double.TryParse(parts[0].Trim(),
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var dpi))
                {
                    return dpi;
                }
            }
        }
    }
    catch
    {
        // xdpyinfo not available; fall through
    }
    return null;
}

/// <summary>
/// Minimal logging provider that writes to a FileStream (no console).
/// </summary>
public class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    public FileLoggerProvider(Stream stream)
    {
        _writer = new StreamWriter(stream) { AutoFlush = true };
    }
    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, _writer);
    public void Dispose() => _writer.Dispose();
}

public class FileLogger : ILogger
{
    private readonly string _categoryName;
    private readonly StreamWriter _writer;
    public FileLogger(string categoryName, StreamWriter writer)
    {
        _categoryName = categoryName;
        _writer = writer;
    }
    public IDisposable BeginScope<TState>(TState state) => default!;
    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var level = logLevel.ToString().ToUpperInvariant();
        var message = formatter(state, exception);
        _writer.WriteLine($"{timestamp} {level}: {_categoryName} {message}");
        if (exception != null)
            _writer.WriteLine(exception.ToString());
    }
    public System.Type? LoggerType => typeof(FileLogger);
}

public class App : Application
{
    public static App Instance { get; private set; } = default!;
    public static Window? MainWindow { get; set; }

    public override void Initialize()
    {
        Instance = this;
        Styles.Add(new Avalonia.Themes.Simple.SimpleTheme());
        base.Initialize();
    }

    public static void ShowContent(Control content, int width, int height)
    {
        if (MainWindow is not null)
        {
            MainWindow.Content = content;
            MainWindow.Width = width;
            MainWindow.Height = height;
            MainWindow.WindowState = WindowState.Normal;
            MainWindow.ShowInTaskbar = true;
            MainWindow.Activate();
        }
    }

    public static void HideContent()
    {
        if (MainWindow is not null)
        {
            MainWindow.Content = null;
            MainWindow.Width = 1;
            MainWindow.Height = 1;
            MainWindow.ShowInTaskbar = false;
        }
    }
}
