namespace AiRaccoon.Infrastructure.Watch;

/// <summary>
///     Groups a watch's buffer overflows into bursts. A burst is due once the watch has gone
///     <paramref name="quietPeriod" /> without an overflow, or <paramref name="maxWait" /> after the
///     burst began, so a watch that never goes quiet still gets scanned.
/// </summary>
internal sealed class WatchOverflowDebounce(TimeSpan quietPeriod, TimeSpan maxWait)
{
    private readonly Dictionary<WatchKey, Burst> _bursts = new();
    private readonly Lock _gate = new();

    /// <summary>Adds one overflow to the watch's burst, starting a burst if there is none.</summary>
    public void Record(WatchKey key, DateTimeOffset now)
    {
        lock (_gate)
        {
            _bursts[key] = _bursts.TryGetValue(key, out var burst)
                ? burst with { Last = now, Count = burst.Count + 1 }
                : new Burst(now, now, 1);
        }
    }

    /// <summary>The watches whose burst is due. A burst stays until <see cref="Take" /> removes it.</summary>
    public IReadOnlyList<WatchKey> Due(DateTimeOffset now)
    {
        lock (_gate)
        {
            return
            [
                .. _bursts.Where(b => now - b.Value.Last >= quietPeriod || now - b.Value.First >= maxWait)
                    .Select(b => b.Key)
            ];
        }
    }

    /// <summary>Removes the watch's burst and returns how many overflows it held; 0 when there is none.</summary>
    public int Take(WatchKey key)
    {
        lock (_gate)
        {
            return _bursts.Remove(key, out var burst) ? burst.Count : 0;
        }
    }

    private readonly record struct Burst(DateTimeOffset First, DateTimeOffset Last, int Count);
}
