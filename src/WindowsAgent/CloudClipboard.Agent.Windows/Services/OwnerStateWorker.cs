using System;
using System.Threading;
using System.Threading.Tasks;
using CloudClipboard.Agent.Windows.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudClipboard.Agent.Windows.Services;

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
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to refresh owner state");
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
}
