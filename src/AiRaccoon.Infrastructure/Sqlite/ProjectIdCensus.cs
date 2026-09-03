using CommunityToolkit.Diagnostics;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>One id's share of every id-keyed bank surface (air-merge P1 census).</summary>
public sealed record ProjectIdCensusRow(
    string ProjectId,
    bool Registered,
    string? RegisteredName,
    long ProjectEntries,
    long CustomEntries,
    long SharedEntries,
    long WorkspaceEntries,
    long NullContextEntries,
    long CodeEntries,
    long CodeFtsRows,
    long VecCodeRows,
    long EntriesFtsRows,
    long VecEntryRows,
    long VecStructureRows,
    long Queued,
    long Discards,
    long QualityRows,
    long Watches,
    long WatchFiles,
    long DigestClaims,
    long Tombstones,
    long Workspaces,
    long MetricsRows,
    long NoiseRows,
    IReadOnlyList<string> SettingsKeys)
{
    /// <summary>Committed entries rows (project + custom + shared scope).</summary>
    public long EntryTotal => ProjectEntries + CustomEntries + SharedEntries;

    /// <summary>Owns entries but has no projects row — the research record's orphan shape.</summary>
    public bool Orphan => !Registered && EntryTotal > 0;

    /// <summary>Non-entry attachments that must be reviewed before any retire/delete decision.</summary>
    public long AttachmentCount =>
        Queued + Discards + QualityRows + Watches + WatchFiles + DigestClaims +
        Tombstones + Workspaces + WorkspaceEntries + CodeEntries + MetricsRows + NoiseRows + SettingsKeys.Count;
}

/// <summary>Bank-wide census report: one row per id found on any id-keyed surface, plus NULL counters.</summary>
public sealed record ProjectIdCensusReport(
    IReadOnlyList<ProjectIdCensusRow> Rows,
    long NullScopeEntries,
    long NullContextEntries,
    long NullProjectEntries,
    long NullQualityRows,
    IReadOnlyList<string> UnattributedSettingsKeys)
{
    /// <summary>Rows owning no entries — retire/delete candidates with their attachments listed on the row.</summary>
    public IReadOnlyList<ProjectIdCensusRow> ZeroEntryRows => Rows.Where(r => r.EntryTotal == 0).ToList();

    /// <summary>Throws when the id owns nothing on any surface.</summary>
    public ProjectIdCensusRow Row(string projectId)
    {
        Guard.IsNotNullOrWhiteSpace(projectId);
        return Rows.Single(r => r.ProjectId == projectId);
    }
}

/// <summary>Read-only orphan/fragment census: SELECT-only over every id-keyed surface, grouped per id (air-merge P1).</summary>
public static class ProjectIdCensus
{
    /// <summary>Collects the census with SELECT statements only — safe under PRAGMA query_only.</summary>
    public static async Task<ProjectIdCensusReport> CollectAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        Guard.IsNotNull(connection);
        var builders = new Dictionary<string, RowBuilder>(StringComparer.Ordinal);

        RowBuilder For(string? id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return RowBuilder.Null;
            }

            if (!builders.TryGetValue(id, out var builder))
            {
                builder = new RowBuilder(id);
                builders[id] = builder;
            }

