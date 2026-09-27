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

/// <summary>What a chunk-boundary repair did: files re-ingested, row groups re-chunked in place, rows written, notes left
/// as stored because no order of their rows joins back into the body their path names, and files whose rows kept their
/// content but took their document positions.</summary>
public sealed record ChunkBoundaryRepairReport(int FilesReingested, int GroupsRepaired, int RowsWritten, int NotesUnproven,
    int FilesRepositioned);

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

        var scanner = new ChunkPositionScanner(fileTypeMatcher, embeddingService);
        var budget = await scanner.BudgetAsync(connection, cancellationToken);
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            """
            SELECT id AS Id, hash AS Hash, path AS Path, value AS Value, source_file AS SourceFile, section AS Section,
                   scope AS Scope, project_id AS ProjectId, context_label AS ContextLabel, workspace_id AS WorkspaceId,
                   agent_id AS AgentId, created_at AS CreatedAt, source_id AS SourceId, chunk_index AS ChunkIndex,
                   total_chunks AS TotalChunks
            FROM entries
            WHERE value IS NOT NULL AND path IS NOT NULL
            """, cancellationToken: cancellationToken));

        var filesReingested = 0;
        var groupsRepaired = 0;
        var rowsWritten = 0;
        var notesUnproven = 0;
        var filesRepositioned = 0;
        var groups = rows.GroupBy(row => (row.Scope, row.ProjectId, row.ContextLabel, row.WorkspaceId, row.Path)).Where(group => group.Count() > 1);
        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ordered = InTextOrder(group.ToList());
            if (ordered is null)
            {
                notesUnproven++;
                continue;
            }

            var isFile = ordered[0].IsFileRow;
            if (!HasSeam(ordered, isFile) && !(isFile && HasCollidingPositions(ordered)))
            {
                continue;
            }

            if (isFile)
            {
                var scan = scanner.Scan(ordered[0].Path, [.. ordered.Select(row => new StoredChunk(row.Id, row.Hash))],
                    budget.MaxTokens, budget.OverlayTokens, budget.CountTokens);
                if (scan.FileUsable && ordered.TrueForAll(row => scan.PositionById[row.Id] >= 0))
                {
                    if (await RepositionAsync(connection, ordered, scan, cancellationToken))
                    {
                        filesRepositioned++;
                    }

                    continue;
                }
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

        return new ChunkBoundaryRepairReport(filesReingested, groupsRepaired, rowsWritten, notesUnproven, filesRepositioned);
    }

    /// <summary>A file's rows by position; a note's rows in the order <see cref="NoteTextOrder" /> proves, or null
    /// when no order joins back into the note's body.</summary>
    private static List<Row>? InTextOrder(List<Row> rows)
    {
        if (rows[0].IsFileRow)
        {
            return [.. rows.OrderBy(row => row.ChunkIndex < 0 ? long.MaxValue : row.ChunkIndex).ThenBy(row => row.Id)];
        }

        var byId = rows.ToDictionary(row => row.Id);
        var order = NoteTextOrder.Find(rows[0].Path, [.. rows.Select(row => new NoteRow(row.Id, row.Value, row.ChunkIndex))]);
        return order is null ? null : [.. order.Select(row => byId[row.Id])];
    }

    private static bool HasCollidingPositions(List<Row> ordered) =>
        ordered.Where(row => row.ChunkIndex >= 0).GroupBy(row => row.ChunkIndex).Any(group => group.Count() > 1);

    /// <summary>Gives a file's rows, all of which the current chunker still writes, the positions a re-ingest would;
    /// true when any row moved.</summary>
    private static async Task<bool> RepositionAsync(SqliteConnection connection, List<Row> ordered, ChunkPositionScan scan,
        CancellationToken cancellationToken)
    {
        var moved = ordered.Where(row => row.ChunkIndex != scan.PositionById[row.Id] || row.TotalChunks != scan.TotalChunks).ToList();
        if (moved.Count == 0)
        {
            return false;
        }

        await connection.ExecuteAsync(new CommandDefinition("BEGIN IMMEDIATE", cancellationToken: cancellationToken));
        try
        {
            foreach (var row in moved)
            {
                await connection.ExecuteAsync(new CommandDefinition(MemorySql.SetChunkPosition,
                    new { id = row.Id, chunkIndex = scan.PositionById[row.Id], totalChunks = scan.TotalChunks, section = scan.SectionById[row.Id] },
                    cancellationToken: cancellationToken));
            }

            await connection.ExecuteAsync(new CommandDefinition("COMMIT", cancellationToken: cancellationToken));
        }
        catch
        {
            await connection.ExecuteAsync(new CommandDefinition("ROLLBACK", cancellationToken: CancellationToken.None));
            throw;
        }

        return true;
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
    /// renumbers the source file's position partition with each run's pieces in the run's own place.</summary>
    private async Task<int> ReplaceRowsAsync(SqliteConnection connection, List<Row> ordered,
        List<(List<Row> Old, IReadOnlyList<string> New)> replacements, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var template = ordered[0];
        var newHashes = replacements.SelectMany(r => r.New).Select(piece => ContentHash.Of(template.Path, piece)).ToHashSet(StringComparer.Ordinal);
        List<Placed> placed = [];
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

            Dictionary<long, List<long>> pieceIds = [];
            foreach (var (old, pieces) in replacements)
            {
                var slot = old[0].ChunkIndex < 0 ? long.MaxValue : old[0].ChunkIndex;
                pieceIds[old[0].Id] = [];
                for (var i = 0; i < pieces.Count; i++)
                {
                    var id = await InsertAsync(connection, old[0], pieces[i], now, cancellationToken);
                    if (id is not null)
                    {
                        placed.Add(new Placed(id.Value, slot, i + 1));
                        pieceIds[old[0].Id].Add(id.Value);
                        written++;
                    }
                }
            }

            if (template.SourceFile is not null)
            {
                var replaced = replacements.SelectMany(r => r.Old).Select(row => row.Id).ToHashSet();
                List<long> textOrder =
                [
                    .. ordered.SelectMany(row => pieceIds.TryGetValue(row.Id, out var ids) ? ids
                        : replaced.Contains(row.Id) ? [] : [row.Id])
                ];
                await RenumberPartitionAsync(connection, template, placed, textOrder, cancellationToken);
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

    /// <summary>
    ///     Gives every row sharing the template's source-file position partition a contiguous position: untouched
    ///     rows keep their relative order, a run's new pieces take the slot its first old row held, and the repaired
    ///     group's own rows then take its positions in <paramref name="textOrder" />.
    /// </summary>
    private static async Task RenumberPartitionAsync(SqliteConnection connection, Row template, List<Placed> placed,
        List<long> textOrder, CancellationToken cancellationToken)
    {
        var partition = await connection.QueryAsync<PositionRow>(new CommandDefinition(
            $"""
             SELECT id AS Id, chunk_index AS ChunkIndex FROM entries
             WHERE source_file = @sourceFile AND ({MemorySql.ContextKeyExpression("")}) = ({MemorySql.ContextKeyExpression("@")})
             """,
            new
            {
                sourceFile = template.SourceFile,
                workspace_id = template.WorkspaceId,
                scope = template.Scope,
                project_id = template.ProjectId,
                context_label = template.ContextLabel
            }, cancellationToken: cancellationToken));
        var byId = placed.ToDictionary(p => p.Id);
        var order = partition
            .Select(row => byId.TryGetValue(row.Id, out var p)
                ? p
                : new Placed(row.Id, row.ChunkIndex < 0 ? long.MaxValue : row.ChunkIndex, 0))
            .OrderBy(p => p.Slot).ThenBy(p => p.Piece).ThenBy(p => p.Id)
            .ToList();
        var position = order.Select((p, i) => (p.Id, Index: (long)i)).ToDictionary(p => p.Id, p => p.Index);
        foreach (var (id, index) in NoteTextOrder.Repositioned([.. textOrder.Select(id => new NoteRow(id, string.Empty, position[id]))]))
        {
            position[id] = index;
        }

        foreach (var (id, index) in position)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE entries SET chunk_index = @index, total_chunks = @total WHERE id = @id",
                new { index, total = order.Count, id }, cancellationToken: cancellationToken));
        }
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

    /// <summary>A row's sort key in its partition: the slot it holds, and for a new piece its order within the run.</summary>
    private sealed record Placed(long Id, long Slot, int Piece);

    private sealed record PositionRow(long Id, long ChunkIndex);

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
        long ChunkIndex,
        long TotalChunks)
    {
        /// <summary>A file's own mirror row, as opposed to a memory_write note that may merely cite the file.</summary>
        public bool IsFileRow => SourceFile is not null && string.Equals(Path, SourceFile, StringComparison.Ordinal);
    }
}
