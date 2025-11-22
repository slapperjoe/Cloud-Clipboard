namespace CloudClipboard.Functions.Options;

public sealed class PubSubOptions
{
    public string ConnectionString { get; set; } = string.Empty;
    public string Hub { get; set; } = "clipboard";
    public string OwnerGroupPrefix { get; set; } = "owner";
    public int TokenTtlMinutes { get; set; } = 60;
}
