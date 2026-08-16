using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

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
    private SqliteMemoryStore _store = null!;
    private FakeTimeProvider _time = null!;
    private string _otherModelPath = null!;

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

    private ModelMigrationJob NewJob() =>
        new(new EntryEmbedder(TestData.CreateEmbeddingService(), new SqliteModelMigrationLease(_time)), _time);

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
}
