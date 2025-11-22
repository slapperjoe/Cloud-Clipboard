using System;
using System.Threading;
using System.Threading.Tasks;
using CloudClipboard.Core.Models;

namespace CloudClipboard.Agent.Windows.Services;

public interface IOwnerStateCache
{
    ClipboardOwnerState State { get; }
    bool IsPaused { get; }
    event EventHandler<ClipboardOwnerState>? StateChanged;
    void Update(ClipboardOwnerState state);
    Task WaitForResumeAsync(CancellationToken cancellationToken);
}
