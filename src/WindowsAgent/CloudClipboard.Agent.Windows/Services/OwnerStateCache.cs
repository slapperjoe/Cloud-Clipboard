using System;
using System.Threading;
using System.Threading.Tasks;
using CloudClipboard.Core.Models;

namespace CloudClipboard.Agent.Windows.Services;

public sealed class OwnerStateCache : IOwnerStateCache
{
    private readonly object _lock = new();
    private ClipboardOwnerState _state = new(string.Empty, false, null);

    public ClipboardOwnerState State
    {
        get
        {
            lock (_lock)
            {
                return _state;
            }
        }
    }

    public bool IsPaused
    {
        get
        {
            lock (_lock)
            {
                return _state.IsPaused;
            }
        }
    }

    public event EventHandler<ClipboardOwnerState>? StateChanged;

    public void Update(ClipboardOwnerState state)
    {
        bool changed;
        lock (_lock)
        {
            if (_state == state)
            {
                return;
            }

            _state = state;
            changed = true;
        }

        if (changed)
        {
            StateChanged?.Invoke(this, state);
        }
    }

    public async Task WaitForResumeAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsPaused)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }
}
