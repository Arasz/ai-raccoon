using System.Text;
using AiRaccoon.Core.Decisions;
using AiRaccoon.Core.Projects;
using AiRaccoon.Infrastructure.Decisions;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Decisions;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;

namespace AiRaccoon.Tests.Integration.Decisions;

/// <summary>Seeds a project graph that fills every decision table, and dumps tables for before/after equality.</summary>
internal static class DecisionGraphFixture
{
    public static readonly string[] GraphTables =
        ["decision_edges", "decision_imports", "decision_projects", "decision_requests", "decision_search",
         "decision_tasks", "decision_versions", "decisions"];

    /// <summary>Two tasks, an amended decision, a live and a removed edge, replay receipts and one import row.</summary>
    public static async Task SeedAsync(SqliteConnectionFactory factory, SqliteConnection connection, string projectId,
        CancellationToken ct)
    {
        var service = new DecisionService(new SqliteDecisionStore(factory, new SharedProjectScope()),
            new FakeTimeProvider(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero)));
        await service.RegisterTaskAsync(projectId, new DecisionWrite("first", "s", "register-first"), "First", cancellationToken: ct);
        var kept = await service.RecordAsync(projectId, new DecisionWrite("first", "s", "record-kept"),
            new DecisionContent($"{projectId} kept", "architecture", "s", "o", "r"), DecisionStatus.Accepted, ct);
        var other = await service.RecordAsync(projectId, new DecisionWrite("first", "s", "record-other"),
            new DecisionContent($"{projectId} other", "architecture", "s", "o", "r"), DecisionStatus.Proposed, ct);
        await service.RegisterTaskAsync(projectId, new DecisionWrite("second", "s", "register-second"), "Second", cancellationToken: ct);
        var amended = await service.AmendAsync(projectId, new DecisionWrite("second", "s", "amend"), kept.Value.DecisionId, 1,
            "clarify", new DecisionContent($"{projectId} kept v2", "architecture", "s", "o", "r2"), ct);
        var live = await service.LinkAsync(projectId, new DecisionWrite("second", "s", "link-live"), other.Value.DecisionId,
            kept.Value.DecisionId, DecisionEdgeKind.DependsOn, "needs", amended.ProjectRevision, ct);
        var removed = await service.LinkAsync(projectId, new DecisionWrite("second", "s", "link-removed"), kept.Value.DecisionId,
            other.Value.DecisionId, DecisionEdgeKind.Supports, "backs", live.ProjectRevision, ct);
        await service.UnlinkAsync(projectId, new DecisionWrite("second", "s", "unlink"), removed.Value.EdgeId, "obsolete",
            removed.ProjectRevision, ct);
        await connection.ExecuteAsync("""
            INSERT INTO decision_imports(project_id,source_namespace,external_id,decision_id,task_id,source_digest,raw_payload,mapping)
            VALUES(@projectId,'semantica','ext-1',@decisionId,'first','sha256:fixture','{"unknown":1}','{}')
            """, new { projectId, decisionId = kept.Value.DecisionId });
    }

    /// <summary>Every row of the graph tables, optionally limited to one project, in a stable order.</summary>
    public static async Task<string> DumpGraphAsync(SqliteConnection connection, string? projectId = null)
    {
        var dump = new StringBuilder();
        foreach (var table in GraphTables)
        {
            var filter = projectId is null ? "" : " WHERE project_id=@projectId";
            dump.Append(await DumpTableAsync(connection, table, filter, new { projectId }));
        }

        return dump.ToString();
    }

    /// <summary>The schema (every table, index and trigger), the AUTOINCREMENT counters and every row of every ordinary table, in a stable order.</summary>
    public static async Task<string> DumpBankAsync(SqliteConnection connection)
    {
        var tables = await connection.QueryAsync<string>(
            "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND sql NOT LIKE 'CREATE VIRTUAL%' ORDER BY name");
        var dump = new StringBuilder();
        dump.Append(await DumpQueryAsync(connection, "sqlite_master", "SELECT type,name,sql FROM sqlite_master ORDER BY name"));
        if (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM sqlite_master WHERE name='sqlite_sequence'") > 0)
        {
            dump.Append(await DumpQueryAsync(connection, "sqlite_sequence", "SELECT name,seq FROM sqlite_sequence ORDER BY name"));
        }

        foreach (var table in tables)
        {
            dump.Append(await DumpTableAsync(connection, table, "", null));
        }

        return dump.ToString();
    }

    public static Task<long> TotalChangesAsync(SqliteConnection connection) =>
        connection.ExecuteScalarAsync<long>("SELECT total_changes()");

    private static Task<string> DumpTableAsync(SqliteConnection connection, string table, string filter, object? args) =>
        DumpQueryAsync(connection, table, $"SELECT * FROM \"{table}\"{filter}", args);

    private static async Task<string> DumpQueryAsync(SqliteConnection connection, string label, string sql, object? args = null)
    {
        var rows = (await connection.QueryAsync(sql, args))
            .Select(row => string.Join("|", ((IDictionary<string, object?>)row)
                .Select(column => $"{column.Key}={Format(column.Value)}")))
            .Order(StringComparer.Ordinal);
        return $"[{label}]\n{string.Join("\n", rows)}\n";
    }

    private static string Format(object? value) => value switch
    {
        null => "NULL",
        byte[] bytes => Convert.ToHexString(bytes),
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? ""
    };
}
