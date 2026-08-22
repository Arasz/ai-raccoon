namespace AiRaccoon.Infrastructure.Maintenance;

/// <summary>
///     Broadcast count signal for test seams: WaitAsync returns when the count reaches the
///     target (true) or the timeout/cancellation fires (false). No polling required.
/// </summary>
internal sealed class TickSignal
{
    private readonly Lock _gate = new();
    private readonly List<(long Target, TaskCompletionSource<bool> Completion)> _waiters = [];
    private long _count;

    public long Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    public void Increment()
    {
        List<TaskCompletionSource<bool>> ready;
        lock (_gate)
        {
            _count++;
            ready = [.. _waiters.Where(w => w.Target <= _count).Select(w => w.Completion)];
            _waiters.RemoveAll(w => w.Target <= _count);
        }

        foreach (var completion in ready)
        {
            completion.TrySetResult(true);
        }
    }

    /// <summary>Waits until the count reaches the target; only cancellation ends it early (a test's hang guard, never a clock).</summary>
    public async Task WaitAsync(long target, CancellationToken cancellationToken)
    {
        TaskCompletionSource<bool> completion;
        lock (_gate)
        {
            if (_count >= target)
            {
                return;
            }

            completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((target, completion));
        }

        await completion.Task.WaitAsync(cancellationToken);
    }

    /// <summary>True when the count reached the target; false on timeout or cancellation.</summary>
    public async Task<bool> WaitAsync(long target, TimeSpan timeout, CancellationToken cancellationToken)
    {
        TaskCompletionSource<bool> completion;
        lock (_gate)
        {
            if (_count >= target)
            {
                return true;
            }

            completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((target, completion));
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        await using var registration = cts.Token.Register(() => completion.TrySetResult(false));
        return await completion.Task;
    }
}
