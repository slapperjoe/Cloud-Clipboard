using System;
using System.IO;

namespace CloudClipboard.Agent.Windows.Options;

public sealed class FunctionsDeploymentOptions
{
    public string FunctionAppName { get; set; } = string.Empty;
    public string ResourceGroup { get; set; } = string.Empty;
    public string SubscriptionId { get; set; } = string.Empty;
    public string PackagePath { get; set; } = GetDefaultPackagePath();
    public string Location { get; set; } = "eastus";
    public string StorageAccountName { get; set; } = string.Empty;
    public string PlanName { get; set; } = string.Empty;
    public string PayloadContainer { get; set; } = "clipboardpayloads";
    public string MetadataTable { get; set; } = "ClipboardMetadata";
    public string? LastPackageHash { get; set; }
    public DateTimeOffset? LastDeployedUtc { get; set; }

    public static FunctionsDeploymentOptions CreateDefault()
        => new()
        {
            PackagePath = GetDefaultPackagePath()
        };

    private static string GetDefaultPackagePath()
    {
        try
        {
            var baseDirectory = AppContext.BaseDirectory;
            return Path.Combine(baseDirectory, "CloudClipboard.Functions.zip");
        }
        catch
        {
            return "CloudClipboard.Functions.zip";
        }
    }
}
