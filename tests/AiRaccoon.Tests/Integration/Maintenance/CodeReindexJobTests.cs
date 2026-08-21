using AiRaccoon.Access;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.QueryGuard;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Code;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tools;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Maintenance;

/// <summary>
///     WP7-remainder — CodeReindexJob (§3.3 D-E9/§3.8): the code corpus's own on-demand drain, no
///     outbox, no ToolGate interaction. Proves the pending -> embedded transition end-to-end with a
///     fake engine (WP3-T06), the invalidate-then-drain ordering (activation never drains inside its
///     own transaction), that a fingerprint change re-embeds code only (memory untouched), and that
///     memory tools stay callable while code rows sit pending.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class CodeReindexJobTests : IAsyncLifetime
{
    private readonly string _dataRoot = TestData.CreateTempRoot("airaccoon-code-reindex-tests");
    private SqliteConnectionFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User };
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        await using var warm = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        TestData.DeleteTempRoot(_dataRoot);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task HasWorkAsync_NoCodeEngineConfigured_False_EvenWithPendingRows()
    {
        var job = new CodeReindexJob(new CodeEmbedder(new FakeCodeEmbeddingService()));
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await SeedPendingCodeRowAsync(connection, id: 1);

        (await job.HasWorkAsync(connection, TestContext.Current.CancellationToken)).ShouldBeFalse(
            "a pending code row with no engine is legitimately unembeddable — no error spam, ever due");
    }

    [Fact]
    public async Task HasWorkAsync_Configured_TrueOnlyWithPendingRows()
    {
        var job = new CodeReindexJob(new CodeEmbedder(new FakeCodeEmbeddingService()));
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await ActivateCodeEngineAsync(connection, "/models/code-daemon-embed-v1");

        (await job.HasWorkAsync(connection, TestContext.Current.CancellationToken)).ShouldBeFalse("no pending rows yet");

        await SeedPendingCodeRowAsync(connection, id: 1);
        (await job.HasWorkAsync(connection, TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    [Fact]
    public async Task RunAsync_EmbedsPendingRows_EndToEnd_PendingToEmbedded()
    {
        var job = new CodeReindexJob(new CodeEmbedder(new FakeCodeEmbeddingService()));
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await ActivateCodeEngineAsync(connection, "/models/code-daemon-embed-v1");
        await SeedPendingCodeRowAsync(connection, id: 1);
        await SeedPendingCodeRowAsync(connection, id: 2);

        var createdWork = await job.RunAsync(connection, TestContext.Current.CancellationToken);

        createdWork.ShouldBeFalse("a code drain never leaves anything ELSE pending for another job to pick up");
        var states = await connection.QueryAsync<string>("SELECT embed_state FROM code_entries ORDER BY id");
        states.ShouldAllBe(s => s == "embedded");
        var vecCount = await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM vec_code");
        vecCount.ShouldBe(2L);
    }

    [Fact]
    public async Task ActivateThenDrain_OrderingIsPinned_RowsStayPendingUntilTheNextPollRunsTheJob()
    {
        var embedder = new CodeEmbedder(new FakeCodeEmbeddingService());
        var store = new SqliteCodeEngineStore(_factory, new FakeCodeEmbeddingService());
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await SeedEmbeddedCodeRowAsync(connection, id: 1);

        await store.ActivateCodeEngineAsync("/models/code-daemon-embed-v1", TestContext.Current.CancellationToken);

        // Right after activation (its own transaction, no drain inside it) the row is pending,
        // not yet re-embedded.
        var stateAfterActivation = await connection.ExecuteScalarAsync<string>("SELECT embed_state FROM code_entries WHERE id = 1");
        stateAfterActivation.ShouldBe("pending");

        var job = new CodeReindexJob(embedder);
        await job.RunAsync(connection, TestContext.Current.CancellationToken);

        var stateAfterDrain = await connection.ExecuteScalarAsync<string>("SELECT embed_state FROM code_entries WHERE id = 1");
        stateAfterDrain.ShouldBe("embedded", "only the NEXT poll's RunAsync drains what activation invalidated");
    }

    [Fact]
    public async Task FingerprintChangeViaReactivation_ReEmbedsCodeOnly_MemoryRowsUntouched()
    {
        var store = new SqliteCodeEngineStore(_factory, new FakeCodeEmbeddingService());
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await SeedEmbeddedCodeRowAsync(connection, id: 1);
        await SeedEmbeddedMemoryRowAsync(connection, id: 1);

        await store.ActivateCodeEngineAsync("/models/code-daemon-embed-v1-v2", TestContext.Current.CancellationToken);

        var codeState = await connection.ExecuteScalarAsync<string>("SELECT embed_state FROM code_entries WHERE id = 1");
        var memoryState = await connection.ExecuteScalarAsync<string>("SELECT embed_state FROM entries WHERE id = 1");
        codeState.ShouldBe("pending");
        memoryState.ShouldBe("embedded", "a code-engine fingerprint change must never touch the memory corpus");
    }

    [Fact]
    public async Task MemoryToolsStayCallable_WhileCodeRowsSitPendingMidDrainWindow()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var store = new SqliteCodeEngineStore(_factory, new FakeCodeEmbeddingService());
        await SeedPendingCodeRowAsync(connection, id: 1);
        await store.ActivateCodeEngineAsync("/models/code-daemon-embed-v1", TestContext.Current.CancellationToken);
        // Deliberately never runs CodeReindexJob -- the row stays pending, simulating the drain window.

        var settings = new SqliteSettingsStore(_factory);
        var memoryStore = new SettingsOnlyStore(settings);
        var gate = new ToolGate(new MemoryAccessGuard(memoryStore), new FakePromotionQueue());
        var tools = new MemoryTools(memoryStore, gate,
            new SearchDispatcher(memoryStore, new SqliteCodeSearchService(_factory, new FakeCodeEmbedder()), new NoOpSearchQualityService()),
            new QueryGuardService(settings), new MemoryWriteService(memoryStore, new FakePromotionQueue()),
            new NoOpMeasurementRecorder(), NullLogger<MemoryTools>.Instance);

        var envelope = await tools.Search("acme", "anything", cancellationToken: TestContext.Current.CancellationToken);

        envelope.Data.ShouldNotBeNull("memory tools must never be blocked by a pending code drain -- no ToolGate interaction at all");
    }

    private static async Task ActivateCodeEngineAsync(SqliteConnection connection, string directory) =>
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO settings (key, value) VALUES (@key, @value) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            new { key = EmbeddingSettingsKeys.CodeModel, value = directory }, cancellationToken: TestContext.Current.CancellationToken));

    private static async Task SeedPendingCodeRowAsync(SqliteConnection connection, long id) =>
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO code_entries (id, hash, path, value, source_file, line_start, line_end, project_id, created_at, updated_at)
            VALUES (@id, @hash, @path, @value, @path, 1, 1, 'acme', 1, 1)
            """,
            new { id, hash = $"hash-{id}", path = $"src/File{id}.cs", value = $"class Sample{id} {{ }}" },
            cancellationToken: TestContext.Current.CancellationToken));

    /// <summary>Inserts pending, then flips to embedded via UPDATE -- vec_code_au only fires on UPDATE OF embed_state, never on INSERT.</summary>
    private static async Task SeedEmbeddedCodeRowAsync(SqliteConnection connection, long id)
    {
        await SeedPendingCodeRowAsync(connection, id);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE code_entries SET embed_state = 'embedded', embedding = @embedding WHERE id = @id",
            new { id, embedding = EmbeddingBlob.ToBytes(new float[768]) }, cancellationToken: TestContext.Current.CancellationToken));
    }

    private static async Task SeedEmbeddedMemoryRowAsync(SqliteConnection connection, long id) =>
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO entries (id, hash, value, scope, project_id, created_at, updated_at, embed_state, embedding)
            VALUES (@id, @hash, @value, 'project', 'acme', 1, 1, 'embedded', @embedding)
            """,
            new { id, hash = $"mem-hash-{id}", value = "some memory content", embedding = EmbeddingBlob.ToBytes(new float[384]) },
            cancellationToken: TestContext.Current.CancellationToken));

    /// <summary>MemoryAccessGuard only needs settings reads; the search itself never touches the memory leg's real content.</summary>
    private sealed class SettingsOnlyStore(SqliteSettingsStore settings) : FakeMemoryStore
    {
        public override Task<SearchResults> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SearchResults([], SearchTimings.Empty));

        public override Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default) =>
            settings.GetSettingAsync(key, cancellationToken);

        public override Task<IReadOnlyDictionary<string, string>> GetSettingsByPrefixAsync(string prefix,
            CancellationToken cancellationToken = default) =>
            settings.GetSettingsByPrefixAsync(prefix, cancellationToken);
    }
}
