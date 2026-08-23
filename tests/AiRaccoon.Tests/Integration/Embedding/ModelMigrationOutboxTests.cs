using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     The outbox half of a model migration (ADR-0076): StartModelMigrationAsync commits the new
///     engine settings and the migration-started record in one transaction, marks the bank's
///     embedded rows pending, and never re-embeds inline — that is the relay's job.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class ModelMigrationOutboxTests : IAsyncLifetime
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = CreateTempRoot();
    private SqliteConnectionFactory _factory = null!;
    private SqliteMemoryStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        await TestData.CreateBundledModel().EnsureAsync(TestContext.Current.CancellationToken);
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User };
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(_factory), TestData.RealMarkdownChunker(), new FakeTimeProvider(FixedNow),
            TestData.CreateEmbeddingService(), null, null, null, null, null, null, null);
    }

    public ValueTask DisposeAsync()
    {
        TestData.DeleteTempRoot(_dataRoot);
        return ValueTask.CompletedTask;
    }

    private static string CreateTempRoot() => TestData.CreateTempRoot("ai-raccoon-model-migration-outbox");

    [Fact]
    public async Task StartModelMigrationAsync_OnAFreshBank_WritesSettingsButNoMigrationRow()
    {
        var config = await _store.StartModelMigrationAsync("local", null, null, TestContext.Current.CancellationToken);

        config.Engine.ShouldBe("local:bundled");
        (await ReadMigrationAsync()).ShouldBeNull(); // no prior engine, nothing owed
    }

    [Fact]
    public async Task StartModelMigrationAsync_ChangingEngine_MarksPreviouslyEmbeddedRowsPending_AndDoesNotReEmbedInline()
    {
        await _store.StartModelMigrationAsync("local", null, null, TestContext.Current.CancellationToken);
        var entry = await _store.WriteAsync(new MemoryWriteRequest("acme", "will be marked pending by the next migration"),
            TestContext.Current.CancellationToken);
        (await ReadRowStateAsync(entry.Hash)).ShouldBe("embedded"); // sync embed on write, same as ConfigureEmbeddingAsync's contract

        var otherPath = Path.Combine(Path.GetTempPath(), "ai-raccoon-custom-model", Guid.NewGuid().ToString("N"),
            BundledModel.ModelFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(otherPath)!);
        File.Copy(BundledModel.ResolveModelPath(), otherPath);
        try
        {
            var config = await _store.StartModelMigrationAsync("local", otherPath, null,
                TestContext.Current.CancellationToken);

            config.Engine.ShouldBe($"local:{otherPath}");
            (await ReadRowStateAsync(entry.Hash)).ShouldBe("pending"); // still holds the OLD vector bytes, just excluded from vec0
            (await CountVecRowsAsync()).ShouldBe(0); // the outbox's own payoff: the stale vector left the searchable index at commit

            var migration = await ReadMigrationAsync();
            migration.ShouldNotBeNull();
            migration!.IsOpen.ShouldBeTrue();
            migration.Engine.ShouldBe($"local:{otherPath}");
        }
        finally
        {
            TestData.DeleteTempRoot(Path.GetDirectoryName(otherPath)!);
        }
    }

    [Fact]
    public async Task StartModelMigrationAsync_WhileAPreviousMigrationIsStillOpen_Refuses()
    {
        await SeedAnEmbeddedRowAndOpenAMigrationAsync();

        await Should.ThrowAsync<ModelMigrationInProgressException>(() =>
            _store.StartModelMigrationAsync("openai", "text-embedding-3-small", "https://example.invalid",
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StartModelMigrationAsync_WhileAPreviousMigrationIsStillOpen_LeavesTheOpenRecordUntouched()
    {
        await SeedAnEmbeddedRowAndOpenAMigrationAsync();
        var before = await ReadMigrationAsync();

        await Should.ThrowAsync<ModelMigrationInProgressException>(() =>
            _store.StartModelMigrationAsync("openai", "text-embedding-3-small", "https://example.invalid",
                TestContext.Current.CancellationToken));

        var after = await ReadMigrationAsync();
        after.ShouldBe(before);
    }

    [Fact]
    public async Task StartModelMigrationAsync_OnceThePreviousMigrationFinished_IsAllowedAgain()
    {
        await SeedAnEmbeddedRowAndOpenAMigrationAsync();
        await FinishMigrationAsync();

        var config = await _store.StartModelMigrationAsync("openai", "text-embedding-3-small",
            "https://example.invalid", TestContext.Current.CancellationToken);

        config.Provider.ShouldBe("openai");
        var migration = await ReadMigrationAsync();
        migration!.IsOpen.ShouldBeTrue();
    }

    private async Task SeedAnEmbeddedRowAndOpenAMigrationAsync()
    {
        await _store.StartModelMigrationAsync("local", null, null, TestContext.Current.CancellationToken);
        await _store.WriteAsync(new MemoryWriteRequest("acme", "seed row"), TestContext.Current.CancellationToken);

        var otherPath = Path.Combine(Path.GetTempPath(), "ai-raccoon-custom-model", Guid.NewGuid().ToString("N"),
            BundledModel.ModelFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(otherPath)!);
        File.Copy(BundledModel.ResolveModelPath(), otherPath);
        await _store.StartModelMigrationAsync("local", otherPath, null, TestContext.Current.CancellationToken);
    }

    private async Task FinishMigrationAsync()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE entries SET embed_state = 'embedded' WHERE embed_state = 'pending'",
            cancellationToken: TestContext.Current.CancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE model_migration SET finished_at = @now WHERE id = 1",
            new { now = FixedNow.ToUnixTimeSeconds() }, cancellationToken: TestContext.Current.CancellationToken));
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

    private sealed record MigrationRow(
        string Provider,
        string? Model,
        string? BaseUrl,
        string Engine,
        long StartedAt,
        long? FinishedAt);
}
