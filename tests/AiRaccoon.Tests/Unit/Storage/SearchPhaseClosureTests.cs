using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Unit.Storage;

/// <summary>
///     S2 (docs/plans/2026-08-17-search-phase-attribution.md): the closure gate that keeps #382
///     closed — <c>SearchAsync</c>'s phase brackets must account for every tick of its measured
///     <c>Total</c>, so a future untimed <c>await</c> ahead of the phases cannot hide silently.
/// </summary>
/// <remarks>
///     <para>
///     <b>No wall clock is asserted here, on purpose (owner ruling, 2026-08-22: "they should not
///     fail on any env" — a wall-clock budget belongs in a benchmark, not a test).</b> This gate
///     used to assert <c>Total - Σ(phases) ≤ 28 ms</c> against <see cref="TimeProvider.System" />
///     with a 250 ms <c>Task.Delay</c> stub. That budget sat inside the noise band of a loaded
///     host: its own doc recorded cold residuals of 13-29 ms and 20-26 ms under CPU saturation, so
///     the gate could go red with no defect present at all — which it did on the owner's machine
///     while CI stayed green.
///     </para>
///     <para>
///     The replacement measures the same property with no clock noise in it. <c>SearchAsync</c>
///     already takes its timestamps from an injected <see cref="TimeProvider" />, so the test hands
///     it a <see cref="FakeTimeProvider" /> that is frozen — <c>AutoAdvanceAmount</c> is zero, so
///     nothing moves the clock on its own — and the embedder stub advances it by
///     <see cref="EmbedCost" /> inside <c>EmbedQueryAsync</c>. That advance is then the *only* time
///     that exists during the search, so the arithmetic is exact rather than approximate: the
///     phases must account for exactly it, and the unaccounted remainder must be exactly zero.
///     Real SQLite still runs underneath; it simply no longer contributes noise to the assertion.
///     </para>
///     <para>
///     <b><see cref="SearchTimings.Adjustment" /> is subtracted explicitly.</b>
///     <see cref="SearchTimings.Phases" /> exports eight spans and the record measures nine —
///     <c>search.affinity</c> is bound to <c>Merge</c>, and <c>Adjustment</c> has no exported name
///     at all — so Σ(phases) has never included it and the old residual silently carried the whole
///     adjustment phase inside its 28 ms. Naming it here makes the closure statement true rather
///     than approximately true. Reported to the owner as a separate finding; not changed here,
///     because renaming an exported metric series is not this task's scope.
///     </para>
/// </remarks>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SearchPhaseClosureTests : IDisposable
{
    /// <summary>
    ///     The one cost that exists during the measured search — large, distinct, and injected at a
    ///     known place, so a phase that stops covering it shows up as a residual of exactly this
    ///     size rather than as a few milliseconds of ambiguity.
    /// </summary>
    private static readonly TimeSpan EmbedCost = TimeSpan.FromMilliseconds(250);

    private static readonly DateTimeOffset FixedNow = new(2026, 8, 22, 0, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-search-phase-closure");
    private readonly SqliteConnectionFactory _factory;

    public SearchPhaseClosureTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [Fact]
    public async Task Search_PhaseBracketsAccountForEveryTickOfTotal()
    {
        var clock = new FakeTimeProvider(FixedNow);
        var timings = await SearchAsync(clock);

        Unaccounted(timings).ShouldBe(TimeSpan.Zero,
            "every tick of Total must fall inside a phase bracket — a non-zero remainder means a " +
            "phase under-reported its own work, or a new untimed step was added ahead of the phases");
        timings.Embed.ShouldBe(EmbedCost,
            "the embedder's cost must land inside search.embed, not outside it");
        timings.Total.ShouldBe(EmbedCost,
            "Total brackets the whole SearchAsync body, and the embedder's advance is the only time that passes");
    }

    /// <summary>
    ///     The discrimination proof (`prove-the-check-fails`), permanent and in-suite rather than a
    ///     one-off hand run: the identical search, with a clock that advances on every read instead
    ///     of only inside the embedder, puts time in the gaps *between* the phase brackets — exactly
    ///     the shape of the leak the gate exists to catch — and the remainder above must report it.
    ///     The size is deliberately not asserted; the gate's assertion is "exactly zero", so its
    ///     negation is "more than zero", and pinning a tick count here would only pin how many clock
    ///     reads <c>SearchAsync</c> happens to make.
    /// </summary>
    [Fact]
    public async Task Search_WhenTimePassesBetweenThePhaseBrackets_TheRemainderReportsIt()
    {
        var clock = new FakeTimeProvider(FixedNow) { AutoAdvanceAmount = TimeSpan.FromTicks(1) };
        var timings = await SearchAsync(clock);

        Unaccounted(timings).ShouldBeGreaterThan(TimeSpan.Zero,
            "work outside every phase bracket must surface as an unaccounted remainder, or the " +
            "closure assertion above is measuring nothing");
    }

    /// <summary>
    ///     <c>Total - Σ(phases) - Adjustment</c>: the part of the measured total that no bracket
    ///     claims. <see cref="SearchTimings.Adjustment" /> is measured but absent from
    ///     <see cref="SearchTimings.Phases" /> (see the class remarks), so it is named here instead
    ///     of being left to hide in the remainder.
    /// </summary>
    private static TimeSpan Unaccounted(SearchTimings timings) =>
        timings.Total
        - timings.Phases().Aggregate(TimeSpan.Zero, (acc, phase) => acc + phase.Value)
        - timings.Adjustment;

    private async Task<SearchTimings> SearchAsync(FakeTimeProvider clock)
    {
        var store = SearchTimingsHarness.CreateStore(_factory, clock,
            new SearchTimingsHarness.VectorEmbedderStub(() => clock.Advance(EmbedCost)));
        var result = await store.SearchAsync(new SearchQuery("proj-1", "widgets"),
            TestContext.Current.CancellationToken).ConfigureAwait(false);
        return result.Timings;
    }
}
