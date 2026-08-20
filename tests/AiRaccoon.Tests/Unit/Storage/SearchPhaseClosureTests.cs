using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Sqlite;
using Shouldly;
using Xunit;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Unit.Storage;

/// <summary>
///     S2 (docs/plans/2026-08-17-search-phase-attribution.md): the closure gate that keeps #382
///     closed — <c>SearchAsync</c>'s eight phases must account for its measured <c>Total</c> within a
///     bounded residual, so a future untimed <c>await</c> ahead of the phases cannot hide silently.
///     Real <see cref="TimeProvider.System" />, on purpose: the residual this gate bounds is real
///     wall-clock work (three settings/context reads on an already-open connection, plus pure string
///     parsing) that no fake clock can stand in for.
/// </summary>
/// <remarks>
///     <para>
///     <b>Measured residual (Total minus Σ phases), stub delay 250 ms, empty bank, single-context
///     query, no seeded data</b> — matching the reasoning in the plan (`BuildPlan` + `TryBuild` +
///     `ReadStructureAlphaAsync` + `SearchContexts.ResolveAsync`; `NoFusionRegressionEnabledAsync`
///     does not fire here because a bank with nothing written has no contributing FTS/vector leg).
///     </para>
///     <para>
///     <b>Without a warm-up call</b> (i.e. the very first <c>SearchAsync</c> this process ever runs),
///     20 isolated single-search samples (separate `dotnet test --filter` processes, matching this
///     section's own Gate command) measured 13.46-29.08 ms, typically 13-19 ms — none of it inside a
///     phase bracket, all of it JIT / SQLite query-plan first-compile cost for
///     <c>ReadStructureAlphaAsync</c> and <c>SearchContexts.ResolveAsync</c>. Under simulated CPU
///     saturation (8 busy loops on a 10-core box) the same cold measurement rose to 20.7-26.0 ms. That
///     is a process warm-up artifact, not the per-search cost the gate is meant to bound — at a 28 ms
///     budget it would make this test flake on its own documented Gate command with zero injected
///     defect. <see cref="SearchWithWarmupAsync" /> exists to remove exactly that confound.
///     </para>
///     <para>
///     <b>With one discarded warm-up search first</b> (what every test below actually does), 18
///     samples across both an idle machine and the same saturated-CPU condition measured 0.181-0.679
///     ms (p50 ~0.31 ms, p95 ~0.68 ms) — consistent with the plan's 1-3 ms reasoning and stable under
///     load. <see cref="ResidualBudgetMs" /> keeps the plan's provisional **28 ms**: on the warmed
///     measurement that is ~40x the observed p95, deliberately above the "~10x" floor the plan
///     describes, because the raw cold-start numbers above are the evidence that real-environment
///     noise (JIT, disk, GC) can be an order of magnitude larger than the reasoning estimate, and a
///     budget that only just clears a dozen local samples is not evidence about CI.
///     </para>
/// </remarks>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SearchPhaseClosureTests : IDisposable
{
    /// <summary>
    ///     ~10x the warmed p95 (see class remarks), not the raw cold-start p95 — deliberately
    ///     generous so the gate does not flake on JIT/query-plan noise that is not the defect it
    ///     exists to catch. Matches the plan's provisional value (§S2); the measurement here confirms
    ///     rather than moves it.
    /// </summary>
    private const int ResidualBudgetMs = 28;

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-search-phase-closure");
    private readonly SqliteConnectionFactory _factory;

    public SearchPhaseClosureTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [Fact]
    public async Task Search_PhasesCloseAgainstTotal_WithinBudget()
    {
        var store = SearchTimingsHarness.CreateStore(_factory, TimeProvider.System,
            new SearchTimingsHarness.VectorEmbedderStub(TimeSpan.FromMilliseconds(250)));
        var ct = TestContext.Current.CancellationToken;

        var timings = await SearchWithWarmupAsync(store, ct);
        var sumPhases = timings.Phases().Aggregate(TimeSpan.Zero, (acc, phase) => acc + phase.Value);
        var residual = timings.Total - sumPhases;

        residual.ShouldBeGreaterThanOrEqualTo(TimeSpan.Zero,
            "the phase brackets are disjoint sub-spans of Total, so it can never be negative");
        residual.ShouldBeLessThanOrEqualTo(TimeSpan.FromMilliseconds(ResidualBudgetMs),
            $"Total - Σ(phases) grew past the {ResidualBudgetMs} ms closure budget — either a phase " +
            "under-reported its own work, or a new untimed step was added ahead of the phases");
        timings.Embed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(200),
            "the stub's 250 ms delay must land inside search.embed, not leak into the residual");
    }

    /// <summary>
    ///     Runs <paramref name="store" />'s search once and discards the result before the measured
    ///     call, so the returned <see cref="SearchTimings" /> reflect steady-state per-search cost
    ///     rather than this process's first-ever JIT / SQLite query-plan compile of
    ///     <c>ReadStructureAlphaAsync</c> / <c>SearchContexts.ResolveAsync</c> — see the class remarks
    ///     for the measurement that motivated this.
    /// </summary>
    private static async Task<SearchTimings> SearchWithWarmupAsync(SqliteMemoryStore store,
        CancellationToken cancellationToken)
    {
        await store.SearchAsync(new SearchQuery("proj-1", "widgets"), cancellationToken).ConfigureAwait(false);
        var result = await store.SearchAsync(new SearchQuery("proj-1", "widgets"), cancellationToken)
            .ConfigureAwait(false);
        return result.Timings;
    }
}
