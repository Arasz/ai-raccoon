using AiRaccoon.Core.Projects;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Projects;

/// <summary>
///     Air-merge P4 queue coherence: the research-record split (jsaa-157 + job-search-ai-assistant-89)
///     meets under the single canonical key when the repair folds the queue — full shape, not a
///     scaled-down pair, so a one-sided fixture that passes while dropping a side cannot hide here.
///     The conflict rule itself (same hash → max score / min created_at) stays P2's; this pins the
///     counts meeting plus the loser key's absence.
///     <para>
///         Honesty ledger (mutation : filter : fixture): skip-promotion_queue-rewrite :
///         --filter QueueFold_Meets157Plus89UnderTheWinner : 157 winner + 89 loser queued rows +
///         one same-hash collision pair.
///     </para>
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class QueueRenameTests : IDisposable
{
    private const string Winner = "jsaa";
    private const string Loser = "job-search-ai-assistant";

    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("queue-rename");
    private readonly SqliteConnectionFactory _factory;

    public QueueRenameTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [RetryFact]
    public async Task QueueFold_Meets157Plus89UnderTheWinner()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await _factory.OpenBankAsync(ct);
        for (var i = 0; i < 157; i++)
        {
            await QueueAsync(connection, Winner, $"q-w-{i:000}", 0.5, 10, 12, ct);
        }

        for (var i = 0; i < 89; i++)
        {
            await QueueAsync(connection, Loser, $"q-l-{i:000}", 0.5, 9, 11, ct);
        }

        await QueueAsync(connection, Winner, "q-share", 0.6, 10, 10, ct);
        await QueueAsync(connection, Loser, "q-share", 0.8, 8, 14, ct);

        var plan = ProjectIdsFoldPlan.FromCensus(
            await ProjectIdCensus.CollectAsync(connection, ct).ConfigureAwait(false),
            ProjectIdAliasMap.Default);
        await new ProjectIdsRepair(new FakeTimeProvider(FixedNow)).ApplyAsync(connection, plan, ct);

        (await CountAsync(connection, Winner, ct)).ShouldBe(157 + 89 + 1,
            "every queued row meets under the winner: 157 winner + 89 loser + the merged collision");
        (await CountAsync(connection, Loser, ct)).ShouldBe(0, "the loser key is absent after the fold");
        var merged = await connection.QueryFirstOrDefaultAsync<(double Score, long Created, long Updated)>(
            new CommandDefinition(
                "SELECT score AS Score, created_at AS Created, updated_at AS Updated FROM promotion_queue " +
                "WHERE project_id = @winner AND hash = 'q-share'",
                new { winner = Winner }, cancellationToken: ct));
        merged.Score.ShouldBe(0.8, "same hash keeps the max score");
        merged.Created.ShouldBe(8, "same hash keeps the min created_at");
        merged.Updated.ShouldBe(14, "same hash keeps the max updated_at");
    }

    private static async Task QueueAsync(Microsoft.Data.Sqlite.SqliteConnection connection,
        string projectId, string hash, double score, long created, long updated, CancellationToken ct) =>
        await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO promotion_queue (project_id, hash, value, score, created_at, updated_at) " +
                "VALUES (@projectId, @hash, @hash, @score, @created, @updated)",
                new { projectId, hash, score, created, updated }, cancellationToken: ct));

    private static async Task<long> CountAsync(Microsoft.Data.Sqlite.SqliteConnection connection,
        string projectId, CancellationToken ct) =>
        await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM promotion_queue WHERE project_id = @projectId",
            new { projectId }, cancellationToken: ct));
}
