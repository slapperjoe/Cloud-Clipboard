namespace CloudClipboard.Core.Models;

/// <summary>
/// Describes the primary representation being transported through the clipboard pipeline.
/// </summary>
public enum ClipboardPayloadType
{
    Text = 0,
    FileSet = 1,
    Image = 2
}
