namespace AiRaccoon.Tests;

/// <summary>
///     Returns a monotonic cursor from <c>GetTimestamp</c>, advancing it by the next scripted
///     delta after every call. <see cref="TimeProvider.GetElapsedTime(long)" /> calls
///     <c>GetTimestamp()</c> internally, so a simple bracket's elapsed is exactly the delta
///     consumed between its own start and end calls — and because <c>Total</c> nests around
///     every phase (its own start is the first call, its own end is the last), the same
///     mechanism sums correctly for it too, unlike an alternating start/end toggle which
///     desynchronises the moment a bracket nests inside another one.
///     <para>
///     <see cref="GetUtcNow" /> derives from the cursor instead of returning a fixed instant, so
///     every stamped write inside a scripted run moves with the deltas; it never advances the
///     cursor itself, so a <c>GetUtcNow</c> inside a bracket cannot desynchronise the deltas that
///     bracket is waiting on.
///     </para>
/// </summary>
internal sealed class ScriptedTimeProvider(IReadOnlyList<TimeSpan> deltas, DateTimeOffset? origin = null)
    : TimeProvider
{
    /// <summary>The wall-clock anchor a scripted run's stamped times count from — arbitrary but
    /// fixed; what moves is the cursor, not this instant.</summary>
    private static readonly DateTimeOffset DefaultOrigin = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    private readonly DateTimeOffset _origin = origin ?? DefaultOrigin;

    private long _cursor;
    private int _index;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        var value = _cursor;
        if (_index < deltas.Count)
        {
            _cursor += deltas[_index++].Ticks;
        }

        return value;
    }

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => _origin + TimeSpan.FromTicks(_cursor);

    /// <summary>
    ///     Builds the interleaved delta sequence a nested phase-on-phase call order needs: a zero
    ///     gap before every phase's own start/end pair (the untimed work between brackets), then
    ///     that phase's scripted value, plus a trailing zero gap before the outermost bracket's
    ///     own closing read.
    /// </summary>
    public static ScriptedTimeProvider ForPhases(params TimeSpan[] phaseValues)
    {
        var deltas = new List<TimeSpan>();
        foreach (var value in phaseValues)
        {
            deltas.Add(TimeSpan.Zero);
            deltas.Add(value);
        }

        deltas.Add(TimeSpan.Zero);
        return new ScriptedTimeProvider(deltas);
    }

    /// <summary>
    ///     One start/end bracket's delta sequence: the start call returns the cursor and advances it
    ///     by <paramref name="span" />, so the end call reads exactly that elapsed. A trailing zero
    ///     keeps any later read from perturbing it.
    /// </summary>
    public static ScriptedTimeProvider ForSpan(TimeSpan span) => new([span, TimeSpan.Zero]);
}
