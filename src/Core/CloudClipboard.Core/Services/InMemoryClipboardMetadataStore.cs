using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CloudClipboard.Core.Abstractions;
using CloudClipboard.Core.Models;

namespace CloudClipboard.Core.Services;

/// <summary>
/// Lightweight in-memory metadata store that simplifies local testing.
/// </summary>
public sealed class InMemoryClipboardMetadataStore : IClipboardMetadataStore
{
    private readonly ConcurrentDictionary<(string OwnerId, string ItemId), ClipboardItemMetadata> _items = new();

    public Task<ClipboardItemMetadata> AddAsync(ClipboardItemMetadata metadata, CancellationToken cancellationToken = default)
    {
        _items[(metadata.OwnerId, metadata.Id)] = metadata;
        return Task.FromResult(metadata);
    }

    public Task<ClipboardItemMetadata?> GetAsync(string ownerId, string itemId, CancellationToken cancellationToken = default)
    {
        _items.TryGetValue((ownerId, itemId), out var metadata);
        return Task.FromResult(metadata);
    }

    public Task<IReadOnlyList<ClipboardItemMetadata>> ListRecentAsync(string ownerId, int take, CancellationToken cancellationToken = default)
    {
        var items = _items
            .Where(entry => entry.Key.OwnerId == ownerId)
            .Select(entry => entry.Value)
            .OrderByDescending(item => item.CreatedUtc)
            .Take(take)
            .ToArray();

        return Task.FromResult<IReadOnlyList<ClipboardItemMetadata>>(items);
    }

    public Task<IReadOnlyList<ClipboardItemMetadata>> ListAllAsync(string ownerId, CancellationToken cancellationToken = default)
    {
        var items = _items
            .Where(entry => entry.Key.OwnerId == ownerId)
            .Select(entry => entry.Value)
            .OrderByDescending(item => item.CreatedUtc)
            .ToArray();

        return Task.FromResult<IReadOnlyList<ClipboardItemMetadata>>(items);
    }

    public Task RemoveAsync(string ownerId, string itemId, CancellationToken cancellationToken = default)
    {
        _items.TryRemove((ownerId, itemId), out _);
        return Task.CompletedTask;
    }
}
