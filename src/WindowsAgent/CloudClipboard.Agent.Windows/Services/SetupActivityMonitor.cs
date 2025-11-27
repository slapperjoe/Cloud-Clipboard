using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace CloudClipboard.Agent.Windows.Services;

public interface ISetupActivityMonitor
{
    bool IsBusy { get; }
    IDisposable BeginActivity(string name);
    Task WaitForIdleAsync(CancellationToken cancellationToken, TimeSpan? checkInterval = null);
}

public sealed class SetupActivityMonitor : ISetupActivityMonitor
{
    private readonly ILogger<SetupActivityMonitor> _logger;
    private int _activeCount;

    public SetupActivityMonitor(ILogger<SetupActivityMonitor> logger)
    {
        _logger = logger;
    }

    public bool IsBusy => Volatile.Read(ref _activeCount) > 0;

    public IDisposable BeginActivity(string name)
    {
        var current = Interlocked.Increment(ref _activeCount);
        _logger.LogDebug("Setup activity '{Activity}' started. Active count: {Count}", name, current);
        return new ActivityScope(this, name);
    }

    public async Task WaitForIdleAsync(CancellationToken cancellationToken, TimeSpan? checkInterval = null)
    {
        var delay = checkInterval ?? TimeSpan.FromSeconds(2);
        while (IsBusy && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private void Complete(string name)
    {
        var remaining = Interlocked.Decrement(ref _activeCount);
        _logger.LogDebug("Setup activity '{Activity}' completed. Active count: {Count}", name, remaining);
    }

    private sealed class ActivityScope : IDisposable
    {
        private readonly SetupActivityMonitor _owner;
        private readonly string _name;
        private bool _disposed;

        public ActivityScope(SetupActivityMonitor owner, string name)
        {
            _owner = owner;
            _name = name;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _owner.Complete(_name);
        }
    }
}
