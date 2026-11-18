using System.Threading.Channels;

namespace AiRaccoon.Infrastructure.Watch;

/// <summary>
///     Wakes the watch reconcile loop early. Many requests coalesce into one pending wake, so a burst of
///     watch changes costs at most one extra pass.
/// </summary>
public sealed class WatchReconcileSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    private long _requests;

    /// <summary>How many times a wake was requested, counting coalesced ones.</summary>
    internal long RequestCount => Interlocked.Read(ref _requests);

    /// <summary>Asks the loop to reconcile now. Never blocks.</summary>
    public void Request()
    {
        Interlocked.Increment(ref _requests);
        _channel.Writer.TryWrite(true);
    }

    /// <summary>Completes when a wake is pending, and consumes it.</summary>
    public async ValueTask WaitAsync(CancellationToken cancellationToken) => await _channel.Reader.ReadAsync(cancellationToken);
}
