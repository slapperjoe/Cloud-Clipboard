using System;
using System.Collections.Generic;
using CloudClipboard.Core.Models;

namespace CloudClipboard.Agent.Windows.Services;

public sealed class ClipboardHistoryCache : IClipboardHistoryCache
{
    private readonly object _lock = new();
    private IReadOnlyList<ClipboardItemDto> _items = Array.Empty<ClipboardItemDto>();

    public IReadOnlyList<ClipboardItemDto> Snapshot
    {
        get
        {
            lock (_lock)
            {
                return _items;
            }
        }
    }

    public event EventHandler<IReadOnlyList<ClipboardItemDto>>? HistoryChanged;

    public void Update(IReadOnlyList<ClipboardItemDto> items)
    {
        lock (_lock)
        {
            _items = items;
        }

        HistoryChanged?.Invoke(this, items);
    }
}
