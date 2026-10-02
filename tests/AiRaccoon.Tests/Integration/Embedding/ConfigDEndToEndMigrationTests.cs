using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Embedding.Manifest;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     config-D P5 (plan §2 P5, §3 row 18): the cross-package end-to-end migration on the REAL
///     bundled manifest. A bank built under a fixture manifest whose <c>chunkTokens</c> is 254
///     (everything else the shipped manifest's bytes) is upgraded to the shipped
///     <c>chunkTokens: 1022</c> manifest; one maintenance pass then comes out (a) re-chunked — note
///     groups merged through <see cref="ChunkBudgetReconciler" />, mirror groups re-chunked from
///     disk, split-content rows down; (b) re-embedded — every row <c>embedded</c> with a vector that
///     is a fresh embed of its post-re-chunk value; (c) trimmed at 1022 — no 426 warning over the
///     old 254 budget but under 1022, and the warning quotes 1022 above it; (d) closed with
///     <c>embedding.chunkBudget == 1022</c>; (e) code rows re-embedded with their 510-token
///     boundaries unchanged.
///     <para>
///         Budget resolution, the query trim and the engine fingerprint all run through a real
///         <see cref="EmbeddingService"/> over the real manifest; only inference is replaced (a
///         deterministic value-addressed generator), so the test exercises P1+P2+P3's resolution
///         and value pins rather than a fake that could disagree with them.
///     </para>
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class ConfigDEndToEndMigrationTests : IDisposable
{
    private const string ProjectId = "acme";
    private const int OldBudget = 254;
    private const int NewBudget = 1022;
    private const int QueryTrimmedEventId = 426;

    private static readonly DateTimeOffset FixedNow = new(2026, 9, 28, 15, 0, 0, TimeSpan.Zero);
    private static readonly EmbeddingSettings Bundled = new("local", null, null, null);

    private readonly string _dataRoot = TestData.CreateTempRoot("config-d-e2e");
    private readonly SqliteConnectionFactory _factory;
    private readonly FakeLogger<EmbeddingService> _logger = new();
    private readonly FakeTimeProvider _time = new(FixedNow);
    private readonly EmbeddingService _manifestService;
    private readonly ManifestEmbeddingService _embeddings;
    private readonly SqliteMemoryStore _store;
    private readonly ChunkBudgetReconciler _reconciler;
    private readonly EntryEmbedder _embedder;
    private readonly CodeEmbedder _codeEmbedder;

    public ConfigDEndToEndMigrationTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _manifestService = new EmbeddingService(_logger, new LocalTokenizer(), new EmbeddingTokenizerFactory(),
            new EmbeddingManifestLoader(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator()),
            NoOpMeasurementRecorder.Instance, TimeProvider.System, TestData.EmbeddingOptions());
        _embeddings = new ManifestEmbeddingService(_manifestService);
        _store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(_factory), TestData.RealMarkdownChunker(), _time, _embeddings,
            null, null, null, null, new CodeChunker(new CodeTokenizer()), null, null);
        _reconciler = new ChunkBudgetReconciler(TestData.RealFileTypeMatcher(), TestData.RealMarkdownChunker(),
            _embeddings, _time, () => _store, NullLogger<ChunkBudgetReconciler>.Instance);
        _embedder = TestData.CreateEntryEmbedder(_embeddings, new SqliteModelMigrationLease(_time), _time,
            new VecDimensionReconciler(), _reconciler);
        _codeEmbedder = new CodeEmbedder(_embeddings, NullLogger<CodeEmbedder>.Instance, new VecDimensionReconciler());
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [RetryFact]
    public async Task OneMaintenancePass_MigratesAFixture254BankToTheReal1022Manifest()
    {
        var note = LongNote();
        var filePath = Path.Combine(_dataRoot, "docs", "kept.md");
        var codePath = Path.Combine(_dataRoot, "src", "Sample.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(codePath)!);
        await File.WriteAllTextAsync(filePath, FileBody(), Ct);
        await File.WriteAllTextAsync(codePath, CodeBody(), Ct);

        await using var connection = await OpenAsync();

        // The bank is built under a fixture manifest: the shipped manifest's bytes with chunkTokens
        // 254. The fixture's pinned files are symlinks to the real ones, so the fixture engine is the
        // shipped engine in everything but the one field under test.
        var fixtureDir = WriteFixtureManifest(OldBudget);
        await SetSettingAsync(connection, EmbeddingSettingsKeys.Provider, "local");
        await SetSettingAsync(connection, EmbeddingSettingsKeys.Model, fixtureDir);
        await SetSettingAsync(connection, EmbeddingSettingsKeys.Engine,
            _manifestService.EngineFingerprint("local", fixtureDir, null));
        await SetSettingAsync(connection, EmbeddingSettingsKeys.CodeModel, BundledModel.SettingValue);
        await SetSettingAsync(connection, EmbeddingSettingsKeys.CodeEngine, "local:bundled#pre-config-d");
        await SetSettingAsync(connection, EmbeddingSettingsKeys.CodeDimensions, "384");
        await _store.SetSettingAsync(IngestScopeKeys.ScopeGlobal, IngestScopeKeys.Serialize([_dataRoot]), Ct);

        // Build stage: a long note (splits at 254), a mirror file (splits at 254) and a code file
        // (chunks at the code budget 510, never the memory budget).
        await _store.WriteAsync(new MemoryWriteRequest(ProjectId, note), Ct);
        await _store.IngestFileAsync(ProjectId, filePath, null, Ct);
        await _store.IngestFileAsync(ProjectId, codePath, null, Ct);
        await _store.EmbedPendingAsync(ProjectId, null, Ct);
        await _codeEmbedder.ReconcileVecCodeDimensionsAsync(connection, Ct);
        await _codeEmbedder.EmbedPendingBatchAsync(connection, int.MaxValue, Ct);

        var entriesBefore = await EntriesAsync(connection);
        var noteBefore = entriesBefore.Where(row => row.Path == NotePath(note)).ToList();
        var fileBefore = entriesBefore.Where(row => row.Path == filePath).ToList();
        var codeBefore = await CodeRowsAsync(connection);
        noteBefore.Count.ShouldBeGreaterThan(1, "premise: the note splits at the fixture's 254 budget");
        fileBefore.Count.ShouldBeGreaterThan(1, "premise: the file splits at the fixture's 254 budget");
        codeBefore.Count.ShouldBeGreaterThan(1, "premise: the code file splits at the code budget");
        entriesBefore.ShouldAllBe(row => row.EmbedState == "embedded", "premise: the 254 bank is fully embedded");
        codeBefore.ShouldAllBe(row => row.EmbedState == "embedded", "premise: the code corpus is embedded");
        CodeChunker.DefaultBudget.ShouldBe(510, "config D leaves the code budget alone (owner decision 1)");

        // Upgrade to the shipped manifest: the model setting falls back to bundled, so the engine
        // fingerprint and the resolved chunk budget both move to the real manifest's values.
        await DeleteSettingAsync(connection, EmbeddingSettingsKeys.Model);
        _manifestService.ResolveChunkBudgetFor(Bundled).ShouldBe(NewBudget,
            "the real bundled manifest's chunkTokens is the upgraded budget (config D)");

        var migration = new ModelMigrationJob(_embedder);
        (await migration.HasWorkAsync(connection, Ct)).ShouldBeTrue(
            "the manifest's bytes moved between the fixture and the shipped engine, so the migration opens");
        (await OpenMigrationsAsync(connection)).ShouldBe(1, "one migration is open for the maintenance pass");
        await migration.RunAsync(connection, Ct);

        var codeJob = new CodeReindexJob(_codeEmbedder, TestData.NewEmbedDrainPump());
        (await codeJob.HasWorkAsync(connection, Ct)).ShouldBeTrue(
            "the shipped manifest's bytes are the code engine's fingerprint too, so the bundled-default code engine drifted");
        (await PendingCodeAsync(connection)).ShouldBe(codeBefore.Count,
            "every code row is invalidated and none is embedded again until the drain runs");
        await _codeEmbedder.EmbedPendingBatchAsync(connection, int.MaxValue, Ct);

        // (a) Re-chunked: the note group merged via ChunkBudgetReconciler, the mirror group
        // re-chunked from disk, split content down.
        var entriesAfter = await EntriesAsync(connection);
        entriesAfter.Count.ShouldBeLessThan(entriesBefore.Count, "split content merges at the larger budget");
        var noteAfter = entriesAfter.Where(row => row.Path == NotePath(note)).ToList();
        noteAfter.Count.ShouldBeLessThan(noteBefore.Count, "the note group merged at the resolved budget");
        ChunkReassembly.TryMerge(NotePath(note), [.. noteAfter.Select(row => new NoteRow(row.Id, row.Value, -1))])
            .ShouldBe(note, "the re-chunked note reassembles byte-identically (sha256 == the path stem)");
        var fileAfter = entriesAfter.Where(row => row.Path == filePath).ToList();
        fileAfter.Count.ShouldBeLessThan(fileBefore.Count, "the mirror group re-chunked from its file on disk");
        fileAfter.ShouldAllBe(row => TokenCount(row.Value) <= NewBudget);

        // (b) Re-embedded: every row embedded, every stored vector a fresh embed of its CURRENT value.
        entriesAfter.ShouldAllBe(row => row.EmbedState == "embedded",
            "the migration does not close while any replacement row is pending");
        foreach (var row in entriesAfter)
        {
            row.Embedding.ShouldBe(_embeddings.BlobFor(_embeddings.DocumentText(Bundled, row.Value)),
                $"row {row.Id}'s vector must be a fresh embed of its post-re-chunk value");
        }

        // (c) Query budget 1022: the 426 warning is silent over the old 254 budget and quotes 1022
        // above the new one.
        var tokenizer = _manifestService.ResolveTokenizer(Bundled)!;
        var below = QueryOf(50);
        tokenizer.CountTokens(below).ShouldBeGreaterThan(OldBudget,
            "premise: the short query would have been trimmed under the old budget");
        tokenizer.CountTokens(below).ShouldBeLessThanOrEqualTo(NewBudget);
        _manifestService.TrimQueryToWindow(Bundled, below).ShouldBe(below,
            "between 254 and 1022 tokens the query is no longer trimmed");
        _logger.Collector.GetSnapshot().ShouldNotContain(r => r.Id.Id == QueryTrimmedEventId,
            "no 426 warning below the 1022 budget");
        var above = QueryOf(80);
        tokenizer.CountTokens(above).ShouldBeGreaterThan(NewBudget, "premise: the long query exceeds 1022");
        _manifestService.TrimQueryToWindow(Bundled, above).ShouldNotBe(above);
        _logger.Collector.GetSnapshot().ShouldContain(
            r => r.Id.Id == QueryTrimmedEventId && r.Message.Contains("1022-token window"),
            "the 426 warning quotes the manifest's own 1022, not the legacy 254");

        // (d) The migration closed and the drift stamp records the upgraded budget.
        (await OpenMigrationsAsync(connection)).ShouldBe(0, "the maintenance pass closed the migration");
        (await StampAsync(connection)).ShouldBe(NewBudget.ToString(CultureInfo.InvariantCulture),
            "the reconciler stamped the resolved budget at zero retryable skips");

        // (e) Code rows re-embedded with their chunk boundaries unchanged.
        var codeAfter = await CodeRowsAsync(connection);
        codeAfter.Select(row => (row.Id, row.Value, row.Hash)).ShouldBe(
            codeBefore.Select(row => (row.Id, row.Value, row.Hash)),
            "the memory budget change never moved the code corpus's 510-token chunk boundaries");
        codeAfter.ShouldAllBe(row => row.EmbedState == "embedded", "every code row re-embedded under the shipped engine");
        foreach (var row in codeAfter)
        {
            row.Embedding.ShouldBe(_embeddings.BlobFor(_embeddings.DocumentText(Bundled, row.Value)),
                $"code row {row.Id}'s vector must be a fresh embed of its unchanged value");
        }

        (await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT value FROM settings WHERE key = @key",
                new { key = EmbeddingSettingsKeys.CodeEngine }, cancellationToken: Ct)))
            .ShouldBe(_manifestService.EngineFingerprint("local", BundledModel.SettingValue, null),
                "the code engine's stored fingerprint moved to the shipped manifest's");
    }

    private async Task<SqliteConnection> OpenAsync() => await _factory.OpenBankAsync(Ct);

    private int TokenCount(string value) => _manifestService.ResolveTokenizer(Bundled)!.CountTokens(value);

    /// <summary>The shipped manifest's bytes with only <c>chunkTokens</c> changed and every pinned
    /// file symlinked back to the shipped engine's own copy.</summary>
    private string WriteFixtureManifest(int chunkTokens)
    {
        var source = BundledModel.ResolveDirectory();
        var dir = Path.Combine(_dataRoot, $"manifest-{chunkTokens}");
        Directory.CreateDirectory(dir);
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(source, EmbeddingManifest.FileName)))!;
        manifest["chunkTokens"] = chunkTokens;
        foreach (var relative in PinnedPaths(manifest))
        {
            var link = Path.Combine(dir, relative);
            var parent = Path.GetDirectoryName(link);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            File.CreateSymbolicLink(link, Path.Combine(source, relative));
        }

        File.WriteAllText(Path.Combine(dir, EmbeddingManifest.FileName), manifest.ToJsonString());
        return dir;
    }

    private static IEnumerable<string> PinnedPaths(JsonNode manifest)
    {
        foreach (var group in new JsonNode?[]
                 {
                     manifest["tokenizer"]?["files"], manifest["onnx"]?["files"], manifest["provenanceFiles"]
                 })
        {
            if (group is not JsonArray files)
            {
                continue;
            }

            foreach (var file in files)
            {
                if (file?["path"]?.GetValue<string>() is { } path)
                {
                    yield return path;
                }
            }
        }
    }

    private static async Task SetSettingAsync(SqliteConnection connection, string key, string value) =>
        await connection.ExecuteAsync(new CommandDefinition(MemorySql.UpsertSetting,
            new { key, value }, cancellationToken: Ct));

    private static async Task DeleteSettingAsync(SqliteConnection connection, string key) =>
        await connection.ExecuteAsync(new CommandDefinition(MemorySql.DeleteSetting,
            new { key }, cancellationToken: Ct));

    private static async Task<List<EntryRow>> EntriesAsync(SqliteConnection connection) =>
        [
            .. await connection.QueryAsync<EntryRow>(new CommandDefinition(
                """
                SELECT id AS Id, hash AS Hash, path AS Path, value AS Value, embed_state AS EmbedState,
                       embedding AS Embedding
                FROM entries ORDER BY id
                """, cancellationToken: Ct))
        ];

    private static async Task<List<CodeRow>> CodeRowsAsync(SqliteConnection connection) =>
        [
            .. await connection.QueryAsync<CodeRow>(new CommandDefinition(
                """
                SELECT id AS Id, hash AS Hash, value AS Value, embed_state AS EmbedState, embedding AS Embedding
                FROM code_entries ORDER BY id
                """, cancellationToken: Ct))
        ];

    private static async Task<long> OpenMigrationsAsync(SqliteConnection connection) =>
        await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM model_migration WHERE finished_at IS NULL", cancellationToken: Ct));

    private static async Task<long> PendingCodeAsync(SqliteConnection connection) =>
        await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM code_entries WHERE embed_state = 'pending'", cancellationToken: Ct));

    private static async Task<string?> StampAsync(SqliteConnection connection) =>
        await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT value FROM settings WHERE key = @key",
            new { key = EmbeddingSettingsKeys.ChunkBudget }, cancellationToken: Ct));

    private static string NotePath(string content) => $"{ContentHash.OfValue(content)}.md";

    /// <summary>Long enough to split at 254 tokens and merge into one piece at 1,022.</summary>
    private static string LongNote() => string.Join("\n\n", Enumerable.Range(0, 30).Select(i =>
        $"Item {i:D2} records that the committee approved the tombola budget line {i * 7:D3} after a vote."));

    private static string FileBody() =>
        string.Join("\n\n", Enumerable.Range(1, 5).Select(i =>
            $"## Section {i}\n\n{string.Join(" ", Enumerable.Repeat($"magnetostrictive section {i} body text", 40))}"));

    private static string CodeBody() => string.Join("\n\n", Enumerable.Range(0, 60).Select(i =>
        $"public static int Method{i}(int value)\n{{\n    // handler {i} keeps the ledger balanced\n    return value + {i};\n}}"));

    private static string QueryOf(int repeats) => string.Join(' ', Enumerable.Repeat(
        "how does the retrieval pipeline weigh full text against vectors when the corpus is large", repeats));

    private sealed record EntryRow(long Id, string Hash, string Path, string Value, string EmbedState, byte[] Embedding);

    private sealed record CodeRow(long Id, string Hash, string Value, string EmbedState, byte[] Embedding);

    /// <summary>
    ///     Real manifest resolution (budget, trim, fingerprint, tokenizer, dimensions) with only the
    ///     generator replaced: vectors are a deterministic function of the text they are handed, so
    ///     the test can recompute what a row's vector must be and prove WHEN it was computed.
    /// </summary>
    private sealed class ManifestEmbeddingService(IEmbeddingService inner) : IEmbeddingService
    {
        public byte[] BlobFor(string text) => EmbeddingBlob.ToBytes(VectorFor(text));

        public IEmbeddingGenerator<string, Embedding<float>> CreateGenerator(EmbeddingSettings settings) => new Generator();

        public string TrimQueryToWindow(EmbeddingSettings settings, string query) =>
            inner.TrimQueryToWindow(settings, query);

        public string DocumentText(EmbeddingSettings settings, string text) => inner.DocumentText(settings, text);

        public double? RelevanceFloor(EmbeddingSettings settings) => inner.RelevanceFloor(settings);

        public int ResolveChunkBudgetFor(EmbeddingSettings settings) => inner.ResolveChunkBudgetFor(settings);

        public IEmbeddingTokenizer? ResolveTokenizer(EmbeddingSettings settings) => inner.ResolveTokenizer(settings);

        public string EngineFingerprint(string provider, string? model, string? baseUrl) =>
            inner.EngineFingerprint(provider, model, baseUrl);

        public int ResolveDimensions(EmbeddingSettings settings) => inner.ResolveDimensions(settings);

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
