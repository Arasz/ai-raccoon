using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Watch;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Ingestion;

/// <summary>
///     GH #371 follow-up: fixes what <see cref="ChunkIndexRepair" /> can only mark chunk_index = -1
///     for — a source file whose stored rows a chunker-version change made unreproducible by hash —
///     by re-ingesting it through <see cref="IMemoryStore.ReplaceAsync" />'s unconditional
///     delete-and-reingest, the same atomic replace the watch digest uses, forced rather than
///     fingerprint-gated (the file's content has not changed; the chunker has).
///     <para>
///         Scoped to what a replace can actually touch: mirror rows only (path = source_file, per
///         <see cref="MemorySql.DeleteBySourcePath" />'s own predicate) whose file still exists.
///         A <c>memory_write</c> row that merely cites the path, and any row whose source_file no
///         longer exists, are never candidates and never touched.
///     </para>
///     <para>
///         Discards per-row metadata (rating/access_count/last_accessed_at) for every row it
///         replaces — a re-chunk moves boundaries, so hashes change and there is no 1:1 row to carry
///         them onto. Leaves embedding pending; it never embeds inline — it signals the embed
///         topic's single consumer (<see cref="AiRaccoon.Infrastructure.Embedding.EmbedDrainService" />),
///         which a running server drains on-demand within its next ~15s poll
///         (<see cref="AiRaccoon.Infrastructure.Maintenance.PendingEmbedJob" />). ADR-0075's amendment
///         means <c>apply: true</c> is now only ever reached inside the server process
///         (<see cref="AiRaccoon.Infrastructure.Maintenance.ReingestRepairJob" />, itself gated on a
///         repair_requests row `ai-raccoon repair reingest --apply` commits through the server) — so
///         there is no longer a "no server running" path for this repair to leave undrained.
///     </para>
///     <para>
///         This class itself never implements <see cref="AiRaccoon.Infrastructure.Maintenance.IMaintenanceJob" />
///         — <see cref="AiRaccoon.Infrastructure.Maintenance.ReingestRepairJob" /> wraps it instead,
///         registered on the maintenance list but on-demand only (<c>Interval</c> is null): it only
///         ever runs because a human explicitly requested it via <c>--apply</c>, never on a clock, so
///         it still cannot run unattended against a live bank on open — see
///         <c>ReingestRepairDoesNotAutoStartTests</c> for what that guard actually asserts now.
///     </para>
/// </summary>
public sealed class ReingestRepair(ChunkPositionScanner scanner)
{
    public async Task<ReingestRepairReport> RunAsync(SqliteConnection connection, IMemoryStore store, bool apply,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(store);

        var (maxTokens, overlayTokens, countTokens) = await scanner.BudgetAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        // workspace_id IS NULL and path = source_file: exactly the rows ReplaceAsync's own
        // DeleteBySourcePath predicate would touch — the digest owns mirror rows, never workspace
        // scratch and never a memory_write row that merely cites the path (MemorySql.cs:188-190).
        var groups = (await connection.QueryAsync<GroupKey>(new CommandDefinition(
                """
                SELECT DISTINCT project_id AS ProjectId, source_file AS SourceFile
                FROM entries
                WHERE source_file IS NOT NULL AND workspace_id IS NULL AND path = source_file
                """, cancellationToken: cancellationToken))
            .ConfigureAwait(false)).ToList();

        var filesToReingest = 0;
        var rowsAffected = 0;
        var chunksToEmbed = 0;

        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var rows = (await connection.QueryAsync<GroupRow>(new CommandDefinition(
                    """
                    SELECT id AS Id, hash AS Hash
                    FROM entries
                    WHERE project_id = @projectId AND source_file = @sourceFile AND path = @sourceFile
                      AND workspace_id IS NULL
                    """, new { projectId = group.ProjectId, sourceFile = group.SourceFile },
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false)).ToList();

            if (rows.Count == 0)
            {
                continue;
            }

            var scan = scanner.Scan(group.SourceFile, rows.Select(row => (row.Id, row.Hash)).ToList(),
                maxTokens, overlayTokens, countTokens);

            // A missing/unreadable/unhandled file is never a candidate — a repair verb re-ingests,
            // it does not guess. Nothing to fix when the current chunker reproduces every row either.
            if (!scan.FileUsable || rows.TrueForAll(row => scan.PositionById[row.Id] >= 0))
            {
                continue;
            }

            filesToReingest++;
            rowsAffected += rows.Count;
            chunksToEmbed += scan.TotalChunks;

            if (apply)
            {
                var fileHash = WatchDigestExecutor.ComputeHash(group.SourceFile, scan.Content);
                await store.ReplaceAsync(group.ProjectId, group.SourceFile, fileHash, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return new ReingestRepairReport(filesToReingest, rowsAffected, chunksToEmbed);
    }

    private sealed class GroupKey
    {
        public string ProjectId { get; set; } = "";

        public string SourceFile { get; set; } = "";
    }

    private sealed class GroupRow
    {
        public long Id { get; set; }

        public string Hash { get; set; } = "";
    }
}
