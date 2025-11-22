using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using CloudClipboard.Core.Abstractions;
using CloudClipboard.Functions.Options;
using Microsoft.Extensions.Options;

namespace CloudClipboard.Functions.Storage;

public sealed class BlobClipboardPayloadStore : IClipboardPayloadStore
{
    private readonly BlobContainerClient _containerClient;

    public BlobClipboardPayloadStore(IOptions<StorageOptions> options)
    {
        var value = options.Value;
        var blobServiceClient = new BlobServiceClient(value.BlobConnectionString);
        _containerClient = blobServiceClient.GetBlobContainerClient(value.PayloadContainer);
    }

    public async Task UploadAsync(string blobName, Stream stream, string contentType, CancellationToken cancellationToken = default)
    {
        await _containerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        stream.Position = 0;
        var blobClient = _containerClient.GetBlobClient(blobName);
        var uploadOptions = new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = contentType }
        };
        await blobClient.UploadAsync(stream, uploadOptions, cancellationToken);
    }

    public async Task<Stream> OpenReadAsync(string blobName, CancellationToken cancellationToken = default)
    {
        await _containerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        var blobClient = _containerClient.GetBlobClient(blobName);
        var response = await blobClient.DownloadStreamingAsync(cancellationToken: cancellationToken);
        return response.Value.Content;
    }

    public async Task DeleteAsync(string blobName, CancellationToken cancellationToken = default)
    {
        await _containerClient.DeleteBlobIfExistsAsync(blobName, cancellationToken: cancellationToken);
    }
}
