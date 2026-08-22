using System.Text.Json.Nodes;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Embedding.Manifest;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using SQLitePCL;
using Xunit;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     Plan D3's gate on the NO-CHANGE reconcile path (dimensions already match), stated as work
///     done rather than time taken: <see cref="VecDimensionReconciler" /> must run one
///     <c>sqlite_master</c> probe per vec table and write nothing at all — never touching rows,
///     never recreating a vec table, never re-embedding.
/// </summary>
/// <remarks>
///     <para>
///     <b>Was a wall-clock gate; no millisecond value is asserted here any more</b> (owner ruling,
///     2026-08-22: "they should not fail on any env"; a latency budget belongs in a benchmark or a
///     dedicated performance suite, not in the test suite that gates a PR). The previous form
///     asserted <c>p95 ≤ 25 ms</c> over 20 iterations AND <c>fastest of 3 server boots ≥ 10× p95</c>.
///     Both legs are environment-dependent by construction — the second one doubly so, since its
///     denominator is a whole process launch, so it got stricter the faster the machine booted —
///     and they went red on a loaded developer machine while CI stayed green.
///     </para>
///     <para>
///     What replaces them measures the defect directly. The reconcile is traced with
///     <c>sqlite3_trace</c> on the real connection handle — the same technique
///     <c>MemorySchemaDdlStatementCountTests</c> and <c>ModelMigrationCheckStatementCountTests</c>
///     already use — and the exact statement sequence is pinned. A reconciler that starts doing
///     real work on the no-change path cannot produce that sequence, whatever the host is doing.
///     <see cref="NoChangePath_CostsTheSameOnARealBankAsOnAnEmptyOne" /> proves the O(1)-in-bank-size
///     claim the old doc comment argued for in prose, by comparing two banks instead of timing one.
///     <see cref="ChangePath_RecreatesTheVecTables_AndEveryWorkObservationReportsIt" /> is the
///     discrimination proof: the same observations, against the real reconciler on a real change
///     path, must all move.
///     </para>
///     <para>
///     Fixture note: <c>docs-memory.db</c> (16.43 MiB) is the largest bank in test resources. The
///     plan asked for ≥200 MB; that does not exist here, and under a statement-count gate it no
///     longer matters — <see cref="NoChangePath_CostsTheSameOnARealBankAsOnAnEmptyOne" /> shows the
///     cost does not move with bank size at all.
///     </para>
/// </remarks>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class VecDimensionReconcileWorkTests : IDisposable
{
    /// <summary>
    ///     The whole cost of a no-change reconcile: the embedder's five-key settings read, then one
    ///     transaction wrapping one <c>sqlite_master</c> probe per vec table. Nothing writes, and
    ///     nothing here scales with the size of the bank.
    /// </summary>
    private static readonly string[] NoChangeStatements =
    [
        "SELECT value FROM settings WHERE key = 'embedding.provider' LIMIT 1",
        "SELECT value FROM settings WHERE key = 'embedding.model' LIMIT 1",
        "SELECT value FROM settings WHERE key = 'embedding.baseUrl' LIMIT 1",
        "SELECT value FROM settings WHERE key = 'embedding.apiKey' LIMIT 1",
        "SELECT value FROM settings WHERE key = 'embedding.dimensions' LIMIT 1",
        "BEGIN IMMEDIATE;",
        "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'vec_entries'",
        "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'vec_structure'",
        "COMMIT;"
    ];

    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup; a lingering temp dir does not fail the test.
            }
        }
    }

    [Fact]
    public async Task NoChangePath_ProbesEachVecTableOnce_AndWritesNothing()
    {
        var dataRoot = CreateRoot("ai-raccoon-reconcile-work", seeded: true);
        var embedder = await ArrangeAsync(dataRoot, dimensions: 384);
        await using var connection = await OpenAsync(dataRoot);
        var ct = TestContext.Current.CancellationToken;

        (await TableSqlAsync(connection, "vec_entries")).ShouldContain("float[384]",
            customMessage: "arrangement check: this must be the no-change path, not a real reconcile");
        await embedder.ReconcileVecDimensionsAsync(connection, ct);

        var schemaVersionBefore = await ScalarAsync(connection, "PRAGMA schema_version");
        var changesBefore = await ScalarAsync(connection, "SELECT total_changes()");
        var vecEntriesBefore = await TableSqlAsync(connection, "vec_entries");
        var vecStructureBefore = await TableSqlAsync(connection, "vec_structure");

        var statements = await TraceAsync(connection, c => embedder.ReconcileVecDimensionsAsync(c, ct));

        (await ScalarAsync(connection, "SELECT total_changes()")).ShouldBe(changesBefore,
            "a no-change reconcile must not insert, update or delete a single row");
        (await ScalarAsync(connection, "PRAGMA schema_version")).ShouldBe(schemaVersionBefore,
            "a no-change reconcile must not run any DDL — schema_version moves on every schema change");
        (await TableSqlAsync(connection, "vec_entries")).ShouldBe(vecEntriesBefore);
        (await TableSqlAsync(connection, "vec_structure")).ShouldBe(vecStructureBefore);
        statements.ShouldBe(NoChangeStatements,
            "the no-change path must be one sqlite_master probe per vec table and nothing else");
    }

    /// <summary>
    ///     The O(1)-in-bank-size claim, measured instead of argued: the same reconcile against a
    ///     16 MiB restored bank and against an empty one must issue the identical statement
    ///     sequence. A reconciler that ever started reading or rewriting bank contents on this path
    ///     would diverge between the two.
    /// </summary>
    [Fact]
    public async Task NoChangePath_CostsTheSameOnARealBankAsOnAnEmptyOne()
    {
        var seeded = await TraceOneReconcileAsync(CreateRoot("ai-raccoon-reconcile-work-seeded", seeded: true));
        var empty = await TraceOneReconcileAsync(CreateRoot("ai-raccoon-reconcile-work-empty", seeded: false));

        seeded.ShouldBe(empty,
            "the no-change reconcile is one sqlite_master probe per vec table, so 16 MiB of bank " +
            "content must not change what it does");
        seeded.ShouldBe(NoChangeStatements);
    }

    /// <summary>
    ///     The discrimination proof (`prove-the-check-fails`), permanent and in-suite: every
    ///     observation the gate above makes must move when the reconciler really does work. This
    ///     drives the REAL <see cref="VecDimensionReconciler" /> down its change path by declaring
    ///     the engine at 512 dimensions against a bank built at 384 — not a stub that sleeps, so
    ///     what it proves is that the gate discriminates real work rather than elapsed time.
    /// </summary>
    [Fact]
    public async Task ChangePath_RecreatesTheVecTables_AndEveryWorkObservationReportsIt()
    {
        var dataRoot = CreateRoot("ai-raccoon-reconcile-work-change", seeded: true);
        var embedder = await ArrangeAsync(dataRoot, dimensions: 512);
        await using var connection = await OpenAsync(dataRoot);
        var ct = TestContext.Current.CancellationToken;

        (await TableSqlAsync(connection, "vec_entries")).ShouldContain("float[384]",
            customMessage: "arrangement check: the bank must start at a dimension the engine does not want");
        var schemaVersionBefore = await ScalarAsync(connection, "PRAGMA schema_version");

        var statements = await TraceAsync(connection, c => embedder.ReconcileVecDimensionsAsync(c, ct));

        statements.ShouldNotBe(NoChangeStatements,
            "a real reconcile cannot look like the no-change path, or the gate above proves nothing");
        statements.Count(s => s.StartsWith("DROP TABLE", StringComparison.Ordinal)).ShouldBe(2);
        statements.Count(s => s.StartsWith("CREATE VIRTUAL TABLE", StringComparison.Ordinal)).ShouldBe(2);
        (await ScalarAsync(connection, "PRAGMA schema_version")).ShouldBeGreaterThan(schemaVersionBefore,
            "recreating both vec tables is DDL, so schema_version must move");
        (await TableSqlAsync(connection, "vec_entries")).ShouldContain("float[512]");
        (await TableSqlAsync(connection, "vec_structure")).ShouldContain("float[512]");
    }

    private async Task<IReadOnlyList<string>> TraceOneReconcileAsync(string dataRoot)
    {
        var embedder = await ArrangeAsync(dataRoot, dimensions: 384);
        await using var connection = await OpenAsync(dataRoot);
        var ct = TestContext.Current.CancellationToken;
        await embedder.ReconcileVecDimensionsAsync(connection, ct);
        return await TraceAsync(connection, c => embedder.ReconcileVecDimensionsAsync(c, ct));
    }

    /// <summary>Traces every statement <paramref name="action" /> prepares on
    /// <paramref name="connection" />'s real handle — the factory's own pragmas and schema ensure
    /// already ran by the time the connection is handed back, so they are outside this window.</summary>
    private static async Task<IReadOnlyList<string>> TraceAsync(SqliteConnection connection,
        Func<SqliteConnection, Task> action)
    {
        var statements = new List<string>();
        strdelegate_trace tracer = (_, sql) => statements.Add(sql);
        raw.sqlite3_trace(connection.Handle, tracer, null);
        await action(connection);
        raw.sqlite3_trace(connection.Handle, (strdelegate_trace?)null, null);
        return statements;
    }

    private string CreateRoot(string prefix, bool seeded)
    {
        var dataRoot = TestData.CreateTempRoot(prefix);
        _tempDirs.Add(dataRoot);
        if (seeded)
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Resources", "docs-memory.db"),
                Path.Combine(dataRoot, "memory.db"));
        }

        return dataRoot;
    }

    private async Task<EntryEmbedder> ArrangeAsync(string dataRoot, int dimensions)
    {
        await ConfigureManifestEngineAsync(dataRoot, WriteManifestDir(dimensions));
        return new EntryEmbedder(RealEmbeddingService(), new SqliteModelMigrationLease(TimeProvider.System),
            new FakeTimeProvider(), new VecDimensionReconciler());
    }

    private static Task<SqliteConnection> OpenAsync(string dataRoot)
    {
        var options = TestData.CreateInfrastructureOptions(dataRoot);
        return new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options))
            .OpenBankAsync(TestContext.Current.CancellationToken);
    }

    private static EmbeddingService RealEmbeddingService() =>
        new(new FakeLogger<EmbeddingService>(), new LocalTokenizer(), new EmbeddingTokenizerFactory(),
            new EmbeddingManifestLoader(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator()));

    private static async Task ConfigureManifestEngineAsync(string dataRoot, string manifestDir)
    {
        await using var connection = await OpenAsync(dataRoot);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO settings(key, value) VALUES (@key, @value) " +
            "ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            new[]
            {
                new { key = EmbeddingSettingsKeys.Provider, value = "local" },
                new { key = EmbeddingSettingsKeys.Model, value = manifestDir }
            }, cancellationToken: TestContext.Current.CancellationToken));
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql) =>
        await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            sql, cancellationToken: TestContext.Current.CancellationToken));

    private static async Task<string> TableSqlAsync(SqliteConnection connection, string table) =>
        await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = @table",
            new { table }, cancellationToken: TestContext.Current.CancellationToken))
        ?? throw new InvalidOperationException($"{table} does not exist");

    /// <summary>Same recipe as EmbeddingManifestLoaderTests.WriteModelDir: a minimal, valid v1
    /// manifest directory so `Load` does real disk I/O against a real BERT-shaped model.</summary>
    private string WriteManifestDir(int dimensions)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ai-raccoon-reconcile-work-manifest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        File.WriteAllText(Path.Combine(dir, "vocab.txt"), "vocab");
        File.WriteAllText(Path.Combine(dir, "model.onnx"), "model");

        var manifest = new JsonObject
        {
            ["manifestVersion"] = 1,
            ["model"] = "timing-bert",
            ["source"] = new JsonObject { ["repo"] = "org/timing-bert", ["revision"] = "main", ["provider"] = "huggingface" },
            ["provider"] = "local",
            ["dimensions"] = dimensions,
            ["contextWindowTokens"] = 256,
            ["normalization"] = "l2",
            ["tokenizer"] = new JsonObject
            {
                ["family"] = "bert-wordpiece",
                ["files"] = new JsonArray(new JsonObject { ["path"] = "vocab.txt", ["sha256"] = ShaOf("vocab") })
            },
            ["onnx"] = new JsonObject
            {
                ["files"] = new JsonArray(new JsonObject { ["path"] = "model.onnx", ["sha256"] = ShaOf("model") }),
                ["inputs"] = new JsonArray("input_ids", "attention_mask", "token_type_ids"),
                ["tokenEmbeddingsOutput"] = "last_hidden_state"
            },
            ["pooling"] = new JsonObject { ["mode"] = "mean" }
        };
        File.WriteAllText(Path.Combine(dir, EmbeddingManifest.FileName), manifest.ToJsonString());
        return dir;
    }

    private static string ShaOf(string content)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
