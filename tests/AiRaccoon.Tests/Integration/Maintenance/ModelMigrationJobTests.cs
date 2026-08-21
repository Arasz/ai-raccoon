using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Maintenance;

/// <summary>
///     ModelMigrationJob is the relay half of the outbox (ADR-0076): on-demand, it drains whatever
///     StartModelMigrationAsync left owing and marks the record finished. This is the mechanism the
///     maintenance loop's startup pass relies on to finish a migration interrupted mid-drain.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class ModelMigrationJobTests : IAsyncLifetime
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-model-migration-job");
    private SqliteConnectionFactory _factory = null!;
    private string _otherModelPath = null!;
    private SqliteMemoryStore _store = null!;
    private FakeTimeProvider _time = null!;

    public async ValueTask InitializeAsync()
    {
        await TestData.CreateBundledModel().EnsureAsync(TestContext.Current.CancellationToken);
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User };
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _time = new FakeTimeProvider(FixedNow);
        _store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(_factory), TestData.RealMarkdownChunker(), _time,
            TestData.CreateEmbeddingService());

        _otherModelPath = Path.Combine(Path.GetTempPath(), "ai-raccoon-custom-model", Guid.NewGuid().ToString("N"),
            BundledModel.ModelFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(_otherModelPath)!);
        File.Copy(BundledModel.ResolveModelPath(), _otherModelPath);
    }

    public ValueTask DisposeAsync()
    {
        TestData.DeleteTempRoot(_dataRoot);
        TestData.DeleteTempRoot(Path.GetDirectoryName(_otherModelPath)!);
        return ValueTask.CompletedTask;
    }

    private ModelMigrationJob NewJob() => new(new EntryEmbedder(TestData.CreateEmbeddingService(), new SqliteModelMigrationLease(_time), _time));

    [Fact]
    public async Task HasWorkAsync_WithNoOpenMigration_IsFalse()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);

        (await NewJob().HasWorkAsync(connection, TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task HasWorkAsync_AfterStartModelMigrationAsync_IsTrue()
    {
        await OpenAMigrationAsync();
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);

        (await NewJob().HasWorkAsync(connection, TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    [Fact]
    public async Task RunAsync_DrainsEveryPendingRow_UnderTheNewEngine()
    {
        var entry = await OpenAMigrationAsync();
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);

        var drained = await NewJob().RunAsync(connection, TestContext.Current.CancellationToken);

        drained.ShouldBeFalse(); // no further work for the general pending-embed sweep
        (await ReadRowStateAsync(entry.Hash)).ShouldBe("embedded");
        (await CountVecRowsAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task RunAsync_MarksTheMigrationFinished()
    {
        await OpenAMigrationAsync();
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);

        await NewJob().RunAsync(connection, TestContext.Current.CancellationToken);

        (await IsOpenAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task HasWorkAsync_AfterRunAsync_IsFalseAgain()
    {
        await OpenAMigrationAsync();
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var job = NewJob();
        await job.RunAsync(connection, TestContext.Current.CancellationToken);

        (await job.HasWorkAsync(connection, TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    /// <summary>The crash-recovery contract: a fresh job instance (a new process, in effect) finishes what an earlier one started but never completed.</summary>
    [Fact]
    public async Task RunAsync_ByADifferentJobInstance_ResumesAndFinishesAnAlreadyOpenMigration()
    {
        var entry = await OpenAMigrationAsync();

        var resumer = NewJob(); // stands in for "a different, restarted process"
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await resumer.RunAsync(connection, TestContext.Current.CancellationToken);

        (await ReadRowStateAsync(entry.Hash)).ShouldBe("embedded");
        (await IsOpenAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task RunAsync_WithNoOpenMigration_IsANoOp()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);

        await Should.NotThrowAsync(() => NewJob().RunAsync(connection, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunAsync_WhenAnotherRelayHoldsTheLease_DoesNotDrain()
    {
        var entry = await OpenAMigrationAsync();
        await using var holderConnection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var holder = new SqliteModelMigrationLease(_time);
        (await holder.TryAcquireAsync(holderConnection, TestContext.Current.CancellationToken)).ShouldBeTrue();

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await NewJob().RunAsync(connection, TestContext.Current.CancellationToken);

        (await ReadRowStateAsync(entry.Hash)).ShouldBe("pending"); // untouched: the rival never got the lease
        (await IsOpenAsync()).ShouldBeTrue();
    }

    private async Task<MemoryEntry> OpenAMigrationAsync()
    {
        await _store.StartModelMigrationAsync("local", null, null, TestContext.Current.CancellationToken);
        var entry = await _store.WriteAsync(new MemoryWriteRequest("acme", "will migrate"),
            TestContext.Current.CancellationToken);
        await _store.StartModelMigrationAsync("local", _otherModelPath, null, TestContext.Current.CancellationToken);
        return entry;
    }

    private async Task<string> ReadRowStateAsync(string hash)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.QuerySingleAsync<string>(new CommandDefinition(
            "SELECT embed_state FROM entries WHERE hash = @hash", new { hash },
            cancellationToken: TestContext.Current.CancellationToken));
    }

    private async Task<int> CountVecRowsAsync()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.QuerySingleAsync<int>(new CommandDefinition(
            "SELECT count(*) FROM vec_entries", cancellationToken: TestContext.Current.CancellationToken));
    }

    private async Task<bool> IsOpenAsync()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.QuerySingleAsync<int>(new CommandDefinition(
            "SELECT count(*) FROM model_migration WHERE id = 1 AND finished_at IS NULL",
            cancellationToken: TestContext.Current.CancellationToken)) > 0;
    }

    /// <summary>
    ///     Regression for the reported bug: `finished_at` must reflect the clock after the drain loop
    ///     ran, not the instant the relay called in. A fake generator that advances <see cref="_time" />
    ///     on every embed call stands in for the drain's real wall-clock cost, with no sleeping needed.
    /// </summary>
    [Fact]
    public async Task RunAsync_StampsFinishedAt_AfterTheDrainCompletes_NotBeforeItStarts()
    {
        await OpenAMigrationAsync();
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var perBatchAdvance = TimeSpan.FromSeconds(5);
        var embedder = new EntryEmbedder(new ClockAdvancingEmbeddingService(_time, perBatchAdvance),
            new SqliteModelMigrationLease(_time), _time);
        var beforeDrain = _time.GetUtcNow();

        await embedder.DrainMigrationAsync(connection, TestContext.Current.CancellationToken);

        var migration = await ReadMigrationAsync();
        migration!.FinishedAt.ShouldNotBeNull();
        migration.FinishedAt!.Value.ShouldBeGreaterThanOrEqualTo(beforeDrain + perBatchAdvance,
            "finished_at must reflect the time after the drain loop ran, not the time it was entered");
    }

    private async Task<ModelMigration?> ReadMigrationAsync()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<MigrationRow>(new CommandDefinition(
            "SELECT provider AS Provider, model AS Model, base_url AS BaseUrl, engine AS Engine, " +
            "started_at AS StartedAt, finished_at AS FinishedAt FROM model_migration WHERE id = 1",
            cancellationToken: TestContext.Current.CancellationToken));
        return row is null
            ? null
            : new ModelMigration(row.Provider, row.Model, row.BaseUrl, row.Engine,
                DateTimeOffset.FromUnixTimeSeconds(row.StartedAt),
                row.FinishedAt is { } finished ? DateTimeOffset.FromUnixTimeSeconds(finished) : null);
    }

    private sealed record MigrationRow(
        string Provider,
        string? Model,
        string? BaseUrl,
        string Engine,
        long StartedAt,
        long? FinishedAt);

    /// <summary>Advances a <see cref="FakeTimeProvider"/> by a fixed step on every generator call, standing in for real embedding latency without sleeping.</summary>
    private sealed class ClockAdvancingEmbeddingService(FakeTimeProvider time, TimeSpan perCallAdvance) : IEmbeddingService
    {
    public string EngineFingerprint(string provider, string? model, string? baseUrl) =>
        $"test:{provider}:{model}@{baseUrl}";
        public IEmbeddingGenerator<string, Embedding<float>> CreateGenerator(EmbeddingSettings settings) => new ClockAdvancingGenerator(time, perCallAdvance);

        public string TrimQueryToWindow(EmbeddingSettings settings, string query) => query;

        public int ResolveChunkBudgetFor(EmbeddingSettings settings) => OnnxEmbeddingGenerator.MaxContentTokens;

        /// <summary>The real bundled tokenizer — the resolver contract says local ⇒ a tokenizer exists (D9).</summary>
        public IEmbeddingTokenizer? ResolveTokenizer(EmbeddingSettings settings) =>
            string.Equals(settings.Provider, "local", StringComparison.OrdinalIgnoreCase)
                ? WordPieceEmbeddingTokenizer.Create(BundledModel.ResolveVocabPath())
                : null;

        private sealed class ClockAdvancingGenerator(FakeTimeProvider time, TimeSpan perCallAdvance)
            : IEmbeddingGenerator<string, Embedding<float>>
        {
            public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
                EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
            {
                time.Advance(perCallAdvance);
                var items = values.ToList();
                var embeddings = new GeneratedEmbeddings<Embedding<float>>(items.Count);
                foreach (var _ in items)
                {
                    embeddings.Add(new Embedding<float>(new float[384]));
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
