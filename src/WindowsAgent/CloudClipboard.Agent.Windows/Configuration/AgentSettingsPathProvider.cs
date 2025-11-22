using System;
using System.IO;

namespace CloudClipboard.Agent.Windows.Configuration;

public static class AgentSettingsPathProvider
{
    private const string SettingsFileName = "agentsettings.json";

    public static string GetSettingsPath()
        => Path.Combine(GetSettingsDirectory(), SettingsFileName);

    public static string GetSettingsDirectory()
    {
        var baseDirectory = AppContext.BaseDirectory;
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            baseDirectory = Environment.CurrentDirectory;
        }

        Directory.CreateDirectory(baseDirectory);
        return baseDirectory;
    }
}
