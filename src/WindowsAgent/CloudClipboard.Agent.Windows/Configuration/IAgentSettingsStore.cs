using CloudClipboard.Agent.Windows.Options;

namespace CloudClipboard.Agent.Windows.Configuration;

public interface IAgentSettingsStore
{
    AgentOptions Load();
    void Save(AgentOptions options, BackupScope? backupScope = null);
}
