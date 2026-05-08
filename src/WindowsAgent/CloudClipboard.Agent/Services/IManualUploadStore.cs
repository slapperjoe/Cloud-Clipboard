using System;
using CloudClipboard.Core.Models;

namespace CloudClipboard.Agent.Services;

public interface IManualUploadStore
{
    event EventHandler? PendingChanged;
    bool HasPending { get; }
    ClipboardUploadRequest? Pending { get; }
    void Store(ClipboardUploadRequest request);
    bool TryTake(out ClipboardUploadRequest? request);
    void Clear();
}
