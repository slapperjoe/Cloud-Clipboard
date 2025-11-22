using System;

namespace CloudClipboard.Agent.Windows.Services;

public interface IAgentDiagnostics
{
    DateTimeOffset? LastCaptureUtc { get; }
    DateTimeOffset? LastUploadUtc { get; }
    DateTimeOffset? LastUploadFailureUtc { get; }
    DateTimeOffset? LastManualDownloadUtc { get; }
    TimeSpan? LastUploadDuration { get; }
    int PendingUploadCount { get; }
    event EventHandler? Changed;
    void RecordCapture(DateTimeOffset timestamp);
    void IncrementPendingUploads();
    void DecrementPendingUploads();
    void RecordUploadSuccess(DateTimeOffset timestamp, TimeSpan duration);
    void RecordUploadFailure(DateTimeOffset timestamp);
    void RecordManualDownload(DateTimeOffset timestamp);
}
