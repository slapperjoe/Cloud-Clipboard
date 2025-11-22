using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using CloudClipboard.Core.Models;

namespace CloudClipboard.Agent.Windows.Services;

public sealed class ClipboardUploadQueue : IClipboardUploadQueue
{
    private readonly Channel<ClipboardUploadRequest> _channel = Channel.CreateUnbounded<ClipboardUploadRequest>(new UnboundedChannelOptions
    {
        AllowSynchronousContinuations = false,
        SingleReader = true,
        SingleWriter = false
    });

    public ValueTask EnqueueAsync(ClipboardUploadRequest request, CancellationToken cancellationToken = default)
        => _channel.Writer.WriteAsync(request, cancellationToken);

    public IAsyncEnumerable<ClipboardUploadRequest> ReadAllAsync(CancellationToken cancellationToken = default)
        => _channel.Reader.ReadAllAsync(cancellationToken);
}
