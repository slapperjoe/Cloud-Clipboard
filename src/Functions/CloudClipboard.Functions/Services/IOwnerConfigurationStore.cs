using System.Threading;
using System.Threading.Tasks;
using CloudClipboard.Core.Models;

namespace CloudClipboard.Functions.Services;

public interface IOwnerConfigurationStore
{
    Task<OwnerConfiguration?> GetAsync(string ownerId, CancellationToken cancellationToken);
    Task<OwnerConfiguration> SetAsync(string ownerId, string configurationJson, CancellationToken cancellationToken);
}
