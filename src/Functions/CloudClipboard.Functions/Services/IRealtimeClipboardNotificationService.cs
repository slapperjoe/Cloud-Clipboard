using System.Threading;
using System.Threading.Tasks;
using CloudClipboard.Core.Models;
using CloudClipboard.Functions.Dtos;

namespace CloudClipboard.Functions.Services;

public interface IRealtimeClipboardNotificationService
{
    bool IsEnabled { get; }
    Task PublishAsync(string ownerId, ClipboardItemMetadata metadata, CancellationToken cancellationToken);
    Task<NotificationConnectionInfo?> NegotiateAsync(string ownerId, CancellationToken cancellationToken);
}
