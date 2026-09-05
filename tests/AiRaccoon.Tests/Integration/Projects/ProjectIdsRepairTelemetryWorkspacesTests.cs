using System.Text.Json;
using AiRaccoon.Core.Projects;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Projects;

/// <summary>
///     Package C gate (D3): telemetry follows the fold and workspaces never move. Each test seeds
///     its own scratch bank and drives the public <see cref="ProjectIdsRepair.ApplyAsync" /> with
///     a hand-built plan — no job, no CLI.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ProjectIdsRepairTelemetryWorkspacesTests
{
    private const string Winner = "jsaa";
    private const string Loser = "job-search-ai-assistant";

    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    ///     C1: metrics/noise rows re-key to the winner with the fold (trivial UPDATE — H4: no
    ///     triggers and no vec/FTS shadows on either table, so no invalidation leg exists).
    ///     Ledger — telemetry-ownership :
    ///     --filter Fold_RekeysMetricsAndNoiseRowsToWinner : loser metrics + noise beside one
    ///     folding committed row (a telemetry-ignoring applier strands them under the loser).
    /// </summary>
    [RetryFact]
    public async Task Fold_RekeysMetricsAndNoiseRowsToWinner()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await OpenAsync(ct);
        await CommittedAsync(connection, "move-labeled", Loser, ct);
        await MetricAsync(connection, Loser, ct);
        await MetricAsync(connection, Loser, ct);
        await NoiseAsync(connection, Loser, ct);

        var result = await ApplyAsync(connection, ct);

        (await CountAsync(connection, "SELECT count(*) FROM metrics WHERE project_id = @winner",
                ct, new { winner = Winner }))
            .ShouldBe(2, "both loser metrics rows re-key to the winner");
        (await CountAsync(connection, "SELECT count(*) FROM metrics WHERE project_id = @loser",
                ct, new { loser = Loser }))
            .ShouldBe(0, "no metrics row keeps the retired loser key");
        (await CountAsync(connection, "SELECT count(*) FROM noise_entries WHERE project_id = @winner",
                ct, new { winner = Winner }))
            .ShouldBe(1, "the loser noise row re-keys to the winner");
        (await CountAsync(connection, "SELECT count(*) FROM noise_entries WHERE project_id = @loser",
                ct, new { loser = Loser }))
            .ShouldBe(0, "no noise row keeps the retired loser key");
        result.MetricsMoved.ShouldBe(2);
        result.NoiseMoved.ShouldBe(1);

        var second = await ApplyAsync(connection, ct);
        second.TotalChanges.ShouldBe(0, "re-applying moves nothing — telemetry never re-plans");
    }

    /// <summary>
    ///     C2: workspaces and their scratch rows never move across projects — byte-identical
    ///     under the loser key after a fold that moves its committed rows.
    ///     Ledger — workspace-immovable :
    ///     --filter Fold_LeavesWorkspacesAndScratchByteIdentical : open workspace + scratch row
    ///     beside one folding committed row (a workspace-moving applier rewrites the rows).
    /// </summary>
    [RetryFact]
    public async Task Fold_LeavesWorkspacesAndScratchByteIdentical()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await OpenAsync(ct);
        await CommittedAsync(connection, "move-labeled", Loser, ct);
        await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO workspaces (id, project_id, status, created_at) VALUES ('ws-1', @loser, 'open', 1)",
                new { loser = Loser }, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO entries (hash, path, value, source_file, section, scope, project_id, context_label, workspace_id, created_at, updated_at, embed_state) " +
                "VALUES ('stay-ws', 'stay-ws', 'scratch', 'seed.md', 's', NULL, @loser, NULL, 'ws-1', 1, 1, 'pending')",
                new { loser = Loser }, cancellationToken: ct));
        var workspaceBefore = await DumpRowAsync(connection,
            "SELECT id, project_id, agent_id, name, status, created_at, closed_at FROM workspaces WHERE id = 'ws-1'", ct);
        var scratchBefore = await DumpRowAsync(connection,
            "SELECT hash, path, value, source_file, section, scope, project_id, context_label, workspace_id, created_at, updated_at FROM entries WHERE hash = 'stay-ws'", ct);

        var result = await ApplyAsync(connection, ct);

        result.EntriesMoved.ShouldBe(1, "arrange proof: the fold ran — the committed row moves");
        (await DumpRowAsync(connection,
                "SELECT id, project_id, agent_id, name, status, created_at, closed_at FROM workspaces WHERE id = 'ws-1'", ct))
            .ShouldBe(workspaceBefore, "the workspace row is byte-identical — it never moves across projects");
        (await DumpRowAsync(connection,
                "SELECT hash, path, value, source_file, section, scope, project_id, context_label, workspace_id, created_at, updated_at FROM entries WHERE hash = 'stay-ws'", ct))
            .ShouldBe(scratchBefore, "the scratch row is byte-identical under the loser's workspace key");
    }

    private static async Task<ProjectIdsRepairResult> ApplyAsync(SqliteConnection connection, CancellationToken ct)
    {
        var plan = new ProjectIdsFoldPlan([new ProjectIdFold(Loser, Winner)], [], [], []);
        return await new ProjectIdsRepair(new FakeTimeProvider(FixedNow)).ApplyAsync(connection, plan, ct);
    }

    private static async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        connection.EnableExtensions();
        connection.LoadVector();
        await MemorySchema.EnsureAsync(connection, ct);
        return connection;
    }

    private static async Task CommittedAsync(SqliteConnection connection, string hash, string projectId,
        CancellationToken ct) =>
        await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO entries (hash, path, value, source_file, section, scope, project_id, context_label, created_at, updated_at, embed_state) " +
                "VALUES (@hash, @hash, @hash, 'seed.md', 's', 'project', @projectId, 'ctx-a', 1, 1, 'pending')",
                new { hash, projectId }, cancellationToken: ct));

    private static async Task MetricAsync(SqliteConnection connection, string projectId, CancellationToken ct) =>
        await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO metrics (name, kind, value, unit, project_id, recorded_at) VALUES ('m', 'k', 1, 'u', @projectId, 1)",
                new { projectId }, cancellationToken: ct));

    private static async Task NoiseAsync(SqliteConnection connection, string projectId, CancellationToken ct) =>
        await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO noise_entries (request_content, project_id, detected_by_policy, expires_at, created_at) " +
                "VALUES ('junk', @projectId, 'p', 2, 1)",
                new { projectId }, cancellationToken: ct));

    private static async Task<string> DumpRowAsync(SqliteConnection connection, string sql, CancellationToken ct,
        object? param = null)
    {
        var rows = await connection.QueryAsync(new CommandDefinition(sql, param, cancellationToken: ct));
        var row = (IDictionary<string, object?>)rows.Single();
        return JsonSerializer.Serialize(new Dictionary<string, object?>(row, StringComparer.Ordinal));
    }

    private static async Task<long> CountAsync(SqliteConnection connection, string sql, CancellationToken ct,
        object? param = null) =>
        await connection.ExecuteScalarAsync<long>(new CommandDefinition(sql, param, cancellationToken: ct));
}
