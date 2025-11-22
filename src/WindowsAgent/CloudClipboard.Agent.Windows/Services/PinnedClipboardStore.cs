using System;
using System.Collections.Generic;
using System.Linq;
using CloudClipboard.Agent.Windows.Configuration;
using CloudClipboard.Agent.Windows.Options;
using CloudClipboard.Core.Models;
using Microsoft.Extensions.Options;

namespace CloudClipboard.Agent.Windows.Services;

public sealed class PinnedClipboardStore : IPinnedClipboardStore
{
    private const int MaxPinnedItems = 8;
    private readonly IAgentSettingsStore _settingsStore;
    private readonly IOptionsMonitor<AgentOptions> _options;
    private readonly object _lock = new();
    private IReadOnlyList<PinnedClipboardItem> _snapshot = Array.Empty<PinnedClipboardItem>();

    public PinnedClipboardStore(IAgentSettingsStore settingsStore, IOptionsMonitor<AgentOptions> options)
    {
        _settingsStore = settingsStore;
        _options = options;
        _snapshot = (options.CurrentValue.PinnedItems ?? new List<PinnedClipboardItem>()).ToList();
        _options.OnChange(OnOptionsChanged);
    }

    public IReadOnlyList<PinnedClipboardItem> Snapshot
    {
        get
        {
            lock (_lock)
            {
                return _snapshot;
            }
        }
    }

    public event EventHandler<IReadOnlyList<PinnedClipboardItem>>? Changed;

    public void Pin(ClipboardItemDto item, string displayLabel)
    {
        if (string.IsNullOrWhiteSpace(item.Id))
        {
            return;
        }

        lock (_lock)
        {
            var options = _settingsStore.Load();
            options.PinnedItems ??= new List<PinnedClipboardItem>();
            if (options.PinnedItems.Any(p => string.Equals(p.Id, item.Id, StringComparison.Ordinal)))
            {
                return;
            }

            options.PinnedItems.Insert(0, PinnedClipboardItem.FromDto(item, displayLabel));
            if (options.PinnedItems.Count > MaxPinnedItems)
            {
                options.PinnedItems.RemoveAt(options.PinnedItems.Count - 1);
            }

            _settingsStore.Save(options);
            _snapshot = options.PinnedItems.ToList();
            RaiseChanged();
        }
    }

    public void Unpin(string itemId)
    {
        lock (_lock)
        {
            var options = _settingsStore.Load();
            options.PinnedItems ??= new List<PinnedClipboardItem>();
            var removed = options.PinnedItems.RemoveAll(p => string.Equals(p.Id, itemId, StringComparison.Ordinal)) > 0;
            if (!removed)
            {
                return;
            }

            _settingsStore.Save(options);
            _snapshot = options.PinnedItems.ToList();
            RaiseChanged();
        }
    }

    public bool TryGet(string itemId, out PinnedClipboardItem? pinned)
    {
        lock (_lock)
        {
            pinned = _snapshot.FirstOrDefault(p => string.Equals(p.Id, itemId, StringComparison.Ordinal));
            return pinned is not null;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            var options = _settingsStore.Load();
            if (options.PinnedItems is null || options.PinnedItems.Count == 0)
            {
                return;
            }

            options.PinnedItems.Clear();
            _settingsStore.Save(options);
            _snapshot = Array.Empty<PinnedClipboardItem>();
        }

        RaiseChanged();
    }

    private void OnOptionsChanged(AgentOptions options)
    {
        lock (_lock)
        {
            _snapshot = (options.PinnedItems ?? new List<PinnedClipboardItem>()).ToList();
        }

        RaiseChanged();
    }

    private void RaiseChanged()
        => Changed?.Invoke(this, Snapshot);
}
