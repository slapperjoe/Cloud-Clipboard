using CloudClipboard.Core.Abstractions;
using CloudClipboard.Core.Services;
using CloudClipboard.Functions.Options;
using CloudClipboard.Functions.Services;
using CloudClipboard.Functions.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureAppConfiguration((context, builder) =>
    {
        builder.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
               .AddJsonFile($"appsettings.{context.HostingEnvironment.EnvironmentName}.json", optional: true)
               .AddEnvironmentVariables();
    })
    .ConfigureServices((context, services) =>
    {
        services.Configure<StorageOptions>(context.Configuration.GetSection("Storage"));
        services.Configure<PubSubOptions>(context.Configuration.GetSection("Notifications:PubSub"));
        services.AddSingleton<IClipboardMetadataStore, TableClipboardMetadataStore>();
        services.AddSingleton<IClipboardPayloadStore, BlobClipboardPayloadStore>();
        services.AddSingleton<IClipboardOwnerStateStore, TableOwnerStateStore>();
        services.AddSingleton<ClipboardPayloadSerializer>();
        services.AddSingleton<ClipboardCoordinator>();
        services.AddSingleton<IClipboardNotificationService, TableClipboardNotificationService>();
        services.AddSingleton<IRealtimeClipboardNotificationService, WebPubSubNotificationService>();
        services.AddSingleton<IOwnerConfigurationStore, TableOwnerConfigurationStore>();
    })
    .Build();

host.Run();
