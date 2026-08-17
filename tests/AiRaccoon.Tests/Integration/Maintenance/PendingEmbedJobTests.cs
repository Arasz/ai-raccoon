using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Maintenance;

/// <summary>
///     PendingEmbedJob (.NET-F1) is the on-demand relay for `entries.embed_state = 'pending'`
///     rows left by a write, a watch digest, or a repair (e.g. `repair reingest --apply`) —
///     modelled directly on <see cref="ModelMigrationJob" />: <see cref="PendingEmbedJob.HasWorkAsync" />
///     is the only gate, so it is picked up by the maintenance loop's startup pass and every 15s
///     on-demand poll after that (<see cref="BankMaintenanceHostedService.OnDemandPollInterval" />),
///     never waiting for the heavy pass's own (default 60-minute) cadence.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class PendingEmbedJobTests : IDisposable
{
    private const string ProjectId = "acme";

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-pending-embed-job");
    private readonly SqliteConnectionFactory _factory;

    public PendingEmbedJobTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    private static PendingEmbedJob NewJob() => new(new EntryEmbedder(new CountingEmbeddingService()));

    /// <summary>
    ///     Mutation-check this one (project instruction: a check never seen to fail is not a check):
    ///     delete the provider guard in PendingEmbedJob.HasWorkAsync and this goes red, because a
    ///     pending row now exists and nothing else would stop HasWorkAsync from reporting it.
    /// </summary>
    [Fact]
    public async Task HasWorkAsync_NoProviderConfigured_WithPendingRows_IsFalse()
    {
        await SeedPendingRowsAsync(1);
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);

        (await NewJob().HasWorkAsync(connection, TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task HasWorkAsync_ProviderConfigured_NoPendingRows_IsFalse()
    {
        await ConfigureProviderAsync();
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);

        (await NewJob().HasWorkAsync(connection, TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task HasWorkAsync_ProviderConfigured_WithPendingRows_IsTrue()
    {
        await ConfigureProviderAsync();
        await SeedPendingRowsAsync(1);
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);

        (await NewJob().HasWorkAsync(connection, TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    [Fact]
    public async Task RunAsync_EmbedsPendingRows_HasWorkAsyncThenFalse()
    {
        await ConfigureProviderAsync();
        await SeedPendingRowsAsync(3);
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var job = NewJob();

        await job.RunAsync(connection, TestContext.Current.CancellationToken);

        (await PendingCountAsync()).ShouldBe(0);
        (await job.HasWorkAsync(connection, TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    /// <summary>
    ///     The per-run bound (RowsPerRun = 4 * EntryEmbedder.BatchSize = 128): a run leaves the
    ///     remainder pending rather than draining a whole backlog in one on-demand poll, and
    ///     HasWorkAsync stays true so the runner brings the job back next poll for what is left.
    /// </summary>
    [Fact]
    public async Task RunAsync_MoreRowsThanTheBound_LeavesTheRemainderPending_AndHasWorkAsyncStaysTrue()
    {
        const int rowsPerRun = 4 * EntryEmbedder.BatchSize;
        await ConfigureProviderAsync();
        await SeedPendingRowsAsync(rowsPerRun + 2);
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var job = NewJob();

        await job.RunAsync(connection, TestContext.Current.CancellationToken);

        (await PendingCountAsync()).ShouldBe(2);
        (await job.HasWorkAsync(connection, TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    /// <summary>Never anything but a fill: the job only ever writes embed_state/embedding/heading columns on rows already committed by something else.</summary>
    [Fact]
    public async Task RunAsync_NeverChangesRowCount()
    {
        await ConfigureProviderAsync();
        await SeedPendingRowsAsync(3);
        var before = await RowCountAsync();
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);

        await NewJob().RunAsync(connection, TestContext.Current.CancellationToken);

        (await RowCountAsync()).ShouldBe(before);
    }

    private async Task SeedPendingRowsAsync(int count)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        for (var i = 0; i < count; i++)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at)
                VALUES (@hash, @path, @value, 'project', @projectId, 0, 0)
                """,
                new
                {
                    hash = $"{Guid.NewGuid():N}-{i}", path = $"p{i}.md", value = $"pending row {i}",
                    projectId = ProjectId
                },
                cancellationToken: TestContext.Current.CancellationToken));
        }
    }

    private async Task ConfigureProviderAsync()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT OR REPLACE INTO settings (key, value) VALUES (@key, @value)",
            new { key = EmbeddingSettingsKeys.Provider, value = "local" },
            cancellationToken: TestContext.Current.CancellationToken));
    }

    private async Task<long> PendingCountAsync()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM entries WHERE embed_state = 'pending'",
            cancellationToken: TestContext.Current.CancellationToken));
    }

    private async Task<long> RowCountAsync()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM entries", cancellationToken: TestContext.Current.CancellationToken));
    }
}
