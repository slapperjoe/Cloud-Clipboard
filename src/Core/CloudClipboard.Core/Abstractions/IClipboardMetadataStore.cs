using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CloudClipboard.Core.Models;

namespace CloudClipboard.Core.Abstractions;

public interface IClipboardMetadataStore
{
    Task<ClipboardItemMetadata> AddAsync(ClipboardItemMetadata metadata, CancellationToken cancellationToken = default);
    Task<ClipboardItemMetadata?> GetAsync(string ownerId, string itemId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ClipboardItemMetadata>> ListRecentAsync(string ownerId, int take, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ClipboardItemMetadata>> ListAllAsync(string ownerId, CancellationToken cancellationToken = default);
    Task RemoveAsync(string ownerId, string itemId, CancellationToken cancellationToken = default);
}
