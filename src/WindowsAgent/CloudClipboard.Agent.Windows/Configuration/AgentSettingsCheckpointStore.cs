using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudClipboard.Agent.Windows.Options;

namespace CloudClipboard.Agent.Windows.Configuration;

internal static class AgentSettingsCheckpointStore
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static void Save(AgentOptions options)
    {
        var path = GetCheckpointPath();
        EnsureDirectory(path);
        var payload = new AgentSettingsDocument { Agent = options };
        var json = JsonSerializer.Serialize(payload, WriteOptions);
        File.WriteAllText(path, json);
    }

    public static AgentOptions? Load()
    {
        var path = GetCheckpointPath();
        if (!File.Exists(path))
        {
            return null;
        }

        var document = JsonSerializer.Deserialize<AgentSettingsDocument>(File.ReadAllText(path), ReadOptions);
        var options = document?.Agent;
        if (options is null)
        {
            return null;
        }

        options.PinnedItems ??= new();
        return options;
    }

    private static string GetCheckpointPath()
    {
        var settingsPath = AgentSettingsPathProvider.GetSettingsPath();
        var directory = Path.GetDirectoryName(settingsPath) ?? AppContext.BaseDirectory;
        var nameWithoutExtension = Path.GetFileNameWithoutExtension(settingsPath);
        return Path.Combine(directory, $"{nameWithoutExtension}.checkpoint.json");
    }

    private static void EnsureDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private sealed class AgentSettingsDocument
    {
        public AgentOptions Agent { get; set; } = new();
    }
}
