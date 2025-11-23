using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace CloudClipboard.Agent.Windows.Services;

public sealed class LocalUploadTracker : ILocalUploadTracker
{
    private readonly ConcurrentDictionary<string, UploadBucket> _buckets = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);

    public void Record(string ownerId, string itemId, DateTimeOffset createdUtc)
    {
        if (string.IsNullOrWhiteSpace(ownerId) || string.IsNullOrWhiteSpace(itemId))
        {
            return;
        }

        var bucket = _buckets.GetOrAdd(ownerId, _ => new UploadBucket());
        bucket.Add(itemId, createdUtc);
    }

    public bool TryConsume(string ownerId, string itemId)
    {
        if (string.IsNullOrWhiteSpace(ownerId) || string.IsNullOrWhiteSpace(itemId))
        {
            return false;
        }

        if (!_buckets.TryGetValue(ownerId, out var bucket))
        {
            return false;
        }

        return bucket.Remove(itemId);
    }

    private sealed class UploadBucket
    {
        private readonly LinkedList<TrackedUpload> _uploads = new();
        private readonly object _lock = new();

        public void Add(string itemId, DateTimeOffset createdUtc)
        {
            lock (_lock)
            {
                _uploads.AddLast(new TrackedUpload(itemId, createdUtc));
                Trim();
            }
        }

        public bool Remove(string itemId)
        {
            lock (_lock)
            {
                Trim();
                var node = _uploads.First;
                while (node is not null)
                {
                    if (string.Equals(node.Value.ItemId, itemId, StringComparison.OrdinalIgnoreCase))
                    {
                        _uploads.Remove(node);
                        return true;
                    }

                    node = node.Next;
                }
            }

            return false;
        }

        private void Trim()
        {
            var cutoff = DateTimeOffset.UtcNow - Retention;
            while (_uploads.First is { Value: { CreatedUtc: var created } } node && created < cutoff)
            {
                _uploads.RemoveFirst();
            }
        }
    }

    private readonly record struct TrackedUpload(string ItemId, DateTimeOffset CreatedUtc);
}
