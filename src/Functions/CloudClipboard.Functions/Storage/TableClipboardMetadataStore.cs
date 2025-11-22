using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using CloudClipboard.Core.Abstractions;
using CloudClipboard.Core.Models;
using CloudClipboard.Functions.Options;
using Microsoft.Extensions.Options;

namespace CloudClipboard.Functions.Storage;

public sealed class TableClipboardMetadataStore : IClipboardMetadataStore
{
    private readonly TableClient _tableClient;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private bool _tableInitialized;

    public TableClipboardMetadataStore(IOptions<StorageOptions> options)
    {
        var value = options.Value;
        var connectionString = string.IsNullOrWhiteSpace(value.TableConnectionString)
            ? value.BlobConnectionString
            : value.TableConnectionString;

        _tableClient = new TableClient(connectionString, value.MetadataTable);
    }

    public async Task<ClipboardItemMetadata> AddAsync(ClipboardItemMetadata metadata, CancellationToken cancellationToken = default)
    {
        await EnsureTableExistsAsync(cancellationToken);
        await _tableClient.UpsertEntityAsync(ClipboardItemEntity.From(metadata), TableUpdateMode.Replace, cancellationToken);
        return metadata;
    }

    public async Task<ClipboardItemMetadata?> GetAsync(string ownerId, string itemId, CancellationToken cancellationToken = default)
    {
        await EnsureTableExistsAsync(cancellationToken);
        try
        {
            var result = await _tableClient.GetEntityAsync<ClipboardItemEntity>(ownerId, itemId, cancellationToken: cancellationToken);
            return result.Value.ToMetadata();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<ClipboardItemMetadata>> ListRecentAsync(string ownerId, int take, CancellationToken cancellationToken = default)
    {
        await EnsureTableExistsAsync(cancellationToken);
        var filter = BuildPartitionFilter(ownerId);
        var query = _tableClient.QueryAsync<ClipboardItemEntity>(filter: filter, cancellationToken: cancellationToken);
        var results = new List<ClipboardItemEntity>();
        await foreach (var entity in query)
        {
            results.Add(entity);
        }

        return results
            .OrderByDescending(entity => entity.CreatedUtc)
            .Take(take)
            .Select(entity => entity.ToMetadata())
            .ToArray();
    }

    public async Task<IReadOnlyList<ClipboardItemMetadata>> ListAllAsync(string ownerId, CancellationToken cancellationToken = default)
    {
        await EnsureTableExistsAsync(cancellationToken);
        var filter = BuildPartitionFilter(ownerId);
        var query = _tableClient.QueryAsync<ClipboardItemEntity>(filter: filter, cancellationToken: cancellationToken);
        var results = new List<ClipboardItemEntity>();
        await foreach (var entity in query)
        {
            results.Add(entity);
        }

        return results
            .OrderByDescending(entity => entity.CreatedUtc)
            .Select(entity => entity.ToMetadata())
            .ToArray();
    }

    private static string BuildPartitionFilter(string ownerId)
        => TableClient.CreateQueryFilter($"PartitionKey eq {ownerId}");

    public async Task RemoveAsync(string ownerId, string itemId, CancellationToken cancellationToken = default)
    {
        await EnsureTableExistsAsync(cancellationToken);
        await _tableClient.DeleteEntityAsync(ownerId, itemId, cancellationToken: cancellationToken);
    }

    private async Task EnsureTableExistsAsync(CancellationToken cancellationToken)
    {
        if (_tableInitialized)
        {
            return;
        }

        await _initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (_tableInitialized)
            {
                return;
            }

            await _tableClient.CreateIfNotExistsAsync(cancellationToken);
            _tableInitialized = true;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private sealed record ClipboardItemEntity : ITableEntity
    {
        public string PartitionKey { get; set; } = default!; // OwnerId
        public string RowKey { get; set; } = default!;       // ItemId
        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; }
        public string PayloadType { get; init; } = default!;
        public string BlobName { get; init; } = default!;
        public string ContentType { get; init; } = default!;
        public long ContentLength { get; init; }
        public DateTimeOffset CreatedUtc { get; init; }
        public DateTimeOffset? ExpiresUtc { get; init; }
        public string? FileName { get; init; }
        public bool IsEncrypted { get; init; }
        public string? DeviceName { get; init; }

        public static ClipboardItemEntity From(ClipboardItemMetadata metadata)
            => new()
            {
                PartitionKey = metadata.OwnerId,
                RowKey = metadata.Id,
                PayloadType = metadata.PayloadType.ToString(),
                BlobName = metadata.BlobName,
                ContentType = metadata.ContentType,
                ContentLength = metadata.ContentLength,
                CreatedUtc = metadata.CreatedUtc,
                ExpiresUtc = metadata.ExpiresUtc,
                FileName = metadata.FileName,
                IsEncrypted = metadata.IsEncrypted,
                DeviceName = metadata.Properties.TryGetValue("device", out var device) ? device : null
            };

        public ClipboardItemMetadata ToMetadata()
            => new(
                Id: RowKey,
                OwnerId: PartitionKey,
                PayloadType: Enum.Parse<ClipboardPayloadType>(PayloadType),
                BlobName: BlobName,
                ContentType: ContentType,
                ContentLength: ContentLength,
                CreatedUtc: CreatedUtc,
                ExpiresUtc: ExpiresUtc,
                FileName: FileName,
                IsEncrypted: IsEncrypted,
                Properties: new Dictionary<string, string>
                {
                    ["device"] = DeviceName ?? "unknown"
                }
            );
    }
}
