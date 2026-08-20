using AiRaccoon.Core.Memory;

namespace AiRaccoon.Infrastructure.Sqlite.Memory;

public sealed record SearchTimingsCollector(long StartTimestamp)
{
    public TimeSpan Open { get; set; }
    public TimeSpan Embed { get; set; }
    public TimeSpan Fts { get; set; }
    public TimeSpan Vector { get; set; }
    public TimeSpan Fusion { get; set; }
    public TimeSpan Merge { get; set; }
    public TimeSpan Adjustment { get; set; }
    public TimeSpan Snippets { get; set; }
    public TimeSpan Bump { get; set; }

    private TimeSpan Total(TimeProvider timeProvider) => timeProvider.GetElapsedTime(StartTimestamp);

    public SearchTimings ToCollected(TimeProvider timeProvider) =>
        new(Open, Embed, Fts, Vector, Fusion,
            Merge,
            Adjustment,
            Snippets,
            Bump, Total(timeProvider));
}
