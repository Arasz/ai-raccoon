using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Ingestion;

/// <summary>
///     The code corpus's file-ingestion mechanics (docs/work/2026-08-21-code-search-implementation-plan.md
///     §3.4/§4.2): scope, hidden, and code-extension self-filtering; chunk via <see cref="ICodeChunker" />;
///     dedup-insert by (project, path, hash). No embedding in this lane — the code engine (WP5) is
///     engine-blocked, so every new row lands `embed_state = 'pending'`.
/// </summary>
public sealed class CodeIngestor(
    ICodeFileTypeMatcher codeFileTypeMatcher,
    ICodeChunker codeChunker,
    TimeProvider timeProvider) : ICodeIngestor
{
    public async Task<CodeIngestResult> IngestFileAsync(SqliteConnection connection, string projectId, string path,
        CancellationToken cancellationToken, IReadOnlyList<string>? scope = null)
    {
        // S12: cheap self-filtering first — a hidden or non-code file returns 0 regardless of
        // scope, so it must not pay for (or fail on) a scope check it can never affect the outcome of.
        if (IsHidden(path) || !codeFileTypeMatcher.IsCodeFile(path))
        {
            return new CodeIngestResult(0, false);
        }

        var normalizedPath = IngestPath.Normalize(path);
        if (scope is null)
        {
            await RequireInScopeAsync(connection, projectId, path, cancellationToken).ConfigureAwait(false);
        }
        else if (!scope.Any(entry => IngestPath.IsWithinScope(normalizedPath, entry)))
        {
            throw new PathOutsideScopeException(normalizedPath);
        }

        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var chunks = codeChunker.Chunk(content);
        if (chunks.Count == 0)
        {
            return new CodeIngestResult(0, string.IsNullOrWhiteSpace(content), []);
        }

        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var inserted = 0;
        var hashes = new List<string>(chunks.Count);

        for (var ordinal = 0; ordinal < chunks.Count; ordinal++)
        {
            var chunk = chunks[ordinal];
            var hash = ContentHash.Of(normalizedPath, chunk.Text);
            hashes.Add(hash);
            var existingId = await connection.ExecuteScalarAsync<long?>(
                    Def(MemorySql.SelectCodeChunkIdByPathAndHash,
                        new { projectId, path = normalizedPath, hash }, cancellationToken))
                .ConfigureAwait(false);

            if (existingId is not null)
            {
                // D-E11: refresh the full position (line range + ordinal), not just the ordinal —
                // for code, the line range IS the retrieval payload.
                await connection.ExecuteAsync(
                        Def(MemorySql.UpdateCodeChunkPosition,
                            new
                            {
                                id = existingId.Value, lineStart = chunk.LineStart, lineEnd = chunk.LineEnd,
                                chunkIndex = ordinal, totalChunks = chunks.Count, updatedAt = now
                            }, cancellationToken))
                    .ConfigureAwait(false);
                continue;
            }

            await connection.ExecuteAsync(
                    Def(MemorySql.InsertCodeEntry,
                        new
                        {
                            hash, path = normalizedPath, value = chunk.Text, sourceFile = normalizedPath,
                            lineStart = chunk.LineStart, lineEnd = chunk.LineEnd, projectId,
                            createdAt = now, updatedAt = now, chunkIndex = ordinal, totalChunks = chunks.Count
                        }, cancellationToken))
                .ConfigureAwait(false);

            // Post-conflict re-read (FileIngestor.cs ~167-184's shape): a concurrent ingest may
            // have won the ON CONFLICT DO NOTHING race, so re-read rather than trust the insert.
            var newId = await connection.ExecuteScalarAsync<long?>(
                    Def(MemorySql.SelectCodeChunkIdByPathAndHash,
                        new { projectId, path = normalizedPath, hash }, cancellationToken))
                .ConfigureAwait(false);
            if (newId is null)
            {
                continue;
            }

            inserted++;
        }

        return new CodeIngestResult(inserted > 0 ? 1 : 0, false, hashes);
    }

    private static async Task RequireInScopeAsync(SqliteConnection connection, string projectId, string path,
        CancellationToken cancellationToken)
    {
        var scope = await ReadScopeAsync(connection, projectId, cancellationToken).ConfigureAwait(false);
        var normalized = IngestPath.Normalize(path);
        if (!scope.Any(entry => IngestPath.IsWithinScope(normalized, entry)))
        {
            throw new PathOutsideScopeException(normalized);
        }
    }

    private static async Task<IReadOnlyList<string>> ReadScopeAsync(SqliteConnection connection, string projectId,
        CancellationToken cancellationToken) =>
        IngestScopeKeys.Parse(
            await ReadSettingAsync(connection, IngestScopeKeys.ScopeProject(projectId), cancellationToken)
                .ConfigureAwait(false))
        ?? IngestScopeKeys.Parse(
            await ReadSettingAsync(connection, IngestScopeKeys.ScopeGlobal, cancellationToken)
                .ConfigureAwait(false))
        ?? [];

    private static async Task<string?> ReadSettingAsync(SqliteConnection connection, string key,
        CancellationToken cancellationToken) =>
        await connection.QuerySingleOrDefaultAsync<string?>(
                Def(MemorySql.SelectSetting, new { key }, cancellationToken))
            .ConfigureAwait(false);

    private static bool IsHidden(string path) => Path.GetFileName(path).StartsWith('.');

    private static CommandDefinition Def(string sql, object? parameters = null,
        CancellationToken cancellationToken = default) =>
        new(sql, parameters, cancellationToken: cancellationToken);
}