            return builder;
        }

        var entryGroups = await connection.QueryAsync<EntryGroup>(new CommandDefinition(
            "SELECT project_id AS Id, scope AS Scope, COUNT(*) AS N, " +
            "SUM(CASE WHEN context_label IS NULL THEN 1 ELSE 0 END) AS NullCtx " +
            "FROM entries WHERE project_id IS NOT NULL GROUP BY project_id, scope",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        foreach (var group in entryGroups)
        {
            var row = For(group.Id);
            row.NullContextEntries += group.NullCtx;
            switch (group.Scope)
            {
                case "project": row.ProjectEntries += group.N; break;
                case "custom": row.CustomEntries += group.N; break;
                case "shared": row.SharedEntries += group.N; break;
                default: row.WorkspaceEntries += group.N; break;
            }
        }

        await CountByIdAsync(connection, "SELECT project_id AS Id, COUNT(*) AS N FROM code_entries GROUP BY project_id",
            (row, n) => row.CodeEntries += n, cancellationToken).ConfigureAwait(false);
        await CountByIdAsync(connection,
            "SELECT ce.project_id AS Id, COUNT(*) AS N FROM code_fts f JOIN code_entries ce ON f.rowid = ce.id GROUP BY ce.project_id",
            (row, n) => row.CodeFtsRows += n, cancellationToken).ConfigureAwait(false);
        await CountByIdAsync(connection, "SELECT ctx AS Id, COUNT(*) AS N FROM vec_code GROUP BY ctx",
            (row, n) => row.VecCodeRows += n, cancellationToken).ConfigureAwait(false);
        await CountByIdAsync(connection,
            "SELECT e.project_id AS Id, COUNT(*) AS N FROM entries_fts f JOIN entries e ON f.rowid = e.id " +
            "WHERE e.project_id IS NOT NULL GROUP BY e.project_id",
            (row, n) => row.EntriesFtsRows += n, cancellationToken).ConfigureAwait(false);
        await CountByIdAsync(connection,
            "SELECT e.project_id AS Id, COUNT(*) AS N FROM vec_entries v JOIN entries e ON v.rowid = e.id " +
            "WHERE e.project_id IS NOT NULL GROUP BY e.project_id",
            (row, n) => row.VecEntryRows += n, cancellationToken).ConfigureAwait(false);
        await CountByIdAsync(connection,
            "SELECT e.project_id AS Id, COUNT(*) AS N FROM vec_structure v JOIN entries e ON v.rowid = e.id " +
            "WHERE e.project_id IS NOT NULL GROUP BY e.project_id",
            (row, n) => row.VecStructureRows += n, cancellationToken).ConfigureAwait(false);
        await CountByIdAsync(connection, "SELECT project_id AS Id, COUNT(*) AS N FROM promotion_queue GROUP BY project_id",
            (row, n) => row.Queued += n, cancellationToken).ConfigureAwait(false);
        await CountByIdAsync(connection, "SELECT project_id AS Id, COUNT(*) AS N FROM promotion_discards GROUP BY project_id",
            (row, n) => row.Discards += n, cancellationToken).ConfigureAwait(false);
        await CountByIdAsync(connection,
            "SELECT project_id AS Id, COUNT(*) AS N FROM search_quality WHERE project_id IS NOT NULL GROUP BY project_id",
            (row, n) => row.QualityRows += n, cancellationToken).ConfigureAwait(false);
        await CountByIdAsync(connection, "SELECT project_id AS Id, COUNT(*) AS N FROM watches GROUP BY project_id",
            (row, n) => row.Watches += n, cancellationToken).ConfigureAwait(false);
        await CountByIdAsync(connection, "SELECT project_id AS Id, COUNT(*) AS N FROM watch_files GROUP BY project_id",
            (row, n) => row.WatchFiles += n, cancellationToken).ConfigureAwait(false);
        await CountByIdAsync(connection, "SELECT project_id AS Id, COUNT(*) AS N FROM watch_digest_claims GROUP BY project_id",
            (row, n) => row.DigestClaims += n, cancellationToken).ConfigureAwait(false);
        await CountByIdAsync(connection, "SELECT project_id AS Id, COUNT(*) AS N FROM sync_tombstones GROUP BY project_id",
            (row, n) => row.Tombstones += n, cancellationToken).ConfigureAwait(false);
        await CountByIdAsync(connection, "SELECT project_id AS Id, COUNT(*) AS N FROM workspaces GROUP BY project_id",
            (row, n) => row.Workspaces += n, cancellationToken).ConfigureAwait(false);
        await CountByIdAsync(connection,
            "SELECT project_id AS Id, COUNT(*) AS N FROM metrics WHERE project_id IS NOT NULL GROUP BY project_id",
            (row, n) => row.MetricsRows += n, cancellationToken).ConfigureAwait(false);
        await CountByIdAsync(connection, "SELECT project_id AS Id, COUNT(*) AS N FROM noise_entries GROUP BY project_id",
            (row, n) => row.NoiseRows += n, cancellationToken).ConfigureAwait(false);

        var registered = await connection.QueryAsync<ProjectRow>(new CommandDefinition(
            "SELECT id AS Id, name AS Name FROM projects", cancellationToken: cancellationToken)).ConfigureAwait(false);
        foreach (var project in registered)
        {
            var row = For(project.Id);
            row.Registered = true;
            row.RegisteredName = project.Name;
        }

        var unattributed = new List<string>();
        var keys = await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT key FROM settings", cancellationToken: cancellationToken)).ConfigureAwait(false);
        foreach (var key in keys)
        {
            if (TryAttributeSetting(key, out var owner))
            {
                For(owner).SettingsKeys.Add(key);
            }
            else
            {
                unattributed.Add(key);
            }
        }

        unattributed.Sort(StringComparer.Ordinal);
        var rows = builders.Values
            .Select(b => b.Build())
            .OrderBy(r => r.ProjectId, StringComparer.Ordinal)
            .ToList();

        return new ProjectIdCensusReport(rows,
            await ScalarAsync(connection, "SELECT COUNT(*) FROM entries WHERE scope IS NULL", cancellationToken).ConfigureAwait(false),
            await ScalarAsync(connection, "SELECT COUNT(*) FROM entries WHERE context_label IS NULL", cancellationToken).ConfigureAwait(false),
            await ScalarAsync(connection, "SELECT COUNT(*) FROM entries WHERE project_id IS NULL", cancellationToken).ConfigureAwait(false),
            await ScalarAsync(connection, "SELECT COUNT(*) FROM search_quality WHERE project_id IS NULL", cancellationToken).ConfigureAwait(false),
            unattributed);

        async Task CountByIdAsync(SqliteConnection conn, string sql, Action<RowBuilder, long> add, CancellationToken ct)
        {
            var groups = await conn.QueryAsync<IdCount>(new CommandDefinition(sql, cancellationToken: ct)).ConfigureAwait(false);
            foreach (var group in groups)
            {
                add(For(group.Id), group.N);
            }
        }

        static async Task<long> ScalarAsync(SqliteConnection conn, string sql, CancellationToken ct) =>
            await conn.ExecuteScalarAsync<long>(new CommandDefinition(sql, cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <summary>Attributes an id-embedding settings key to its owner; false for global keys.</summary>
    internal static bool TryAttributeSetting(string key, out string? owner)
    {
        if (TryStripPrefix(key, "ingest.scope.", out owner) && owner != "global")
        {
            return true;
        }

        if (TryStripPrefix(key, "watch.scope.", out owner))
        {
            return true;
        }

        if (TryStripPrefix(key, "watch.enabled.", out owner) && owner != "global")
        {
            return true;
        }

        if (TryStripPrefix(key, "watch.concurrency.", out owner) && owner != "global")
        {
            return true;
        }

        if (TryStripPrefix(key, "access.mode.project:", out owner))
        {
            return true;
        }

        owner = null;
        return false;
    }

    private static bool TryStripPrefix(string key, string prefix, out string? rest)
    {
        if (key.StartsWith(prefix, StringComparison.Ordinal) && key.Length > prefix.Length)
        {
            rest = key[prefix.Length..];
            return true;
        }

        rest = null;
        return false;
    }

    private sealed class EntryGroup
    {
        public string Id { get; set; } = "";
        public string? Scope { get; set; }
        public long N { get; set; }
        public long NullCtx { get; set; }
    }

    private sealed class IdCount
    {
        public string Id { get; set; } = "";
        public long N { get; set; }
    }

    private sealed class ProjectRow
    {
        public string Id { get; set; } = "";
        public string? Name { get; set; }
    }

    private sealed class RowBuilder(string id)
    {
        public static readonly RowBuilder Null = new("");

        public long ProjectEntries;
        public long CustomEntries;
        public long SharedEntries;
        public long WorkspaceEntries;
        public long NullContextEntries;
        public bool Registered;
        public string? RegisteredName;
        public readonly List<string> SettingsKeys = [];

        public ProjectIdCensusRow Build() => new(
            id, Registered, RegisteredName,
            ProjectEntries, CustomEntries, SharedEntries, WorkspaceEntries, NullContextEntries,
            CodeEntries, CodeFtsRows, VecCodeRows, EntriesFtsRows, VecEntryRows, VecStructureRows,
            Queued, Discards, QualityRows, Watches, WatchFiles, DigestClaims,
            Tombstones, Workspaces, MetricsRows, NoiseRows,
            SettingsKeys.Order(StringComparer.Ordinal).ToList());

        public long CodeEntries;
        public long CodeFtsRows;
        public long VecCodeRows;
        public long EntriesFtsRows;
        public long VecEntryRows;
        public long VecStructureRows;
        public long Queued;
        public long Discards;
        public long QualityRows;
        public long Watches;
        public long WatchFiles;
        public long DigestClaims;
        public long Tombstones;
        public long Workspaces;
        public long MetricsRows;
        public long NoiseRows;
    }
}
