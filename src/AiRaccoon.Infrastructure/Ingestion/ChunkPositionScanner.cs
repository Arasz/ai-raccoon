using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Embedding;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Ingestion;

/// <summary>
///     What re-chunking one source file with the current chunker found. <see cref="PositionById" />
///     maps every given row's id to its document position, or -1 when this chunker does not
///     reproduce its content hash. <see cref="SectionById" /> maps every given row's id to the
///     section the current chunker reports for it (docs/adr/0048, #549), or null when it holds no
///     section or its position is unknown. <see cref="FileUsable" /> is false when the source file
///     is missing, unreadable, or has no matching handler — every row then maps to -1/null without a
///     document position ever being computed, and <see cref="TotalChunks" />/<see cref="Content" />
///     are empty/zero.
/// </summary>
public sealed record ChunkPositionScan(
    bool FileUsable, string Content, int TotalChunks,
    IReadOnlyDictionary<long, int> PositionById, IReadOnlyDictionary<long, string?> SectionById);

/// <summary>
///     Shared chunk-position detection (GH #371): re-chunks a source file with the current chunker
///     and matches stored rows to it by content hash. Used by <see cref="ChunkIndexRepair" /> to
///     re-derive chunk_index/total_chunks, and by repair reingest to find files a chunker change
///     left with rows it can no longer reproduce. Budget AND counter resolve through
///     <see cref="IEmbeddingService" /> so they always match the configured engine (D9/ADR-0036).
/// </summary>
public sealed class ChunkPositionScanner(IFileTypeMatcher fileTypeMatcher, IEmbeddingService embeddingService)
{

    public ChunkPositionScan Scan(string sourceFile, IReadOnlyList<(long Id, string Hash)> rows,
        int maxTokens, int overlayTokens, TokenCount countTokens)
    {
        if (!fileTypeMatcher.TryGetHandler(sourceFile, out var handler) || !TryReadFile(sourceFile, out var content))
        {
            return new ChunkPositionScan(false, "", 0, rows.ToDictionary(row => row.Id, _ => -1),
                rows.ToDictionary(row => row.Id, _ => (string?)null));
        }

        var chunks = handler.Chunker.ChunkWithHeadings(content, maxTokens, overlayTokens, countTokens);
        var byHash = rows.ToDictionary(row => row.Hash, row => row.Id, StringComparer.Ordinal);
        var positionById = new Dictionary<long, int>(rows.Count);
        var sectionById = new Dictionary<long, string?>(rows.Count);
        for (var ordinal = 0; ordinal < chunks.Count; ordinal++)
        {
            var hash = ContentHash.Of(sourceFile, chunks[ordinal].Text);
            if (byHash.TryGetValue(hash, out var id))
            {
                positionById[id] = ordinal;
                sectionById[id] = chunks[ordinal].SectionLabel();
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

    /// <summary>The same resolution the ingest path uses, read from the same settings (mirrors <see cref="Ingestion.ChunkBackfill" />'s BudgetAsync).</summary>
    public async Task<ChunkBudget> BudgetAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var provider = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT value FROM settings WHERE key = 'embedding.provider'", cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        var model = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT value FROM settings WHERE key = 'embedding.model'", cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        provider = string.IsNullOrWhiteSpace(provider) ? "local" : provider;

        var settings = new EmbeddingSettings(provider, model, null, null);
        var budget = embeddingService.ResolveChunkBudgetFor(settings);
        var overlay = Math.Min(ChunkingDefaults.OverlayTokens, Math.Max(0, budget - 1));
        // local counts with the engine's own tokenizer; non-local uses the same o200k proxy the
        // ingest path's chunker-default counter uses (D9 — the repair family and the ingest path
        // must count with the same tokenizer per engine).
        TokenCount countTokens = provider.Equals("local", StringComparison.OrdinalIgnoreCase)
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
