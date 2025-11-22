using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using CloudClipboard.Core.Models;
using CloudClipboard.Functions.Options;
using Microsoft.Extensions.Options;

namespace CloudClipboard.Functions.Services;

public sealed class TableClipboardNotificationService : IClipboardNotificationService
{
    private readonly TableClient _tableClient;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private bool _initialized;

    public TableClipboardNotificationService(IOptions<StorageOptions> options)
    {
        var value = options.Value;
        var connectionString = string.IsNullOrWhiteSpace(value.TableConnectionString)
            ? value.BlobConnectionString
            : value.TableConnectionString;

        _tableClient = new TableClient(connectionString, value.NotificationsTable);
    }

    public async Task PublishAsync(string ownerId, ClipboardItemMetadata metadata, CancellationToken cancellationToken)
    {
        // Notifications piggy-back on Table Storage so the Functions app can run without extra services.
        await EnsureTableAsync(cancellationToken).ConfigureAwait(false);
        var entity = new NotificationEntity
        {
            PartitionKey = ownerId,
            RowKey = BuildRowKey(),
            ItemId = metadata.Id,
            CreatedUtc = metadata.CreatedUtc
        };

        await _tableClient.AddEntityAsync(entity, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ClipboardNotification>> DrainAsync(string ownerId, int maxItems, CancellationToken cancellationToken)
    {
        // Drain removes notifications after reading so the agent does not see duplicates when long-polling.
        await EnsureTableAsync(cancellationToken).ConfigureAwait(false);
            var filter = TableClient.CreateQueryFilter($"PartitionKey eq {ownerId}");
            var query = _tableClient.QueryAsync<NotificationEntity>(filter, cancellationToken: cancellationToken);
        var items = new List<NotificationEntity>();
        await foreach (var entity in query.ConfigureAwait(false))
        {
            items.Add(entity);
            if (items.Count >= maxItems)
            {
                break;
            }
        }

        if (items.Count == 0)
        {
            return Array.Empty<ClipboardNotification>();
        }

        foreach (var entity in items)
        {
            await _tableClient.DeleteEntityAsync(entity.PartitionKey, entity.RowKey, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        return items
            .OrderBy(e => e.CreatedUtc)
            .Select(e => new ClipboardNotification(e.PartitionKey, e.ItemId, e.CreatedUtc))
            .ToArray();
    }

    private async Task EnsureTableAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            await _tableClient.CreateIfNotExistsAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private static string BuildRowKey()
    {
        // Millisecond precision keeps entities ordered for quick draining across multiple uploads.
        return $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():D019}-{Guid.NewGuid():N}";
    }

    private sealed record NotificationEntity : ITableEntity
    {
        public string PartitionKey { get; set; } = default!;
        public string RowKey { get; set; } = default!;
        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; }
        public string ItemId { get; init; } = string.Empty;
        public DateTimeOffset CreatedUtc { get; init; }
    }
}
