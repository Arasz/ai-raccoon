using AiRaccoon.Core.EventPump;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
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
///     <para>
///         WP11-B2: <see cref="PendingEmbedJob.RunAsync" /> no longer embeds — it signals the embed
///         topic's single consumer (<see cref="EmbedDrainService" />). The rows-per-run bound and
///         the actual pending-&gt;embedded transition are that consumer's own tests now
///         (EmbedDrainServiceTests); this file keeps only what <see cref="PendingEmbedJob" /> itself
///         still owns: due-ness and the enqueue.
///     </para>
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class PendingEmbedJobTests : IDisposable
{
    private const string ProjectId = "acme";

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-pending-embed-job");
    private readonly SqliteConnectionFactory _factory;
    private readonly IModelMigrationLease _modelMigrationLease = Substitute.For<IModelMigrationLease>();
    private readonly TimeProvider _timeProvider = new FakeTimeProvider();

    public PendingEmbedJobTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    private PendingEmbedJob NewJob(IEventPump<EmbedDrainRequest>? pump = null) =>
        new(new EntryEmbedder(new CountingEmbeddingService(), _modelMigrationLease, _timeProvider),
            pump ?? TestData.NewEmbedDrainPump());

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

    /// <summary>E7: RunAsync signals the embed topic instead of embedding — rows stay pending and
    /// the drain consumer's own generator is never called from here.</summary>
    [Fact]
    public async Task RunAsync_EnqueuesInsteadOfEmbedding()
    {
        await ConfigureProviderAsync();
        await SeedPendingRowsAsync(3);
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var pump = TestData.NewEmbedDrainPump();
        var job = NewJob(pump);

        var createdWork = await job.RunAsync(connection, TestContext.Current.CancellationToken);

        createdWork.ShouldBeFalse();
        (await PendingCountAsync()).ShouldBe(3, "RunAsync must never embed itself — that is the drain consumer's job now");
        pump.EnqueuedCount.ShouldBe(1);
        var queued = pump.DrainUpTo(1).ShouldHaveSingleItem();
        queued.Corpus.ShouldBe(EmbedCorpus.Memory);
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
}
