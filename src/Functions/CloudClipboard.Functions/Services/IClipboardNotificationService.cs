using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CloudClipboard.Core.Models;

namespace CloudClipboard.Functions.Services;

public interface IClipboardNotificationService
{
    Task PublishAsync(string ownerId, ClipboardItemMetadata metadata, CancellationToken cancellationToken);
    Task<IReadOnlyList<ClipboardNotification>> DrainAsync(string ownerId, int maxItems, CancellationToken cancellationToken);
}

public sealed record ClipboardNotification(string OwnerId, string ItemId, DateTimeOffset CreatedUtc);
