using System;
using System.Collections.Generic;
using CloudClipboard.Agent.Options;
using CloudClipboard.Core.Models;

namespace CloudClipboard.Agent.Services;

public interface IPinnedClipboardStore
{
    IReadOnlyList<PinnedClipboardItem> Snapshot { get; }
    event EventHandler<IReadOnlyList<PinnedClipboardItem>>? Changed;
    void Pin(ClipboardItemDto item, string displayLabel);
    void Unpin(string itemId);
    void Clear();
    bool TryGet(string itemId, out PinnedClipboardItem? pinned);
}
