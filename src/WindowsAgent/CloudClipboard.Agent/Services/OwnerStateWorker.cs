using System;
using System.Threading;
using System.Threading.Tasks;
using CloudClipboard.Agent.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudClipboard.Agent.Services;

public sealed class OwnerStateWorker : BackgroundService
{
    private readonly ICloudClipboardClient _client;
    private readonly IOwnerStateCache _cache;
    private readonly IOptionsMonitor<AgentOptions> _options;
    private readonly ILogger<OwnerStateWorker> _logger;

    public OwnerStateWorker(
        ICloudClipboardClient client,
        IOwnerStateCache cache,
        IOptionsMonitor<AgentOptions> options,
        ILogger<OwnerStateWorker> logger)
    {
        _client = client;
        _cache = cache;
        _options = options;
        _logger = logger;
    }

    private int _consecutiveConnectionErrors;
    private const int MaxBackoffSeconds = 30;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var ownerId = _options.CurrentValue.OwnerId;
                if (string.IsNullOrWhiteSpace(ownerId))
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }

                var state = await _client.GetStateAsync(ownerId, stoppingToken);
                _cache.Update(state);
                _consecutiveConnectionErrors = 0;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to refresh owner state");

                if (IsConnectionRefused(ex))
                {
                    _consecutiveConnectionErrors++;
                    var backoff = Math.Min(1 << (_consecutiveConnectionErrors - 1), MaxBackoffSeconds);
                    _logger.LogWarning("Connection refused ({Count} consecutive errors) — backing off for {Seconds}s.", _consecutiveConnectionErrors, backoff);
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(backoff), stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    continue;
                }
            }

            var delaySeconds = Math.Max(5, _options.CurrentValue.OwnerStatePollSeconds);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static bool IsConnectionRefused(Exception ex)
    {
        var current = ex;
        while (current is not null)
        {
            if (current is System.Net.Http.HttpRequestException or System.Net.Sockets.SocketException)
                return true;
            current = current.InnerException;
        }
        return false;
    }
}
