using AiRaccoon.Access;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.EventPump;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.QueryGuard;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Embedding.Manifest;
using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Code;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tools;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
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
        var job = new CodeReindexJob(new CodeEmbedder(new FakeCodeEmbeddingService(), NullLogger<CodeEmbedder>.Instance),
            TestData.NewEmbedDrainPump());
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await SeedPendingCodeRowAsync(connection, id: 1);

        (await job.HasWorkAsync(connection, TestContext.Current.CancellationToken)).ShouldBeFalse(
            "a pending code row with no engine is legitimately unembeddable — no error spam, ever due");
    }

    [Fact]
    public async Task HasWorkAsync_Configured_TrueOnlyWithPendingRows()
    {
        var job = new CodeReindexJob(new CodeEmbedder(new FakeCodeEmbeddingService(), NullLogger<CodeEmbedder>.Instance),
            TestData.NewEmbedDrainPump());
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await ActivateCodeEngineAsync(connection, "/models/code-daemon-embed-v1");

        (await job.HasWorkAsync(connection, TestContext.Current.CancellationToken)).ShouldBeFalse("no pending rows yet");

        await SeedPendingCodeRowAsync(connection, id: 1);
        (await job.HasWorkAsync(connection, TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    /// <summary>E9: RunAsync signals the embed topic's code item instead of embedding — the actual
    /// pending-&gt;embedded transition is EmbedDrainService's own (EmbedDrainServiceTests).</summary>
    [Fact]
    public async Task RunAsync_EnqueuesTheCodeItem()
    {
        var pump = TestData.NewEmbedDrainPump();
        var job = new CodeReindexJob(new CodeEmbedder(new FakeCodeEmbeddingService(), NullLogger<CodeEmbedder>.Instance), pump);
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await ActivateCodeEngineAsync(connection, "/models/code-daemon-embed-v1");
        await SeedPendingCodeRowAsync(connection, id: 1);
        await SeedPendingCodeRowAsync(connection, id: 2);

        var createdWork = await job.RunAsync(connection, TestContext.Current.CancellationToken);

        createdWork.ShouldBeFalse("a code drain never leaves anything ELSE pending for another job to pick up");
        var states = await connection.QueryAsync<string>("SELECT embed_state FROM code_entries ORDER BY id");
        states.ShouldAllBe(s => s == "pending", "WP11-B2: RunAsync only signals — it never embeds itself");
        pump.EnqueuedCount.ShouldBe(1);
        var queued = pump.DrainUpTo(1).ShouldHaveSingleItem();
        queued.Corpus.ShouldBe(EmbedCorpus.Code);
    }

    /// <summary>
    ///     WP11-B2: RunAsync itself no longer drains — this pins the ordering end to end with the
    ///     real <see cref="EmbedDrainService" /> consumer standing in for "the next poll".
    /// </summary>
    [Fact]
    public async Task ActivateThenDrain_OrderingIsPinned_RowsStayPendingUntilTheNextPollRunsTheJob()
    {
        var embedder = new CodeEmbedder(new FakeCodeEmbeddingService(), NullLogger<CodeEmbedder>.Instance);
        var pump = TestData.NewEmbedDrainPump();
        var store = new SqliteCodeEngineStore(_factory, new FakeCodeEmbeddingService(), TestData.CreateManifestLoader(), TestData.CreateManifestPoolingRepair());
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await SeedEmbeddedCodeRowAsync(connection, id: 1);

        var modelDir = Path.Combine(_dataRoot, "models", "code-daemon-embed-v1");
        TestData.SeedCodeManifestDirectory(modelDir);
        await store.ActivateCodeEngineAsync(modelDir, TestContext.Current.CancellationToken);

        // Right after activation (its own transaction, no drain inside it) the row is pending,
        // not yet re-embedded.
        var stateAfterActivation = await connection.ExecuteScalarAsync<string>("SELECT embed_state FROM code_entries WHERE id = 1");
        stateAfterActivation.ShouldBe("pending");

        var job = new CodeReindexJob(embedder, pump);
        await job.RunAsync(connection, TestContext.Current.CancellationToken);

        var stateAfterSignal = await connection.ExecuteScalarAsync<string>("SELECT embed_state FROM code_entries WHERE id = 1");
        stateAfterSignal.ShouldBe("pending", "RunAsync only signals — it must not drain itself");

        var drainService = new EmbedDrainService(pump, _factory, new EntryEmbedder(new CountingEmbeddingService(),
            Substitute.For<IModelMigrationLease>(), TimeProvider.System), embedder,
            new SqliteSettingsStore(_factory), TestTelemetry.None, NullLogger<EmbedDrainService>.Instance);
        var request = pump.DrainUpTo(1).ShouldHaveSingleItem();
        await drainService.DrainOnceAsync(request, TestContext.Current.CancellationToken);

        var stateAfterDrain = await connection.ExecuteScalarAsync<string>("SELECT embed_state FROM code_entries WHERE id = 1");
        stateAfterDrain.ShouldBe("embedded", "only the NEXT signal's drain pass drains what activation invalidated");
    }

    [Fact]
    public async Task FingerprintChangeViaReactivation_ReEmbedsCodeOnly_MemoryRowsUntouched()
    {
        var store = new SqliteCodeEngineStore(_factory, new FakeCodeEmbeddingService(), TestData.CreateManifestLoader(), TestData.CreateManifestPoolingRepair());
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await SeedEmbeddedCodeRowAsync(connection, id: 1);
        await SeedEmbeddedMemoryRowAsync(connection, id: 1);

        var modelDir = Path.Combine(_dataRoot, "models", "code-daemon-embed-v1-v2");
        TestData.SeedCodeManifestDirectory(modelDir);
        await store.ActivateCodeEngineAsync(modelDir, TestContext.Current.CancellationToken);

        var codeState = await connection.ExecuteScalarAsync<string>("SELECT embed_state FROM code_entries WHERE id = 1");
        var memoryState = await connection.ExecuteScalarAsync<string>("SELECT embed_state FROM entries WHERE id = 1");
        codeState.ShouldBe("pending");
        memoryState.ShouldBe("embedded", "a code-engine fingerprint change must never touch the memory corpus");
    }

    /// <summary>
    ///     S6: `embedding.codeEngine` was write-only — activation writes it, but nothing ever
    ///     compared it against a freshly-computed fingerprint, so a manifest changed IN PLACE on
    ///     disk (a re-download, a changed pin) after activation never re-triggered a re-embed. The
    ///     drain's own `HasWorkAsync` is where this must be caught: it runs on every 15s on-demand
    ///     poll regardless of whether anything is already pending.
    /// </summary>
    [Fact]
    public async Task HasWorkAsync_ManifestChangedInPlaceSinceActivation_InvalidatesAndUpdatesTheStoredFingerprint()
    {
        var embeddingService = TestData.CreateEmbeddingService();
        var store = new SqliteCodeEngineStore(_factory, embeddingService, TestData.CreateManifestLoader(), TestData.CreateManifestPoolingRepair());
        var job = new CodeReindexJob(new CodeEmbedder(embeddingService, NullLogger<CodeEmbedder>.Instance), TestData.NewEmbedDrainPump());
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);

        var modelDir = Path.Combine(_dataRoot, "models", "code-daemon-embed-v1-drift");
        TestData.SeedCodeManifestDirectory(modelDir);
        await store.ActivateCodeEngineAsync(modelDir, TestContext.Current.CancellationToken);
        await SeedEmbeddedCodeRowAsync(connection, id: 1);
        var storedEngineBefore = await ReadCodeEngineSettingAsync(connection);

        // Simulate a re-download that changed the pinned model without going through
        // `model set code local` again — the manifest's own bytes (and so its sha256) change.
        var manifestPath = Path.Combine(modelDir, EmbeddingManifest.FileName);
        File.WriteAllText(manifestPath, File.ReadAllText(manifestPath).Replace("faxenoff", "faxenoff-v2"));

        (await job.HasWorkAsync(connection, TestContext.Current.CancellationToken)).ShouldBeTrue(
            "the manifest drifted -- HasWorkAsync must invalidate the row to pending, not just report FALSE work");

        var stateAfter = await connection.ExecuteScalarAsync<string>("SELECT embed_state FROM code_entries WHERE id = 1");
        stateAfter.ShouldBe("pending", "a drifted manifest must invalidate previously-embedded rows exactly like reactivation");
        var storedEngineAfter = await ReadCodeEngineSettingAsync(connection);
        storedEngineAfter.ShouldNotBe(storedEngineBefore, "the stored fingerprint must be updated to the freshly-computed one");
    }

    /// <summary>S6: an UNCHANGED manifest must never invalidate — a poll must be a no-op when nothing drifted.</summary>
    [Fact]
    public async Task HasWorkAsync_ManifestUnchanged_NeverInvalidatesAlreadyEmbeddedRows()
    {
        var embeddingService = TestData.CreateEmbeddingService();
        var store = new SqliteCodeEngineStore(_factory, embeddingService, TestData.CreateManifestLoader(), TestData.CreateManifestPoolingRepair());
        var job = new CodeReindexJob(new CodeEmbedder(embeddingService, NullLogger<CodeEmbedder>.Instance), TestData.NewEmbedDrainPump());
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);

        var modelDir = Path.Combine(_dataRoot, "models", "code-daemon-embed-v1-stable");
        TestData.SeedCodeManifestDirectory(modelDir);
        await store.ActivateCodeEngineAsync(modelDir, TestContext.Current.CancellationToken);
        await SeedEmbeddedCodeRowAsync(connection, id: 1);

        (await job.HasWorkAsync(connection, TestContext.Current.CancellationToken)).ShouldBeFalse(
            "nothing drifted and nothing is pending -- a poll must be a true no-op");

        var state = await connection.ExecuteScalarAsync<string>("SELECT embed_state FROM code_entries WHERE id = 1");
        state.ShouldBe("embedded", "an unchanged manifest must never invalidate an already-embedded row");
    }

    private static async Task<string?> ReadCodeEngineSettingAsync(SqliteConnection connection) =>
        await connection.ExecuteScalarAsync<string?>("SELECT value FROM settings WHERE key = @key",
            new { key = EmbeddingSettingsKeys.CodeEngine });

    /// <summary>
    ///     N1: renamed from "...WhileCodeRowsSitPendingMidDrainWindow" — this test never holds a
    ///     real drain in flight (it deliberately never runs CodeReindexJob), so it does not prove
    ///     anything about a mid-drain window specifically. What it actually proves: activation opens
    ///     no model_migration row and interacts with no ToolGate, so a code row sitting pending for
    ///     ANY reason (activation just ran, or a drain never got scheduled) never blocks a memory
    ///     tool call — there is no cross-corpus coupling to accidentally introduce.
    /// </summary>
    [Fact]
    public async Task MemoryToolsStayCallable_RegardlessOfPendingCodeRows_NoToolGateOrModelMigrationCoupling()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var store = new SqliteCodeEngineStore(_factory, new FakeCodeEmbeddingService(), TestData.CreateManifestLoader(), TestData.CreateManifestPoolingRepair());
        await SeedPendingCodeRowAsync(connection, id: 1);
        var modelDir = Path.Combine(_dataRoot, "models", "code-daemon-embed-v1");
        TestData.SeedCodeManifestDirectory(modelDir);
        await store.ActivateCodeEngineAsync(modelDir, TestContext.Current.CancellationToken);
        // Deliberately never runs CodeReindexJob -- the row stays pending; see the summary above
        // for what that does and does not prove.

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

    /// <summary>Mirrors real activation: codeModel and codeEngine always land together, so
    /// MarkCodeEmbedded's S1 engine guard has a real value to compare against, not a NULL that
    /// could never match any captured engine.</summary>
    private static async Task ActivateCodeEngineAsync(SqliteConnection connection, string directory)
    {
        await UpsertSettingAsync(connection, EmbeddingSettingsKeys.CodeModel, directory);
        await UpsertSettingAsync(connection, EmbeddingSettingsKeys.CodeEngine, $"test:local:{directory}");
    }

    private static async Task UpsertSettingAsync(SqliteConnection connection, string key, string value) =>
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO settings (key, value) VALUES (@key, @value) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            new { key, value }, cancellationToken: TestContext.Current.CancellationToken));

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
