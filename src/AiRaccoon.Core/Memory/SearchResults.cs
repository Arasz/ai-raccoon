using System.Reflection;

namespace AiRaccoon.Core.Memory;

/// <summary>
///     The envelope <see cref="IMemoryStore.SearchAsync" /> returns (owner ruling; WP1 wires it).
///     <see cref="Timings" /> rides out with the results so the host can tag and record them
///     without a side channel back into the store.
/// </summary>
public sealed record SearchResults(IReadOnlyList<MemorySearchResult> Results, SearchTimings Timings);

/// <summary>Per-phase durations for one <see cref="IMemoryStore.SearchAsync" /> call.</summary>
public sealed record SearchTimings(
    TimeSpan Fts,
    TimeSpan Vector,
    TimeSpan Fusion,
    TimeSpan Affinity,
    TimeSpan Snippets,
    TimeSpan Bump)
{
    /// <summary>All-zero timings, for a caller that has not measured phases yet (WP1, before WP2 lands).</summary>
    public static SearchTimings Empty { get; } = new(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);

    /// <summary>
    ///     This record's own TimeSpan properties, reflected once at type load — the source of truth
    ///     for both <see cref="PhaseNames" /> and <see cref="Phases" />, so a phase added here needs
    ///     no list edited anywhere else (derive-or-delete-the-list).
    /// </summary>
    private static readonly PropertyInfo[] PhaseProperties =
    [
        .. typeof(SearchTimings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(TimeSpan))
    ];

    /// <summary>The metric names a search records — `search.&lt;phase&gt;` per <see cref="TimeSpan" /> member above, in declaration order.</summary>
    public static IReadOnlyList<string> PhaseNames { get; } = [.. PhaseProperties.Select(PhaseName)];

    /// <summary>This instance's phases as name/value pairs, in the same order as <see cref="PhaseNames" />.</summary>
    public IReadOnlyList<(string Name, TimeSpan Value)> Phases() =>
        [.. PhaseProperties.Select(p => (PhaseName(p), (TimeSpan)p.GetValue(this)!))];

    private static string PhaseName(PropertyInfo property) => $"search.{property.Name.ToLowerInvariant()}";
}
