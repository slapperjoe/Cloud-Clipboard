using System.Threading;
using System.Threading.Tasks;
using CloudClipboard.Core.Models;

namespace CloudClipboard.Core.Abstractions;

public interface IClipboardOwnerStateStore
{
    Task<ClipboardOwnerState> GetAsync(string ownerId, CancellationToken cancellationToken = default);
    Task<ClipboardOwnerState> SetAsync(string ownerId, bool isPaused, CancellationToken cancellationToken = default);
}
