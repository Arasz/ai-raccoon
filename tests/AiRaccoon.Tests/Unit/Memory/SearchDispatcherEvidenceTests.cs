using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Code;
using AiRaccoon.Core.Memory.Fusion;
using AiRaccoon.Core.SearchQuality;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory;

/// <summary>
///     P6a — quality-writer threading: <see cref="SearchDispatcher" /> joins the served
///     <see cref="MemorySearchResult" /> rows to the P4 sidecar by hash and passes the typed
///     Core evidence through to <see cref="ISearchQualityService" />. Core stays JSON-free;
///     persistence is P6b's seam.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class SearchDispatcherEvidenceTests
{
    [Fact]
    public async Task DispatchAsync_WithEvidenceInSidecar_PassesJoinedEvidenceToQualityService()
    {
        var first = new MemorySearchResult("h1", 1.0, "a.md", "snippet a", SourceFile: "a.md");
        var second = new MemorySearchResult("h2", 0.5, "b.md", "snippet b", SourceFile: "b.md");
        var evidenceH1 = new RetrievalEvidence("h1", 1.0, [new LegRank("fts", 1), new LegRank("vector", 1)], 0.9);
        var evidenceH2 = new RetrievalEvidence("h2", 0.4, [new LegRank("fts", 2)], null);
        var store = new EvidenceStore(
            [first, second],
            evidenceByHash: new Dictionary<string, RetrievalEvidence>(StringComparer.Ordinal)
            {
                ["h1"] = evidenceH1,
                ["h2"] = evidenceH2,
                ["unserved"] = new RetrievalEvidence("unserved", 0.1, [new LegRank("fts", 9)], null),
            });
        var quality = new CapturingQualityService();
        var dispatcher = new SearchDispatcher(store, new EmptyCodeSearchService(), quality);

        await dispatcher.DispatchAsync(
            new SearchQuery("acme", "widgets"),
            SearchKind.Memory,
            "all",
            "corr-evidence",
            sessionId: "sess-test",
            cancellationToken: TestContext.Current.CancellationToken);

        quality.LastEvidence.ShouldNotBeNull("dispatcher must pass the joined sidecar through");
        quality.LastEvidence!.Count.ShouldBe(2);
        quality.LastEvidence[0].ShouldBe(evidenceH1);
        quality.LastEvidence[1].ShouldBe(evidenceH2);
    }

    /// <summary>
    ///     F1: the dispatcher join is by hash, never by position — served [h2, h1] against a
    ///     sidecar inserted [h1, h2] (unserved orphan last). A positional zip would pin h1's
    ///     strength-1.0 two-leg evidence onto h2 and fail every per-row assertion below.
    ///     Pattern copied from the exemplary <c>EvidenceCarryChainTests</c> reorder guard: the
    ///     order-differs assertion proves the fixture genuinely reorders, or the test proves
    ///     nothing. This dispatcher-level reorder identity is also the accepted cheap closure
    ///     for F4 (P7 reorder identity): served order differs from insertion order and each row
    ///     still resolves its own hand-written strength/legs/cosine.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_WithReorderedResults_JoinsEvidenceByHashNotPosition()
    {
        var first = new MemorySearchResult("h1", 1.0, "a.md", "snippet a", SourceFile: "a.md");
        var second = new MemorySearchResult("h2", 0.5, "b.md", "snippet b", SourceFile: "b.md");
        var sidecar = new Dictionary<string, RetrievalEvidence>(StringComparer.Ordinal)
        {
            ["h1"] = new RetrievalEvidence("h1", 1.0, [new LegRank("fts", 1), new LegRank("vector", 1)], 0.9),
            ["h2"] = new RetrievalEvidence("h2", 0.4, [new LegRank("fts", 2)], null),
            ["unserved"] = new RetrievalEvidence("unserved", 0.1, [new LegRank("fts", 9)], null),
        };
        // Served reversed against insertion order: affinity reordered between fuse and join.
        var store = new EvidenceStore([second, first], evidenceByHash: sidecar);
        sidecar.Keys.Take(2).ShouldNotBe(["h2", "h1"],
            "served order must genuinely differ from sidecar insertion order, or this test proves nothing");
        var quality = new CapturingQualityService();
        var dispatcher = new SearchDispatcher(store, new EmptyCodeSearchService(), quality);

        await dispatcher.DispatchAsync(
            new SearchQuery("acme", "widgets"),
            SearchKind.Memory,
            "all",
            "corr-reorder",
            sessionId: "sess-test",
            cancellationToken: TestContext.Current.CancellationToken);

        ReorderedEvidenceShouldMatchRows(quality.LastEvidence);
    }

    /// <summary>
    ///     F1 orphan-first variant: the unserved sidecar entry is inserted BEFORE the served
    ///     hashes, so a positional zip attaches the orphan's evidence to h2 — the sharpest
    ///     misalignment. The hash join is unaffected by insertion order.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_WithOrphanInsertedFirst_StillJoinsEvidenceByHash()
    {
        var first = new MemorySearchResult("h1", 1.0, "a.md", "snippet a", SourceFile: "a.md");
        var second = new MemorySearchResult("h2", 0.5, "b.md", "snippet b", SourceFile: "b.md");
        var sidecar = new Dictionary<string, RetrievalEvidence>(StringComparer.Ordinal)
        {
            ["unserved"] = new RetrievalEvidence("unserved", 0.1, [new LegRank("fts", 9)], null),
            ["h1"] = new RetrievalEvidence("h1", 1.0, [new LegRank("fts", 1), new LegRank("vector", 1)], 0.9),
            ["h2"] = new RetrievalEvidence("h2", 0.4, [new LegRank("fts", 2)], null),
        };
        var store = new EvidenceStore([second, first], evidenceByHash: sidecar);
        var quality = new CapturingQualityService();
        var dispatcher = new SearchDispatcher(store, new EmptyCodeSearchService(), quality);

        await dispatcher.DispatchAsync(
            new SearchQuery("acme", "widgets"),
            SearchKind.Memory,
            "all",
            "corr-orphan-first",
            sessionId: "sess-test",
            cancellationToken: TestContext.Current.CancellationToken);

        ReorderedEvidenceShouldMatchRows(quality.LastEvidence);
    }

    private static void ReorderedEvidenceShouldMatchRows(IReadOnlyList<RetrievalEvidence>? joined)
    {
        // Member-wise, not record ShouldBe: record equality compares the Legs lists by
        // reference, so independently built (but identical) evidence would never be "equal".
        joined.ShouldNotBeNull("the dispatcher must pass the joined sidecar through");
        joined.Count.ShouldBe(2, "served-subset only — the unserved orphan never joins");
        joined[0].Hash.ShouldBe("h2");
        joined[0].FusionStrength.ShouldBe(0.4);
        joined[0].Legs.ShouldBe([new LegRank("fts", 2)]);
        joined[0].Cosine.ShouldBeNull();
        joined[1].Hash.ShouldBe("h1");
        joined[1].FusionStrength.ShouldBe(1.0);
        joined[1].Legs.ShouldBe([new LegRank("fts", 1), new LegRank("vector", 1)]);
        joined[1].Cosine.ShouldBe(0.9);
    }

    [Fact]
    public async Task DispatchAsync_WithNullEvidence_PassesNullAndBehavesAsBefore()
    {
        var first = new MemorySearchResult("h1", 1.0, "a.md", "snippet a", SourceFile: "a.md");
        var store = new EvidenceStore([first], evidenceByHash: null);
        var quality = new CapturingQualityService();
        var dispatcher = new SearchDispatcher(store, new EmptyCodeSearchService(), quality);

        var result = await dispatcher.DispatchAsync(
            new SearchQuery("acme", "widgets"),
            SearchKind.Memory,
            "all",
            "corr-null",
            sessionId: "sess-test",
            cancellationToken: TestContext.Current.CancellationToken);

        quality.LastEvidence.ShouldBeNull("absent sidecar must stay absent, not become an empty list");
        result.Results.Count.ShouldBe(1);
        quality.LastResultCount.ShouldBe(1);
        quality.LastTopSourceFiles.ShouldBe(["a.md"]);
    }

    [Fact]
    public async Task DispatchAsync_KindCode_PassesNullEvidence()
    {
        var store = new EvidenceStore([], evidenceByHash: null);
        var quality = new CapturingQualityService();
        var codeSearch = new StubCodeSearchService([new CodeSearchResult("c1", 1.0, "Foo.cs", "class Foo", 1, 10)]);
        var dispatcher = new SearchDispatcher(store, codeSearch, quality);

        var result = await dispatcher.DispatchAsync(
            new SearchQuery("acme", "widgets"),
            SearchKind.Code,
            "code",
            "corr-code",
            sessionId: "sess-test",
            cancellationToken: TestContext.Current.CancellationToken);

        quality.LastEvidence.ShouldBeNull("the code leg has no memory sidecar, so nothing flows to quality");
        result.Results.ShouldBeEmpty();
        quality.LastResultCount.ShouldBe(1);
    }

    private sealed class EvidenceStore(
        IReadOnlyList<MemorySearchResult> results,
        IReadOnlyDictionary<string, RetrievalEvidence>? evidenceByHash)
        : FakeMemoryStore
    {
        public override Task<SearchResults> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new SearchResults(results, SearchTimings.Empty, EvidenceByHash: evidenceByHash));
        }
    }

    private sealed class EmptyCodeSearchService : ICodeSearchService
    {
        public Task<CodeSearchResults> SearchAsync(CodeSearchQuery query, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new CodeSearchResults([]));
        }

        public Task<CodeEntry?> GetAsync(string projectId, string hash, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<CodeEntry?>(null);
        }
    }

    private sealed class StubCodeSearchService(IReadOnlyList<CodeSearchResult> results) : ICodeSearchService
    {
        public Task<CodeSearchResults> SearchAsync(CodeSearchQuery query, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new CodeSearchResults(results));
        }

        public Task<CodeEntry?> GetAsync(string projectId, string hash, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<CodeEntry?>(null);
        }
    }

    private sealed class CapturingQualityService : ISearchQualityService
    {
        public IReadOnlyList<RetrievalEvidence>? LastEvidence { get; private set; }

        public int LastResultCount { get; private set; } = -1;

        public IReadOnlyList<string> LastTopSourceFiles { get; private set; } = [];

        public Task<int> PurgeOlderThanAsync(long nowUnixSeconds, int retentionDays, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(0);
        }

        public Task RecordSearchAsync(
            string correlationId,
            string query,
            string? scope,
            string? projectId,
            string kind,
            string sessionId,
            int resultCount,
            IReadOnlyList<string> topSourceFiles,
            CancellationToken ct = default,
            IReadOnlyList<RetrievalEvidence>? evidence = null)
        {
            LastResultCount = resultCount;
            LastTopSourceFiles = topSourceFiles;
            LastEvidence = evidence;
            return Task.CompletedTask;
        }

        public Task RecordSearchSafeAsync(
            string correlationId,
            string query,
            string? scope,
            string? projectId,
            string kind,
            string sessionId,
            int resultCount,
            IReadOnlyList<string> topSourceFiles,
            CancellationToken ct = default,
            IReadOnlyList<RetrievalEvidence>? evidence = null)
        {
            LastResultCount = resultCount;
            LastTopSourceFiles = topSourceFiles;
            LastEvidence = evidence;
            return Task.CompletedTask;
        }

        public Task RecordFollowThroughAsync(string correlationId, string filePath, int? servedRank = null, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public Task RecordGradeAsync(string projectId, string correlationId, int grade, string? note, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public Task<SearchQualityMetrics> GetMetricsAsync(string? projectId, DateTimeOffset from, CancellationToken ct = default)
        {
            return Task.FromResult(new SearchQualityMetrics(0, 0, 0, 0, 0, 0, 0));
        }
    }
}
