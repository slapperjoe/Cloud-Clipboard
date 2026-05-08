using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CloudClipboard.Core.Models;

namespace CloudClipboard.Agent.Services;

public interface IClipboardUploadQueue
{
    ValueTask EnqueueAsync(ClipboardUploadRequest request, CancellationToken cancellationToken = default);
    IAsyncEnumerable<ClipboardUploadRequest> ReadAllAsync(CancellationToken cancellationToken = default);
}
