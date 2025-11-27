using System.Threading;
using System.Threading.Tasks;

namespace CloudClipboard.Agent.Windows.Services;

public interface IAzureCliDeviceLoginPrompt
{
    Task<bool> PromptAsync(string azExecutablePath, CancellationToken cancellationToken);
}
