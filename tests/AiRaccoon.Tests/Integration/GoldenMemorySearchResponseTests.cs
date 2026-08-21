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
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _dataRoot = TestData.CreateTempRoot("airaccoon-golden-search-tests");
    private FakeEmbeddingEndpoint _openAi = null!;
    private SqliteSettingsStore _settings = null!;
    private SqliteMemoryStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User };
        var factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _settings = new SqliteSettingsStore(factory);
        _store = TestData.CreateMemoryStore(factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(factory), new StubChunker(), new FakeTimeProvider(FixedNow),
            TestData.CreateEmbeddingService(), settings: _settings);
        _openAi = await FakeEmbeddingEndpoint.StartAsync(TestContext.Current.CancellationToken);
        await _store.SetSettingAsync(EmbeddingSettingsKeys.ApiKey, "test-key-123", TestContext.Current.CancellationToken);
        await _store.ConfigureEmbeddingAsync("openai", "nomic-embed-text", _openAi.BaseUrl, TestContext.Current.CancellationToken);
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
        var gate = new ToolGate(access, new FakePromotionQueue());
        var tools = new MemoryTools(_store, gate,
            new SearchDispatcher(_store, new NoOpCodeSearchService(), new NoOpSearchQualityService()),
            new QueryGuardService(_settings), new MemoryWriteService(_store, new FakePromotionQueue()),
            new NoOpMeasurementRecorder(), NullLogger<MemoryTools>.Instance);

        var response = await tools.Search("acme", "quick fox", cancellationToken: TestContext.Current.CancellationToken);

        var captured = NormalizeCorrelationId(JsonSerializer.SerializeToNode(response, JsonOptions)!.AsObject());
        var golden = JsonNode.Parse(await File.ReadAllTextAsync(TestData.RepoFile(GoldenRelativePath),
            TestContext.Current.CancellationToken))!.AsObject();

        captured.ToJsonString(JsonOptions).ShouldBe(golden.ToJsonString(JsonOptions),
            "the pre-kind memory_search envelope must still match the committed golden file (modulo Meta.CorrelationId) — " +
            "WP6-T01 compares its post-envelope-change capture against this same file");
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
