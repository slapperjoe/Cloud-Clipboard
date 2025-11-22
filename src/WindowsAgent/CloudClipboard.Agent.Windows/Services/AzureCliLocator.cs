using System;
using System.IO;

namespace CloudClipboard.Agent.Windows.Services;

internal static class AzureCliLocator
{
    public static bool TryResolveExecutable(out string executablePath, out string? errorMessage)
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathVariable))
        {
            executablePath = string.Empty;
            errorMessage = "PATH environment variable is empty. Install Azure CLI or update PATH.";
            return false;
        }

        foreach (var segment in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = segment.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var azCmd = Path.Combine(trimmed, "az.cmd");
            if (File.Exists(azCmd))
            {
                executablePath = azCmd;
                errorMessage = null;
                return true;
            }

            var azExe = Path.Combine(trimmed, "az.exe");
            if (File.Exists(azExe))
            {
                executablePath = azExe;
                errorMessage = null;
                return true;
            }
        }

        executablePath = string.Empty;
        errorMessage = "Azure CLI (az) not found on PATH. Install it from https://learn.microsoft.com/cli/azure/install-azure-cli.";
        return false;
    }
}
