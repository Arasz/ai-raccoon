using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     One memory row that can never embed must not hold its batch hostage: the healthy rows
///     beside it still embed, and the bad row leaves the pending selection after a bounded number
///     of failed attempts instead of failing every drain pass forever.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class EntryEmbedderPoisonRowTests : IDisposable
{
    private const string Poison = "POISON";
    private const string Busy = "BUSY";

    private readonly string _dataRoot = TestData.CreateTempRoot("entry-embedder-poison-row");
    private readonly SqliteConnectionFactory _factory;

    public EntryEmbedderPoisonRowTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [RetryFact]
    public async Task EmbedPendingBatch_OnePoisonRowAmongGoodRows_EmbedsTheGoodRows()
    {
        await using var connection = await _factory.OpenBankAsync(Ct);
        await ConfigureProviderAsync(connection);
        var good1 = await InsertAsync(connection, "good one");
        var poison = await InsertAsync(connection, $"{Poison} row");
        var good2 = await InsertAsync(connection, "good two");
        var embedder = NewEmbedder(new PoisonEmbeddingService());

        var embedded = await embedder.EmbedPendingBatchAsync(connection, 32, Ct);

        embedded.ShouldBe(2);
        (await StateOfAsync(connection, good1)).ShouldBe("embedded");
        (await StateOfAsync(connection, good2)).ShouldBe("embedded");
        (await StateOfAsync(connection, poison)).ShouldBe("pending");
    }

    [RetryFact]
    public async Task EmbedPendingBatch_PoisonRowFailsUpToTheCeiling_IsNoLongerSelectedOrPolled()
    {
        await using var connection = await _factory.OpenBankAsync(Ct);
        await ConfigureProviderAsync(connection);
        var poison = await InsertAsync(connection, $"{Poison} row");
        var embeddings = new PoisonEmbeddingService();
        var embedder = NewEmbedder(embeddings);

        for (var pass = 0; pass < EntryEmbedder.MaxEmbedAttempts; pass++)
        {
            await embedder.EmbedPendingBatchAsync(connection, 32, Ct);
        }

        var callsBefore = embeddings.Calls.Count;
        var embedded = await embedder.EmbedPendingBatchAsync(connection, 32, Ct);

        embedded.ShouldBe(0);
        embeddings.Calls.Count.ShouldBe(callsBefore, "an abandoned row must not reach the generator again");
        (await StateOfAsync(connection, poison)).ShouldBe("pending", "abandoned, not falsely marked embedded");
        (await connection.ExecuteScalarAsync<bool>(new CommandDefinition(MemorySql.HasPendingEmbed, cancellationToken: Ct)))
            .ShouldBeFalse("the on-demand poll must stop waking up for a row it has given up on");
    }

    [RetryFact]
    public async Task EmbedPending_ProjectScopedWithNoLimit_TerminatesAndEmbedsTheGoodRows()
    {
        await using var connection = await _factory.OpenBankAsync(Ct);
        await ConfigureProviderAsync(connection);
        await InsertAsync(connection, $"{Poison} row");
        var good = await InsertAsync(connection, "good one");
        var embedder = NewEmbedder(new PoisonEmbeddingService());

        var embedded = await embedder.EmbedPendingAsync(connection, "acme", null, Ct);

        embedded.ShouldBe(1);
        (await StateOfAsync(connection, good)).ShouldBe("embedded");
    }

    [RetryFact]
    public async Task DrainMigration_WithAPoisonRow_StillFinishesTheMigration()
    {
        await using var connection = await _factory.OpenBankAsync(Ct);
        var embedder = NewEmbedder(new PoisonEmbeddingService());
        await embedder.StartMigrationAsync(connection, "local", "model-a", null, DateTimeOffset.UnixEpoch, Ct);
        var good = await InsertAsync(connection, "good one");
        await InsertAsync(connection, $"{Poison} row");
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE entries SET embed_state = 'embedded', embedding = @blob WHERE id = @good",
            new { good, blob = EmbeddingBlob.ToBytes(new float[384]) }, cancellationToken: Ct));
        await embedder.StartMigrationAsync(connection, "local", "model-b", null, DateTimeOffset.UnixEpoch, Ct);

        (await embedder.DrainMigrationAsync(connection, Ct)).ShouldBeTrue();

        (await connection.ExecuteScalarAsync<long>(new CommandDefinition(MemorySql.HasOpenModelMigration, cancellationToken: Ct)))
            .ShouldBe(0, "one poison row must not keep the bank's migration open, and its tool gate shut, forever");
        (await StateOfAsync(connection, good)).ShouldBe("embedded");
    }

    [RetryFact]
    public async Task ModelSwitch_ReadmitsAnAbandonedRow_SoTheNewEngineCanEmbedIt()
    {
        await using var connection = await _factory.OpenBankAsync(Ct);
        var embeddings = new PoisonEmbeddingService();
        var embedder = NewEmbedder(embeddings);
        await embedder.StartMigrationAsync(connection, "local", "model-a", null, DateTimeOffset.UnixEpoch, Ct);
        var poison = await InsertAsync(connection, $"{Poison} row");
        for (var pass = 0; pass < EntryEmbedder.MaxEmbedAttempts; pass++)
        {
            await embedder.EmbedPendingBatchAsync(connection, 32, Ct);
        }

        embeddings.PoisonEnabled = false;
        await embedder.StartMigrationAsync(connection, "local", "model-b", null, DateTimeOffset.UnixEpoch, Ct);
        (await embedder.DrainMigrationAsync(connection, Ct)).ShouldBeTrue();

        (await StateOfAsync(connection, poison)).ShouldBe("embedded");
    }

    [RetryFact]
    public async Task EngineDown_EveryCallFails_AbandonsNothingAndFailsThePass()
    {
        await using var connection = await _factory.OpenBankAsync(Ct);
        await ConfigureProviderAsync(connection);
        await InsertAsync(connection, "good one");
        await InsertAsync(connection, "good two");
        var embeddings = new PoisonEmbeddingService { EngineDown = true };
        var embedder = NewEmbedder(embeddings);

        for (var pass = 0; pass < EntryEmbedder.MaxEmbedAttempts + 2; pass++)
        {
            await Should.ThrowAsync<InvalidOperationException>(
                () => embedder.EmbedPendingBatchAsync(connection, 32, Ct));
        }

        embeddings.EngineDown = false;
        var embedded = await embedder.EmbedPendingBatchAsync(connection, 32, Ct);

        embedded.ShouldBe(2, "an outage is not the rows' fault: they must embed once the engine is back");
    }

    [RetryFact]
    public async Task BankBusy_WhileEmbeddingARow_FailsThePassWithoutChargingTheRow()
    {
        await using var connection = await _factory.OpenBankAsync(Ct);
        await ConfigureProviderAsync(connection);
        var row = await InsertAsync(connection, $"{Busy} row");
        var embedder = NewEmbedder(new PoisonEmbeddingService());

        for (var pass = 0; pass < EntryEmbedder.MaxEmbedAttempts; pass++)
        {
            await Should.ThrowAsync<SqliteException>(() => embedder.EmbedPendingBatchAsync(connection, 32, Ct));
        }

        (await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT embed_attempts FROM entries WHERE id = @row", new { row }, cancellationToken: Ct)))
            .ShouldBe(0L, "another writer holding the lock is not the row's fault");
    }

    [RetryFact]
    public async Task PoisonRow_LogsAWarningPerAttemptAndOneErrorWhenAbandoned()
    {
        await using var connection = await _factory.OpenBankAsync(Ct);
        await ConfigureProviderAsync(connection);
        var poison = await InsertAsync(connection, $"{Poison} row", sourceFile: "notes/poison.md");
        var logger = new FakeLogger<EntryEmbedder>();
        var embedder = NewEmbedder(new PoisonEmbeddingService(), logger);

        for (var pass = 0; pass < EntryEmbedder.MaxEmbedAttempts + 1; pass++)
        {
            await embedder.EmbedPendingBatchAsync(connection, 32, Ct);
        }

        var records = logger.Collector.GetSnapshot();
        var warnings = records.Where(r => r.Level == LogLevel.Warning).ToList();
        warnings.Count.ShouldBe(EntryEmbedder.MaxEmbedAttempts, "one warning per failed attempt, none once abandoned");
        warnings.ShouldAllBe(r => r.Exception != null);
        var giveUp = records.Where(r => r.Level == LogLevel.Error).ShouldHaveSingleItem();
        giveUp.Message.ShouldContain(poison.ToString(System.Globalization.CultureInfo.InvariantCulture));
        giveUp.Message.ShouldContain("notes/poison.md", Case.Sensitive);
    }

    [RetryFact]
    public async Task HealthyDrain_LogsNothingAtWarningOrAbove()
    {
        await using var connection = await _factory.OpenBankAsync(Ct);
        await ConfigureProviderAsync(connection);
        await InsertAsync(connection, "good one");
        var logger = new FakeLogger<EntryEmbedder>();
        var embedder = NewEmbedder(new PoisonEmbeddingService(), logger);

        (await embedder.EmbedPendingBatchAsync(connection, 32, Ct)).ShouldBe(1);

        logger.Collector.GetSnapshot().ShouldNotContain(r => r.Level >= LogLevel.Warning);
    }

    /// <summary>A bank wedged by a poison row before the attempts column existed recovers once it is opened by this build.</summary>
    [RetryFact]
    public async Task ExistingBankWedgedBeforeUpgrade_RecoversItsGoodRowsAndAbandonsThePoisonRow()
    {
        await using var connection = await _factory.OpenBankAsync(Ct);
        await ConfigureProviderAsync(connection);
        await connection.ExecuteAsync(new CommandDefinition(
            $"ALTER TABLE entries DROP COLUMN embed_attempts; PRAGMA application_id = {MemorySchema.SchemaDigest + 1};",
            cancellationToken: Ct));
        var poison = await InsertAsync(connection, $"{Poison} row");
        var good = await InsertAsync(connection, "good one");

        await MemorySchema.EnsureAsync(connection, Ct);
        var embedder = NewEmbedder(new PoisonEmbeddingService());
        for (var pass = 0; pass < EntryEmbedder.MaxEmbedAttempts; pass++)
        {
            await embedder.EmbedPendingBatchAsync(connection, 32, Ct);
        }

        (await StateOfAsync(connection, good)).ShouldBe("embedded");
        (await StateOfAsync(connection, poison)).ShouldBe("pending");
        (await connection.ExecuteScalarAsync<bool>(new CommandDefinition(MemorySql.HasPendingEmbed, cancellationToken: Ct)))
            .ShouldBeFalse();
    }

    /// <summary>The structure heal behind memory_embed_pending must not let one unembeddable heading fail every call.</summary>
    [RetryFact]
    public async Task StructureHeal_OnePoisonHeading_HealsTheOthersAndStopsRetryingIt()
    {
        await using var connection = await _factory.OpenBankAsync(Ct);
        await ConfigureProviderAsync(connection);
        var poison = await InsertLegacyEmbeddedAsync(connection, $"# {Poison} heading\nbody one");
        var good = await InsertLegacyEmbeddedAsync(connection, "# Good heading\nbody two");
        var embeddings = new PoisonEmbeddingService();
        var embedder = NewEmbedder(embeddings);

        await embedder.EmbedPendingAsync(connection, "acme", null, Ct);
        var callsAfterFirst = embeddings.Calls.Count;
        await embedder.EmbedPendingAsync(connection, "acme", null, Ct);

        (await HeadingPathOfAsync(connection, good)).ShouldBe("Good heading");
        (await StructureOfAsync(connection, good)).ShouldNotBeNull();
        (await HeadingPathOfAsync(connection, poison)).ShouldBe("", "the '' sentinel takes the row out of the heal set");
        (await StructureOfAsync(connection, poison)).ShouldBeNull();
        embeddings.Calls.Count.ShouldBe(callsAfterFirst, "a healed-or-given-up row is not retried");
    }

    [RetryFact]
    public async Task StructureHeal_EngineDown_FailsAndGivesUpOnNoRow()
    {
        await using var connection = await _factory.OpenBankAsync(Ct);
        await ConfigureProviderAsync(connection);
        var row = await InsertLegacyEmbeddedAsync(connection, "# Good heading\nbody");
        var embedder = NewEmbedder(new PoisonEmbeddingService { EngineDown = true });

        await Should.ThrowAsync<InvalidOperationException>(() => embedder.EmbedPendingAsync(connection, "acme", null, Ct));

        (await HeadingPathOfAsync(connection, row)).ShouldBeNull("an outage must not stamp the sentinel on a healthy row");
    }

    /// <summary>A const SQL string cannot interpolate the ceiling, so this pins the literal to it.</summary>
    [RetryFact]
    public void PendingSelections_CarryTheCeilingAsTheirLiteral()
    {
        var clause = $"embed_attempts < {EntryEmbedder.MaxEmbedAttempts} ";

        MemorySql.SelectPendingForEmbed.ShouldContain(clause);
        MemorySql.SelectAllPendingForEmbed.ShouldContain(clause);
        MemorySql.HasPendingEmbed.ShouldContain(clause);
    }

    private static EntryEmbedder NewEmbedder(IEmbeddingService embeddings, ILogger<EntryEmbedder>? logger = null) =>
        new(embeddings, new SqliteModelMigrationLease(TimeProvider.System), TimeProvider.System,
            new VecDimensionReconciler(), new EmbedDrainReporter(NoOpMeasurementRecorder.Instance, TimeProvider.System),
            TestTelemetry.None, logger ?? new FakeLogger<EntryEmbedder>());

    private static async Task ConfigureProviderAsync(SqliteConnection connection) =>
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO settings (key, value) VALUES (@key, 'local') ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            new { key = EmbeddingSettingsKeys.Provider }, cancellationToken: Ct));

    private static async Task<long> InsertAsync(SqliteConnection connection, string value, string? sourceFile = null) =>
        await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO entries (hash, path, value, source_file, scope, project_id, created_at, updated_at)
            VALUES (@value, @value, @value, @sourceFile, 'project', 'acme', 0, 0)
            RETURNING id
            """,
            new { value, sourceFile }, cancellationToken: Ct));

    private static async Task<string> StateOfAsync(SqliteConnection connection, long id) =>
        await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT embed_state FROM entries WHERE id = @id", new { id }, cancellationToken: Ct)) ?? "";

    /// <summary>An embedded row from before the structure writer: content vector, no heading path, no structure vector.</summary>
    private static async Task<long> InsertLegacyEmbeddedAsync(SqliteConnection connection, string value)
    {
        var id = await InsertAsync(connection, value);
        var blob = new float[384];
        blob[0] = 1f;
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE entries SET embed_state = 'embedded', embedding = @embedding WHERE id = @id",
            new { id, embedding = EmbeddingBlob.ToBytes(blob) }, cancellationToken: Ct));
        return id;
    }

    private static async Task<string?> HeadingPathOfAsync(SqliteConnection connection, long id) =>
        await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT heading_path FROM entries WHERE id = @id", new { id }, cancellationToken: Ct));

    private static async Task<byte[]?> StructureOfAsync(SqliteConnection connection, long id) =>
        await connection.ExecuteScalarAsync<byte[]?>(new CommandDefinition(
            "SELECT structure_embedding FROM entries WHERE id = @id", new { id }, cancellationToken: Ct));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>384-dim test engine: a call containing a <see cref="Poison" /> input throws, one containing
    /// <see cref="Busy" /> throws SQLITE_BUSY, and <see cref="EngineDown" /> makes every call throw.</summary>
    private sealed class PoisonEmbeddingService : IEmbeddingService, IEmbeddingGenerator<string, Embedding<float>>
    {
        public bool PoisonEnabled { get; set; } = true;

        public bool EngineDown { get; set; }

        public List<IReadOnlyList<string>> Calls { get; } = [];

        public string EngineFingerprint(string provider, string? model, string? baseUrl) => $"test:{provider}:{model}";

        public IEmbeddingGenerator<string, Embedding<float>> CreateGenerator(EmbeddingSettings settings) => this;

        public string TrimQueryToWindow(EmbeddingSettings settings, string query) => query;

        public string DocumentText(EmbeddingSettings settings, string text) => text;

        public double? RelevanceFloor(EmbeddingSettings settings) => null;

        public int ResolveChunkBudgetFor(EmbeddingSettings settings) => 512;

        public int ResolveDimensions(EmbeddingSettings settings) => 384;

        public IEmbeddingTokenizer? ResolveTokenizer(EmbeddingSettings settings) => null;

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            var items = values.ToList();
            Calls.Add(items);
            if (EngineDown)
            {
                throw new InvalidOperationException("simulated engine outage");
            }

            if (items.Any(item => item.Contains(Busy, StringComparison.Ordinal)))
            {
                throw new SqliteException("database is locked", 5);
            }

            if (PoisonEnabled && items.Any(item => item.Contains(Poison, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("simulated poison row: this content can never embed");
            }

            var generated = new GeneratedEmbeddings<Embedding<float>>(items.Count);
            foreach (var _ in items)
            {
                var vector = new float[384];
                vector[0] = 1f;
                generated.Add(new Embedding<float>(vector));
            }

            return Task.FromResult(generated);
        }

        public void Dispose()
        {
        }

        object? IEmbeddingGenerator.GetService(Type serviceType, object? serviceKey) => null;
    }
}
