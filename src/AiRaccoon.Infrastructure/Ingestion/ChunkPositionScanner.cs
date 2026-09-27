using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;
using CommunityToolkit.Diagnostics;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Ingestion;

/// <summary>
///     What re-chunking one source file with the current chunker found. <see cref="PositionById" />
///     maps every given row's id to its document position, or -1 when this chunker does not
///     reproduce its content hash. <see cref="SectionById" /> maps every given row's id to the
///     section the current chunker reports for it (docs/adr/0048), or null when it holds no
///     section or its position is unknown. <see cref="FileUsable" /> is false when the source file
///     is missing, unreadable, or has no matching handler — every row then maps to -1/null without a
///     document position ever being computed, and <see cref="TotalChunks" />/<see cref="Content" />
///     are empty/zero. <see cref="TotalChunks" /> counts the rows the file is stored as — a chunk
///     repeated verbatim once (<see cref="DocumentChunks" />).
/// </summary>
public sealed record ChunkPositionScan(
    bool FileUsable,
    string Content,
    int TotalChunks,
    IReadOnlyDictionary<long, int> PositionById,
    IReadOnlyDictionary<long, string?> SectionById)
{
    /// <summary>True when the given rows are exactly the rows the file is stored as: every one is reproduced and the
    /// file holds no chunk without one.</summary>
    public bool MatchesStoredRows =>
        FileUsable && TotalChunks == PositionById.Count && PositionById.Values.All(position => position >= 0);
}

/// <summary>One stored row of a (ctx, source_file) position partition, as the position repairs read it.</summary>
public sealed record PartitionEntry(
    long Id,
    string Hash,
    string? Path,
    string SourceFile,
    long ChunkIndex,
    long TotalChunks,
    string? Section)
{
    public bool IsFileRow => string.Equals(Path, SourceFile, StringComparison.Ordinal);
}

/// <summary>A position write: the row's new chunk_index, total_chunks and section.</summary>
public sealed record ChunkMove(long Id, long ChunkIndex, long TotalChunks, string? Section, bool SectionChanged);

/// <summary>
///     Re-chunks a source file with the current chunker and matches stored rows to it by content hash, and
///     turns a scan into the position writes for the file's whole (ctx, source_file) partition
///     (<see cref="DocumentChunks.PartitionPositions" />). Budget and counter resolve through
///     <see cref="IEmbeddingService" /> so they match the configured engine (docs/adr/0036).
/// </summary>
public sealed class ChunkPositionScanner(IFileTypeMatcher fileTypeMatcher, IEmbeddingService embeddingService)
{
    public ChunkPositionScan Scan(string sourceFile, IReadOnlyList<StoredChunk> rows,
        int maxTokens, int overlayTokens, TokenCount countTokens)
    {
        if (!fileTypeMatcher.TryGetHandler(sourceFile, out var handler) || !TryReadFile(sourceFile, out var content))
        {
            return new ChunkPositionScan(false, "", 0, rows.ToDictionary(row => row.Id, _ => -1),
                rows.ToDictionary(row => row.Id, _ => (string?)null));
        }

        var chunks = DocumentChunks.Distinct(sourceFile,
            handler.Chunker.ChunkWithHeadings(content, maxTokens, overlayTokens, countTokens));
        var byHash = rows.ToDictionary(row => row.Hash, row => row.Id, StringComparer.Ordinal);
        var positionById = new Dictionary<long, int>(rows.Count);
        var sectionById = new Dictionary<long, string?>(rows.Count);
        foreach (var chunk in chunks)
        {
            if (byHash.TryGetValue(chunk.Hash, out var id))
            {
                positionById[id] = chunk.Position;
                sectionById[id] = chunk.Chunk.SectionLabel();
            }
        }

        // A row this pass's re-chunk did not reproduce (stale content, a boundary shift) has no
        // document position or section to report — unknown, not a guess.
        foreach (var row in rows.Where(row => !positionById.ContainsKey(row.Id)))
        {
            positionById[row.Id] = -1;
            sectionById[row.Id] = null;
        }

        return new ChunkPositionScan(true, content, chunks.Count, positionById, sectionById);
    }

