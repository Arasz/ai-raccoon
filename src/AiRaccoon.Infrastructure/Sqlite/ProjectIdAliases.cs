using AiRaccoon.Core.Projects;
using CommunityToolkit.Diagnostics;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     The durable project-ids alias map (Package D, D4 storage): the repair job appends the
///     applied one-shot map here on success; rows are immutable thereafter (alias-PK
///     first-writer-wins). Choke points (Package E) read this back through
///     <see cref="LoadAsync" /> instead of a per-invocation file.
///     <para>
///         Ordinal semantics: the <c>alias</c> PK uses SQLite's default BINARY collation —
///         case-sensitive, matching the map's Ordinal lookups for the ASCII id alphabet
///         (guids, slugs). <c>OLD-ID</c> and <c>old-id</c> are distinct rows by decision,
///         same as <see cref="ProjectIdAliasMap" />.
///     </para>
///     <para>
///         Sync rule (H2 verdict: <c>SyncService.MergeRemoteAsync</c> is per-table row-merge over
///         an ATTACHed snapshot — a table with no merge arm rides in the snapshot file but never
///         merges, so this table does NOT ride free): rows are insert-only; pull merges with
///         <c>INSERT OR IGNORE</c> on the alias PK (first writer wins); a same-alias-different-winner
///         row is a genuine conflict and must surface as <c>unresolved</c> for a human — the merge
///         arm (Package E2) implements that surfacing, never a silent overwrite. This table is
///         deliberately NOT in <c>SyncService.MachineLocalTables</c>: unlike
///         <c>repair_requests</c> (a machine-local outbox), the map is bank content every replica
///         must converge on.
///     </para>
/// </summary>
public static class ProjectIdAliases
{
    /// <summary>Alias-map row kinds: a loser id folding to its winner, or a dropped id deleted with a tombstone.</summary>
    public const string KindAlias = "alias";

    /// <summary>Dropped-id row kind (winner is NULL — a dropped id folds nowhere).</summary>
    public const string KindDrop = "drop";

    /// <summary>
    ///     The <c>project_id_aliases</c> shape, declared once: the digest-gated
    ///     <see cref="MemorySchema.Ddl" /> block interpolates it for fresh banks, and the v14 ladder
    ///     step executes it for v13-stamped banks — one definition, two call sites, never a copy.
    /// </summary>
    internal const string TableDdl = """
        CREATE TABLE IF NOT EXISTS project_id_aliases (
            alias      TEXT PRIMARY KEY,
            winner     TEXT NULL,
            kind       TEXT NOT NULL CHECK(kind IN ('alias','drop')),
            applied_at INTEGER NOT NULL
        )
        """;

    /// <summary>
    ///     Appends the applied one-shot map: one row per alias entry plus one row per dropped id.
    ///     <c>INSERT OR IGNORE</c> keeps rows immutable — a re-apply (or a racing replica's pull)
    ///     never overwrites the first writer. Canonicals are intentionally not persisted: an unknown
    ///     id already degrades to guid D-form normalization in <see cref="ProjectIdAliasMap.Fold" />.
    /// </summary>
    public static async Task PersistAppliedAsync(
        SqliteConnection connection, ProjectIdAliasMap map, long appliedAt, CancellationToken cancellationToken)
    {
        Guard.IsNotNull(connection);
        Guard.IsNotNull(map);
        foreach (var entry in map.Aliases)
        {
            await connection.ExecuteAsync(new CommandDefinition(MemorySql.InsertProjectIdAlias,
                    new { alias = entry.Alias, winner = (string?)entry.Canonical, kind = KindAlias, appliedAt },
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }

        foreach (var dropped in map.Dropped)
        {
            await connection.ExecuteAsync(new CommandDefinition(MemorySql.InsertProjectIdAlias,
                    new { alias = dropped, winner = (string?)null, kind = KindDrop, appliedAt },
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Reads the durable map back: <c>alias</c> rows become alias entries, <c>drop</c> rows become
    ///     the dropped set. A direct-SQL <c>alias</c> row with a NULL winner is skipped — a null
    ///     winner would fold an id to null downstream (the same refusal
    ///     <see cref="ProjectIdAliasMap.FromJson" /> applies to stored maps).
    /// </summary>
    public static async Task<ProjectIdAliasMap> LoadAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        Guard.IsNotNull(connection);
        var rows = await connection.QueryAsync<(string Alias, string? Winner, string Kind)>(
                new CommandDefinition(MemorySql.SelectProjectIdAliases, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        var aliases = rows
            .Where(row => row.Kind == KindAlias && row.Winner is not null)
            .Select(row => new ProjectIdAliasEntry(row.Alias, row.Winner!))
            .ToList();
        var dropped = rows
            .Where(row => row.Kind == KindDrop)
            .Select(row => row.Alias)
            .ToList();
        return new ProjectIdAliasMap(aliases, [], dropped);
    }
}
