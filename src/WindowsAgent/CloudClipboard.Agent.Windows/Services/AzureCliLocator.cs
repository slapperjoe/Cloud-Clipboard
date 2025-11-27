using System;
using System.IO;
using System.Linq;

namespace CloudClipboard.Agent.Windows.Services;

internal static class AzureCliLocator
{
    private static readonly string[] DefaultInstallDirectories = GetDefaultInstallDirectories();

    public static bool TryResolveExecutable(out string executablePath, out string? errorMessage)
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathVariable))
        {
            foreach (var segment in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var normalized = NormalizePathSegment(segment);
                if (normalized.Length == 0)
                {
                    continue;
                }

                if (TryResolveFromDirectory(normalized, out executablePath))
                {
                    errorMessage = null;
                    return true;
                }
            }
        }

        foreach (var directory in DefaultInstallDirectories)
        {
            if (TryResolveFromDirectory(directory, out executablePath))
            {
                errorMessage = null;
                return true;
            }
        }

        executablePath = string.Empty;
        errorMessage = string.IsNullOrWhiteSpace(pathVariable)
            ? "PATH environment variable is empty. Install Azure CLI or update PATH."
            : "Azure CLI (az) not found on PATH. Install it from https://learn.microsoft.com/cli/azure/install-azure-cli.";
        return false;
    }

    private static bool TryResolveFromDirectory(string directory, out string executablePath)
    {
        var azCmd = Path.Combine(directory, "az.cmd");
        if (File.Exists(azCmd))
        {
            executablePath = azCmd;
            return true;
        }

        var azExe = Path.Combine(directory, "az.exe");
        if (File.Exists(azExe))
        {
            executablePath = azExe;
            return true;
        }

        executablePath = string.Empty;
        return false;
    }

    private static string NormalizePathSegment(string segment)
    {
        var expanded = Environment.ExpandEnvironmentVariables(segment);
        var trimmed = expanded.Trim().Trim('\"');
        return trimmed;
    }

    private static string[] GetDefaultInstallDirectories()
    {
        return new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
            }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.Combine(path!, "Microsoft SDKs", "Azure", "CLI2", "wbin"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
