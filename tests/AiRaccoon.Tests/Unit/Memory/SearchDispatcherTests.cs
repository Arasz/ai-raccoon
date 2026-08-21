using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Code;
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
        var dispatcher = new SearchDispatcher(new EmptyMemoryStore(), codeSearch, new NoOpSearchQualityService());
        var searchQuery = new SearchQuery("acme", "widget", Limit: 20, MinRelativeScore: 0.0);

        await dispatcher.DispatchAsync(searchQuery, SearchKind.Code, "code", "corr-1",
            codeLimit: 5, codeMinRelativeScore: 0.4, cancellationToken: TestContext.Current.CancellationToken);

        codeSearch.LastQuery.ShouldNotBeNull();
        codeSearch.LastQuery!.Limit.ShouldBe(5);
        codeSearch.LastQuery.MinRelativeScore.ShouldBe(0.4);
    }

    [Fact]
    public async Task DispatchAsync_CodeLimitAndMinRelativeScoreOmitted_FallBackToTheSharedQueryValues()
    {
        var codeSearch = new SpyCodeSearchService { ResultToReturn = new CodeSearchResults([]) };
        var dispatcher = new SearchDispatcher(new EmptyMemoryStore(), codeSearch, new NoOpSearchQualityService());
        var searchQuery = new SearchQuery("acme", "widget", Limit: 20, MinRelativeScore: 0.3);

        await dispatcher.DispatchAsync(searchQuery, SearchKind.Code, "code", "corr-1", cancellationToken: TestContext.Current.CancellationToken);

        codeSearch.LastQuery!.Limit.ShouldBe(20);
        codeSearch.LastQuery.MinRelativeScore.ShouldBe(0.3);
    }

    [Fact]
    public async Task DispatchAsync_ForwardsTheCodeService_OwnWarning_VerbatimNoLongerHardcoded()
    {
        var codeSearch = new SpyCodeSearchService { ResultToReturn = new CodeSearchResults([], "a real, service-computed warning") };
        var dispatcher = new SearchDispatcher(new EmptyMemoryStore(), codeSearch, new NoOpSearchQualityService());

        var result = await dispatcher.DispatchAsync(new SearchQuery("acme", "widget"), SearchKind.Code, "code", "corr-1", cancellationToken: TestContext.Current.CancellationToken);

        result.CodeWarning.ShouldBe("a real, service-computed warning");
    }

    [Fact]
    public async Task DispatchAsync_KindCode_ConfiguredButUnloadableCodeEngine_ThrowsActionableError()
    {
        var codeSearch = new SpyCodeSearchService { ThrowOnSearch = new CodeEngineUnloadableException("/models/broken", new InvalidOperationException("bad manifest")) };
        var dispatcher = new SearchDispatcher(new EmptyMemoryStore(), codeSearch, new NoOpSearchQualityService());

        await Should.ThrowAsync<CodeEngineUnloadableException>(() =>
            dispatcher.DispatchAsync(new SearchQuery("acme", "widget"), SearchKind.Code, "code", "corr-1",
                cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DispatchAsync_KindMemory_UnaffectedByABrokenCodeEngine_NeverCallsTheCodeService()
    {
        var codeSearch = new SpyCodeSearchService { ThrowOnSearch = new CodeEngineUnloadableException("/models/broken", new InvalidOperationException("bad manifest")) };
        var dispatcher = new SearchDispatcher(new EmptyMemoryStore(), codeSearch, new NoOpSearchQualityService());

        var result = await dispatcher.DispatchAsync(new SearchQuery("acme", "widget"), SearchKind.Memory, "memory", "corr-1", cancellationToken: TestContext.Current.CancellationToken);

        result.Results.ShouldBeEmpty();
        codeSearch.WasCalled.ShouldBeFalse("kind=memory must never touch the code engine at all — the two are independent settings rows");
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
}
