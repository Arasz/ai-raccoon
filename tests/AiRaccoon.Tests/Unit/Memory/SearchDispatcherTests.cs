using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Code;
using AiRaccoon.Core.Memory.Fusion;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory;

/// <summary>
///     WP5 — SearchDispatcher's code-section plumbing: per-section limit/minRelativeScore override
///     (§3.6), the code leg's own Warning forwarded verbatim (no more hardcoded
///     EngineNotConfigured), and the degraded-mode witness: a configured-but-unloadable code engine
///     refuses kind=code searches while kind=memory searches (which never touch the code engine)
///     stay unaffected (docs/work/2026-08-21-code-search-implementation-plan.md §3.3/§12.2 H5).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class SearchDispatcherTests
{
    [Fact]
    public async Task DispatchAsync_CodeLimitAndMinRelativeScoreProvided_OverrideTheSharedQueryValues()
    {
        var codeSearch = new SpyCodeSearchService { ResultToReturn = new CodeSearchResults([]) };
        var dispatcher = new SearchDispatcher(new EmptyMemoryStore(), codeSearch, new NoOpSearchQualityService(), TestData.SharedScope);
        var searchQuery = new SearchQuery("acme", "widget", Limit: 20, MinRelativeScore: 0.0);

        await dispatcher.DispatchAsync(searchQuery, EntryType.Code, "code", "corr-1", sessionId: "sess-test",
            codeLimit: 5, codeMinRelativeScore: 0.4, cancellationToken: TestContext.Current.CancellationToken);

        codeSearch.LastQuery.ShouldNotBeNull();
        codeSearch.LastQuery!.Limit.ShouldBe(5);
        codeSearch.LastQuery.MinRelativeScore.ShouldBe(0.4);
    }

    /// <summary>
    ///     ADR-0088 decision 5: rrfK/ftsWeight/vectorWeight/candidateWindow are per-call tuning
    ///     args that apply to the code section too, same values as memory's — no separate
    ///     codeRrfK/codeFtsWeight/etc. knobs in v1. Only limit/minRelativeScore get a genuinely
    ///     separate per-section override.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_PerCallTuningArgs_ReachTheCodeSection_SameValuesAsMemory()
    {
        var codeSearch = new SpyCodeSearchService { ResultToReturn = new CodeSearchResults([]) };
        var dispatcher = new SearchDispatcher(new EmptyMemoryStore(), codeSearch, new NoOpSearchQualityService(), TestData.SharedScope);
        var searchQuery = new SearchQuery("acme", "widget", RrfK: 30, FtsWeight: 2, VectorWeight: 5,
            CandidateWindow: CandidateWindowMode.Max5X50);

        await dispatcher.DispatchAsync(searchQuery, EntryType.Code, "code", "corr-1", sessionId: "sess-test",
            cancellationToken: TestContext.Current.CancellationToken);

        var source = (ISearchParametersSource)codeSearch.LastQuery!;
        source.RrfK.ShouldBe(30);
        source.FtsWeight.ShouldBe(2);
        source.VectorWeight.ShouldBe(5);
        source.CandidateWindow.ShouldBe(CandidateWindowMode.Max5X50);
    }

    [Fact]
    public async Task DispatchAsync_CodeLimitAndMinRelativeScoreOmitted_FallBackToTheSharedQueryValues()
    {
        var codeSearch = new SpyCodeSearchService { ResultToReturn = new CodeSearchResults([]) };
        var dispatcher = new SearchDispatcher(new EmptyMemoryStore(), codeSearch, new NoOpSearchQualityService(), TestData.SharedScope);
        var searchQuery = new SearchQuery("acme", "widget", Limit: 20, MinRelativeScore: 0.3);

        await dispatcher.DispatchAsync(searchQuery, EntryType.Code, "code", "corr-1", sessionId: "sess-test", cancellationToken: TestContext.Current.CancellationToken);

        codeSearch.LastQuery!.Limit.ShouldBe(20);
        codeSearch.LastQuery.MinRelativeScore.ShouldBe(0.3);
    }

    [Fact]
    public async Task DispatchAsync_ForwardsTheCodeService_OwnWarning_VerbatimNoLongerHardcoded()
    {
        var codeSearch = new SpyCodeSearchService { ResultToReturn = new CodeSearchResults([], "a real, service-computed warning") };
        var dispatcher = new SearchDispatcher(new EmptyMemoryStore(), codeSearch, new NoOpSearchQualityService(), TestData.SharedScope);

        var result = await dispatcher.DispatchAsync(new SearchQuery("acme", "widget"), EntryType.Code, "code", "corr-1", sessionId: "sess-test", cancellationToken: TestContext.Current.CancellationToken);

        result.CodeWarning.ShouldBe("a real, service-computed warning");
    }

    [Fact]
    public async Task DispatchAsync_KindCode_ConfiguredButUnloadableCodeEngine_ThrowsActionableError()
    {
        var codeSearch = new SpyCodeSearchService { ThrowOnSearch = new CodeEngineUnloadableException("/models/broken", new InvalidOperationException("bad manifest")) };
        var dispatcher = new SearchDispatcher(new EmptyMemoryStore(), codeSearch, new NoOpSearchQualityService(), TestData.SharedScope);

        await Should.ThrowAsync<CodeEngineUnloadableException>(() =>
            dispatcher.DispatchAsync(new SearchQuery("acme", "widget"), EntryType.Code, "code", "corr-1", sessionId: "sess-test",
                cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DispatchAsync_KindMemory_UnaffectedByABrokenCodeEngine_NeverCallsTheCodeService()
    {
        var codeSearch = new SpyCodeSearchService { ThrowOnSearch = new CodeEngineUnloadableException("/models/broken", new InvalidOperationException("bad manifest")) };
        var dispatcher = new SearchDispatcher(new EmptyMemoryStore(), codeSearch, new NoOpSearchQualityService(), TestData.SharedScope);

        var result = await dispatcher.DispatchAsync(new SearchQuery("acme", "widget"), EntryType.Memory | EntryType.File, "memory", "corr-1", sessionId: "sess-test", cancellationToken: TestContext.Current.CancellationToken);

        result.Results.ShouldBeEmpty();
        codeSearch.WasCalled.ShouldBeFalse("kind=memory must never touch the code engine at all — the two are independent settings rows");
    }

    /// <summary>
    ///     P1: the dispatcher forwards the alongside session id to the quality service verbatim —
    ///     SearchQuery stays attribution-free (it feeds backends that must ignore attribution).
    /// </summary>
    [Fact]
    public async Task DispatchAsync_ForwardsSessionId_ToQualityServiceVerbatim()
    {
        var quality = new SpyQualityService();
        var dispatcher = new SearchDispatcher(new EmptyMemoryStore(), new SpyCodeSearchService(), quality, TestData.SharedScope);
        var searchQuery = new SearchQuery("acme", "widget");

        await dispatcher.DispatchAsync(searchQuery: searchQuery, entryTypes: EntryType.Memory | EntryType.File, rawScope: "memory",
            correlationId: "corr-1", sessionId: "sess-abc-123",
            cancellationToken: TestContext.Current.CancellationToken);

        quality.LastSessionId.ShouldBe("sess-abc-123");
    }

    /// <summary>
    ///     P3 (ADR-0097): the dispatcher forwards the requested kind to the quality service as
    ///     the lowercase wire string — the memory leg keeps the <c>memory</c> label and both
    ///     keeps <c>both</c> (the row still describes the memory leg per ADR-0094; kind names
    ///     the request, not the leg). Mutation: forward the leg instead → the Both case fails.
    /// </summary>
    [Theory]
    [InlineData(EntryType.Memory | EntryType.File, "memory")]
    [InlineData(EntryType.Code, "code")]
    [InlineData(EntryTypeParameter.Default, "both")]
    public async Task DispatchAsync_ForwardsKind_ToQualityServiceLowercased(EntryType kind, string expected)
    {
        var quality = new SpyQualityService();
        var dispatcher = new SearchDispatcher(new EmptyMemoryStore(), new SpyCodeSearchService(), quality, TestData.SharedScope);

        await dispatcher.DispatchAsync(searchQuery: new SearchQuery("acme", "widget"), entryTypes: kind,
            rawScope: "all", correlationId: "corr-1", sessionId: "sess-test",
            cancellationToken: TestContext.Current.CancellationToken);

        quality.LastKind.ShouldBe(expected);
    }

    [Theory]
    [InlineData("acme", SearchScope.Project, true)]
    [InlineData("acme", SearchScope.All, true)]
    [InlineData("Shared", SearchScope.All, true)]
    [InlineData("acme", SearchScope.Shared, false)]
    [InlineData("shared", SearchScope.All, true)]
    [InlineData("shared", SearchScope.Shared, false)]
    public async Task DispatchAsync_CodeSearchRespectsProjectAndSharedRouting(string projectId,
        SearchScope scope, bool callsCode)
    {
        var codeSearch = new SpyCodeSearchService();
        var dispatcher = new SearchDispatcher(new EmptyMemoryStore(), codeSearch,
            new NoOpSearchQualityService(), TestData.SharedScope);

        await dispatcher.DispatchAsync(new SearchQuery(projectId, "widget", Scope: scope), EntryTypeParameter.Default,
            "all", "correlation", "session", cancellationToken: TestContext.Current.CancellationToken);

        codeSearch.WasCalled.ShouldBe(callsCode);
    }

    [Fact]
    public async Task DispatchAsync_InvalidSharedSearchRefusesBeforeCallingCode()
    {
        var codeSearch = new SpyCodeSearchService();
        var dispatcher = new SearchDispatcher(new EmptyMemoryStore(), codeSearch,
            new NoOpSearchQualityService(), TestData.SharedScope);

        await Should.ThrowAsync<ArgumentException>(() => dispatcher.DispatchAsync(
            new SearchQuery("shared", "widget", Scope: SearchScope.Project), EntryTypeParameter.Default,
            "project", "correlation", "session", cancellationToken: TestContext.Current.CancellationToken));

        codeSearch.WasCalled.ShouldBeFalse();
    }

    private sealed class SpyCodeSearchService : ICodeSearchService
    {
        public CodeSearchQuery? LastQuery { get; private set; }

        public bool WasCalled { get; private set; }

        public CodeSearchResults ResultToReturn { get; set; } = new([]);

        public Exception? ThrowOnSearch { get; set; }

        public Task<CodeSearchResults> SearchAsync(CodeSearchQuery query, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            LastQuery = query;
            if (ThrowOnSearch is { } ex)
            {
                throw ex;
            }

            return Task.FromResult(ResultToReturn);
        }

        public Task<CodeEntry?> GetAsync(string projectId, string hash, CancellationToken cancellationToken = default) =>
            Task.FromResult<CodeEntry?>(null);
    }

    private sealed class EmptyMemoryStore : FakeMemoryStore
    {
        public override Task<SearchResults> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SearchResults([], SearchTimings.Empty));
    }

    /// <summary>Dumb record-and-return quality spy: captures the session id and kind, nothing more.</summary>
    private sealed class SpyQualityService : AiRaccoon.Core.SearchQuality.ISearchQualityService
    {
        public string? LastSessionId { get; private set; }

        public string? LastKind { get; private set; }

        public Task RecordSearchAsync(string correlationId, string query, string? scope, string? projectId,
            string kind, string sessionId, int resultCount, IReadOnlyList<string> topSourceFiles, CancellationToken ct = default,
            IReadOnlyList<RetrievalEvidence>? evidence = null,
        AiRaccoon.Core.Memory.EntryType? entryTypes = null)
        {
            LastKind = kind;
            LastSessionId = sessionId;
            return Task.CompletedTask;
        }

        public Task RecordSearchSafeAsync(string correlationId, string query, string? scope, string? projectId,
            string kind, string sessionId, int resultCount, IReadOnlyList<string> topSourceFiles, CancellationToken ct = default,
            IReadOnlyList<RetrievalEvidence>? evidence = null,
        AiRaccoon.Core.Memory.EntryType? entryTypes = null)
        {
            LastKind = kind;
            LastSessionId = sessionId;
            return Task.CompletedTask;
        }

        public Task<bool> RecordFollowThroughAsync(string projectId, string correlationId, string filePath, int? servedRank = null,
            CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<bool> RecordGradeAsync(string projectId, string correlationId, int grade, string? note,
            CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<AiRaccoon.Core.SearchQuality.SearchQualityMetrics> GetMetricsAsync(string? projectId, DateTimeOffset from,
            CancellationToken ct = default) =>
            Task.FromResult(new AiRaccoon.Core.SearchQuality.SearchQualityMetrics(0, 0, 0, 0, 0, 0, 0));

        public Task<int> PurgeOlderThanAsync(long nowUnixSeconds, int retentionDays,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }
}
