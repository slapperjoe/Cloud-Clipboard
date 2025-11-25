using System;
using System.Text;

namespace CloudClipboard.Agent.Windows.Services;

internal static class ProvisioningNameGenerator
{
    public static string CreateResourceGroupName(string ownerId)
        => TrimAndNormalize($"cloudclip-{Sanitize(ownerId)}-rg", 80);

    public static string CreateFunctionAppName(string ownerId)
        => TrimAndNormalize($"cloudclip-{Sanitize(ownerId)}", 60);

    public static string CreatePlanName(string ownerId)
        => TrimAndNormalize($"cloudclip-{Sanitize(ownerId)}-plan", 60);

    public static string CreateStorageAccountName(string ownerId)
    {
        var cleaned = new StringBuilder();
        foreach (var ch in ownerId)
        {
            if (char.IsLetterOrDigit(ch))
            {
                cleaned.Append(char.ToLowerInvariant(ch));
            }
        }

        if (cleaned.Length < 8)
        {
            cleaned.Append(Guid.NewGuid().ToString("N")[..(8 - cleaned.Length)]);
        }

        var baseName = $"clip{cleaned}";
        if (baseName.Length > 24)
        {
            baseName = baseName[..24];
        }

        return baseName;
    }

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch) || ch == '-')
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
            else if (char.IsWhiteSpace(ch) || ch is '_' or '.')
            {
                builder.Append('-');
            }
        }

        var sanitized = builder.ToString();
        while (sanitized.Contains("--", StringComparison.Ordinal))
        {
            sanitized = sanitized.Replace("--", "-", StringComparison.Ordinal);
        }

        return sanitized.Trim('-');
    }

    private static string TrimAndNormalize(string value, int maxLength)
    {
        var sanitized = Sanitize(value);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = $"cloudclip-{Guid.NewGuid():N}";
        }

        if (sanitized.Length > maxLength)
        {
            sanitized = sanitized[..maxLength];
        }

        return sanitized;
    }
}
