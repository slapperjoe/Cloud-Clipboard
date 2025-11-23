using System;

namespace CloudClipboard.Agent.Windows.Services;

public interface ILocalUploadTracker
{
    void Record(string ownerId, string itemId, DateTimeOffset createdUtc);
    bool TryConsume(string ownerId, string itemId);
}
