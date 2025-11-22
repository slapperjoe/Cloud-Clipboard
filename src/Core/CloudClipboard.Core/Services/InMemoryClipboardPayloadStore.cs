using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CloudClipboard.Core.Abstractions;

namespace CloudClipboard.Core.Services;

/// <summary>
/// Non-durable payload store useful for unit tests and offline development.
/// </summary>
public sealed class InMemoryClipboardPayloadStore : IClipboardPayloadStore
{
    private readonly ConcurrentDictionary<string, byte[]> _payloads = new();

    public Task UploadAsync(string blobName, Stream stream, string contentType, CancellationToken cancellationToken = default)
    {
        using var memoryStream = new MemoryStream();
        stream.CopyTo(memoryStream);
        _payloads[blobName] = memoryStream.ToArray();
        return Task.CompletedTask;
    }

    public Task<Stream> OpenReadAsync(string blobName, CancellationToken cancellationToken = default)
    {
        if (!_payloads.TryGetValue(blobName, out var buffer))
        {
            throw new FileNotFoundException($"Blob '{blobName}' was not found in the in-memory payload store.");
        }

        Stream stream = new MemoryStream(buffer, writable: false);
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string blobName, CancellationToken cancellationToken = default)
    {
        _payloads.TryRemove(blobName, out _);
        return Task.CompletedTask;
    }
}
