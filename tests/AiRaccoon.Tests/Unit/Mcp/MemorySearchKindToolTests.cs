using AiRaccoon.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Code;
using AiRaccoon.Core.Memory.QueryGuard;
using AiRaccoon.Core.Metrics;
using AiRaccoon.Core.SearchQuality;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Mcp;

/// <summary>
///     WP6 — memory_search kind (docs/work/2026-08-21-code-search-implementation-plan.md §3.6):
///     kind=memory|code|both, the pinned results/code envelope, the search_quality recording rule
///     for every kind (ADR-0094: both records the memory leg, code its count with no files), and
///     the FTS5-only-mode code section warning. WP6-T02…T06/T12.
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
        var gate = new ToolGate(access, new FakePromotionQueue(), new NeverMigratingStore(), new AllowingRegistrationGuard());
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

        var envelope = await _tools.Search("acme", "widgets", sessionId: "sess-test", kind: "code",
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

        var envelope = await _tools.Search("acme", "widgets", sessionId: "sess-test", kind: "both",
            cancellationToken: TestContext.Current.CancellationToken);

        envelope.Data!.Results.ShouldHaveSingleItem().Hash.ShouldBe("mem-hash");
        envelope.Data!.Code!.ShouldHaveSingleItem().Hash.ShouldBe("code-hash");
    }

    [Fact]
    public async Task Search_InvalidKind_IsRejectedWithInvalidParams()
    {
        var ex = await Should.ThrowAsync<McpException>(() =>
            _tools.Search("acme", "widgets", sessionId: "sess-test", kind: "banana", cancellationToken: TestContext.Current.CancellationToken));

        ex.Message.ShouldBe("invalid-params: Invalid kind 'banana': expected memory, code, or both.");
        _store.SearchCallCount.ShouldBe(0, "an invalid kind must fail fast, before any bank work");
    }

    /// <summary>Pins the wire default itself (mirrors SearchScoreFloorContractTests' shape): a
    /// silent default revert back to "memory" must show up here, not only in behavior tests.</summary>
    [Fact]
    public void Search_KindParameter_DefaultsToBoth()
    {
        var parameter = typeof(MemoryTools)
            .GetMethod(nameof(MemoryTools.Search), System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .ShouldNotBeNull()
            .GetParameters()
            .SingleOrDefault(p => p.Name == "kind");

        parameter.ShouldNotBeNull("the MCP parameter must still be named 'kind'");
        parameter.HasDefaultValue.ShouldBeTrue();
        parameter.DefaultValue.ShouldBe("both");
    }

    /// <summary>S5: codeLimit/codeMinRelativeScore override limit/minRelativeScore for the code
    /// section only — they must be validated the same way, not passed through silently.</summary>
    [Fact]
    public async Task Search_CodeLimitZeroOrNegative_IsRejectedWithInvalidParams()
    {
        var ex = await Should.ThrowAsync<McpException>(() =>
            _tools.Search("acme", "widgets", sessionId: "sess-test", kind: "code", codeLimit: 0,
                cancellationToken: TestContext.Current.CancellationToken));

        ex.Message.ShouldStartWith("invalid-params: ");
        ex.Message.ShouldContain("codeLimit");
        _codeSearch.SearchCallCount.ShouldBe(0, "an invalid codeLimit must fail fast, before either corpus is queried");
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public async Task Search_CodeMinRelativeScoreOutOfRange_IsRejectedWithInvalidParams(double codeMinRelativeScore)
    {
        var ex = await Should.ThrowAsync<McpException>(() =>
            _tools.Search("acme", "widgets", sessionId: "sess-test", kind: "code", codeMinRelativeScore: codeMinRelativeScore,
                cancellationToken: TestContext.Current.CancellationToken));

        ex.Message.ShouldStartWith("invalid-params: ");
        ex.Message.ShouldContain("codeMinRelativeScore");
        _codeSearch.SearchCallCount.ShouldBe(0);
    }

    [Theory]
    [InlineData("CODE")]
    [InlineData("Code")]
    public async Task Search_KindCode_IsCaseInsensitiveLikeScope(string kind)
    {
        _codeSearch.StubResults = [new CodeSearchResult("code-hash", 1.0, "Foo.cs", "class Foo", 1, 10)];

        var envelope = await _tools.Search("acme", "widgets", sessionId: "sess-test", kind: kind,
            cancellationToken: TestContext.Current.CancellationToken);

        envelope.Data!.Code!.ShouldHaveSingleItem().Hash.ShouldBe("code-hash");
    }

    [Fact]
    public async Task Search_KindBoth_IsCaseInsensitive()
    {
        _store.StubResults = [new MemorySearchResult("mem-hash", 0.9, "p.md", "memory hit")];
        _codeSearch.StubResults = [new CodeSearchResult("code-hash", 1.0, "Foo.cs", "class Foo", 1, 10)];

        var envelope = await _tools.Search("acme", "widgets", sessionId: "sess-test", kind: "Both",
            cancellationToken: TestContext.Current.CancellationToken);

        envelope.Data!.Results.ShouldHaveSingleItem();
        envelope.Data!.Code!.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Search_DefaultKind_IsBoth()
    {
        _store.StubResults = [new MemorySearchResult("mem-hash", 0.9, "p.md", "memory hit")];
        _codeSearch.StubResults = [new CodeSearchResult("code-hash", 1.0, "Foo.cs", "class Foo", 1, 10)];

        var envelope = await _tools.Search("acme", "widgets", sessionId: "sess-test", cancellationToken: TestContext.Current.CancellationToken);

        envelope.Data!.Results.ShouldHaveSingleItem().Hash.ShouldBe("mem-hash",
            "the default kind runs the memory leg");
        envelope.Data!.Code!.ShouldHaveSingleItem().Hash.ShouldBe("code-hash",
            "the default kind is both -- the code leg runs with no explicit kind");
        _codeSearch.SearchCallCount.ShouldBe(1);
    }

    [Fact]
    public async Task Search_KindMemory_Explicit_RecordsSearchQuality()
    {
        _store.StubResults = [new MemorySearchResult("mem-hash", 0.9, "p.md", "memory hit")];

        await _tools.Search("acme", "widgets", sessionId: "sess-test", kind: "memory", cancellationToken: TestContext.Current.CancellationToken);

        _quality.RecordedCorrelationIds.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Search_KindCode_RecordsSearchQuality()
    {
        _codeSearch.StubResults = [new CodeSearchResult("code-hash", 1.0, "Foo.cs", "class Foo", 1, 10)];

        await _tools.Search("acme", "widgets", sessionId: "sess-test", kind: "code", cancellationToken: TestContext.Current.CancellationToken);

        _quality.RecordedCorrelationIds.ShouldHaveSingleItem(
            "ADR-0094: every kind records -- a default (both) search with no row behind it is a dead quality signal");
    }

    [Fact]
    public async Task Search_KindBoth_RecordsSearchQuality()
    {
        _store.StubResults = [new MemorySearchResult("mem-hash", 0.9, "p.md", "memory hit")];
        _codeSearch.StubResults = [new CodeSearchResult("code-hash", 1.0, "Foo.cs", "class Foo", 1, 10)];

        await _tools.Search("acme", "widgets", sessionId: "sess-test", kind: "both", cancellationToken: TestContext.Current.CancellationToken);

        _quality.RecordedCorrelationIds.ShouldHaveSingleItem("ADR-0094: kind=both records exactly like kind=memory");
    }

    [Fact]
    public async Task Search_KindBoth_RecordsTheMemoryLeg_NeverCodePaths()
    {
        _store.StubResults =
        [
            new MemorySearchResult("mem-hash", 0.9, "p.md", "memory hit", SourceFile: "p.md"),
            new MemorySearchResult("mem-hash-2", 0.8, "q.md", "second hit", SourceFile: "q.md"),
        ];
        _codeSearch.StubResults = [new CodeSearchResult("code-hash", 1.0, "Foo.cs", "class Foo", 1, 10)];

        await _tools.Search("acme", "widgets", sessionId: "sess-test", kind: "both", cancellationToken: TestContext.Current.CancellationToken);

        _quality.LastResultCount.ShouldBe(2, "the row describes the memory leg");
        _quality.LastTopSourceFiles.ShouldBe(["p.md", "q.md"]);
        _quality.LastTopSourceFiles.ShouldNotContain("Foo.cs",
            "code paths must never enter the syncing search_quality table (ADR-0085 never-syncs rule)");
    }

    [Fact]
    public async Task Search_KindCode_RecordsTheCodeCount_WithNoSourceFiles()
    {
        _codeSearch.StubResults =
        [
            new CodeSearchResult("code-hash", 1.0, "Foo.cs", "class Foo", 1, 10),
            new CodeSearchResult("code-hash-2", 0.9, "Bar.cs", "class Bar", 20, 30),
        ];

        await _tools.Search("acme", "widgets", sessionId: "sess-test", kind: "code", cancellationToken: TestContext.Current.CancellationToken);

        _quality.LastResultCount.ShouldBe(2, "a code row with a memory-leg 0 would make grades uninterpretable");
        _quality.LastTopSourceFiles.ShouldBeEmpty(
            "code paths must never enter the syncing search_quality table (ADR-0085 never-syncs rule)");
    }

    [Fact]
    public async Task Search_KindCode_WithSharedScope_ReturnsEmptyCodeSection_WithoutCallingTheCodeSearch()
    {
        var envelope = await _tools.Search("acme", "widgets", sessionId: "sess-test", scope: "shared", kind: "code",
            cancellationToken: TestContext.Current.CancellationToken);

        envelope.Data!.Code.ShouldNotBeNull();
        envelope.Data!.Code.ShouldBeEmpty("code has no shared tier (§3.1) -- scope=shared never contributes code rows");
        _codeSearch.SearchCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task Search_KindCode_ForwardsTheCodeService_OwnWarning()
    {
        // WP5: the warning is no longer SearchDispatcher's own hardcoded constant -- it is
        // whatever CodeSearchResults.Warning the real SqliteCodeSearchService computed
        // (EngineNotConfigured, QueryTrimmedToCodeWindow, or null), forwarded verbatim.
        _codeSearch.StubResults = [new CodeSearchResult("code-hash", 1.0, "Foo.cs", "class Foo", 1, 10)];
        _codeSearch.StubWarning = CodeSearchWarnings.EngineNotConfigured;

        var envelope = await _tools.Search("acme", "widgets", sessionId: "sess-test", kind: "code",
            cancellationToken: TestContext.Current.CancellationToken);

        envelope.Data!.Warning.ShouldNotBeNull().ShouldContain(CodeSearchWarnings.EngineNotConfigured);
    }

    [Fact]
    public async Task Search_KindMemory_HasNoEngineNotConfiguredWarning()
    {
        _store.StubResults = [new MemorySearchResult("mem-hash", 0.9, "p.md", "memory hit")];

        var envelope = await _tools.Search("acme", "widgets", sessionId: "sess-test", kind: "memory",
            cancellationToken: TestContext.Current.CancellationToken);

        envelope.Data!.Warning.ShouldBeNull();
    }

    /// <summary>
    ///     ADR-0094 supersedes the integration-review S6 withholding rule: every kind now records
    ///     a search_quality row, so every envelope carries a backed correlation id and a later
    ///     memory_record_grade/memory_record_followthrough call always has a row to key on.
    /// </summary>
    [Fact]
    public async Task Search_KindCode_HasCorrelationIdInMeta()
    {
        _codeSearch.StubResults = [new CodeSearchResult("code-hash", 1.0, "Foo.cs", "class Foo", 1, 10)];

        var envelope = await _tools.Search("acme", "widgets", sessionId: "sess-test", kind: "code",
            cancellationToken: TestContext.Current.CancellationToken);

        envelope.Meta.CorrelationId.ShouldNotBeNullOrEmpty(
            "ADR-0094: a kind=code row is recorded, so the correlation id is backed");
    }

    [Fact]
    public async Task Search_KindBoth_HasCorrelationIdInMeta()
    {
        _store.StubResults = [new MemorySearchResult("mem-hash", 0.9, "p.md", "memory hit")];
        _codeSearch.StubResults = [new CodeSearchResult("code-hash", 1.0, "Foo.cs", "class Foo", 1, 10)];

        var envelope = await _tools.Search("acme", "widgets", sessionId: "sess-test", kind: "both",
            cancellationToken: TestContext.Current.CancellationToken);

        envelope.Meta.CorrelationId.ShouldNotBeNullOrEmpty("ADR-0094: kind=both records exactly like kind=memory");
    }

    [Fact]
    public async Task Search_KindMemory_Explicit_HasCorrelationIdInMeta()
    {
        _store.StubResults = [new MemorySearchResult("mem-hash", 0.9, "p.md", "memory hit")];

        var envelope = await _tools.Search("acme", "widgets", sessionId: "sess-test", kind: "memory",
            cancellationToken: TestContext.Current.CancellationToken);

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
        var tools = new MemoryTools(_store, new ToolGate(new MemoryAccessGuard(_store), new FakePromotionQueue(), new NeverMigratingStore(), new AllowingRegistrationGuard()),
            new SearchDispatcher(_store, _codeSearch, _quality), new QueryGuardService(new InMemorySettings()),
            new MemoryWriteService(_store, new FakePromotionQueue()), recorder, NullLogger<MemoryTools>.Instance);

        await tools.Search("acme", "widgets", sessionId: "sess-test", kind: "both", cancellationToken: TestContext.Current.CancellationToken);

        recorder.Recorded.ShouldNotBeEmpty("kind=both must still record performance metrics -- only the query hash is excluded");
        recorder.Recorded.ShouldAllBe(m => m.QueryHash == null,
            "a kind=both query hash would leak a code-adjacent query the same way search_quality's exclusion prevents");
    }

    [Fact]
    public async Task Search_KindMemory_Explicit_RecordsMetricsWithAQueryHash()
    {
        _store.StubResults = [new MemorySearchResult("mem-hash", 0.9, "p.md", "memory hit")];
        var recorder = new SpyMeasurementRecorder();
        var tools = new MemoryTools(_store, new ToolGate(new MemoryAccessGuard(_store), new FakePromotionQueue(), new NeverMigratingStore(), new AllowingRegistrationGuard()),
            new SearchDispatcher(_store, _codeSearch, _quality), new QueryGuardService(new InMemorySettings()),
            new MemoryWriteService(_store, new FakePromotionQueue()), recorder, NullLogger<MemoryTools>.Instance);

        await tools.Search("acme", "widgets", sessionId: "sess-test", kind: "memory", cancellationToken: TestContext.Current.CancellationToken);

        recorder.Recorded.ShouldNotBeEmpty();
        recorder.Recorded.ShouldAllBe(m => m.QueryHash != null, "kind=memory is not code-adjacent -- its query hash is unchanged");
    }

    [Fact]
    public async Task Search_KindCode_RefuseTierQuery_IsRefused_AndNeverReachesTheCodeSearch()
    {
        var ex = await Should.ThrowAsync<McpException>(() =>
            _tools.Search("acme", RefuseTierQuery, sessionId: "sess-test", kind: "code", cancellationToken: TestContext.Current.CancellationToken));

        ex.Message.ShouldStartWith("invalid-params: ");
        _codeSearch.SearchCallCount.ShouldBe(0, "the guard refuses before either corpus is queried");
    }

    [Fact]
    public async Task Search_KindBoth_RefuseTierQuery_IsRefused_AndNeverReachesEitherStore()
    {
        var ex = await Should.ThrowAsync<McpException>(() =>
            _tools.Search("acme", RefuseTierQuery, sessionId: "sess-test", kind: "both", cancellationToken: TestContext.Current.CancellationToken));

        ex.Message.ShouldStartWith("invalid-params: ");
        _store.SearchCallCount.ShouldBe(0);
        _codeSearch.SearchCallCount.ShouldBe(0);
    }

    /// <summary>
    ///     The default kind is both (2026-08-24 default flip): a default search runs the code leg
    ///     too, and with no engine configured it must degrade — FTS5-only results plus an
    ///     EngineNotConfigured warning — never refuse. The memory leg is unaffected.
    /// </summary>
    [Fact]
    public async Task Search_KindBoth_WithNoEngineConfigured_DegradesToFtsOnlyWithWarning()
    {
        _store.StubResults = [new MemorySearchResult("mem-hash", 0.9, "p.md", "memory hit")];
        _codeSearch.StubResults = [new CodeSearchResult("code-hash", 1.0, "Foo.cs", "class Foo", 1, 10)];
        _codeSearch.StubWarning = CodeSearchWarnings.EngineNotConfigured;

        var envelope = await _tools.Search("acme", "widgets", sessionId: "sess-test", cancellationToken: TestContext.Current.CancellationToken);

        envelope.Data!.Results.ShouldHaveSingleItem("the memory leg is unaffected by the missing code engine");
        envelope.Data!.Code.ShouldNotBeNull().ShouldHaveSingleItem("FTS5-only results are still returned");
        envelope.Data!.Warning.ShouldNotBeNull().ShouldContain(CodeSearchWarnings.EngineNotConfigured);
    }

    /// <summary>
    ///     P1: the present session id travels tool → dispatcher → quality service verbatim.
    ///     The verbatim-persisted assertion lives in SearchQualityServiceTests (real bank row-read);
    ///     this pins the tool-boundary forwarding leg through the live dispatcher.
    /// </summary>
    [Fact]
    public async Task Search_PresentSessionId_ForwardedToQualityServiceVerbatim()
    {
        _store.StubResults = [new MemorySearchResult("mem-hash", 0.9, "p.md", "memory hit")];

        await _tools.Search("acme", "widgets", sessionId: "sess-abc-123", kind: "memory",
            cancellationToken: TestContext.Current.CancellationToken);

        _quality.LastSessionId.ShouldBe("sess-abc-123");
    }

    /// <summary>
    ///     P1: blank/whitespace session is rejected fail-fast at the tool boundary, before any
    ///     bank work and before any quality row. Mutation: accept whitespace → this fails.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public async Task Search_BlankSessionId_RejectedFailFast_BeforeAnyBankWork(string sessionId)
    {
        _store.StubResults = [new MemorySearchResult("mem-hash", 0.9, "p.md", "memory hit")];

        await Should.ThrowAsync<ArgumentException>(() =>
            _tools.Search("acme", "widgets", sessionId: sessionId, kind: "memory",
                cancellationToken: TestContext.Current.CancellationToken));

        _store.SearchCallCount.ShouldBe(0, "a blank session must fail fast, before any bank work");
        _quality.RecordedCorrelationIds.ShouldBeEmpty("a rejected search records no quality row");
    }

    /// <summary>
    ///     P1: a throwing quality STORE never fails the search it describes — the real Safe leg
    ///     swallows, so the envelope still returns with a correlation id. Mutation: remove Safe's
    ///     try/catch (propagate) → this fails.
    /// </summary>
    [Fact]
    public async Task Search_ThrowingQualityService_WithSession_StillReturnsEnvelopeWithCorrelationId()
    {
        _store.StubResults = [new MemorySearchResult("mem-hash", 0.9, "p.md", "memory hit")];
        var quality = new SqliteSearchQualityService(new ThrowingConnectionFactory(),
            NullLogger<SqliteSearchQualityService>.Instance);
        var tools = new MemoryTools(_store,
            new ToolGate(new MemoryAccessGuard(_store), new FakePromotionQueue(), new NeverMigratingStore(), new AllowingRegistrationGuard()),
            new SearchDispatcher(_store, _codeSearch, quality), new QueryGuardService(new InMemorySettings()),
            new MemoryWriteService(_store, new FakePromotionQueue()), new NoOpMeasurementRecorder(),
            NullLogger<MemoryTools>.Instance);

        var envelope = await tools.Search("acme", "widgets", sessionId: "sess-xyz", kind: "memory",
            cancellationToken: TestContext.Current.CancellationToken);

        envelope.Data.ShouldNotBeNull();
        envelope.Meta.CorrelationId.ShouldNotBeNullOrEmpty();
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

        public string? StubWarning { get; set; }

        public int SearchCallCount { get; private set; }

        public Task<CodeSearchResults> SearchAsync(CodeSearchQuery query, CancellationToken cancellationToken = default)
        {
            SearchCallCount++;
            return Task.FromResult(new CodeSearchResults(StubResults, StubWarning));
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

        public string? LastQuery { get; private set; }

        public int LastResultCount { get; private set; } = -1;

        public IReadOnlyList<string> LastTopSourceFiles { get; private set; } = [];

        public string? LastSessionId { get; private set; }

        public string? LastKind { get; private set; }

        public Task<int> PurgeOlderThanAsync(long nowUnixSeconds, int retentionDays,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task RecordSearchAsync(string correlationId, string query, string? scope, string? projectId,
            string kind, string sessionId, int resultCount, IReadOnlyList<string> topSourceFiles, CancellationToken ct = default)
        {
            RecordedCorrelationIds.Add(correlationId);
            LastQuery = query;
            LastKind = kind;
            LastSessionId = sessionId;
            LastResultCount = resultCount;
            LastTopSourceFiles = topSourceFiles;
            return Task.CompletedTask;
        }

        public Task RecordSearchSafeAsync(string correlationId, string query, string? scope, string? projectId,
            string kind, string sessionId, int resultCount, IReadOnlyList<string> topSourceFiles, CancellationToken ct = default)
        {
            RecordedCorrelationIds.Add(correlationId);
            LastQuery = query;
            LastKind = kind;
            LastSessionId = sessionId;
            LastResultCount = resultCount;
            LastTopSourceFiles = topSourceFiles;
            return Task.CompletedTask;
        }

        public Task RecordFollowThroughAsync(string correlationId, string filePath, int? servedRank = null,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task RecordGradeAsync(string projectId, string correlationId, int grade, string? note,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<SearchQualityMetrics> GetMetricsAsync(string? projectId, DateTimeOffset from,
            CancellationToken ct = default) =>
            Task.FromResult(new SearchQualityMetrics(0, 0, 0, 0, 0, 0, 0));
    }

    /// <summary>Dumb always-throwing connection factory: every open fails, so the real Safe leg's
    /// swallow is what keeps the search alive — no fake logic, just a dead store.</summary>
    private sealed class ThrowingConnectionFactory : ISqliteConnectionFactory
    {
        public string BankPath => ":throwing:";

        public Task<SqliteConnection> OpenBankAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("boom");

        public Task<bool> MigrateLegacyKeyAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("boom");

        public Task<SqliteConnection> OpenBankWithResolvedKeyAsync(ResolvedKey resolvedKey,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("boom");

        public Task RekeyBankAsync(string newKey, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("boom");

        public Task RekeyBankAsync(string newKey, string? currentKey, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("boom");

        public Task<SqliteConnection> OpenBankWithKeyAsync(string? key, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("boom");

        public Task<SqliteConnection> OpenBankSkippingEnsureAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("boom");
    }
}
