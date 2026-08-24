using System.Text.Json;
using System.Text.Json.Nodes;
using AiRaccoon.Access;
using AiRaccoon.Core.Isolation;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.QueryGuard;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol;
using Shouldly;
using Xunit;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     WP1 golden capture (docs/work/2026-08-21-code-search-implementation-plan.md §3.6): the
///     exact <c>memory_search</c> response BEFORE the `kind` parameter exists, committed as
///     <c>Resources/golden-memory-search-response.json</c> — see the sibling README. WP6-T01
///     regenerates this same response after the envelope change and diffs it against the
///     committed file modulo <c>Meta.CorrelationId</c>; this test is the regression guard that
///     the committed file still matches a fresh capture (correlation id excepted) as long as the
///     pre-<c>kind</c> code path exists.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class GoldenMemorySearchResponseTests : IAsyncLifetime
{
    private const string GoldenRelativePath = "tests/AiRaccoon.Tests/Resources/golden-memory-search-response.json";

    /// <summary>Placeholder swapped in for the real (random, per-call) correlation id before comparing/writing.</summary>
    private const string CorrelationIdPlaceholder = "<CORRELATION_ID>";

    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    ///     The exact serializer the MCP wire uses (McpJsonUtilities.DefaultOptions), not
    ///     JsonSerializerDefaults.Web — DefaultOptions sets DefaultIgnoreCondition=WhenWritingNull,
    ///     so a null property is genuinely absent from what the server emits, not present with a
    ///     null value (integration review S1). WriteIndented is layered on the copy for readable
    ///     diffs; McpJsonUtilities.DefaultOptions is shared/frozen, so it cannot be mutated in place.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new(McpJsonUtilities.DefaultOptions) { WriteIndented = true };

    private readonly string _dataRoot = TestData.CreateTempRoot("airaccoon-golden-search-tests");
    private FakeEmbeddingEndpoint _openAi = null!;
    private SqliteSettingsStore _settings = null!;
    private SqliteMemoryStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User };
        var factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        var clock = new FakeTimeProvider(FixedNow);
        _settings = new SqliteSettingsStore(factory);
        _store = TestData.CreateMemoryStore(factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(factory), new StubChunker(), clock,
            TestData.CreateEmbeddingService(), modelMigrationLease: null, jsonChunker: null, noisePolicies: null, settings: _settings, codeChunker: null, ignoreRulesProvider: null, measurements: null);
        _openAi = await FakeEmbeddingEndpoint.StartAsync(TestContext.Current.CancellationToken);
        await _store.SetSettingAsync(EmbeddingSettingsKeys.ApiKey, "test-key-123", TestContext.Current.CancellationToken);
        await TestData.ConfigureAndDrainEmbeddingAsync(_store, factory, TestData.CreateEmbeddingService(),
            "openai", "nomic-embed-text", _openAi.BaseUrl, TestContext.Current.CancellationToken, clock);
    }

    public async ValueTask DisposeAsync()
    {
        await _openAi.DisposeAsync();
        TestData.DeleteTempRoot(_dataRoot);
    }

    [Fact]
    public async Task Search_TodaysPreKindEnvelope_MatchesTheCommittedGoldenFile()
    {
        await _store.WriteAsync(new MemoryWriteRequest("acme", "the quick brown fox leaps over the lazy dog"),
            TestContext.Current.CancellationToken);
        await _store.WriteAsync(new MemoryWriteRequest("acme", "a second note about narwhals and tusks"),
            TestContext.Current.CancellationToken);

        var access = new MemoryAccessGuard(_store);
        var gate = new ToolGate(access, new FakePromotionQueue(), new NeverMigratingStore(), new AllowingRegistrationGuard());
        var tools = new MemoryTools(_store, gate,
            new SearchDispatcher(_store, new NoOpCodeSearchService(), new NoOpSearchQualityService()),
            new QueryGuardService(_settings), new MemoryWriteService(_store, new FakePromotionQueue()),
            new NoOpMeasurementRecorder(), NullLogger<MemoryTools>.Instance);

        var response = await tools.Search("acme", "quick fox", kind: "memory",
            cancellationToken: TestContext.Current.CancellationToken);

        var captured = NormalizeCorrelationId(JsonSerializer.SerializeToNode(response, JsonOptions)!.AsObject());
        var golden = JsonNode.Parse(await File.ReadAllTextAsync(TestData.RepoFile(GoldenRelativePath),
            TestContext.Current.CancellationToken))!.AsObject();

        captured.ToJsonString(JsonOptions).ShouldBe(golden.ToJsonString(JsonOptions),
            "the pre-kind memory_search envelope must still match the committed golden file (modulo Meta.CorrelationId) — " +
            "WP6-T01 compares its post-envelope-change capture against this same file");
    }

    /// <summary>
    ///     Integration review S1: SearchResultList.Code carries an explicit
    ///     <c>[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]</c> documenting that a
    ///     null Code is omitted, not just null-valued — but McpJsonUtilities.DefaultOptions already
    ///     sets DefaultIgnoreCondition=WhenWritingNull globally, so that per-property attribute does
    ///     no independent work on the actual wire. Proven here on Warning, a property with no
    ///     JsonIgnore attribute at all: the wire options omit it too, on the global setting alone.
    /// </summary>
    [Fact]
    public void McpWireOptions_OmitNullProperties_EvenWithoutAJsonIgnoreAttribute()
    {
        var result = new MemoryTools.SearchResultList([], Warning: null, Code: null);

        var json = JsonSerializer.Serialize(result, McpJsonUtilities.DefaultOptions);

        // Warning has no [JsonIgnore] attribute — the wire's global DefaultIgnoreCondition must
        // omit it alone. Code's [JsonIgnore] attribute is redundant with that global option, not
        // the thing doing the work.
        json.ShouldNotContain("\"warning\"");
        json.ShouldNotContain("\"code\"");
    }

    private static JsonObject NormalizeCorrelationId(JsonObject envelope)
    {
        if (envelope["meta"] is JsonObject meta)
        {
            meta["correlationId"] = CorrelationIdPlaceholder;
        }

        return envelope;
    }
}
