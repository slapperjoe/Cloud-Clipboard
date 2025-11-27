using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CloudClipboard.Core.Models;
using CloudClipboard.Core.Services;

namespace CloudClipboard.Agent.Windows.Services;

public sealed class SetupAwareCloudClipboardClient : ICloudClipboardClient, IDisposable
{
    private readonly HttpCloudClipboardClient _inner;
    private readonly ISetupActivityMonitor _activityMonitor;

    public SetupAwareCloudClipboardClient(HttpCloudClipboardClient inner, ISetupActivityMonitor activityMonitor)
    {
        _inner = inner;
        _activityMonitor = activityMonitor;
    }

    public async Task<ClipboardItemDto?> UploadAsync(ClipboardUploadRequest request, CancellationToken cancellationToken = default)
    {
        await WaitForSetupAsync(cancellationToken).ConfigureAwait(false);
        return await _inner.UploadAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ClipboardItemDto>> ListAsync(string ownerId, int take, CancellationToken cancellationToken = default)
    {
        await WaitForSetupAsync(cancellationToken).ConfigureAwait(false);
        return await _inner.ListAsync(ownerId, take, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ClipboardItemDto?> DownloadAsync(string ownerId, string itemId, CancellationToken cancellationToken = default)
    {
        await WaitForSetupAsync(cancellationToken).ConfigureAwait(false);
        return await _inner.DownloadAsync(ownerId, itemId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ClipboardOwnerState> GetStateAsync(string ownerId, CancellationToken cancellationToken = default)
    {
        await WaitForSetupAsync(cancellationToken).ConfigureAwait(false);
        return await _inner.GetStateAsync(ownerId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ClipboardOwnerState> SetStateAsync(string ownerId, bool isPaused, CancellationToken cancellationToken = default)
    {
        await WaitForSetupAsync(cancellationToken).ConfigureAwait(false);
        return await _inner.SetStateAsync(ownerId, isPaused, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteOwnerAsync(string ownerId, CancellationToken cancellationToken = default)
    {
        await WaitForSetupAsync(cancellationToken).ConfigureAwait(false);
        await _inner.DeleteOwnerAsync(ownerId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ClipboardNotificationEvent>> PollNotificationsAsync(string ownerId, int timeoutSeconds, CancellationToken cancellationToken = default)
    {
        await WaitForSetupAsync(cancellationToken).ConfigureAwait(false);
        return await _inner.PollNotificationsAsync(ownerId, timeoutSeconds, cancellationToken).ConfigureAwait(false);
    }

    public async Task<NotificationConnectionInfo?> GetNotificationConnectionAsync(string ownerId, CancellationToken cancellationToken = default)
    {
        await WaitForSetupAsync(cancellationToken).ConfigureAwait(false);
        return await _inner.GetNotificationConnectionAsync(ownerId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OwnerConfiguration?> GetOwnerConfigurationAsync(string ownerId, CancellationToken cancellationToken = default)
    {
        await WaitForSetupAsync(cancellationToken).ConfigureAwait(false);
        return await _inner.GetOwnerConfigurationAsync(ownerId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OwnerConfiguration> SetOwnerConfigurationAsync(string ownerId, string configurationJson, CancellationToken cancellationToken = default)
    {
        await WaitForSetupAsync(cancellationToken).ConfigureAwait(false);
        return await _inner.SetOwnerConfigurationAsync(ownerId, configurationJson, cancellationToken).ConfigureAwait(false);
    }

    private Task WaitForSetupAsync(CancellationToken cancellationToken)
        => _activityMonitor.WaitForIdleAsync(cancellationToken);

    public void Dispose()
    {
        _inner.Dispose();
    }
}