    /// <summary>Every row sharing the (ctx, source_file) position partition of the row <paramref name="memberId" />.</summary>
    public static async Task<IReadOnlyList<PartitionEntry>> PartitionAsync(SqliteConnection connection, long memberId,
        CancellationToken cancellationToken)
    {
        Guard.IsNotNull(connection);

        return [.. await connection.QueryAsync<PartitionEntry>(new CommandDefinition(
            $"""
             WITH member AS (SELECT source_file AS sf, {MemorySql.ContextKeyExpression("")} AS ctx FROM entries WHERE id = @memberId)
             SELECT e.id AS Id, e.hash AS Hash, e.path AS Path, e.source_file AS SourceFile, e.chunk_index AS ChunkIndex,
                    e.total_chunks AS TotalChunks, e.section AS Section
             FROM entries e, member m
             WHERE e.source_file = m.sf AND ({MemorySql.ContextKeyExpression("e.")}) = m.ctx
             """, new { memberId }, cancellationToken: cancellationToken))];
    }

    /// <summary>The writes that give a partition its positions: the file's rows at <paramref name="scan" />'s
    /// positions and sections, or in stored order when it is null; only rows whose position or total changes.</summary>
    public static IReadOnlyList<ChunkMove> Moves(IReadOnlyList<PartitionEntry> partition, ChunkPositionScan? scan)
    {
        Guard.IsNotNull(partition);

        var positions = DocumentChunks.PartitionPositions(
            [.. partition.Select(row => new PartitionRow(row.Id, row.IsFileRow, row.ChunkIndex))], scan?.PositionById);
        List<ChunkMove> moves = [];
        foreach (var row in partition)
        {
            var index = positions[row.Id];
            if (index == row.ChunkIndex && row.TotalChunks == partition.Count)
            {
                continue;
            }

            // A row the scan does not place keeps its section: unknown is not gone.
            var section = scan is not null && row.IsFileRow && index >= 0 ? scan.SectionById[row.Id] : row.Section;
            moves.Add(new ChunkMove(row.Id, index, partition.Count, section, !string.Equals(section, row.Section, StringComparison.Ordinal)));
        }

        return moves;
    }

    /// <summary>Applies <paramref name="moves" /> in one transaction; a section is written only when it changes, so the
    /// full-text index is not rewritten for a position-only move.</summary>
    public static async Task WriteAsync(SqliteConnection connection, IReadOnlyList<ChunkMove> moves,
        CancellationToken cancellationToken)
    {
        Guard.IsNotNull(connection);
        Guard.IsNotNull(moves);
        if (moves.Count == 0)
        {
            return;
        }

        await connection.ExecuteAsync(new CommandDefinition("BEGIN IMMEDIATE", cancellationToken: cancellationToken));
        try
        {
            foreach (var move in moves)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    move.SectionChanged ? MemorySql.SetChunkPosition : MemorySql.SetChunkColumns,
                    new { id = move.Id, chunkIndex = move.ChunkIndex, totalChunks = move.TotalChunks, section = move.Section },
                    cancellationToken: cancellationToken));
            }

            await connection.ExecuteAsync(new CommandDefinition("COMMIT", cancellationToken: cancellationToken));
        }
        catch
        {
            await connection.ExecuteAsync(new CommandDefinition("ROLLBACK", cancellationToken: CancellationToken.None));
            throw;
        }
    }

    /// <summary>The same resolution the ingest path uses, read from the same settings (mirrors <see cref="Ingestion.ChunkBackfill" />'s BudgetAsync).</summary>
    public async Task<ChunkBudget> BudgetAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var provider = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT value FROM settings WHERE key = 'embedding.provider'", cancellationToken: cancellationToken));
        var model = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT value FROM settings WHERE key = 'embedding.model'", cancellationToken: cancellationToken));
        provider = string.IsNullOrWhiteSpace(provider) ? "local" : provider;

        var settings = new EmbeddingSettings(provider, model, null, null);
        var budget = embeddingService.ResolveChunkBudgetFor(settings);
        var overlay = Math.Min(ChunkingDefaults.OverlayTokens, Math.Max(0, budget - 1));
        // local counts with the engine's own tokenizer; non-local uses the same o200k proxy the
        // ingest path's chunker-default counter uses (D9 — the repair family and the ingest path
        // must count with the same tokenizer per engine).
        var countTokens = provider.Equals("local", StringComparison.OrdinalIgnoreCase)
            ? new TokenCount(embeddingService.ResolveTokenizer(settings)!.CountTokens)
            : new TokenCount(new O200kTokenizer().CountTokens);
        return new ChunkBudget(budget, overlay, countTokens);
    }

    private static bool TryReadFile(string path, out string content)
    {
        try
        {
            content = File.ReadAllText(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            content = "";
            return false;
        }
    }
}
