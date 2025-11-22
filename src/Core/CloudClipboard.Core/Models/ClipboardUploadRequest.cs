namespace CloudClipboard.Core.Models;

public sealed record ClipboardUploadRequest(
    string OwnerId,
    ClipboardPayloadDescriptor Payload,
    string? DeviceName = null,
    string? FileName = null,
    bool EncryptPayload = false
);
