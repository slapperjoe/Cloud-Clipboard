using System;
using System.Threading;

namespace CloudClipboard.Agent.Windows.Services;

public sealed class AgentDiagnostics : IAgentDiagnostics
{
    private readonly object _lock = new();
    private DateTimeOffset? _lastCaptureUtc;
    private DateTimeOffset? _lastUploadUtc;
    private DateTimeOffset? _lastUploadFailureUtc;
    private DateTimeOffset? _lastManualDownloadUtc;
    private TimeSpan? _lastUploadDuration;
    private int _pendingUploadCount;

    public event EventHandler? Changed;

    public DateTimeOffset? LastCaptureUtc
    {
        get { lock (_lock) { return _lastCaptureUtc; } }
    }

    public DateTimeOffset? LastUploadUtc
    {
        get { lock (_lock) { return _lastUploadUtc; } }
    }

    public DateTimeOffset? LastUploadFailureUtc
    {
        get { lock (_lock) { return _lastUploadFailureUtc; } }
    }

    public DateTimeOffset? LastManualDownloadUtc
    {
        get { lock (_lock) { return _lastManualDownloadUtc; } }
    }

    public TimeSpan? LastUploadDuration
    {
        get { lock (_lock) { return _lastUploadDuration; } }
    }

    public int PendingUploadCount => Volatile.Read(ref _pendingUploadCount);

    public void RecordCapture(DateTimeOffset timestamp)
    {
        lock (_lock)
        {
            _lastCaptureUtc = timestamp;
        }

        RaiseChanged();
    }

    public void IncrementPendingUploads()
    {
        Interlocked.Increment(ref _pendingUploadCount);
        RaiseChanged();
    }

    public void DecrementPendingUploads()
    {
        var newValue = Interlocked.Decrement(ref _pendingUploadCount);
        if (newValue < 0)
        {
            Interlocked.Exchange(ref _pendingUploadCount, 0);
        }

        RaiseChanged();
    }

    public void RecordUploadSuccess(DateTimeOffset timestamp, TimeSpan duration)
    {
        lock (_lock)
        {
            _lastUploadUtc = timestamp;
            _lastUploadDuration = duration;
        }

        RaiseChanged();
    }

    public void RecordUploadFailure(DateTimeOffset timestamp)
    {
        lock (_lock)
        {
            _lastUploadFailureUtc = timestamp;
        }

        RaiseChanged();
    }

    public void RecordManualDownload(DateTimeOffset timestamp)
    {
        lock (_lock)
        {
            _lastManualDownloadUtc = timestamp;
        }

        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
