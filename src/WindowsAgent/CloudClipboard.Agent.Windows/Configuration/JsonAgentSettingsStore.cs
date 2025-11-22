using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudClipboard.Agent.Windows.Options;

namespace CloudClipboard.Agent.Windows.Configuration;

public sealed class JsonAgentSettingsStore : IAgentSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
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

    public AgentOptions Load()
    {
        var path = AgentSettingsPathProvider.GetSettingsPath();
        if (!File.Exists(path))
        {
            var defaults = CreateDefaults();
            WriteSettingsFile(path, defaults);
            return defaults;
        }

        try
        {
            var document = JsonSerializer.Deserialize<AgentSettingsDocument>(File.ReadAllText(path), SerializerOptions);
            var result = document?.Agent ?? CreateDefaults();
            result.PinnedItems ??= new();
            result.FunctionsDeployment ??= FunctionsDeploymentOptions.CreateDefault();
            return result;
        }
        catch (JsonException)
        {
            var defaults = CreateDefaults();
            WriteSettingsFile(path, defaults);
            return defaults;
        }
        catch (IOException)
        {
            var defaults = CreateDefaults();
            WriteSettingsFile(path, defaults);
            return defaults;
        }
    }

    public void Save(AgentOptions options, BackupScope? backupScope = null)
    {
        var path = AgentSettingsPathProvider.GetSettingsPath();
        options.PinnedItems ??= new();
        if (backupScope is BackupScope scope)
        {
            AgentSettingsBackup.TryCreateBackup(path, scope);
        }
        WriteSettingsFile(path, options);
    }

    private sealed class AgentSettingsDocument
    {
        public AgentOptions Agent { get; set; } = new();
    }

    private static AgentOptions CreateDefaults()
        => new()
        {
            PinnedItems = new(),
            FunctionsDeployment = FunctionsDeploymentOptions.CreateDefault()
        };

    private static void WriteSettingsFile(string path, AgentOptions options)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var payload = new AgentSettingsDocument { Agent = options };
        var json = JsonSerializer.Serialize(payload, WriteOptions);
        File.WriteAllText(path, json);
    }
}
