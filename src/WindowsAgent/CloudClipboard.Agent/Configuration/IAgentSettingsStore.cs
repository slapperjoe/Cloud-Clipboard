using CloudClipboard.Agent.Options;

namespace CloudClipboard.Agent.Configuration;

public interface IAgentSettingsStore
{
    AgentOptions Load();
    void Save(AgentOptions options, BackupScope? backupScope = null);
}
