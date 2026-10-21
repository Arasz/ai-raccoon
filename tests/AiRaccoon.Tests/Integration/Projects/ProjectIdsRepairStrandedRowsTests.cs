using AiRaccoon.Core.Projects;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Projects;

/// <summary>
///     The reported defect, end to end: an alias id whose workspace rows have all closed held its
///     committed rows hostage, because the census counted workspace ROWS and the repair pinned any
///     id that owned one. The rows were in the bank and unreachable — get_entry folds the caller's
///     id to the canonical one, so a row keyed under the alias matched nothing.
///     Ledger — stranded-rows-unblocked : --filter StrandedRows_UnderAClosedWorkspaceAlias_FoldToTheCanonicalId :
///     alias loser, 25 project + 65 custom + 8 shared rows, 14 closed workspace rows.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ProjectIdsRepairStrandedRowsTests : IDisposable
{
    private const string Winner = "jsaa";
    private const string Loser = "job-search-ai-assistant";

    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("project-ids-stranded-rows");
    private readonly SqliteConnectionFactory _factory;

    public ProjectIdsRepairStrandedRowsTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [RetryFact]
    public async Task StrandedRows_UnderAClosedWorkspaceAlias_FoldToTheCanonicalId()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await _factory.OpenBankAsync(ct);
        await EntryAsync(connection, "stranded-project", "project", Loser, null, "stranded project row", ct);
        await EntryAsync(connection, "stranded-custom", "custom", Loser, "ctx-a", "stranded custom row", ct);
        await EntryAsync(connection, "shared-row", "shared", Loser, null, "cross-project shared row", ct);
        await WorkspaceAsync(connection, "ws-closed-1", Loser, "Closed", ct);
        await WorkspaceAsync(connection, "ws-closed-2", Loser, "Discarded", ct);

        // The plan the repair derives: a live census plus the operator's alias map.
        var census = await ProjectIdCensus.CollectAsync(connection, ct);
        var plan = ProjectIdsFoldPlan.FromCensus(census, FixtureMap());

        plan.Folds.ShouldBe([new ProjectIdFold(Loser, Winner)],
            "closed workspaces hold no live scratch — the alias id folds instead of pinning");
        plan.Pinned.ShouldBeEmpty();

        var result = await new ProjectIdsRepair(new FakeTimeProvider(FixedNow), TestData.SharedScope).ApplyAsync(connection, plan, ct);

        result.EntriesMoved.ShouldBe(2, "the project-scope and custom-scope rows move to the canonical id");
        (await CountAsync(connection,
                "SELECT count(*) FROM entries WHERE project_id = @winner AND scope IN ('project','custom')",
                ct, new { winner = Winner }))
            .ShouldBe(2, "the stranded rows now live under the canonical id");
        (await CountAsync(connection,
                "SELECT count(*) FROM entries WHERE project_id = @loser AND scope IN ('project','custom')",
                ct, new { loser = Loser }))
            .ShouldBe(0, "no committed row stays under the alias id");
        (await CountAsync(connection,
                "SELECT count(*) FROM entries WHERE project_id = @loser AND scope = 'shared'",
                ct, new { loser = Loser }))
            .ShouldBe(1, "shared rows are cross-project content — the repair never folds them");
        (await CountAsync(connection,
                "SELECT count(*) FROM workspaces WHERE project_id = @loser",
                ct, new { loser = Loser }))
            .ShouldBe(2, "workspace rows never move across projects");

        // The read path the tool runs: hash + the caller's (folded) project id.
        (await ReadAsync(connection, "stranded-project", Winner, ct))
            .ShouldBe(1, "get_entry under the canonical id finds the formerly stranded row");
        (await ReadAsync(connection, "stranded-custom", Winner, ct))
            .ShouldBe(1, "the custom-scope row is reachable too");
    }

    private static ProjectIdAliasMap FixtureMap() => new(
        [new ProjectIdAliasEntry(Loser, Winner)], [Winner], []);

    /// <summary>Runs the statement <c>get_entry</c> runs, so the reachability claim is the product's own.</summary>
    private static async Task<long> ReadAsync(SqliteConnection connection, string hash, string projectId,
        CancellationToken ct) =>
        await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT count(*) FROM ({MemorySql.SelectEntryByHashForRead})",
            new { hash, projectId }, cancellationToken: ct));

    private static async Task EntryAsync(SqliteConnection connection, string hash, string scope, string projectId,
        string? label, string value, CancellationToken ct) =>
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO entries (hash, path, value, source_file, section, scope, project_id, context_label, created_at, updated_at, embed_state) " +
            "VALUES (@hash, @hash, @value, 'seed.md', 's', @scope, @projectId, @label, 1, 1, 'pending')",
            new { hash, scope, projectId, label, value }, cancellationToken: ct));

    private static async Task WorkspaceAsync(SqliteConnection connection, string id, string projectId, string status,
        CancellationToken ct) =>
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO workspaces (id, project_id, status, created_at) VALUES (@id, @projectId, @status, 1)",
            new { id, projectId, status }, cancellationToken: ct));

    private static async Task<long> CountAsync(SqliteConnection connection, string sql, CancellationToken ct,
        object? param = null) =>
        await connection.ExecuteScalarAsync<long>(new CommandDefinition(sql, param, cancellationToken: ct));
}
