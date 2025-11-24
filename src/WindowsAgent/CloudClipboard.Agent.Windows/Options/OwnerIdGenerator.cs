using System;
using System.Text;

namespace CloudClipboard.Agent.Windows.Options;

internal static class OwnerIdGenerator
{
    public static string GetDefaultOwnerId()
    {
        var candidate = BuildLoginName();
        var sanitized = Sanitize(candidate);
        if (!string.IsNullOrWhiteSpace(sanitized))
        {
            return sanitized;
        }

        return $"owner-{Guid.NewGuid():N}"[..12];
    }

    public static string CreateOwnerIdFromLogin(string? loginName)
    {
        var sanitized = Sanitize(loginName);
        return string.IsNullOrWhiteSpace(sanitized) ? GetDefaultOwnerId() : sanitized;
    }

    private static string BuildLoginName()
    {
        var user = Environment.UserName;
        if (string.IsNullOrWhiteSpace(user))
        {
            return Environment.MachineName;
        }

        var domain = Environment.UserDomainName;
        if (string.IsNullOrWhiteSpace(domain) || string.Equals(domain, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            return user;
        }

        return $"{domain}-{user}";
    }

    private static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
                continue;
            }

            if (ch is '-' or '_')
            {
                builder.Append(ch);
                continue;
            }

            if (ch is '.' or '@' or '\\')
            {
                builder.Append('-');
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                builder.Append('-');
            }
        }

        var sanitized = builder.ToString().Trim('-');
        while (sanitized.Contains("--", StringComparison.Ordinal))
        {
            sanitized = sanitized.Replace("--", "-", StringComparison.Ordinal);
        }

        const int maxLength = 64;
        if (sanitized.Length > maxLength)
        {
            sanitized = sanitized[..maxLength];
        }

        return sanitized;
    }
}
