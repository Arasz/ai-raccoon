using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Ingestion;

/// <summary>
///     The bank's file-ingestion mechanics: scope containment, chunking, and chunk insertion.
///     Takes an already-open connection rather than opening its own — every caller has already
///     opened the bank once for the whole walk, so the compiler enforces one-bank-open-per-ingest (WI-8).
/// </summary>
internal sealed class FileIngestor(IChunker chunker, EntryEmbedder embedder, TimeProvider timeProvider)
{
    // Chunk bounds (see docs/work/2026-08-03-native-memory-plan.md §8): 512 tokens exceeded the bundled all-MiniLM-L6-v2's
    // 256-token window, diluting embeddings via truncation; defaults are now 256/48 and the
    // chunk size is clamped to the configured engine's window where the engine knows it.
    private const int DefaultMaxTokens = 256;
    private const int DefaultOverlayTokens = 48;

    private static readonly HashSet<string> IndexableExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".md", ".markdown", ".txt" };

    public async Task<int> IngestFileAsync(SqliteConnection connection, string projectId, string path,
        string? context, CancellationToken cancellationToken)
    {
        await RequireInScopeAsync(connection, projectId, path, cancellationToken).ConfigureAwait(false);

        if (!IsIndexableFile(path))
        {
            return 0;
        }

        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return await InsertChunksAsync(connection, projectId, path, content, context, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<int> IngestDirectoryAsync(SqliteConnection connection, string projectId, string path,
        string? context, CancellationToken cancellationToken)
    {
        var scope = await ReadScopeAsync(connection, projectId, cancellationToken).ConfigureAwait(false);
        RequireInScope(scope, path);

        // Directory.EnumerateFiles descends into directory symlinks, so a link inside the scoped
        // root can point anywhere on disk; a per-file recheck against the same scope list is the
        // only thing that actually enforces containment for what gets read. A file whose symlink
        // (directly, or via a symlinked ancestor directory) resolves outside scope is skipped, not
        // treated as a reason to refuse the whole directory — one stray link must not DoS an
        // otherwise legitimate ingest.
        var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Where(file => !IsHidden(file) && IsIndexableFile(file) && IsInScope(scope, file))
            .OrderBy(file => file, StringComparer.Ordinal);

        var indexed = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            indexed += await InsertChunksAsync(connection, projectId, file, content, context, cancellationToken)
                .ConfigureAwait(false);
        }

        return indexed;
    }

    /// <summary>Embeds one freshly inserted row when an engine is configured; deferred otherwise (FR-NM-3 s4; see docs/work/features-native-memory/native-memory.feature).</summary>
    private async Task<int> InsertChunksAsync(SqliteConnection connection, string projectId, string path,
        string content, string? context, CancellationToken cancellationToken)
    {
        var resolvedContext = context ?? ContextNaming.ProjectContext(projectId);
        var bucket = EntryBucket.For(resolvedContext, projectId);
        var (chunkMaxTokens, chunkOverlayTokens) = await ChunkSizeForAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        var chunks = chunker.Chunk(content, chunkMaxTokens, chunkOverlayTokens);
        if (chunks.Count == 0)
        {
            return 0;
        }

        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();

        var inserted = 0;
        foreach (var chunk in chunks)
        {
            var hash = ContentHash.Of(path, chunk);
            var exists = await connection.ExecuteScalarAsync<long?>(
                    Def(MemorySql.EntryExistsByPathAndHashInBucket,
                        new
                        {
                            hash,
                            path,
                            scope = bucket.Scope,
                            projectId = bucket.ProjectId,
                            contextLabel = bucket.ContextLabel,
                            workspaceId = bucket.WorkspaceId
                        }, cancellationToken))
                .ConfigureAwait(false) is not null;
            if (exists)
            {
                continue;
            }

            await connection.ExecuteAsync(
                    Def(MemorySql.InsertEntry,
                        new
                        {
                            hash,
                            path,
                            value = chunk,
                            sourceFile = path,
                            section = (string?)null,
                            scope = bucket.Scope,
                            projectId = bucket.ProjectId,
                            contextLabel = bucket.ContextLabel,
                            workspaceId = bucket.WorkspaceId,
                            agentId = (string?)null,
                            createdAt = now,
                            updatedAt = now
                        },
                        cancellationToken))
                .ConfigureAwait(false);
            // Re-select by bucket key: a concurrent same-file ingest may have won this chunk's
            // insert (ON CONFLICT DO NOTHING), and last_insert_rowid is stale on a lost race (F3).
            var chunkId = await connection.ExecuteScalarAsync<long?>(
                    Def(MemorySql.SelectChunkIdByPathAndHashInBucket,
                        new
                        {
                            hash,
                            path,
                            scope = bucket.Scope,
                            projectId = bucket.ProjectId,
                            contextLabel = bucket.ContextLabel,
                            workspaceId = bucket.WorkspaceId
                        }, cancellationToken))
                .ConfigureAwait(false);
            if (chunkId is null)
            {
                continue;
            }

            await embedder.EmbedIfConfiguredAsync(connection, chunkId.Value, chunk, cancellationToken)
                .ConfigureAwait(false);
            inserted++;
        }

        return inserted > 0 ? 1 : 0;
    }

    /// <summary>
    ///     Chunk bounds tied to the configured engine's token window; the chunk size never
    ///     exceeds the engine's max input tokens (avoids truncation dilution at embed time).
    /// </summary>
    private static async Task<(int MaxTokens, int OverlayTokens)> ChunkSizeForAsync(
        SqliteConnection connection, CancellationToken cancellationToken)
    {
        var provider = await connection.QuerySingleOrDefaultAsync<string?>(
                Def(MemorySql.SelectSetting, new { key = EmbeddingSettingsKeys.Provider }, cancellationToken))
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(provider))
        {
            return (DefaultMaxTokens, DefaultOverlayTokens);
        }

        var model = await connection.QuerySingleOrDefaultAsync<string?>(
                Def(MemorySql.SelectSetting, new { key = EmbeddingSettingsKeys.Model }, cancellationToken))
            .ConfigureAwait(false);
        var context = EmbeddingService.ContextTokensFor(provider, model);
        return (Math.Min(DefaultMaxTokens, context),
            Math.Min(DefaultOverlayTokens, Math.Max(0, context - 1)));
    }

    /// <summary>
    ///     Ingest reads whatever path it is handed, so the project's declared scope contains it —
    ///     the same rule and the same primitive memory_watch_add uses, and deny-by-default for the
    ///     same reason: an unscoped project would otherwise let any caller read any file the
    ///     server can. Enforced here rather than in the tool so every client is bound.
    /// </summary>
    private static async Task RequireInScopeAsync(SqliteConnection connection, string projectId, string path,
        CancellationToken cancellationToken)
    {
        var scope = await ReadScopeAsync(connection, projectId, cancellationToken).ConfigureAwait(false);
        RequireInScope(scope, path);
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

    private static void RequireInScope(IReadOnlyList<string> scope, string path)
    {
        var normalized = IngestPath.Normalize(path);
        if (!IsInScope(scope, normalized))
        {
            throw new PathOutsideScopeException(normalized);
        }
    }

    private static bool IsInScope(IReadOnlyList<string> scope, string path) =>
        scope.Any(entry => IngestPath.IsWithinScope(path, entry));

    private static async Task<string?> ReadSettingAsync(SqliteConnection connection, string key,
        CancellationToken cancellationToken) =>
        await connection.QuerySingleOrDefaultAsync<string?>(
                Def(MemorySql.SelectSetting, new { key }, cancellationToken))
            .ConfigureAwait(false);

    private static bool IsIndexableFile(string path) => !IsHidden(path) && IndexableExtensions.Contains(Path.GetExtension(path));

    private static bool IsHidden(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith('.');
    }

    private static CommandDefinition Def(string sql, object? parameters = null,
        CancellationToken cancellationToken = default) =>
        new(sql, parameters, cancellationToken: cancellationToken);
}
