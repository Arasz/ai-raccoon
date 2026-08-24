using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     WP5 — <c>CodeEmbedder</c> (docs/work/2026-08-21-code-search-implementation-plan.md §12.2
///     H5): resolves the code engine via the keyed <see cref="IEmbeddingService.CreateGenerator" />
///     seam, degrades to <see cref="QueryVector.Empty" /> when unconfigured, and refuses actionably
///     when configured but unloadable. Uses a fake <see cref="IEmbeddingService" /> throughout — no
///     real 187 MB model download in tests.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class CodeEmbedderTests : IAsyncLifetime
{
    private readonly string _dataRoot = TestData.CreateTempRoot("airaccoon-code-embedder-tests");
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
    public async Task EmbedQueryAsync_NoCodeEngineConfigured_ReturnsEmptyVector()
    {
        var embedder = new CodeEmbedder(new FakeCodeEmbeddingService(), NullLogger<CodeEmbedder>.Instance);
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);

        var vector = await embedder.EmbedQueryAsync(connection, "QuokkaFinder", TestContext.Current.CancellationToken);

        vector.IsEmpty.ShouldBeTrue("no embedding.codeModel row means code search degrades to FTS5-only");
    }

    [Fact]
    public async Task EmbedQueryAsync_Configured_ReturnsA768DimensionVector()
    {
        var fake = new FakeCodeEmbeddingService();
        var embedder = new CodeEmbedder(fake, NullLogger<CodeEmbedder>.Instance);
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await ActivateCodeModelAsync(connection, "/models/code-daemon-embed-v1");

        var vector = await embedder.EmbedQueryAsync(connection, "QuokkaFinder", TestContext.Current.CancellationToken);

        vector.IsEmpty.ShouldBeFalse();
        vector.Data.Length.ShouldBe(768 * sizeof(float));
        fake.Calls.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task EmbedQueryAsync_QueryExceedsWindow_TrimsAndSetsTrimmedFlag()
    {
        var fake = new FakeCodeEmbeddingService
        {
            TrimOverride = query => query.Length > 10 ? query[..10] : query
        };
        var embedder = new CodeEmbedder(fake, NullLogger<CodeEmbedder>.Instance);
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await ActivateCodeModelAsync(connection, "/models/code-daemon-embed-v1");

        var vector = await embedder.EmbedQueryAsync(connection, "a very long query well over the trim window",
            TestContext.Current.CancellationToken);

        vector.Trimmed.ShouldBeTrue();
        fake.Calls.ShouldHaveSingleItem().ShouldHaveSingleItem().ShouldBe("a very lon");
    }

    [Fact]
    public async Task EmbedQueryAsync_QueryWithinWindow_NotTrimmed()
    {
        var fake = new FakeCodeEmbeddingService();
        var embedder = new CodeEmbedder(fake, NullLogger<CodeEmbedder>.Instance);
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await ActivateCodeModelAsync(connection, "/models/code-daemon-embed-v1");

        var vector = await embedder.EmbedQueryAsync(connection, "short query", TestContext.Current.CancellationToken);

        vector.Trimmed.ShouldBeFalse();
    }

    [Fact]
    public async Task EmbedQueryAsync_ConfiguredButUnloadable_ThrowsCodeEngineUnloadableException()
    {
        var fake = new FakeCodeEmbeddingService
        {
            FailOnCreateGenerator = () => new InvalidOperationException("model.onnx is not a valid ONNX file")
        };
        var embedder = new CodeEmbedder(fake, NullLogger<CodeEmbedder>.Instance);
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await ActivateCodeModelAsync(connection, "/models/broken-code-engine");

        var ex = await Should.ThrowAsync<AiRaccoon.Core.Memory.Code.CodeEngineUnloadableException>(
            () => embedder.EmbedQueryAsync(connection, "QuokkaFinder", TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("/models/broken-code-engine");
        ex.Message.ShouldContain("model code set local");
    }

    [Fact]
    public async Task EmbedPendingBatchAsync_NoCodeEngineConfigured_ReturnsZero_LeavesRowsPending()
    {
        var embedder = new CodeEmbedder(new FakeCodeEmbeddingService(), NullLogger<CodeEmbedder>.Instance);
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await SeedPendingCodeRowAsync(connection, id: 1, projectId: "acme");

        var processed = await embedder.EmbedPendingBatchAsync(connection, 32, TestContext.Current.CancellationToken);

        processed.ShouldBe(0);
        var state = await connection.ExecuteScalarAsync<string>(
            "SELECT embed_state FROM code_entries WHERE id = 1");
        state.ShouldBe("pending");
    }

    [Fact]
    public async Task EmbedPendingBatchAsync_Configured_EmbedsRowsAndFlipsEmbedState()
    {
        var fake = new FakeCodeEmbeddingService();
        var embedder = new CodeEmbedder(fake, NullLogger<CodeEmbedder>.Instance);
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await ActivateCodeModelAsync(connection, "/models/code-daemon-embed-v1");
        await SeedPendingCodeRowAsync(connection, id: 1, projectId: "acme");
        await SeedPendingCodeRowAsync(connection, id: 2, projectId: "acme");

        var processed = await embedder.EmbedPendingBatchAsync(connection, 32, TestContext.Current.CancellationToken);

        processed.ShouldBe(2);
        var states = await connection.QueryAsync<string>("SELECT embed_state FROM code_entries ORDER BY id");
        states.ShouldAllBe(s => s == "embedded");
        var vecCount = await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM vec_code");
        vecCount.ShouldBe(2L, "MarkCodeEmbedded fires vec_code_au, which inserts into vec_code");
    }

    /// <summary>
    ///     S1: a batch's SELECT reads the pending rows under one engine, but generation (here, the
    ///     fake's <see cref="FakeCodeEmbeddingService.OnGenerateAsync" /> hook — the same window a
    ///     real engine call occupies) races a real activation to a NEW engine before the per-row
    ///     UPDATE runs. Without the MarkCodeEmbedded engine guard, that UPDATE would silently mark
    ///     the row 'embedded' with a vector generated under the OLD (stale) engine, permanently
    ///     mismatched with the NEW engine's fingerprint the settings row now names.
    /// </summary>
    [Fact]
    public async Task EmbedPendingBatchAsync_ActivationRunsMidBatch_RowStaysPending_NextDrainUsesTheNewEngine()
    {
        var fake = new FakeCodeEmbeddingService();
        var embedder = new CodeEmbedder(fake, NullLogger<CodeEmbedder>.Instance);
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await ActivateCodeModelAsync(connection, "/models/code-daemon-embed-v1");
        await SeedPendingCodeRowAsync(connection, id: 1, projectId: "acme");

        fake.OnGenerateAsync = async () =>
        {
            await using var activationConnection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
            await UpsertSettingAsync(activationConnection, EmbeddingSettingsKeys.CodeEngine, "test:local:/models/code-daemon-embed-v1-v2");
        };

        var processed = await embedder.EmbedPendingBatchAsync(connection, 32, TestContext.Current.CancellationToken);

        processed.ShouldBe(0, "MarkCodeEmbedded's engine guard must block the stale-engine write");
        var state = await connection.ExecuteScalarAsync<string>("SELECT embed_state FROM code_entries WHERE id = 1");
        state.ShouldBe("pending", "the row must stay pending, never embedded under a previous engine's vector");

        fake.OnGenerateAsync = null;
        var reprocessed = await embedder.EmbedPendingBatchAsync(connection, 32, TestContext.Current.CancellationToken);

        reprocessed.ShouldBe(1, "the next drain re-embeds the row under the now-current engine");
        var finalState = await connection.ExecuteScalarAsync<string>("SELECT embed_state FROM code_entries WHERE id = 1");
        finalState.ShouldBe("embedded");
    }

    /// <summary>
    ///     S2: one row's content breaks the WHOLE batch generator call — without a per-row fallback,
    ///     every row in the batch (including the two healthy ones) would stay pending forever, and
    ///     the maintenance loop's 15s on-demand poll would retry the identical failing batch forever
    ///     since the ledger never stamps a thrown job. The fallback must let the healthy rows finish
    ///     AND bump the poison row's own attempt count instead of retrying it unbounded.
    /// </summary>
    [Fact]
    public async Task EmbedPendingBatchAsync_OneRowPoisonsTheBatch_HealthyRowsStillEmbed_PoisonRowStaysPending()
    {
        var fake = new FakeCodeEmbeddingService();
        fake.PoisonValues.Add("class Poison { }");
        var embedder = new CodeEmbedder(fake, NullLogger<CodeEmbedder>.Instance);
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await ActivateCodeModelAsync(connection, "/models/code-daemon-embed-v1");
        await SeedPendingCodeRowAsync(connection, id: 1, projectId: "acme");
        await SeedPoisonCodeRowAsync(connection, id: 2, projectId: "acme");
        await SeedPendingCodeRowAsync(connection, id: 3, projectId: "acme");

        var processed = await embedder.EmbedPendingBatchAsync(connection, 32, TestContext.Current.CancellationToken);

        processed.ShouldBe(2, "the two healthy rows must still embed despite row 2 poisoning the batch call");
        (await ReadEmbedStateAsync(connection, 1)).ShouldBe("embedded");
        (await ReadEmbedStateAsync(connection, 2)).ShouldBe("pending", "the poison row stays pending, not lost");
        (await ReadEmbedStateAsync(connection, 3)).ShouldBe("embedded");
        (await ReadEmbedAttemptsAsync(connection, 2)).ShouldBe(1, "the per-row fallback's own failure must be counted");
    }

    /// <summary>S2: once a poison row crosses CodeCorpusSchema.MaxEmbedAttempts, the drain must stop
    /// selecting it — otherwise it is retried forever and HasPendingWorkAsync never goes quiet.</summary>
    [Fact]
    public async Task EmbedPendingBatchAsync_PoisonRowCrossesMaxAttempts_ExcludedFromFutureSelection()
    {
        var fake = new FakeCodeEmbeddingService();
        fake.PoisonValues.Add("class Poison { }");
        var embedder = new CodeEmbedder(fake, NullLogger<CodeEmbedder>.Instance);
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await ActivateCodeModelAsync(connection, "/models/code-daemon-embed-v1");
        await SeedPoisonCodeRowAsync(connection, id: 1, projectId: "acme");

        for (var i = 0; i < AiRaccoon.Core.Memory.Code.CodeCorpusSchema.MaxEmbedAttempts; i++)
        {
            (await embedder.HasPendingWorkAsync(connection, TestContext.Current.CancellationToken)).ShouldBeTrue(
                $"attempt {i}: the row is still under the attempt ceiling");
            await embedder.EmbedPendingBatchAsync(connection, 32, TestContext.Current.CancellationToken);
        }

        (await embedder.HasPendingWorkAsync(connection, TestContext.Current.CancellationToken)).ShouldBeFalse(
            "the row crossed the attempt ceiling and must no longer be selected — no more 15s retry storm");
        (await ReadEmbedStateAsync(connection, 1)).ShouldBe("pending", "excluded from selection, not silently dropped");
    }

    /// <summary>
    ///     MemorySql's SelectAllPendingCodeForEmbed/HasPendingCodeEmbed embed the attempt ceiling as
    ///     a literal SQL integer (a const string can't interpolate CodeCorpusSchema.MaxEmbedAttempts,
    ///     a const int) — this pins the two together so an edit to one alone fails the build instead
    ///     of silently drifting.
    /// </summary>
    [Fact]
    public void CodeEmbedAttemptCeiling_SqlLiteral_MatchesCodeCorpusSchemaMaxEmbedAttempts()
    {
        var expected = $"embed_attempts < {AiRaccoon.Core.Memory.Code.CodeCorpusSchema.MaxEmbedAttempts} ";

        MemorySql.SelectAllPendingCodeForEmbed.ShouldContain(expected);
        MemorySql.HasPendingCodeEmbed.ShouldContain($"embed_attempts < {AiRaccoon.Core.Memory.Code.CodeCorpusSchema.MaxEmbedAttempts} LIMIT");
    }

    [Fact]
    public async Task HasPendingWorkAsync_NoCodeEngineConfigured_FalseEvenWithPendingRows()
    {
        var embedder = new CodeEmbedder(new FakeCodeEmbeddingService(), NullLogger<CodeEmbedder>.Instance);
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await SeedPendingCodeRowAsync(connection, id: 1, projectId: "acme");

        var hasWork = await embedder.HasPendingWorkAsync(connection, TestContext.Current.CancellationToken);

        hasWork.ShouldBeFalse("a pending code row with no engine is legitimately unembeddable, forever");
    }

    [Fact]
    public async Task HasPendingWorkAsync_ConfiguredWithPendingRows_True()
    {
        var embedder = new CodeEmbedder(new FakeCodeEmbeddingService(), NullLogger<CodeEmbedder>.Instance);
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await ActivateCodeModelAsync(connection, "/models/code-daemon-embed-v1");
        await SeedPendingCodeRowAsync(connection, id: 1, projectId: "acme");

        var hasWork = await embedder.HasPendingWorkAsync(connection, TestContext.Current.CancellationToken);

        hasWork.ShouldBeTrue();
    }

    [Fact]
    public async Task HasPendingWorkAsync_ConfiguredNoPendingRows_False()
    {
        var embedder = new CodeEmbedder(new FakeCodeEmbeddingService(), NullLogger<CodeEmbedder>.Instance);
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await ActivateCodeModelAsync(connection, "/models/code-daemon-embed-v1");

        var hasWork = await embedder.HasPendingWorkAsync(connection, TestContext.Current.CancellationToken);

        hasWork.ShouldBeFalse();
    }

    /// <summary>Mirrors real activation (SqliteCodeEngineStore.ActivateCodeEngineAsync): codeModel
    /// and codeEngine always land together, so MarkCodeEmbedded's S1 engine guard has a real value
    /// to compare against, not a NULL that could never match any captured engine.</summary>
    private static async Task ActivateCodeModelAsync(SqliteConnection connection, string directory)
    {
        await UpsertSettingAsync(connection, EmbeddingSettingsKeys.CodeModel, directory);
        await UpsertSettingAsync(connection, EmbeddingSettingsKeys.CodeEngine, $"test:local:{directory}");
    }

    private static async Task UpsertSettingAsync(SqliteConnection connection, string key, string value) =>
        await connection.ExecuteAsync(
            "INSERT INTO settings (key, value) VALUES (@key, @value) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            new { key, value });

    private static async Task SeedPendingCodeRowAsync(SqliteConnection connection, long id, string projectId) =>
        await connection.ExecuteAsync(
            """
            INSERT INTO code_entries (id, hash, path, value, source_file, line_start, line_end, project_id, created_at, updated_at)
            VALUES (@id, @hash, @path, @value, @path, 1, 1, @projectId, 1, 1)
            """,
            new { id, hash = $"hash-{id}", path = $"src/File{id}.cs", value = $"class Sample{id} {{ }}", projectId });

    /// <summary>A row whose value matches a FakeCodeEmbeddingService.PoisonValues entry.</summary>
    private static async Task SeedPoisonCodeRowAsync(SqliteConnection connection, long id, string projectId) =>
        await connection.ExecuteAsync(
            """
            INSERT INTO code_entries (id, hash, path, value, source_file, line_start, line_end, project_id, created_at, updated_at)
            VALUES (@id, @hash, @path, @value, @path, 1, 1, @projectId, 1, 1)
            """,
            new { id, hash = $"hash-{id}", path = $"src/Poison{id}.cs", value = "class Poison { }", projectId });

    private static async Task<string?> ReadEmbedStateAsync(SqliteConnection connection, long id) =>
        await connection.ExecuteScalarAsync<string?>("SELECT embed_state FROM code_entries WHERE id = @id", new { id });

    private static async Task<int> ReadEmbedAttemptsAsync(SqliteConnection connection, long id) =>
        await connection.ExecuteScalarAsync<int>("SELECT embed_attempts FROM code_entries WHERE id = @id", new { id });
}
