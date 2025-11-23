using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using CloudClipboard.Core.Models;
using CloudClipboard.Core.Services;
using Xunit;

namespace CloudClipboard.Functions.Tests;

public sealed class ClipboardCoordinatorTests
{
    [Fact]
    public async Task SaveAsync_PersistsMetadataAndPayload()
    {
        var metadataStore = new InMemoryClipboardMetadataStore();
        var payloadStore = new InMemoryClipboardPayloadStore();
        var serializer = new ClipboardPayloadSerializer();
        var fixedTime = new FixedTimeProvider(new DateTimeOffset(2025, 01, 01, 12, 0, 0, TimeSpan.Zero));
        var coordinator = new ClipboardCoordinator(metadataStore, payloadStore, serializer, fixedTime);

        var descriptor = BuildTextDescriptor("hello cloud clipboard");
        var request = new ClipboardUploadRequest("owner-a", descriptor, DeviceName: "laptop", FileName: "snippet.txt");

        var metadata = await coordinator.SaveAsync(request);

        Assert.Equal("owner-a", metadata.OwnerId);
        Assert.Equal(ClipboardPayloadType.Text, metadata.PayloadType);
        Assert.Equal("snippet.txt", metadata.FileName);
        Assert.Equal(fixedTime.GetUtcNow(), metadata.CreatedUtc);

        var storedMetadata = await metadataStore.GetAsync("owner-a", metadata.Id);
        Assert.NotNull(storedMetadata);
        Assert.Equal(metadata.BlobName, storedMetadata!.BlobName);

        using var payloadStream = await payloadStore.OpenReadAsync(metadata.BlobName);
        using var reader = new StreamReader(payloadStream, Encoding.UTF8);
        var text = await reader.ReadToEndAsync();
        Assert.Equal("hello cloud clipboard", text);
    }

    [Fact]
    public async Task DeleteOwnerAsync_RemovesMetadataAndPayloads()
    {
        var metadataStore = new InMemoryClipboardMetadataStore();
        var payloadStore = new InMemoryClipboardPayloadStore();
        var serializer = new ClipboardPayloadSerializer();
        var coordinator = new ClipboardCoordinator(metadataStore, payloadStore, serializer);

        var descriptor = BuildTextDescriptor("first");
        var secondDescriptor = BuildTextDescriptor("second");
        var first = await coordinator.SaveAsync(new ClipboardUploadRequest("owner-b", descriptor));
        var second = await coordinator.SaveAsync(new ClipboardUploadRequest("owner-b", secondDescriptor));

        var deleted = await coordinator.DeleteOwnerAsync("owner-b");

        Assert.Equal(2, deleted);
        var items = await metadataStore.ListAllAsync("owner-b");
        Assert.Empty(items);
        await Assert.ThrowsAsync<FileNotFoundException>(async () => await payloadStore.OpenReadAsync(first.BlobName));
        await Assert.ThrowsAsync<FileNotFoundException>(async () => await payloadStore.OpenReadAsync(second.BlobName));
    }

    private static ClipboardPayloadDescriptor BuildTextDescriptor(string text)
    {
        var buffer = Encoding.UTF8.GetBytes(text);
        return new ClipboardPayloadDescriptor(
            ClipboardPayloadType.Text,
            new[]
            {
                new ClipboardPayloadPart(
                    "payload.txt",
                    "text/plain",
                    buffer.Length,
                    () => new MemoryStream(buffer, writable: false))
            },
            PreferredContentType: "text/plain");
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
            => _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
