using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CloudClipboard.Core.Models;
using CloudClipboard.Core.Services;

namespace CloudClipboard.Agent.Windows.Services;

public interface ICloudClipboardClient
{
    Task UploadAsync(ClipboardUploadRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ClipboardItemDto>> ListAsync(string ownerId, int take, CancellationToken cancellationToken = default);
    Task<ClipboardItemDto?> DownloadAsync(string ownerId, string itemId, CancellationToken cancellationToken = default);
    Task<ClipboardOwnerState> GetStateAsync(string ownerId, CancellationToken cancellationToken = default);
    Task<ClipboardOwnerState> SetStateAsync(string ownerId, bool isPaused, CancellationToken cancellationToken = default);
    Task DeleteOwnerAsync(string ownerId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ClipboardNotificationEvent>> PollNotificationsAsync(string ownerId, int timeoutSeconds, CancellationToken cancellationToken = default);
    Task<NotificationConnectionInfo?> GetNotificationConnectionAsync(string ownerId, CancellationToken cancellationToken = default);
}
