using System;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using CloudClipboard.Core.Models;
using CloudClipboard.Functions.Options;
using Microsoft.Extensions.Options;

namespace CloudClipboard.Functions.Services;

public sealed class TableOwnerConfigurationStore : IOwnerConfigurationStore
{
    private readonly TableClient _tableClient;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private bool _initialized;

    public TableOwnerConfigurationStore(IOptions<StorageOptions> options)
    {
        var storage = options.Value;
        var connectionString = string.IsNullOrWhiteSpace(storage.TableConnectionString)
            ? storage.BlobConnectionString
            : storage.TableConnectionString;

        _tableClient = new TableClient(connectionString, storage.OwnerConfigurationTable);
    }

    public async Task<OwnerConfiguration?> GetAsync(string ownerId, CancellationToken cancellationToken)
    {
        await EnsureTableAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var response = await _tableClient.GetEntityAsync<OwnerConfigurationEntity>(ownerId, OwnerConfigurationEntity.RowKeyValue, cancellationToken: cancellationToken).ConfigureAwait(false);
            return response.Value.ToModel();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<OwnerConfiguration> SetAsync(string ownerId, string configurationJson, CancellationToken cancellationToken)
    {
        await EnsureTableAsync(cancellationToken).ConfigureAwait(false);
        var entity = new OwnerConfigurationEntity
        {
            PartitionKey = ownerId,
            RowKey = OwnerConfigurationEntity.RowKeyValue,
            ConfigurationJson = configurationJson ?? string.Empty,
            UpdatedUtc = DateTimeOffset.UtcNow
        };

        await _tableClient.UpsertEntityAsync(entity, TableUpdateMode.Replace, cancellationToken).ConfigureAwait(false);
        return entity.ToModel();
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

    private sealed record OwnerConfigurationEntity : ITableEntity
    {
        public const string RowKeyValue = "configuration";

        public string PartitionKey { get; set; } = string.Empty;
        public string RowKey { get; set; } = RowKeyValue;
        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; }
        public string ConfigurationJson { get; set; } = string.Empty;
        public DateTimeOffset? UpdatedUtc { get; set; }

        public OwnerConfiguration ToModel()
        {
            var updated = UpdatedUtc ?? Timestamp ?? DateTimeOffset.UtcNow;
            return new OwnerConfiguration(PartitionKey, ConfigurationJson, updated);
        }
    }
}
