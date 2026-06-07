using System;
using System.IO;
using System.Linq;

namespace CloudClipboard.Agent.Services;

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

        // Linux/Unix bare binary (no extension)
        var az = Path.Combine(directory, "az");
        if (File.Exists(az))
        {
            executablePath = az;
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
        var directories = new System.Collections.Generic.List<string>();

        // Windows install locations
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (var basePath in new[] { programFilesX86, programFiles })
        {
            if (!string.IsNullOrWhiteSpace(basePath))
            {
                directories.Add(Path.Combine(basePath, "Microsoft SDKs", "Azure", "CLI2", "wbin"));
            }
        }

        // Linux / macOS install locations
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            directories.Add(Path.Combine(home, ".local", "bin"));
            directories.Add(Path.Combine(home, ".azure-cli", "bin"));
            directories.Add(Path.Combine(home, "bin"));
            directories.Add(Path.Combine(home, "lib", "azure-cli", "bin"));
        }

        directories.Add("/usr/local/bin");
        directories.Add("/usr/bin");

        return directories
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
