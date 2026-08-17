using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Ingestion;

/// <summary>What a backfill pass found and did. A dry run fills it in without writing.</summary>
public sealed record ChunkBackfillReport(
    int RowsExamined,
    int RowsReplaced,
    int PiecesWritten,
    long CharsBefore,
    long CharsAfter);

/// <summary>
///     WP3 step 4: splits rows holding more text than the embedding window into in-budget pieces.
///     Operates on each row's own stored value — it never reads the source file (docs/adr/0069).
/// </summary>
public sealed class ChunkBackfill(IMarkdownChunker chunker, TimeProvider timeProvider, ILocalTokenizer localTokenizer)
{
    public async Task<ChunkBackfillReport> RunAsync(SqliteConnection connection, bool dryRun,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var (budget, overlay, countTokens) = await BudgetAsync(connection, cancellationToken).ConfigureAwait(false);
        var charsBefore = await connection.ExecuteScalarAsync<long?>(
                new CommandDefinition("SELECT SUM(length(value)) FROM entries", cancellationToken: cancellationToken))
            .ConfigureAwait(false) ?? 0;

        List<Row> rows =
        [
            .. await connection.QueryAsync<Row>(new CommandDefinition(
                """
                SELECT id AS Id, hash AS Hash, path AS Path, value AS Value, source_file AS SourceFile,
                       section AS Section, scope AS Scope, project_id AS ProjectId,
                       context_label AS ContextLabel, workspace_id AS WorkspaceId, source_id AS SourceId
                FROM entries WHERE value IS NOT NULL
                """, cancellationToken: cancellationToken))
        ];

        var replaced = 0;
        var pieces = 0;
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        foreach (var row in rows.Where(r => countTokens(r.Value) > budget))
        {
            var split = chunker.Chunk(row.Value, budget, overlay, countTokens);
            // A single piece means the chunker could not split it — replacing one over-window row
            // with one identical over-window row is churn, not a fix, so leave it and let the
            // report's own count show it was not fixed.
            if (split.Count <= 1)
            {
                continue;
            }

            replaced++;
            pieces += split.Count;
            if (dryRun)
            {
                continue;
            }

            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM entries WHERE id = @id", new { id = row.Id }, cancellationToken: cancellationToken))
                .ConfigureAwait(false);

            foreach (var piece in split)
            {
                await connection.ExecuteAsync(new CommandDefinition(MemorySql.InsertEntry,
                        new
                        {
                            hash = ContentHash.Of(row.Path ?? string.Empty, piece),
                            path = row.Path,
                            value = piece,
                            sourceFile = row.SourceFile,
                            section = row.Section,
                            scope = row.Scope,
                            projectId = row.ProjectId,
                            contextLabel = row.ContextLabel,
                            workspaceId = row.WorkspaceId,
                            agentId = (string?)null,
                            createdAt = now,
                            updatedAt = now,
                            sourceId = row.SourceId,
                            // Position unknown here — the bank-wide recompute below fills it in.
                            chunkIndex = -1,
                            totalChunks = 0
                        }, cancellationToken: cancellationToken))
                    .ConfigureAwait(false);
            }
        }

        if (!dryRun && replaced > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                    MemorySql.RecomputeChunkColumnsBankWide, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }

        var charsAfter = await connection.ExecuteScalarAsync<long?>(
                new CommandDefinition("SELECT SUM(length(value)) FROM entries", cancellationToken: cancellationToken))
            .ConfigureAwait(false) ?? 0;
        return new ChunkBackfillReport(rows.Count, replaced, pieces, charsBefore, charsAfter);
    }

    /// <summary>The same resolution the ingest path uses, read from the same settings.</summary>
    private async Task<ChunkBudget> BudgetAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var provider = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT value FROM settings WHERE key = 'embedding.provider'", cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        var model = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT value FROM settings WHERE key = 'embedding.model'", cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        provider = string.IsNullOrWhiteSpace(provider) ? "local" : provider;

        var budget = Math.Min(256, EmbeddingService.SafeChunkBudgetFor(provider, model));
        var overlay = Math.Min(ChunkingDefaults.OverlayTokens, Math.Max(0, budget - 1));
        return new ChunkBudget(budget, overlay, localTokenizer.CountTokens);
    }

    private sealed record Row(
        long Id, string Hash, string? Path, string Value, string? SourceFile, string? Section,
        string? Scope, string? ProjectId, string? ContextLabel, string? WorkspaceId, long? SourceId);
}
