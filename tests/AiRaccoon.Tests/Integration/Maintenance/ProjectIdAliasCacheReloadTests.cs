using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Projects;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.Unit.Projects;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Maintenance;

/// <summary>
///     Package E1's two reload legs, proven against the real cache: the repair job reloads the
///     choke-point cache after persisting the applied map (the same-process post-apply probe —
///     D6 iv's instrument), and the startup warm service loads the durable table into an empty
///     cache (a restart must not silently disarm P3 enforcement). Both join the Default
///     collection — the process-static map is the shared state under test.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
[Collection(ProjectIdAliasDefaultCollection.Name)]
public sealed class ProjectIdAliasCacheReloadTests : IDisposable
{
    private const string Loser = "job-search-ai-assistant";
    private const string Winner = "jsaa";

    private readonly string _dataRoot = TestData.CreateTempRoot("alias-cache-reload");
    private readonly InfrastructureOptions _options;

    public ProjectIdAliasCacheReloadTests()
    {
        _options = new InfrastructureOptions { DataRoot = _dataRoot, Scope = InstallScope.User };
    }

    [RetryFact]
    public async Task RunAsync_ReloadsTheDefaultCache_AfterPersistingTheAppliedMap()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await OpenMemoryBank(ct);
        await MemorySchema.EnsureAsync(connection, ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO entries (hash, path, value, source_file, section, scope, project_id, context_label, created_at, updated_at, embed_state) " +
            "VALUES (@hash, @hash, @hash, 'seed.md', 's', 'project', @loser, 'ctx', @now, @now, 'pending')",
            new { hash = "reload-1", loser = Loser, now = FixedNow.ToUnixTimeSeconds() },
            cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            MemorySql.RequestRepair,
            new { kind = RepairKinds.ProjectIds, requestedAt = FixedNow.ToUnixTimeSeconds(), mapJson = MapJson() },
            cancellationToken: ct));

        var job = new ProjectIdsRepairJob(
            new FileTypeMatcher([new MarkdownFileTypeHandler(new StubChunker())]),
            TestData.CreateEmbeddingService(),
            new FakeTimeProvider(FixedNow));

        ProjectIdAliasMap.Default.Fold(Loser).ShouldBe(Loser,
            "arrange proof: the process-static cache starts at the empty steady state");

        (await job.RunAsync(connection, ct)).ShouldBeTrue("the open request schedules the fold");

        ProjectIdAliasMap.Default.Fold(Loser).ShouldBe(Winner,
            "the job's reload arms the choke points in the same process, before any restart");
        ProjectIdAliasMap.Default.IsDropped("dropped-id").ShouldBeTrue("the applied drop refuses writes");
    }

    [RetryFact]
    public async Task Start_WarmsTheCache_FromTheDurableTable()
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = new SqliteConnectionFactory(_options, NullKeyProvider.Resolver(_options));
        await using (var connection = await factory.OpenBankAsync(ct))
        {
            await ProjectIdAliases.PersistAppliedAsync(
                connection,
                new ProjectIdAliasMap([new ProjectIdAliasEntry(Loser, Winner)], [], ["dropped-id"]),
                FixedNow.ToUnixTimeSeconds(), ct);
        }

        // A restart resets the process-static cache to the empty steady state — the exact
        // state a real server process boots with.
        ProjectIdAliasMap.ResetDefault();

        var warm = new ProjectIdAliasCacheHostedService(factory, TestTelemetry.None, NullLogger<ProjectIdAliasCacheHostedService>.Instance);
        await warm.StartAsync(ct);

        ProjectIdAliasMap.Default.Fold(Loser).ShouldBe(Winner,
            "startup warm loads the durable map — P3 enforcement survives a restart");
        ProjectIdAliasMap.Default.IsDropped("dropped-id").ShouldBeTrue();
    }

    public void Dispose()
    {
        ProjectIdAliasMap.ResetDefault();
        TestData.DeleteTempRoot(_dataRoot);
    }

    private static async Task<SqliteConnection> OpenMemoryBank(CancellationToken ct)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        connection.EnableExtensions();
        connection.LoadVector();
        return connection;
    }

    private static string MapJson() =>
        new ProjectIdAliasMap(
            [new ProjectIdAliasEntry(Loser, Winner)],
            [Winner],
            ["dropped-id"]).ToJson();

    private static readonly DateTimeOffset FixedNow = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
}
