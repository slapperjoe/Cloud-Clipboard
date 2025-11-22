using System;
using CloudClipboard.Core.Models;

namespace CloudClipboard.Agent.Windows.Services;

public sealed class ManualUploadStore : IManualUploadStore
{
    private readonly object _sync = new();
    private ClipboardUploadRequest? _pending;

    public event EventHandler? PendingChanged;

    public bool HasPending
    {
        get
        {
            lock (_sync)
            {
                return _pending is not null;
            }
        }
    }

    public ClipboardUploadRequest? Pending
    {
        get
        {
            lock (_sync)
            {
                return _pending;
            }
        }
    }

    public void Store(ClipboardUploadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_sync)
        {
            _pending = request;
        }

        OnChanged();
    }

    public bool TryTake(out ClipboardUploadRequest? request)
    {
        lock (_sync)
        {
            if (_pending is null)
            {
                request = null;
                return false;
            }

            request = _pending;
            _pending = null;
        }

        OnChanged();
        return true;
    }

    public void Clear()
    {
        var changed = false;
        lock (_sync)
        {
            if (_pending is not null)
            {
                _pending = null;
                changed = true;
            }
        }

        if (changed)
        {
            OnChanged();
        }
    }

    private void OnChanged() => PendingChanged?.Invoke(this, EventArgs.Empty);
}
