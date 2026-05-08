namespace CloudClipboard.Agent.Services;

public sealed record ProvisioningProgressUpdate(string Message, double? PercentComplete = null, bool IsVerbose = false);
