using System;
using System.IO;
using System.Linq;

namespace CloudClipboard.Agent.Windows.Configuration;

public enum BackupScope
{
    Startup,
    ManualSave
}

internal static class AgentSettingsBackup
{
    private const int MaxBackups = 3;

    public static void TryCreateBackup(string settingsPath, BackupScope scope)
    {
        if (string.IsNullOrWhiteSpace(settingsPath) || !File.Exists(settingsPath))
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(settingsPath);
            if (string.IsNullOrEmpty(directory))
            {
                return;
            }

            var backupDirectory = Path.Combine(directory, "backups", GetScopeSegment(scope));
            Directory.CreateDirectory(backupDirectory);

            var nameWithoutExtension = Path.GetFileNameWithoutExtension(settingsPath);
            var extension = Path.GetExtension(settingsPath);
            var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            var backupFileName = string.IsNullOrEmpty(extension)
                ? $"{nameWithoutExtension}.{timestamp}.bak"
                : $"{nameWithoutExtension}.{timestamp}{extension}.bak";
            var backupPath = Path.Combine(backupDirectory, backupFileName);

            File.Copy(settingsPath, backupPath, overwrite: false);

            PruneBackups(backupDirectory, nameWithoutExtension);
        }
        catch (IOException)
        {
            // Intentionally ignore backup failures to avoid blocking startup.
        }
        catch (UnauthorizedAccessException)
        {
            // Ignore; agent can still run even if backup failed.
        }
    }

    private static void PruneBackups(string backupDirectory, string fileStem)
    {
        var pattern = $"{fileStem}.*.bak";
        var files = new DirectoryInfo(backupDirectory)
            .GetFiles(pattern, SearchOption.TopDirectoryOnly)
            .OrderByDescending(f => f.CreationTimeUtc)
            .ToList();

        if (files.Count <= MaxBackups)
        {
            return;
        }

        foreach (var file in files.Skip(MaxBackups))
        {
            try
            {
                file.Delete();
            }
            catch (IOException)
            {
                // Ignore individual deletion failures.
            }
            catch (UnauthorizedAccessException)
            {
                // Ignore; best effort cleanup.
            }
        }
    }

    private static string GetScopeSegment(BackupScope scope)
        => scope switch
        {
            BackupScope.Startup => "startup",
            BackupScope.ManualSave => "manual",
            _ => "other"
        };
}
