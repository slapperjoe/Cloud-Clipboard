using System;
using CloudClipboard.Core.Models;

namespace CloudClipboard.Agent.Windows.Services;

public interface IManualUploadStore
{
    event EventHandler? PendingChanged;
    bool HasPending { get; }
    ClipboardUploadRequest? Pending { get; }
    void Store(ClipboardUploadRequest request);
    bool TryTake(out ClipboardUploadRequest? request);
    void Clear();
}
