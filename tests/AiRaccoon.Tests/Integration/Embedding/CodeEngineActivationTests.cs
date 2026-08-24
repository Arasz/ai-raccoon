using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Code;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Embedding.Manifest;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Code;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     The code corpus's own activation transaction (§3.3 D-E9): ActivateCodeEngineAsync commits
///     embedding.codeModel/codeEngine and invalidates every embedded code row to 'pending' in ONE
///     transaction — no outbox, no relay. The vec_code_pending trigger (proven in
///     SqliteCodeStoreTests.CodeRow_RoundTrips_ThroughFtsAndVecCodeViaEmbedStateTriggers) is what
///     empties vec_code at commit; this pins that the invalidation actually reaches the trigger
///     through this call, with no drain/reconcile phase in between (unlike the memory bank).
///     <para>
///         B1: the manifest gate lives on the STORE, not just the CLI pre-flight (SettingsCommands.
///         ModelSetCodeLocalAsync) — the HTTP settings endpoint calls this method directly with no
///         CLI in the path, so a refusal test that only drives the CLI never proves the server itself
///         is protected. Every refusal here must leave NOTHING written: no settings rows, no
///         invalidation.
///     </para>
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class CodeEngineActivationTests : IAsyncLifetime
{
    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-code-engine-activation");
    private SqliteConnectionFactory _factory = null!;
    private SqliteCodeEngineStore _store = null!;

    public ValueTask InitializeAsync()
    {
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User };
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _store = new SqliteCodeEngineStore(_factory, TestData.CreateEmbeddingService(), TestData.CreateManifestLoader(),
            TestData.CreateManifestPoolingRepair(), new VecDimensionReconciler());
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        TestData.DeleteTempRoot(_dataRoot);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ActivateCodeEngineAsync_WritesCodeModelAndCodeEngineSettingsRows()
    {
        var dir = Path.Combine(_dataRoot, "code-model");
        TestData.SeedCodeManifestDirectory(dir);

        var config = await _store.ActivateCodeEngineAsync(dir, TestContext.Current.CancellationToken);

        config.Provider.ShouldBe("local");
        config.Model.ShouldBe(Path.GetFullPath(dir));
        (await ReadSettingAsync(EmbeddingSettingsKeys.CodeModel)).ShouldBe(Path.GetFullPath(dir));
        (await ReadSettingAsync(EmbeddingSettingsKeys.CodeEngine)).ShouldBe(config.Engine);
    }

    [Fact]
    public async Task ActivateCodeEngineAsync_MarksEmbeddedCodeRowsPending_AndEmptiesVecCodeInTheSameCommit()
    {
        await SeedAnEmbeddedCodeRowAsync();
        (await CountVecCodeRowsAsync()).ShouldBe(1, "the seed must land an embedded row before activation");

        var dir = Path.Combine(_dataRoot, "code-model");
        TestData.SeedCodeManifestDirectory(dir);
        await _store.ActivateCodeEngineAsync(dir, TestContext.Current.CancellationToken);

        (await ReadCodeEmbedStateAsync(1)).ShouldBe("pending",
            "activation must invalidate every previously-embedded code row");
        (await CountVecCodeRowsAsync()).ShouldBe(0,
            "the vec_code_pending trigger must empty vec_code in the SAME commit as the settings write " +
            "— no stale-vector window, no separate reconcile phase for code");
    }

    /// <summary>
    ///     S7: the test above only checks POST-commit state — three separate autocommit statements
    ///     that all happened to succeed would satisfy it exactly as well as a real transaction, so
    ///     it cannot fail on a broken atomicity guarantee. This one forces the LAST statement
    ///     (MarkAllCodeEmbeddedPending) to fail via a real (non-temporary, so it fires regardless of
    ///     which connection ActivateCodeEngineAsync opens internally) trigger, then asserts the TWO
    ///     EARLIER settings writes were rolled back too — the only outcome that distinguishes one
    ///     real transaction from three independently-committed statements.
    /// </summary>
    [Fact]
    public async Task ActivateCodeEngineAsync_FailureOnTheLastStatement_RollsBackTheEarlierSettingsWritesToo()
    {
        await SeedAnEmbeddedCodeRowAsync();
        await using (var setupConnection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await setupConnection.ExecuteAsync(new CommandDefinition(
                """
                CREATE TRIGGER s7_witness_abort_pending BEFORE UPDATE OF embed_state ON code_entries
                WHEN NEW.embed_state = 'pending'
                BEGIN
                    SELECT RAISE(ABORT, 'S7 witness: forced failure on the invalidate step');
                END
                """, cancellationToken: TestContext.Current.CancellationToken));
        }

        var dir = Path.Combine(_dataRoot, "code-model-s7");
        TestData.SeedCodeManifestDirectory(dir);

        await Should.ThrowAsync<SqliteException>(
            () => _store.ActivateCodeEngineAsync(dir, TestContext.Current.CancellationToken));

        (await ReadSettingAsync(EmbeddingSettingsKeys.CodeModel)).ShouldBeNull(
            "a failed activation must roll back EVERYTHING it wrote, not just skip the statement that threw");
        (await ReadSettingAsync(EmbeddingSettingsKeys.CodeEngine)).ShouldBeNull();
        (await ReadCodeEmbedStateAsync(1)).ShouldBe("embedded",
            "the invalidate step itself must roll back too -- the row must be untouched");
    }

    /// <summary>
    ///     #470: a manifest whose pooling mode its own graph makes unappliable is corrected HERE,
    ///     where invalidation is already unconditional — and before the fingerprint is taken, so the
    ///     rewrite does not read as a second engine change on the code-reindex job's next poll.
    /// </summary>
    [Fact]
    public async Task ActivateCodeEngineAsync_ManifestPoolingTheGraphCannotApply_IsRepairedBeforeTheFingerprintIsTaken()
    {
        var dir = Path.Combine(_dataRoot, "code-model-unappliable-pooling");
        TestData.SeedCodeManifestDirectory(dir);
        TestData.WriteManifestPooling(dir, PoolingMode.Cls, "last_hidden_state");
        var store = new SqliteCodeEngineStore(_factory, TestData.CreateEmbeddingService(),
            TestData.CreateManifestLoader(),
            TestData.CreateManifestPoolingRepair(TestData.GraphWithOutputRanks(("last_hidden_state", 2))),
            new VecDimensionReconciler());

        var config = await store.ActivateCodeEngineAsync(dir, TestContext.Current.CancellationToken);

        var manifest = new EmbeddingManifestSerializer()
            .Deserialize(await File.ReadAllTextAsync(Path.Combine(dir, EmbeddingManifest.FileName),
                TestContext.Current.CancellationToken));
        manifest.Pooling.Mode.ShouldBe(PoolingMode.ModelOutput);
        manifest.Onnx.EmbeddingOutput.ShouldBe("last_hidden_state");
        config.Engine.ShouldBe(
            TestData.CreateEmbeddingService().EngineFingerprint("local", Path.GetFullPath(dir), null),
            "the recorded fingerprint must be the REPAIRED manifest's — otherwise the code-reindex " +
            "job's next poll sees a changed engine and re-embeds the whole corpus a second time");
    }

    /// <summary>
    ///     Gate review of #475: the repair must sit AFTER every refusal leg. A model this corpus
    ///     will never accept — one whose window is NARROWER than the chunker's budget, refused by
    ///     the chunk-budget gate — whose manifest also names a self-pooling output must be
    ///     refused with its file untouched: rewriting a manifest whose pins were never verified,
    ///     for an activation that is about to be refused, is a write nobody asked for. (The old
    ///     refusal leg this test used was the 1024-dimension gate; dimensions are no longer a
    ///     refusal — vec-code-unfix-dim — so the chunk-budget gate takes its place.)
    /// </summary>
    [Fact]
    public async Task ActivateCodeEngineAsync_RefusedManifest_IsNotRepaired()
    {
        var dir = Path.Combine(_dataRoot, "code-model-narrow-selfpooling");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "sentencepiece.bpe.model"), "tokenizer",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(dir, "model.onnx"), "model",
            TestContext.Current.CancellationToken);
        var manifest = File.ReadAllText(
                TestData.RepoFile("tests/AiRaccoon.Tests/Resources/ManifestFixtures/code-daemon-embed-v1.json"))
            .Replace("\"contextWindowTokens\": 512", "\"contextWindowTokens\": 128");
        await File.WriteAllTextAsync(Path.Combine(dir, EmbeddingManifest.FileName), manifest,
            TestContext.Current.CancellationToken);
        TestData.WriteManifestPooling(dir, PoolingMode.Cls, "last_hidden_state");
        var before = await File.ReadAllTextAsync(Path.Combine(dir, EmbeddingManifest.FileName),
            TestContext.Current.CancellationToken);
        var logger = new FakeLogger<ManifestPoolingRepair>();
        var store = new SqliteCodeEngineStore(_factory, TestData.CreateEmbeddingService(),
            TestData.CreateManifestLoader(),
            TestData.CreateManifestPoolingRepair(TestData.GraphWithOutputRanks(("last_hidden_state", 2)), logger),
            new VecDimensionReconciler());

        await Should.ThrowAsync<CodeEngineActivationRefusedException>(
            () => store.ActivateCodeEngineAsync(dir, TestContext.Current.CancellationToken));

        (await File.ReadAllTextAsync(Path.Combine(dir, EmbeddingManifest.FileName),
            TestContext.Current.CancellationToken)).ShouldBe(before,
            "a refused activation must leave the user's manifest exactly as it found it");
        logger.Collector.GetSnapshot().ShouldBeEmpty();
    }

    /// <summary>
    ///     #472: this refusal must carry a type the settings endpoint can catch and map to a 4xx —
    ///     a bare <see cref="InvalidOperationException" /> escapes as an unhandled 500. It stays an
    ///     <see cref="InvalidOperationException" /> (the CLI's own pre-flight catch is unaffected).
    /// </summary>
    [Fact]
    public async Task ActivateCodeEngineAsync_NonexistentDirectory_RefusesAndWritesNothing()
    {
        var dir = Path.Combine(_dataRoot, "does-not-exist");

        await Should.ThrowAsync<CodeEngineActivationRefusedException>(
            () => _store.ActivateCodeEngineAsync(dir, TestContext.Current.CancellationToken));

        (await ReadSettingAsync(EmbeddingSettingsKeys.CodeModel)).ShouldBeNull(
            "a refused activation must never commit the code-model setting row");
        (await ReadSettingAsync(EmbeddingSettingsKeys.CodeEngine)).ShouldBeNull(
            "a refused activation must never commit the code-engine setting row");
    }

    /// <summary>
    ///     vec-code-unfix-dim: dimensions are no longer a refusal. A 1024-dim manifest activates,
    ///     reconciles vec_code to float[1024] and records embedding.codeDimensions — in the SAME
    ///     transaction as the settings write and the invalidation.
    /// </summary>
    [Fact]
    public async Task ActivateCodeEngineAsync_Non768Manifest_ReconcilesVecCodeAndWritesCodeDimensions()
    {
        await SeedAnEmbeddedCodeRowAsync();
        var dir = Path.Combine(_dataRoot, "code-model-1024");
        TestData.SeedCodeManifestDirectory(dir, 1024);

        var config = await _store.ActivateCodeEngineAsync(dir, TestContext.Current.CancellationToken);

        config.Model.ShouldBe(Path.GetFullPath(dir));
        (await ReadSettingAsync(EmbeddingSettingsKeys.CodeModel)).ShouldBe(Path.GetFullPath(dir));
        (await ReadSettingAsync(EmbeddingSettingsKeys.CodeDimensions)).ShouldBe("1024");
        (await TableSqlAsync("vec_code")).ShouldContain("float[1024]",
            customMessage: "activation must reconcile vec_code to the manifest's dimension");
        (await ReadCodeEmbedStateAsync(1)).ShouldBe("pending");
        (await CountVecCodeRowsAsync()).ShouldBe(0);
    }

    /// <summary>A matching-dimension activation must not drop the populated index — reconcile is a
    /// create-if-missing-or-mismatch, and the dimension is still recorded.</summary>
    [Fact]
    public async Task ActivateCodeEngineAsync_768Manifest_NoDdlAndWritesCodeDimensions()
    {
        await SeedAnEmbeddedCodeRowAsync();
        (await CountVecCodeRowsAsync()).ShouldBe(1, "the seed must land an embedded row first");
        var dir = Path.Combine(_dataRoot, "code-model-768");
        TestData.SeedCodeManifestDirectory(dir);

        await _store.ActivateCodeEngineAsync(dir, TestContext.Current.CancellationToken);

        (await ReadSettingAsync(EmbeddingSettingsKeys.CodeDimensions)).ShouldBe("768");
        (await TableSqlAsync("vec_code")).ShouldContain("float[768]",
            customMessage: "a matching dimension must not drop and recreate a populated index");
        (await ReadCodeEmbedStateAsync(1)).ShouldBe("pending");
    }

    /// <summary>
    ///     S7 extension: the reconcile's DDL lives INSIDE the activation transaction. Forcing the
    ///     last statement (MarkAllCodeEmbeddedPending) to fail must roll back the settings rows AND
    ///     the vec_code DDL — a separately-committed reconcile would survive the rollback with the
    ///     table left at float[1024] and no engine configured.
    /// </summary>
    [Fact]
    public async Task ActivateCodeEngineAsync_ReconcileFailure_RollsBackSettingsAndDdl()
    {
        await SeedAnEmbeddedCodeRowAsync();
        await using (var setupConnection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await setupConnection.ExecuteAsync(new CommandDefinition(
                """
                CREATE TRIGGER s7b_witness_abort_pending BEFORE UPDATE OF embed_state ON code_entries
                WHEN NEW.embed_state = 'pending'
                BEGIN
                    SELECT RAISE(ABORT, 'S7b witness: forced failure on the invalidate step');
                END
                """, cancellationToken: TestContext.Current.CancellationToken));
        }

        var dir = Path.Combine(_dataRoot, "code-model-s7b");
        TestData.SeedCodeManifestDirectory(dir, 1024);

        await Should.ThrowAsync<SqliteException>(
            () => _store.ActivateCodeEngineAsync(dir, TestContext.Current.CancellationToken));

        (await ReadSettingAsync(EmbeddingSettingsKeys.CodeModel)).ShouldBeNull();
        (await ReadSettingAsync(EmbeddingSettingsKeys.CodeEngine)).ShouldBeNull();
        (await ReadSettingAsync(EmbeddingSettingsKeys.CodeDimensions)).ShouldBeNull(
            customMessage: "a failed activation must roll back the dimension row too");
        (await TableSqlAsync("vec_code")).ShouldContain("float[768]",
            customMessage: "the reconcile's DDL must roll back with the settings — it is inside the same transaction");
        (await ReadCodeEmbedStateAsync(1)).ShouldBe("embedded");
    }

    /// <summary>
    ///     #422: a manifest NARROWER than the chunker's budget is still refused — the chunker emits
    ///     up to <see cref="CodeChunker.DefaultBudget" /> tokens and that engine would silently
    ///     truncate every one of them at embed time. This is the half of the old fixed-126 gate that
    ///     was protecting something real.
    /// </summary>
    [Fact]
    public async Task ActivateCodeEngineAsync_ManifestWindowNarrowerThanTheChunkerBudget_Refuses_AndWritesNothing()
    {
        var dir = Path.Combine(_dataRoot, "code-model-narrow-ctx");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "sentencepiece.bpe.model"), "tokenizer");
        File.WriteAllText(Path.Combine(dir, "model.onnx"), "model");
        var manifest = File.ReadAllText(
                TestData.RepoFile("tests/AiRaccoon.Tests/Resources/ManifestFixtures/code-daemon-embed-v1.json"))
            .Replace("\"contextWindowTokens\": 512", "\"contextWindowTokens\": 128");
        File.WriteAllText(Path.Combine(dir, EmbeddingManifest.FileName), manifest);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _store.ActivateCodeEngineAsync(dir, TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("126");
        ex.Message.ShouldContain(CodeChunker.DefaultBudget.ToString());
        (await ReadSettingAsync(EmbeddingSettingsKeys.CodeModel)).ShouldBeNull();
    }

    /// <summary>
    ///     #422: a WIDER window is accepted, not refused. Under-filled chunks are a retrieval-quality
    ///     choice, never a correctness fault — the old gate refused this case too, which is what made
    ///     the flagship model unactivatable.
    /// </summary>
    [Fact]
    public async Task ActivateCodeEngineAsync_ManifestWindowWiderThanTheChunkerBudget_Activates()
    {
        var dir = Path.Combine(_dataRoot, "code-model-wide-ctx");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "sentencepiece.bpe.model"), "tokenizer");
        File.WriteAllText(Path.Combine(dir, "model.onnx"), "model");
        var manifest = File.ReadAllText(
                TestData.RepoFile("tests/AiRaccoon.Tests/Resources/ManifestFixtures/code-daemon-embed-v1.json"))
            .Replace("\"contextWindowTokens\": 512", "\"contextWindowTokens\": 8192");
        File.WriteAllText(Path.Combine(dir, EmbeddingManifest.FileName), manifest);

        var config = await _store.ActivateCodeEngineAsync(dir, TestContext.Current.CancellationToken);

        config.Model.ShouldBe(Path.GetFullPath(dir));
        (await ReadSettingAsync(EmbeddingSettingsKeys.CodeModel)).ShouldBe(Path.GetFullPath(dir));
    }

    /// <summary>
    ///     The #422 acceptance in one test: the manifest `model download faxenoff/code-daemon-embed-v1`
    ///     actually writes (512-token window, 768 dims) activates with no hand-edit in between.
    /// </summary>
    [Fact]
    public async Task ActivateCodeEngineAsync_TheManifestTheDownloaderWritesForTheDefaultModel_Activates()
    {
        var dir = Path.Combine(_dataRoot, "code-model-as-downloaded");
        TestData.SeedCodeManifestDirectory(dir);

        var config = await _store.ActivateCodeEngineAsync(dir, TestContext.Current.CancellationToken);

        config.Provider.ShouldBe("local");
        (await ReadSettingAsync(EmbeddingSettingsKeys.CodeEngine)).ShouldBe(config.Engine);
    }

    private async Task SeedAnEmbeddedCodeRowAsync()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO code_entries (id, hash, path, value, source_file, line_start, line_end, project_id, created_at, updated_at)
            VALUES (1, 'hash-1', 'src/foo.cs', 'seed row', 'src/foo.cs', 1, 1, 'acme', 1, 1)
            """, cancellationToken: TestContext.Current.CancellationToken));
        var vector = EmbeddingBlob.ToBytes(Enumerable.Repeat(0.5f, 768).ToArray());
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE code_entries SET embed_state = 'embedded', embedding = @embedding WHERE id = 1",
            new { embedding = vector }, cancellationToken: TestContext.Current.CancellationToken));
    }

    private async Task<string?> ReadSettingAsync(string key)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT value FROM settings WHERE key = @key", new { key },
            cancellationToken: TestContext.Current.CancellationToken));
    }

    private async Task<string> ReadCodeEmbedStateAsync(long id)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.QuerySingleAsync<string>(new CommandDefinition(
            "SELECT embed_state FROM code_entries WHERE id = @id", new { id },
            cancellationToken: TestContext.Current.CancellationToken));
    }

    private async Task<int> CountVecCodeRowsAsync()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.QuerySingleAsync<int>(new CommandDefinition(
            "SELECT count(*) FROM vec_code", cancellationToken: TestContext.Current.CancellationToken));
    }

    private async Task<string> TableSqlAsync(string table)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.QuerySingleAsync<string>(new CommandDefinition(
            "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = @table", new { table },
            cancellationToken: TestContext.Current.CancellationToken));
    }
}
