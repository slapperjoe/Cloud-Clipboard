using System;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using CloudClipboard.Core.Abstractions;
using CloudClipboard.Core.Models;
using CloudClipboard.Functions.Options;
using Microsoft.Extensions.Options;

namespace CloudClipboard.Functions.Storage;

public sealed class TableOwnerStateStore : IClipboardOwnerStateStore
{
    private const string StateRowKey = "_state";

    private readonly TableClient _tableClient;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private bool _tableInitialized;

    public TableOwnerStateStore(IOptions<StorageOptions> options)
    {
        var value = options.Value;
        var connectionString = string.IsNullOrWhiteSpace(value.TableConnectionString)
            ? value.BlobConnectionString
            : value.TableConnectionString;

        _tableClient = new TableClient(connectionString, value.OwnerStateTable);
    }

    public async Task<ClipboardOwnerState> GetAsync(string ownerId, CancellationToken cancellationToken = default)
    {
        await EnsureTableExistsAsync(cancellationToken);
        try
        {
            var result = await _tableClient.GetEntityAsync<StateEntity>(ownerId, StateRowKey, cancellationToken: cancellationToken);
            return result.Value.ToModel();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return new ClipboardOwnerState(ownerId, IsPaused: false, UpdatedUtc: null);
        }
    }

    public async Task<ClipboardOwnerState> SetAsync(string ownerId, bool isPaused, CancellationToken cancellationToken = default)
    {
        await EnsureTableExistsAsync(cancellationToken);
        var entity = StateEntity.From(ownerId, isPaused, DateTimeOffset.UtcNow);
        await _tableClient.UpsertEntityAsync(entity, TableUpdateMode.Replace, cancellationToken);
        return entity.ToModel();
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

    private sealed record StateEntity : ITableEntity
    {
        public string PartitionKey { get; set; } = default!;
        public string RowKey { get; set; } = default!;
        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; }
        public bool IsPaused { get; init; }
        public DateTimeOffset UpdatedUtc { get; init; }

        public static StateEntity From(string ownerId, bool isPaused, DateTimeOffset updatedUtc)
            => new()
            {
                PartitionKey = ownerId,
                RowKey = StateRowKey,
                IsPaused = isPaused,
                UpdatedUtc = updatedUtc
            };

        public ClipboardOwnerState ToModel()
            => new(PartitionKey, IsPaused, UpdatedUtc);
    }
}
