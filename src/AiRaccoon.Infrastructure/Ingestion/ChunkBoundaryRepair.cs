using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Watch;
using CommunityToolkit.Diagnostics;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Ingestion;

/// <summary>What a chunk-boundary repair did: files re-ingested, row groups re-chunked in place, rows written.</summary>
public sealed record ChunkBoundaryRepairReport(int FilesReingested, int GroupsRepaired, int RowsWritten);

/// <summary>
///     Repairs rows an older chunker cut through the middle of a term (docs/adr/0120). A file row whose file is
///     still readable is re-ingested from the file; any other group (a memory_write note, a file gone from disk)
///     is re-chunked from its own rows, which concatenate back to the original text across such a cut.
///     New rows are left pending for the embed drain.
/// </summary>
public sealed class ChunkBoundaryRepair(
    IFileTypeMatcher fileTypeMatcher,
    IMarkdownChunker noteChunker,
    IPlainTextChunker fallbackChunker,
    IEmbeddingService embeddingService,
    TimeProvider timeProvider)
{
    public async Task<ChunkBoundaryRepairReport> RunAsync(SqliteConnection connection, IMemoryStore store,
        CancellationToken cancellationToken = default)
    {
        Guard.IsNotNull(connection);
        Guard.IsNotNull(store);

        var budget = await new ChunkPositionScanner(fileTypeMatcher, embeddingService).BudgetAsync(connection, cancellationToken);
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            """
            SELECT id AS Id, hash AS Hash, path AS Path, value AS Value, source_file AS SourceFile, section AS Section,
                   scope AS Scope, project_id AS ProjectId, context_label AS ContextLabel, workspace_id AS WorkspaceId,
                   agent_id AS AgentId, created_at AS CreatedAt, source_id AS SourceId, chunk_index AS ChunkIndex
            FROM entries
            WHERE value IS NOT NULL AND path IS NOT NULL
            """, cancellationToken: cancellationToken));

        var filesReingested = 0;
        var groupsRepaired = 0;
        var rowsWritten = 0;
        var groups = rows.GroupBy(row => (row.Scope, row.ProjectId, row.ContextLabel, row.WorkspaceId, row.Path)).Where(group => group.Count() > 1);
        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ordered = InTextOrder(group.ToList());
            var isFile = ordered[0].IsFileRow;
            if (!HasSeam(ordered, isFile))
            {
                continue;
            }

            if (isFile && ordered[0].WorkspaceId is null && await TryReingestAsync(store, ordered[0].ProjectId!, ordered[0].Path, cancellationToken))
            {
                filesReingested++;
                continue;
            }

            var written = await RechunkRunsAsync(connection, ordered, budget, cancellationToken);
            if (written > 0)
            {
                groupsRepaired++;
                rowsWritten += written;
            }
        }

        if (groupsRepaired > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(MemorySql.RecomputeChunkColumnsBankWide, cancellationToken: cancellationToken));
        }

        filesReingested += await ReingestCutCodeFilesAsync(connection, store, cancellationToken);

        return new ChunkBoundaryRepairReport(filesReingested, groupsRepaired, rowsWritten);
    }

    /// <summary>A file's rows by position; a note's first chunk is written last, so it holds the highest id.</summary>
    private static List<Row> InTextOrder(List<Row> rows)
    {
        if (rows[0].IsFileRow)
        {
            return [.. rows.OrderBy(row => row.ChunkIndex < 0 ? long.MaxValue : row.ChunkIndex).ThenBy(row => row.Id)];
        }

        var first = rows.MaxBy(row => row.Id)!;
        return [first, .. rows.Where(row => row.Id != first.Id).OrderBy(row => row.Id)];
    }

    private static bool HasSeam(List<Row> ordered, bool isFile)
    {
        for (var i = 0; i + 1 < ordered.Count; i++)
        {
            if (ChunkSeam.CutsATerm(ordered[i].Value, ordered[i + 1].Value)
                || (isFile && ChunkSeam.MayCutAFencedTerm(ordered[i].Value, ordered[i + 1].Value)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Code rows carry line ranges that cannot be re-derived from the rows, so a cut code file is only
    /// re-ingested from disk; one no longer on disk is left for the watch digest to prune.</summary>
    private static async Task<int> ReingestCutCodeFilesAsync(SqliteConnection connection, IMemoryStore store,
        CancellationToken cancellationToken)
    {
        var rows = await connection.QueryAsync<CodeRow>(new CommandDefinition(
            "SELECT id AS Id, project_id AS ProjectId, path AS Path, value AS Value, chunk_index AS ChunkIndex FROM code_entries",
            cancellationToken: cancellationToken));
        var reingested = 0;
        foreach (var group in rows.GroupBy(row => (row.ProjectId, row.Path)))
        {
            var ordered = group.OrderBy(row => row.ChunkIndex < 0 ? long.MaxValue : row.ChunkIndex).ThenBy(row => row.Id).ToList();
            var cut = Enumerable.Range(0, ordered.Count - 1).Any(i => ChunkSeam.CutsATerm(ordered[i].Value, ordered[i + 1].Value));
            if (cut && await TryReingestAsync(store, group.Key.ProjectId, group.Key.Path, cancellationToken))
            {
                reingested++;
            }
        }

        return reingested;
    }

    /// <summary>Re-ingests a file through the same unconditional replace the reingest repair uses; false when the
    /// file cannot be read or ingested, so the caller repairs from the rows instead.</summary>
    private static async Task<bool> TryReingestAsync(IMemoryStore store, string projectId, string path, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var content = await File.ReadAllTextAsync(path, cancellationToken);
            await store.ReplaceAsync(projectId, path, WatchDigestExecutor.ComputeHash(path, content), cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PathOutsideScopeException)
        {
            return false;
        }
    }

    /// <summary>
    ///     Joins each run of rows linked by a mid-term cut and re-chunks it. A cut is joined only when the piece
    ///     before it is larger than the overlay, which proves no overlay text was repeated after it.
    /// </summary>
    private async Task<int> RechunkRunsAsync(SqliteConnection connection, List<Row> ordered, ChunkBudget budget,
        CancellationToken cancellationToken)
    {
        var chunker = ChunkerFor(ordered[0]);
        List<(List<Row> Old, IReadOnlyList<string> New)> replacements = [];
        var start = 0;
        while (start < ordered.Count)
        {
            var end = start;
            while (end + 1 < ordered.Count && IsJoinable(ordered[end].Value, ordered[end + 1].Value, budget))
            {
                end++;
            }

            if (end > start)
            {
                var old = ordered.GetRange(start, end - start + 1);
                var pieces = chunker.Chunk(string.Concat(old.Select(row => row.Value)), budget.MaxTokens, 0, budget.CountTokens);
                if (!pieces.SequenceEqual(old.Select(row => row.Value), StringComparer.Ordinal))
                {
                    replacements.Add((old, pieces));
                }
            }

            start = end + 1;
        }

        if (replacements.Count == 0)
        {
            return 0;
        }

        return await ReplaceRowsAsync(connection, ordered, replacements, cancellationToken);
    }

    private static bool IsJoinable(string before, string after, ChunkBudget budget)
    {
        if (!ChunkSeam.CutsATerm(before, after))
        {
            return false;
        }

        var piece = before[(before.LastIndexOf('\n') + 1)..];
        return budget.CountTokens(piece) > budget.OverlayTokens;
    }

    private IChunker ChunkerFor(Row row)
    {
        if (!row.IsFileRow)
        {
            return noteChunker;
        }

        return fileTypeMatcher.TryGetHandler(row.Path, out var handler) ? handler.Chunker : fallbackChunker;
    }

    /// <summary>Swaps each run for its new pieces in one transaction, tombstoning every hash that disappears, and
    /// renumbers a file group's positions in text order.</summary>
    private async Task<int> ReplaceRowsAsync(SqliteConnection connection, List<Row> ordered,
        List<(List<Row> Old, IReadOnlyList<string> New)> replacements, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var template = ordered[0];
        var newHashes = replacements.SelectMany(r => r.New).Select(piece => ContentHash.Of(template.Path, piece)).ToHashSet(StringComparer.Ordinal);
        List<long> finalOrder = [];
        var written = 0;

        await connection.ExecuteAsync(new CommandDefinition("BEGIN IMMEDIATE", cancellationToken: cancellationToken));
        try
        {
            foreach (var row in replacements.SelectMany(r => r.Old))
            {
                if (!newHashes.Contains(row.Hash))
                {
                    await connection.ExecuteAsync(new CommandDefinition(MemorySql.TombstoneFromPredicate("id = @id"),
                        new { id = row.Id, deletedAt = now }, cancellationToken: cancellationToken));
                }

                await connection.ExecuteAsync(new CommandDefinition("DELETE FROM entries WHERE id = @id",
                    new { id = row.Id }, cancellationToken: cancellationToken));
            }

            var replacedIds = replacements.SelectMany(r => r.Old).Select(row => row.Id).ToHashSet();
            foreach (var row in ordered)
            {
                var replacement = replacements.FirstOrDefault(r => r.Old[0].Id == row.Id);
                if (replacement.Old is not null)
                {
                    foreach (var piece in replacement.New)
                    {
                        var id = await InsertAsync(connection, row, piece, now, cancellationToken);
                        if (id is not null)
                        {
                            finalOrder.Add(id.Value);
                            written++;
                        }
                    }
                }
                else if (!replacedIds.Contains(row.Id))
                {
                    finalOrder.Add(row.Id);
                }
            }

            if (template.IsFileRow)
            {
                for (var i = 0; i < finalOrder.Count; i++)
                {
                    await connection.ExecuteAsync(new CommandDefinition(
                        "UPDATE entries SET chunk_index = @index, total_chunks = @total WHERE id = @id",
                        new { index = i, total = finalOrder.Count, id = finalOrder[i] }, cancellationToken: cancellationToken));
                }
            }

            await connection.ExecuteAsync(new CommandDefinition("COMMIT", cancellationToken: cancellationToken));
        }
        catch
        {
            await connection.ExecuteAsync(new CommandDefinition("ROLLBACK", cancellationToken: CancellationToken.None));
            throw;
        }

        return written;
    }

    private static Task<long?> InsertAsync(SqliteConnection connection, Row row, string piece, long now,
        CancellationToken cancellationToken) =>
        connection.ExecuteScalarAsync<long?>(new CommandDefinition(MemorySql.InsertEntry + " RETURNING id",
            new
            {
                hash = ContentHash.Of(row.Path, piece),
                path = row.Path,
                value = piece,
                sourceFile = row.SourceFile,
                section = row.Section,
                scope = row.Scope,
                projectId = row.ProjectId,
                contextLabel = row.ContextLabel,
                workspaceId = row.WorkspaceId,
                agentId = row.AgentId,
                createdAt = row.CreatedAt,
                updatedAt = now,
                sourceId = row.SourceId,
                chunkIndex = -1,
                totalChunks = 0
            }, cancellationToken: cancellationToken));

    private sealed record CodeRow(long Id, string ProjectId, string Path, string Value, long ChunkIndex);

    private sealed record Row(
        long Id,
        string Hash,
        string Path,
        string Value,
        string? SourceFile,
        string? Section,
        string? Scope,
        string? ProjectId,
        string? ContextLabel,
        string? WorkspaceId,
        string? AgentId,
        long CreatedAt,
        long? SourceId,
        long ChunkIndex)
    {
        /// <summary>A file's own mirror row, as opposed to a memory_write note that may merely cite the file.</summary>
        public bool IsFileRow => SourceFile is not null && string.Equals(Path, SourceFile, StringComparison.Ordinal);
    }
}
