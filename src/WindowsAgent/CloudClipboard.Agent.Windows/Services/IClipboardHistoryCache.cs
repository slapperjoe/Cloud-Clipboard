using System;
using System.Collections.Generic;
using CloudClipboard.Core.Models;

namespace CloudClipboard.Agent.Windows.Services;

public interface IClipboardHistoryCache
{
    IReadOnlyList<ClipboardItemDto> Snapshot { get; }
    event EventHandler<IReadOnlyList<ClipboardItemDto>>? HistoryChanged;
    void Update(IReadOnlyList<ClipboardItemDto> items);
}
