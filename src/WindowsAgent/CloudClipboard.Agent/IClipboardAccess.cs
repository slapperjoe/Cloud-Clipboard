namespace CloudClipboard.Agent;

/// <summary>
/// Abstraction over platform-specific clipboard operations.
/// </summary>
public interface IClipboardAccess
{
    /// <summary>
    /// Reads text from the clipboard.
    /// </summary>
    Task<string?> ReadTextAsync(CancellationToken ct = default);

    /// <summary>
    /// Reads an image (PNG bytes) from the clipboard.
    /// </summary>
    Task<byte[]?> ReadImageAsync(CancellationToken ct = default);

    /// <summary>
    /// Writes text to the clipboard.
    /// </summary>
    Task WriteTextAsync(string text, CancellationToken ct = default);

    /// <summary>
    /// Writes an image (PNG bytes) to the clipboard.
    /// </summary>
    Task WriteImageAsync(byte[] imageData, CancellationToken ct = default);

    /// <summary>
    /// Writes a set of file paths to the clipboard.
    /// </summary>
    Task WriteFilesAsync(string[] filePaths, CancellationToken ct = default);
}
