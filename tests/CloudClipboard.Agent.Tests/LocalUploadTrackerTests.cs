using System;
using CloudClipboard.Agent.Windows.Services;
using Xunit;

namespace CloudClipboard.Agent.Tests;

public sealed class LocalUploadTrackerTests
{
    [Fact]
    public void RecordThenConsume_SucceedsOnce()
    {
        var tracker = new LocalUploadTracker();
        var created = DateTimeOffset.UtcNow;

        tracker.Record("owner-1", "item-1", created);

        Assert.True(tracker.TryConsume("owner-1", "item-1"));
        Assert.False(tracker.TryConsume("owner-1", "item-1"));
    }

    [Fact]
    public void TryConsume_ReturnsFalseWhenOwnerUnknown()
    {
        var tracker = new LocalUploadTracker();

        Assert.False(tracker.TryConsume("missing", "item-1"));
    }

    [Fact]
    public void Record_IgnoresBlankIdentifiers()
    {
        var tracker = new LocalUploadTracker();
        tracker.Record(string.Empty, "item", DateTimeOffset.UtcNow);

        Assert.False(tracker.TryConsume(string.Empty, "item"));
    }
}
