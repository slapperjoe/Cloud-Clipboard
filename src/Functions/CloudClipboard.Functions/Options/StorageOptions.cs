namespace CloudClipboard.Functions.Options;

public sealed class StorageOptions
{
    public string BlobConnectionString { get; set; } = "UseDevelopmentStorage=true";
    public string PayloadContainer { get; set; } = "clipboardpayloads";
    public string TableConnectionString { get; set; } = "UseDevelopmentStorage=true";
    public string MetadataTable { get; set; } = "ClipboardItems";
    public string OwnerStateTable { get; set; } = "ClipboardOwnerState";
    public string NotificationsTable { get; set; } = "ClipboardNotifications";
}
