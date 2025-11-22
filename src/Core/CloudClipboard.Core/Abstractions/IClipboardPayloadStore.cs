using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CloudClipboard.Core.Abstractions;

public interface IClipboardPayloadStore
{
    Task UploadAsync(string blobName, Stream stream, string contentType, CancellationToken cancellationToken = default);
    Task<Stream> OpenReadAsync(string blobName, CancellationToken cancellationToken = default);
    Task DeleteAsync(string blobName, CancellationToken cancellationToken = default);
}
