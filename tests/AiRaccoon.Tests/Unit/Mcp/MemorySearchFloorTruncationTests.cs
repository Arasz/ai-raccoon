using System.Text.Json;
using AiRaccoon.Access;
using AiRaccoon.Core.Isolation;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Code;
using AiRaccoon.Core.Memory.Fusion;
using AiRaccoon.Core.Memory.QueryGuard;
using AiRaccoon.Core.Projects;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Mcp;

/// <summary>
///     P3.2 (F20): a response shortened by a score floor says so. Each applied floor reports its
///     own name, threshold and dropped count, so "the bank has N matches" and "the floor cut the
///     rest" stop reading the same. The relative floor's count comes from the merge; the absolute
///     floor's rows are judged here at the tool layer.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class MemorySearchFloorTruncationTests
{
    private readonly FakeStore _store = new();
    private readonly MemoryTools _tools;

    public MemorySearchFloorTruncationTests()
    {
        var access = new MemoryAccessGuard(_store);
        var gate = new ToolGate(access, new FakePromotionQueue(), new NeverMigratingStore(), new AllowingRegistrationGuard(), new StubMigrationGate(migrated: false));
        _tools = new MemoryTools(_store, gate, new SearchDispatcher(_store, new NoOpCodeSearchService(), new NoOpSearchQualityService()),
            new QueryGuardService(new InMemorySettings()),
            new MemoryWriteService(_store, new FakePromotionQueue()), new NoOpMeasurementRecorder(),
            new InMemorySettings(), NullLogger<MemoryTools>.Instance);
    }

    /// <summary>
    ///     The absolute floor drops four of six candidates and the response comes back two rows
    ///     against a requested ten: the truncation marker must name that floor and its count.
    /// </summary>
    [Fact]
    public async Task Search_AbsoluteFloorDrops_ShortOfTheRequestedLimit_ReportTruncation()
    {
        StubSixCandidates(_store, lowCosineCount: 4);

        var envelope = await _tools.Search("acme", "harbor tide charts", kind: "memory", limit: 10,
            sessionId: "sess-test", cancellationToken: TestContext.Current.CancellationToken);

        envelope.Data!.Results.Count.ShouldBe(2, "the four rows below the absolute relevance floor are dropped");
        var json = JsonSerializer.Serialize(envelope.Data, McpJsonUtilities.DefaultOptions);
        json.ShouldContain("\"truncation\"");
        json.ShouldContain("\"absoluteRelevance\"");
        json.ShouldContain("\"dropped\":4");
    }

    /// <summary>
    ///     Positive control: candidates the floors do not touch return complete and unmarked —
    ///     a marker that always fires cannot tell a caller anything.
    /// </summary>
    [Fact]
    public async Task Search_KeptCandidates_ShortOnlyBecauseTheBankEnds_CarryNoTruncationMarker()
    {
        StubSixCandidates(_store, lowCosineCount: 0);

        var envelope = await _tools.Search("acme", "harbor tide charts", kind: "memory", limit: 10,
            sessionId: "sess-test", cancellationToken: TestContext.Current.CancellationToken);

        envelope.Data!.Results.Count.ShouldBe(6, "every candidate clears the floors");
        JsonSerializer.Serialize(envelope.Data, McpJsonUtilities.DefaultOptions).ShouldNotContain("truncation");
    }

    private static void StubSixCandidates(FakeStore store, int lowCosineCount)
    {
        var rows = new List<MemorySearchResult>();
        var evidence = new Dictionary<string, RetrievalEvidence>(StringComparer.Ordinal);
        for (var index = 0; index < 6; index++)
        {
            var hash = $"r{index}";
            rows.Add(new MemorySearchResult(hash, 1.0 - index * 0.02, $"docs/{hash}.md", $"row {index}"));
            // The first rows clear the absolute floor; the rest measure like the review's
            // zero-overlap probe (0.05 down to -0.04).
            var cosine = index < 6 - lowCosineCount ? 0.62 - index * 0.02 : 0.05 - (index - (6 - lowCosineCount)) * 0.03;
            evidence[hash] = new RetrievalEvidence(hash, 0.9, [new LegRank("vector", index + 1)], cosine);
        }

        store.StubResults = rows;
        store.StubEvidence = evidence;
        store.StubStats = new FusionStats(0.3, 0.2, 0.0164, ["vector"]);
    }

    private sealed class FakeStore : FakeMemoryStore
    {
        public IReadOnlyList<MemorySearchResult> StubResults { get; set; } = [];

        public IReadOnlyDictionary<string, RetrievalEvidence>? StubEvidence { get; set; }

        public FusionStats? StubStats { get; set; }

        public override Task<SearchResults> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SearchResults(StubResults, SearchTimings.Empty, null, StubEvidence, StubStats));
    }

    private sealed class StubMigrationGate(bool migrated) : IProjectIdsMigrationGate
    {
        public Task<bool> IsMigratedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(migrated);
    }
}
