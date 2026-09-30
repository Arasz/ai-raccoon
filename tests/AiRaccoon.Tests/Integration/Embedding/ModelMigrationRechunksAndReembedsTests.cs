using System.Security.Cryptography;
using System.Text;
using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Metrics;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     config-D P1c (plan §3 row 6, AC5): one migration run over a bank written at 254 leaves it
///     re-chunked at the resolved budget AND re-embedded — every stored vector equals a fresh embed
///     of that row's post-re-chunk value, all rows <c>embedded</c>, the migration closed. The phase
///     runs inside <see cref="EntryEmbedder.DrainMigrationAsync" /> before the embed loop, so every
///     vector is computed from post-re-chunk values and no replacement row is left pending.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class ModelMigrationRechunksAndReembedsTests : IDisposable
{
    private const string ProjectId = "acme";
    private const int OldBudget = 254;
    private const int NewBudget = 1022;

    private static readonly DateTimeOffset FixedNow = new(2026, 9, 28, 13, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("model-migration-rechunks");
    private readonly SqliteConnectionFactory _factory;
    private readonly ValueAddressedEmbeddingService _embeddings = new();
    private readonly FakeTimeProvider _time = new(FixedNow);
    private readonly SqliteMemoryStore _store;
    private readonly ChunkBudgetReconciler _reconciler;
    private readonly EntryEmbedder _embedder;
    private readonly FakeLogger<ChunkBudgetReconciler> _reconcilerLog = new();
    private readonly RecordingMeasurementRecorder _measurements = new();

    public ModelMigrationRechunksAndReembedsTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _embeddings.ChunkBudgetOverride = OldBudget;
        _store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(_factory), TestData.RealMarkdownChunker(), _time,
            _embeddings, null, null, null, null, null, null, null);
        _reconciler = new ChunkBudgetReconciler(TestData.RealFileTypeMatcher(), TestData.RealMarkdownChunker(),
            _embeddings, _time, () => _store, _reconcilerLog);
        _embedder = TestData.CreateEntryEmbedder(_embeddings, new SqliteModelMigrationLease(_time), _time,
            new VecDimensionReconciler(), _reconciler, measurements: _measurements);
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string LongNote() => string.Join("\n\n", Enumerable.Range(0, 30).Select(i =>
        $"Item {i:D2} records that the committee approved the tombola budget line {i * 7:D3} after a vote."));

    private static string NotePath(string content) => $"{ContentHash.OfValue(content)}.md";

    private static string FileBody() =>
        string.Join("\n\n", Enumerable.Range(1, 5).Select(i =>
            $"## Section {i}\n\n" + string.Join(" ", Enumerable.Repeat($"magnetostrictive section {i} body text", 40))));

    private async Task<SqliteConnection> OpenAsync() => await _factory.OpenBankAsync(Ct);

    private static async Task<List<Row>> RowsAsync(SqliteConnection connection) =>
        [
            .. await connection.QueryAsync<Row>(new CommandDefinition(
                """
                SELECT id AS Id, value AS Value, embed_state AS EmbedState, embedding AS Embedding
                FROM entries ORDER BY id
                """, cancellationToken: Ct))
        ];

    [RetryFact]
    public async Task OneMigrationRun_RechunksTheBankAndEmbedsEveryRowFromItsNewValue()
    {
        var content = LongNote();
        await using (var setup = await OpenAsync())
        {
            await ConfigureEngineAsync(setup);
            await _store.WriteAsync(new MemoryWriteRequest(ProjectId, content), Ct);
            await _store.EmbedPendingAsync(ProjectId, null, Ct);
        }

        await using var connection = await OpenAsync();
        var before = await RowsAsync(connection);
        before.Count.ShouldBeGreaterThan(1, "premise: the note splits at 254");
        before.ShouldAllBe(row => row.EmbedState == "embedded", "premise: the 254 bank is fully embedded");

        _embeddings.ChunkBudgetOverride = NewBudget;
        (await _embedder.ReconcileFingerprintAsync(connection, Ct)).ShouldBeTrue(
            "the manifest's bytes moved with chunkTokens, so the fingerprint opens the migration");

        (await _embedder.DrainMigrationAsync(connection, Ct)).ShouldBeTrue();

        var after = await RowsAsync(connection);
        after.Count.ShouldBeLessThan(before.Count, "the rows re-chunk at the resolved budget (rows stay at 254 = the phase is gone)");
        var merged = ChunkReassembly.TryMerge(NotePath(content),
            [.. after.Select(row => new NoteRow(row.Id, row.Value, -1))]);
        merged.ShouldBe(content, "the re-chunked rows merge back byte-identically");
        after.ShouldAllBe(row => row.EmbedState == "embedded",
            "the migration does not close while any row is pending (AC5)");
        foreach (var row in after)
        {
            row.Embedding.ShouldBe(ValueAddressedEmbeddingService.BlobFor(row.Value),
                $"row {row.Id}'s vector must be a fresh embed of its POST-re-chunk value (AC5)");
        }

        (await connection.ExecuteScalarAsync<long?>(
            "SELECT finished_at FROM model_migration ORDER BY id DESC LIMIT 1")).ShouldNotBeNull(
            "the migration closes once the drain is done");
    }

    /// <summary>
    ///     The migration drain's re-chunk phase reports its duration and group count through the
    ///     drain's shared reporter. The groups value is checked against the fixture's own known
    ///     counts — exactly one over-budget note and one over-budget file, so one group each — not
    ///     only against the reconciler's counters: an independent oracle.
    /// </summary>
    [RetryFact]
    public async Task MigrationDrain_RecordsRechunkMeasurements_AfterDrain()
    {
        const int knownRechunkedNoteGroups = 1;
        const int knownRechunkedMirrorGroups = 1;
        var content = LongNote();
        var file = Path.Combine(_dataRoot, "migration-file.md");
        await File.WriteAllTextAsync(file, FileBody(), Ct);
        await using (var setup = await OpenAsync())
        {
            await ConfigureEngineAsync(setup);
            await _store.SetSettingAsync(IngestScopeKeys.ScopeGlobal, IngestScopeKeys.Serialize([_dataRoot]), Ct);
            await _store.WriteAsync(new MemoryWriteRequest(ProjectId, content), Ct);
            await _store.IngestFileAsync(ProjectId, file, null, Ct);
            await _store.EmbedPendingAsync(ProjectId, null, Ct);
        }

        await using var connection = await OpenAsync();
        _embeddings.ChunkBudgetOverride = NewBudget;
        (await _embedder.ReconcileFingerprintAsync(connection, Ct)).ShouldBeTrue(
            "the stamp drift opens the re-chunking migration");

        (await _embedder.DrainMigrationAsync(connection, Ct)).ShouldBeTrue();

        var record448 = _reconcilerLog.Collector.GetSnapshot().Single(record => record.Id.Id == 448);
        record448.Level.ShouldBe(LogLevel.Information,
            "448 is documented at Information — a Debug downgrade hides it from default logs");
        record448.Message.ShouldContain($"1 note group(s) and {knownRechunkedMirrorGroups} mirror group(s) re-chunked",
            customMessage: "the reconciler's own counters say both groups were replaced");

        var duration = _measurements.Recorded.Single(m => m.Name == "chunk.rechunk.duration_ms");
        duration.Kind.ShouldBe(MeasurementKind.Histogram);
        duration.Unit.ShouldBe("ms");
        duration.ProjectId.ShouldBe(MetricsConfigKeys.SelfMetricsProjectId);

        var groups = _measurements.Recorded.Single(m => m.Name == "chunk.rechunk.groups");
        groups.Kind.ShouldBe(MeasurementKind.Histogram);
        groups.Unit.ShouldBe("count");
        groups.ProjectId.ShouldBe(MetricsConfigKeys.SelfMetricsProjectId);
        groups.Value.ShouldBe(knownRechunkedNoteGroups + knownRechunkedMirrorGroups,
            "the fixture wrote one over-budget note and one over-budget file: both re-chunked");
    }

    /// <summary>
    ///     F2(a): the reporter's own unit test calls it directly, so nothing there can see what the
    ///     drain passes in. Running the drain's reconciler on a scripted clock of a known span makes
    ///     a zeroed <c>TimeSpan</c> at the call site show up as 0 ms instead of the phase's elapsed.
    /// </summary>
    [RetryFact]
    public async Task MigrationDrain_PinsThePhaseDurationValue()
    {
        var content = LongNote();
        await using (var setup = await OpenAsync())
        {
            await ConfigureEngineAsync(setup);
            await _store.WriteAsync(new MemoryWriteRequest(ProjectId, content), Ct);
            await _store.EmbedPendingAsync(ProjectId, null, Ct);
        }

        await using var connection = await OpenAsync();
        _embeddings.ChunkBudgetOverride = NewBudget;
        (await _embedder.ReconcileFingerprintAsync(connection, Ct)).ShouldBeTrue(
            "the stamp drift opens the re-chunking migration");
        var reconciler = new ChunkBudgetReconciler(TestData.RealFileTypeMatcher(), TestData.RealMarkdownChunker(),
            _embeddings, ScriptedTimeProvider.ForSpan(TimeSpan.FromSeconds(305.5)), () => _store, _reconcilerLog);
        var embedder = TestData.CreateEntryEmbedder(_embeddings, new SqliteModelMigrationLease(_time), _time,
            new VecDimensionReconciler(), reconciler, measurements: _measurements);

        (await embedder.DrainMigrationAsync(connection, Ct)).ShouldBeTrue();

        _measurements.Recorded.Single(m => m.Name == "chunk.rechunk.duration_ms").Value.ShouldBe(305500,
            "the drain must pass the phase report's own elapsed (305.5 s), not a zeroed span");
    }

    private static async Task ConfigureEngineAsync(SqliteConnection connection)
    {
        foreach (var (key, value) in new[]
                 {
                     ("embedding.provider", "local"),
                     // A pre-upgrade engine record: the reconcile sees the fingerprint changed.
                     ("embedding.engine", "engine-before-config-d"),
                 })
        {
            await connection.ExecuteAsync(new CommandDefinition(MemorySql.UpsertSetting,
                new { key, value }, cancellationToken: Ct));
        }
    }

    private sealed record Row(long Id, string Value, string EmbedState, byte[] Embedding);

    /// <summary>Embeds every text as a deterministic function of its bytes, so a test can recompute
    /// what a row's vector must be and prove WHEN it was computed.</summary>
    private sealed class ValueAddressedEmbeddingService : IEmbeddingService
    {
        public int? ChunkBudgetOverride { get; set; }

        public string EngineFingerprint(string provider, string? model, string? baseUrl) =>
            $"test:{provider}:{model}@{baseUrl}";

        public IEmbeddingGenerator<string, Embedding<float>> CreateGenerator(EmbeddingSettings settings) => new Generator();

        public string TrimQueryToWindow(EmbeddingSettings settings, string query) => query;

        public string DocumentText(EmbeddingSettings settings, string text) => text;

        public double? RelevanceFloor(EmbeddingSettings settings) => null;

        public int ResolveChunkBudgetFor(EmbeddingSettings settings) =>
            ChunkBudgetOverride ?? OnnxEmbeddingGenerator.MaxContentTokens;

        public int ResolveDimensions(EmbeddingSettings settings) => 384;

        public IEmbeddingTokenizer? ResolveTokenizer(EmbeddingSettings settings) =>
            string.Equals(settings.Provider, "local", StringComparison.OrdinalIgnoreCase)
                ? WordPieceEmbeddingTokenizer.Create(BundledModel.ResolveVocabPath())
                : null;

        public static byte[] BlobFor(string text) => EmbeddingBlob.ToBytes(VectorFor(text));

        private static float[] VectorFor(string text)
        {
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(text));
            var vector = new float[384];
            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] = digest[i % digest.Length] / 255f;
            }

            return vector;
        }

        private sealed class Generator : IEmbeddingGenerator<string, Embedding<float>>
        {
            public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
                EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
            {
                var embeddings = new GeneratedEmbeddings<Embedding<float>>();
                foreach (var value in values)
                {
                    embeddings.Add(new Embedding<float>(VectorFor(value)));
                }

                return Task.FromResult(embeddings);
            }

            public void Dispose()
            {
            }

            object? IEmbeddingGenerator.GetService(Type serviceType, object? serviceKey) => null;
        }
    }
}
