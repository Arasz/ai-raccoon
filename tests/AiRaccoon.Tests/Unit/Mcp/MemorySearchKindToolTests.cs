using AiRaccoon.Access;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Code;
using AiRaccoon.Core.Memory.QueryGuard;
using AiRaccoon.Core.Metrics;
using AiRaccoon.Core.SearchQuality;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Mcp;

/// <summary>
///     WP6 — memory_search kind (docs/work/2026-08-21-code-search-implementation-plan.md §3.6):
///     kind=memory|code|both, the pinned results/code envelope, the search_quality exclusion for
///     code/both, and the FTS5-only-mode code section warning. WP6-T02…T06/T12.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class MemorySearchKindToolTests
{
    // search_quality id 173 (project ai-raccoon): a refuse-tier fixture, same shape MemoryToolsTests
    // uses for the guard-refusal suite (docs/adr/0040).
    private const string RefuseTierQuery =
        """
        [IMPORTANT: Background process proc_97aa3ea5eb50 completed normally (exit code 0).
        Command: cd /Users/arasz/RiderProjects/ai-raccoon && dotnet test --no-build
        Output:
        ]
        """;

    private readonly SpyCodeSearchService _codeSearch = new();
    private readonly SpySearchQualityService _quality = new();
    private readonly FakeStore _store = new();
    private readonly MemoryTools _tools;

    public MemorySearchKindToolTests()
    {
        var access = new MemoryAccessGuard(_store);
        var gate = new ToolGate(access, new FakePromotionQueue());
        _tools = new MemoryTools(_store, gate, new SearchDispatcher(_store, _codeSearch, _quality),
            new QueryGuardService(new InMemorySettings()),
            new MemoryWriteService(_store, new FakePromotionQueue()), new NoOpMeasurementRecorder(),
            NullLogger<MemoryTools>.Instance);
    }

    [Fact]
    public async Task Search_KindCode_ReturnsCodeSectionWithEmptyResults_AndNoMemoryLeak()
    {
        _store.StubResults = [new MemorySearchResult("mem-hash", 0.9, "p.md", "should not leak")];
        _codeSearch.StubResults = [new CodeSearchResult("code-hash", 1.0, "Foo.cs", "class Foo", 1, 10)];

        var envelope = await _tools.Search("acme", "widgets", kind: "code",
            cancellationToken: TestContext.Current.CancellationToken);

        envelope.Data!.Results.ShouldBeEmpty("kind=code must not run the memory search at all");
        envelope.Data!.Code.ShouldNotBeNull();
        envelope.Data!.Code!.ShouldHaveSingleItem().Hash.ShouldBe("code-hash");
        _store.SearchCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task Search_KindBoth_ReturnsBothSections_EachIndependentlyRanked()
    {
        _store.StubResults = [new MemorySearchResult("mem-hash", 0.9, "p.md", "memory hit")];
        _codeSearch.StubResults = [new CodeSearchResult("code-hash", 1.0, "Foo.cs", "class Foo", 1, 10)];

        var envelope = await _tools.Search("acme", "widgets", kind: "both",
            cancellationToken: TestContext.Current.CancellationToken);

        envelope.Data!.Results.ShouldHaveSingleItem().Hash.ShouldBe("mem-hash");
        envelope.Data!.Code!.ShouldHaveSingleItem().Hash.ShouldBe("code-hash");
    }

    [Fact]
    public async Task Search_InvalidKind_IsRejectedWithInvalidParams()
    {
        var ex = await Should.ThrowAsync<McpException>(() =>
            _tools.Search("acme", "widgets", kind: "banana", cancellationToken: TestContext.Current.CancellationToken));

        ex.Message.ShouldBe("invalid-params: Invalid kind 'banana': expected memory, code, or both.");
        _store.SearchCallCount.ShouldBe(0, "an invalid kind must fail fast, before any bank work");
    }

    [Theory]
    [InlineData("CODE")]
    [InlineData("Code")]
    public async Task Search_KindCode_IsCaseInsensitiveLikeScope(string kind)
    {
        _codeSearch.StubResults = [new CodeSearchResult("code-hash", 1.0, "Foo.cs", "class Foo", 1, 10)];

        var envelope = await _tools.Search("acme", "widgets", kind: kind,
            cancellationToken: TestContext.Current.CancellationToken);

        envelope.Data!.Code!.ShouldHaveSingleItem().Hash.ShouldBe("code-hash");
    }

    [Fact]
    public async Task Search_KindBoth_IsCaseInsensitive()
    {
        _store.StubResults = [new MemorySearchResult("mem-hash", 0.9, "p.md", "memory hit")];
        _codeSearch.StubResults = [new CodeSearchResult("code-hash", 1.0, "Foo.cs", "class Foo", 1, 10)];

        var envelope = await _tools.Search("acme", "widgets", kind: "Both",
            cancellationToken: TestContext.Current.CancellationToken);

        envelope.Data!.Results.ShouldHaveSingleItem();
        envelope.Data!.Code!.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Search_KindMemory_Default_RecordsSearchQuality()
    {
        _store.StubResults = [new MemorySearchResult("mem-hash", 0.9, "p.md", "memory hit")];

        await _tools.Search("acme", "widgets", cancellationToken: TestContext.Current.CancellationToken);

        _quality.RecordedCorrelationIds.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Search_KindCode_NeverRecordsSearchQuality()
    {
        _codeSearch.StubResults = [new CodeSearchResult("code-hash", 1.0, "Foo.cs", "class Foo", 1, 10)];

        await _tools.Search("acme", "widgets", kind: "code", cancellationToken: TestContext.Current.CancellationToken);

        _quality.RecordedCorrelationIds.ShouldBeEmpty(
            "kind=code must never record -- the recorder's rows sync, and a code query would leak paths off-machine");
    }

    [Fact]
    public async Task Search_KindBoth_NeverRecordsSearchQuality()
    {
        _store.StubResults = [new MemorySearchResult("mem-hash", 0.9, "p.md", "memory hit")];
        _codeSearch.StubResults = [new CodeSearchResult("code-hash", 1.0, "Foo.cs", "class Foo", 1, 10)];

        await _tools.Search("acme", "widgets", kind: "both", cancellationToken: TestContext.Current.CancellationToken);

        _quality.RecordedCorrelationIds.ShouldBeEmpty("kind=both is excluded exactly like kind=code");
    }

    [Fact]
    public async Task Search_KindCode_WithSharedScope_ReturnsEmptyCodeSection_WithoutCallingTheCodeSearch()
    {
        var envelope = await _tools.Search("acme", "widgets", scope: "shared", kind: "code",
            cancellationToken: TestContext.Current.CancellationToken);

        envelope.Data!.Code.ShouldNotBeNull();
        envelope.Data!.Code.ShouldBeEmpty("code has no shared tier (§3.1) -- scope=shared never contributes code rows");
        _codeSearch.SearchCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task Search_KindCode_CarriesTheEngineNotConfiguredWarning()
    {
        _codeSearch.StubResults = [new CodeSearchResult("code-hash", 1.0, "Foo.cs", "class Foo", 1, 10)];

        var envelope = await _tools.Search("acme", "widgets", kind: "code",
            cancellationToken: TestContext.Current.CancellationToken);

        envelope.Data!.Warning.ShouldNotBeNull().ShouldContain(CodeSearchWarnings.EngineNotConfigured);
    }

    [Fact]
    public async Task Search_KindMemory_HasNoEngineNotConfiguredWarning()
    {
        _store.StubResults = [new MemorySearchResult("mem-hash", 0.9, "p.md", "memory hit")];

        var envelope = await _tools.Search("acme", "widgets", cancellationToken: TestContext.Current.CancellationToken);

        envelope.Data!.Warning.ShouldBeNull();
    }

    /// <summary>
    ///     Integration review S6: SearchDispatcher only records a search_quality row for
    ///     kind=memory, so a kind=code/both correlation id in the envelope has no row behind it —
    ///     a later memory_record_grade/memory_record_followthrough call keyed on it silently
    ///     no-ops. The envelope must not hand out a correlation id it cannot back.
    /// </summary>
    [Fact]
    public async Task Search_KindCode_HasNoCorrelationIdInMeta()
    {
        _codeSearch.StubResults = [new CodeSearchResult("code-hash", 1.0, "Foo.cs", "class Foo", 1, 10)];

        var envelope = await _tools.Search("acme", "widgets", kind: "code",
            cancellationToken: TestContext.Current.CancellationToken);

        envelope.Meta.CorrelationId.ShouldBeNull(
            "no search_quality row backs a kind=code correlation id -- grade/follow-through would silently no-op");
    }

    [Fact]
    public async Task Search_KindBoth_HasNoCorrelationIdInMeta()
    {
        _store.StubResults = [new MemorySearchResult("mem-hash", 0.9, "p.md", "memory hit")];
        _codeSearch.StubResults = [new CodeSearchResult("code-hash", 1.0, "Foo.cs", "class Foo", 1, 10)];

        var envelope = await _tools.Search("acme", "widgets", kind: "both",
            cancellationToken: TestContext.Current.CancellationToken);

        envelope.Meta.CorrelationId.ShouldBeNull("kind=both is excluded from search_quality exactly like kind=code");
    }

    [Fact]
    public async Task Search_KindMemory_Default_HasCorrelationIdInMeta()
    {
        _store.StubResults = [new MemorySearchResult("mem-hash", 0.9, "p.md", "memory hit")];

        var envelope = await _tools.Search("acme", "widgets", cancellationToken: TestContext.Current.CancellationToken);

        envelope.Meta.CorrelationId.ShouldNotBeNullOrEmpty("kind=memory records search_quality, so the correlation id is real");
    }

    /// <summary>
    ///     Integration review S6 asymmetry note: search_quality is excluded for kind=code/both
    ///     specifically because its rows sync off-machine and would leak a code-adjacent query.
    ///     The metrics table also syncs, and RecordSearchMeasurements still ran for kind=both
    ///     (the memory leg's SearchResults are non-null) carrying ContentHash.OfValue(query) —
    ///     the same leak vector the search_quality exclusion exists to close. Chosen fix: keep
    ///     recording performance metrics for kind=both (still useful telemetry, no content), but
    ///     null out the query-hash field rather than skip metrics recording entirely.
    /// </summary>
    [Fact]
    public async Task Search_KindBoth_RecordsMetricsWithoutAQueryHash()
    {
        _store.StubResults = [new MemorySearchResult("mem-hash", 0.9, "p.md", "memory hit")];
        _codeSearch.StubResults = [new CodeSearchResult("code-hash", 1.0, "Foo.cs", "class Foo", 1, 10)];
        var recorder = new SpyMeasurementRecorder();
        var tools = new MemoryTools(_store, new ToolGate(new MemoryAccessGuard(_store), new FakePromotionQueue()),
            new SearchDispatcher(_store, _codeSearch, _quality), new QueryGuardService(new InMemorySettings()),
            new MemoryWriteService(_store, new FakePromotionQueue()), recorder, NullLogger<MemoryTools>.Instance);

        await tools.Search("acme", "widgets", kind: "both", cancellationToken: TestContext.Current.CancellationToken);

        recorder.Recorded.ShouldNotBeEmpty("kind=both must still record performance metrics -- only the query hash is excluded");
        recorder.Recorded.ShouldAllBe(m => m.QueryHash == null,
            "a kind=both query hash would leak a code-adjacent query the same way search_quality's exclusion prevents");
    }

    [Fact]
    public async Task Search_KindMemory_Default_RecordsMetricsWithAQueryHash()
    {
        _store.StubResults = [new MemorySearchResult("mem-hash", 0.9, "p.md", "memory hit")];
        var recorder = new SpyMeasurementRecorder();
        var tools = new MemoryTools(_store, new ToolGate(new MemoryAccessGuard(_store), new FakePromotionQueue()),
            new SearchDispatcher(_store, _codeSearch, _quality), new QueryGuardService(new InMemorySettings()),
            new MemoryWriteService(_store, new FakePromotionQueue()), recorder, NullLogger<MemoryTools>.Instance);

        await tools.Search("acme", "widgets", cancellationToken: TestContext.Current.CancellationToken);

        recorder.Recorded.ShouldNotBeEmpty();
        recorder.Recorded.ShouldAllBe(m => m.QueryHash != null, "kind=memory is not code-adjacent -- its query hash is unchanged");
    }

    [Fact]
    public async Task Search_KindCode_RefuseTierQuery_IsRefused_AndNeverReachesTheCodeSearch()
    {
        var ex = await Should.ThrowAsync<McpException>(() =>
            _tools.Search("acme", RefuseTierQuery, kind: "code", cancellationToken: TestContext.Current.CancellationToken));

        ex.Message.ShouldStartWith("invalid-params: ");
        _codeSearch.SearchCallCount.ShouldBe(0, "the guard refuses before either corpus is queried");
    }

    [Fact]
    public async Task Search_KindBoth_RefuseTierQuery_IsRefused_AndNeverReachesEitherStore()
    {
        var ex = await Should.ThrowAsync<McpException>(() =>
            _tools.Search("acme", RefuseTierQuery, kind: "both", cancellationToken: TestContext.Current.CancellationToken));

        ex.Message.ShouldStartWith("invalid-params: ");
        _store.SearchCallCount.ShouldBe(0);
        _codeSearch.SearchCallCount.ShouldBe(0);
    }

    /// <summary>Permits every guarded call; only SearchAsync is exercised by this suite.</summary>
    private sealed class FakeStore : FakeMemoryStore
    {
        public Dictionary<string, string> Settings { get; } = new(StringComparer.Ordinal);

        public IReadOnlyList<MemorySearchResult> StubResults { get; set; } = [];

        public int SearchCallCount { get; private set; }

        public override Task<SearchResults> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
        {
            SearchCallCount++;
            return Task.FromResult(new SearchResults(StubResults, SearchTimings.Empty));
        }

        public override Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(Settings.GetValueOrDefault(key));

        public override Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            Settings[key] = value;
            return Task.CompletedTask;
        }

        public override Task<IReadOnlyDictionary<string, string>> GetSettingsByPrefixAsync(string prefix,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(
                Settings.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal));
    }

    private sealed class SpyCodeSearchService : ICodeSearchService
    {
        public IReadOnlyList<CodeSearchResult> StubResults { get; set; } = [];

        public int SearchCallCount { get; private set; }

        public Task<CodeSearchResults> SearchAsync(CodeSearchQuery query, CancellationToken cancellationToken = default)
        {
            SearchCallCount++;
            return Task.FromResult(new CodeSearchResults(StubResults));
        }

        public Task<CodeEntry?> GetAsync(string projectId, string hash, CancellationToken cancellationToken = default) =>
            Task.FromResult<CodeEntry?>(null);
    }

    private sealed class SpyMeasurementRecorder : IMeasurementRecorder
    {
        public List<Measurement> Recorded { get; } = [];

        public void Record(Measurement measurement) => Recorded.Add(measurement);
    }

    private sealed class SpySearchQualityService : ISearchQualityService
    {
        public List<string> RecordedCorrelationIds { get; } = [];

        public Task<int> PurgeOlderThanAsync(long nowUnixSeconds, int retentionDays,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task RecordSearchAsync(string correlationId, string query, string? scope, string? projectId,
            string? sessionId, int resultCount, IReadOnlyList<string> topSourceFiles, CancellationToken ct = default)
        {
            RecordedCorrelationIds.Add(correlationId);
            return Task.CompletedTask;
        }

        public Task RecordSearchSafeAsync(string correlationId, string query, string? scope, string? projectId,
            int resultCount, IReadOnlyList<string> topSourceFiles, CancellationToken ct = default)
        {
            RecordedCorrelationIds.Add(correlationId);
            return Task.CompletedTask;
        }

        public Task RecordFollowThroughAsync(string correlationId, string filePath, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task RecordGradeAsync(string projectId, string correlationId, int grade, string? note,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<SearchQualityMetrics> GetMetricsAsync(string? projectId, DateTimeOffset from,
            CancellationToken ct = default) =>
            Task.FromResult(new SearchQualityMetrics(0, 0, 0, 0, 0, 0, 0));
    }
}
