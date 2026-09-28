using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     config-D P1c (plan §3 row 7, AC6/AC7): budget drift — a stale or absent
///     <c>embedding.chunkBudget</c> on a non-empty bank — opens a re-chunk-only migration even when
///     the engine fingerprint matches (no <see cref="MemorySql.MarkAllEmbeddedPending" />; unchanged
///     rows keep their vectors and the engine is never asked for them again). A zero-row bank is
///     stamped without one. Retryable skips withhold the stamp and are retried via this drift path
///     once per server start until they converge — or until the config-D F5 attempt bound (three
///     unproven passes per budget) leaves them terminal and stamps anyway.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class ChunkBudgetDriftOpensAMigrationTests : IDisposable
{
    private const string ProjectId = "acme";
    private const int OldBudget = 254;
    private const int NewBudget = 1022;

    private static readonly DateTimeOffset FixedNow = new(2026, 9, 28, 14, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("chunk-budget-drift");
    private readonly SqliteConnectionFactory _factory;
    private readonly CountingEmbeddingService _embeddings = new();
    private readonly FakeTimeProvider _time = new(FixedNow);
    private readonly SqliteMemoryStore _store;
    private readonly ChunkBudgetReconciler _reconciler;

    public ChunkBudgetDriftOpensAMigrationTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _embeddings.ChunkBudgetOverride = OldBudget;
        _store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(_factory), TestData.RealMarkdownChunker(), _time,
            _embeddings, null, null, null, null, null, null, null);
        _reconciler = new ChunkBudgetReconciler(TestData.RealFileTypeMatcher(), TestData.RealMarkdownChunker(),
            _embeddings, _time, () => _store, NullLogger<ChunkBudgetReconciler>.Instance);
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string LongNote() => string.Join("\n\n", Enumerable.Range(0, 30).Select(i =>
        $"Item {i:D2} records that the committee approved the tombola budget line {i * 7:D3} after a vote."));

    private static string ShortNote() => "A short note that fits comfortably.";

    private static string NotePath(string content) => $"{ContentHash.OfValue(content)}.md";

    private EntryEmbedder NewEmbedder() => TestData.CreateEntryEmbedder(_embeddings,
        new SqliteModelMigrationLease(_time), _time, new VecDimensionReconciler(), _reconciler);

    private async Task<SqliteConnection> OpenAsync() => await _factory.OpenBankAsync(Ct);

    /// <summary>A bank whose engine fingerprint already equals the configured engine's.</summary>
    private async Task ConfigureEqualFingerprintAsync(SqliteConnection connection)
    {
        foreach (var (key, value) in new[]
                 {
                     ("embedding.provider", "local"),
                     ("embedding.engine", _embeddings.EngineFingerprint("local", null, null)),
                 })
        {
            await connection.ExecuteAsync(new CommandDefinition(MemorySql.UpsertSetting,
                new { key, value }, cancellationToken: Ct));
        }
    }

    private static async Task SetStampAsync(SqliteConnection connection, string value) =>
        await connection.ExecuteAsync(new CommandDefinition(MemorySql.UpsertSetting,
            new { key = EmbeddingSettingsKeys.ChunkBudget, value }, cancellationToken: Ct));

    private static async Task<string?> StampAsync(SqliteConnection connection) =>
        await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT value FROM settings WHERE key = @key",
            new { key = EmbeddingSettingsKeys.ChunkBudget }, cancellationToken: Ct));

    private static async Task<long> PendingCountAsync(SqliteConnection connection) =>
        await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM entries WHERE embed_state = 'pending'", cancellationToken: Ct));

    private static async Task<long> OpenMigrationsAsync(SqliteConnection connection) =>
        await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM model_migration WHERE finished_at IS NULL", cancellationToken: Ct));

    [RetryFact]
    public async Task StaleStampOnANonEmptyBank_OpensARechunkOnlyMigrationDespiteAnEqualFingerprint()
    {
        await using var connection = await OpenAsync();
        await ConfigureEqualFingerprintAsync(connection);
        await _store.WriteAsync(new MemoryWriteRequest(ProjectId, LongNote()), Ct);
        await _store.WriteAsync(new MemoryWriteRequest(ProjectId, ShortNote()), Ct);
        await _store.EmbedPendingAsync(ProjectId, null, Ct);
        await SetStampAsync(connection, OldBudget.ToString());
        var callsForShortNote = _embeddings.CallCountFor(ShortNote());
        callsForShortNote.ShouldBeGreaterThan(0, "premise: the short note is embedded before the drift");
        _embeddings.ChunkBudgetOverride = NewBudget;

        (await NewEmbedder().ReconcileFingerprintAsync(connection, Ct)).ShouldBeTrue();

        (await PendingCountAsync(connection)).ShouldBe(0,
            "a drift re-chunks; it never MarkAllEmbeddedPending (rows keep their vectors — AC6)");
        (await OpenMigrationsAsync(connection)).ShouldBe(1);

        (await NewEmbedder().DrainMigrationAsync(connection, Ct)).ShouldBeTrue();
        _embeddings.CallCountFor(ShortNote()).ShouldBe(callsForShortNote,
            "an unchanged row keeps its vector; the engine is never asked for it again (AC6)");
        var rechunked = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM entries WHERE path = @path", new { path = NotePath(LongNote()) },
            cancellationToken: Ct));
        rechunked.ShouldBeLessThan(2, "the long note re-chunks inside the drift migration");
        (await PendingCountAsync(connection)).ShouldBe(0, "the migration does not close with rows pending (AC5)");
        (await OpenMigrationsAsync(connection)).ShouldBe(0, "the drift migration closes");
    }

    [RetryFact]
    public async Task AbsentStampOnANonEmptyBank_OpensARechunkOnlyMigration()
    {
        await using var connection = await OpenAsync();
        await ConfigureEqualFingerprintAsync(connection);
        await _store.WriteAsync(new MemoryWriteRequest(ProjectId, ShortNote()), Ct);
        await _store.EmbedPendingAsync(ProjectId, null, Ct);
        (await StampAsync(connection)).ShouldBeNull("premise: the bank predates the stamp");
        _embeddings.ChunkBudgetOverride = NewBudget;

        (await NewEmbedder().ReconcileFingerprintAsync(connection, Ct)).ShouldBeTrue();

        (await PendingCountAsync(connection)).ShouldBe(0, "no MarkAllEmbeddedPending on the drift path (AC6)");
        (await OpenMigrationsAsync(connection)).ShouldBe(1);
    }

    [RetryFact]
    public async Task ZeroRowBank_IsStampedWithoutAMigration()
    {
        await using var connection = await OpenAsync();
        await ConfigureEqualFingerprintAsync(connection);
        _embeddings.ChunkBudgetOverride = NewBudget;

        (await NewEmbedder().ReconcileFingerprintAsync(connection, Ct)).ShouldBeFalse(
            "a zero-row bank has nothing to re-chunk");

        (await StampAsync(connection)).ShouldBe(NewBudget.ToString(), "it is stamped silently (AC6)");
        (await OpenMigrationsAsync(connection)).ShouldBe(0);
    }

    [RetryFact]
    public async Task CurrentStampAtAnEqualFingerprint_OpensNothing()
    {
        await using var connection = await OpenAsync();
        await ConfigureEqualFingerprintAsync(connection);
        await _store.WriteAsync(new MemoryWriteRequest(ProjectId, ShortNote()), Ct);
        await _store.EmbedPendingAsync(ProjectId, null, Ct);
        await SetStampAsync(connection, OldBudget.ToString());

        (await NewEmbedder().ReconcileFingerprintAsync(connection, Ct)).ShouldBeFalse(
            "steady state: the budget did not move, so there is no drift");

        (await OpenMigrationsAsync(connection)).ShouldBe(0);
    }

    /// <summary>Retried once per server start while the group is still broken — up to the F5 attempt
    /// bound; <see cref="AnUnprovableGroup_DoesNotReopenTheMigrationAfterTheAttemptBound" /> pins the
    /// bound that stops the retry forever for a group whose merge can never prove.</summary>
    [RetryFact]
    public async Task RetryableSkips_AreRetriedOncePerServerStartUntilTheyConverge()
    {
        await using var connection = await OpenAsync();
        await ConfigureEqualFingerprintAsync(connection);
        await _store.WriteAsync(new MemoryWriteRequest(ProjectId, LongNote()), Ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE entries SET value = value || ' tampered' WHERE id = (SELECT MIN(id) FROM entries)",
            cancellationToken: Ct));
        await SetStampAsync(connection, OldBudget.ToString());
        _embeddings.ChunkBudgetOverride = NewBudget;

        var firstStart = NewEmbedder();
        (await firstStart.ReconcileFingerprintAsync(connection, Ct)).ShouldBeTrue("the drift is fresh");
        (await firstStart.DrainMigrationAsync(connection, Ct)).ShouldBeTrue();
        (await StampAsync(connection)).ShouldBeNull("the retryable skip withholds the stamp (AC7)");

        (await firstStart.ReconcileFingerprintAsync(connection, Ct)).ShouldBeFalse(
            "the retry waits for the next server start, not the next poll of this one (AC7)");

        var secondStart = NewEmbedder();
        (await secondStart.ReconcileFingerprintAsync(connection, Ct)).ShouldBeTrue(
            "a later server start retries the drift path");
    }

    [RetryFact]
    public async Task AnUnprovableGroup_DoesNotReopenTheMigrationAfterTheAttemptBound()
    {
        await using var connection = await OpenAsync();
        await ConfigureEqualFingerprintAsync(connection);
        await _store.WriteAsync(new MemoryWriteRequest(ProjectId, LongNote()), Ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE entries SET value = value || ' tampered' WHERE id = (SELECT MIN(id) FROM entries)",
            cancellationToken: Ct));
        await SetStampAsync(connection, OldBudget.ToString());
        _embeddings.ChunkBudgetOverride = NewBudget;

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var start = NewEmbedder();
            (await start.ReconcileFingerprintAsync(connection, Ct)).ShouldBeTrue(
                $"server start {attempt} retries the drift while attempts remain");
            (await start.DrainMigrationAsync(connection, Ct)).ShouldBeTrue();
        }

        (await StampAsync(connection)).ShouldBe(NewBudget.ToString(),
            "three attempts spent: the permanently-unproven group is terminal and no longer withholds the stamp (config-D F5)");

        var laterStart = NewEmbedder();
        (await laterStart.ReconcileFingerprintAsync(connection, Ct)).ShouldBeFalse(
            "a permanently-unprovable group must not re-open the migration forever");
        (await OpenMigrationsAsync(connection)).ShouldBe(0);
    }
}
