using System.Diagnostics.CodeAnalysis;
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
///     opened the bank once for the whole walk, so the compiler enforces one-bank-open-per-ingest.
/// </summary>
public sealed class FileIngestor(
    IFileTypeMatcher fileTypeMatcher,
    IEntryEmbedder embedder,
    IMemorySourceStore sourceStore,
    TimeProvider timeProvider) : IFileIngestor
{
    private const int DefaultMaxTokens = 256;

    /// <summary>
    ///     The bundled vocab never changes with the configured model path (see
    ///     <see cref="EmbeddingService.CreateLocal" />), so one real BERT tokenizer is built lazily and
    ///     reused for every local-engine ingest in this process (docs/adr/0036) — counting is done with
    ///     the same tokenizer that will actually embed the chunk, not the o200k budget proxy.
    /// </summary>
    /// <remarks>
    ///     Known, deliberately unaddressed gap (docs/adr/0036): a long punctuation-free, newline-joined
    ///     run (e.g. a hash list) can collapse to a single [UNK] under this tokenizer's pretokenizer,
    ///     reporting an implausibly small count that a budget ceiling alone would not catch. Measured
    ///     against the live bank at 1/15,246 entries affecting a 123-char fragment — real but
    ///     low-impact; <see cref="OnnxEmbeddingGenerator" />'s embed-time detector makes it visible.
    ///     Chunker-side remediation is out of scope for this wave.
    /// </remarks>
    private static readonly Lazy<TokenCount> LocalCountTokens = new(() =>
    {
        var tokenizer = OnnxEmbeddingGenerator.CreateTokenizer(BundledModel.ResolveVocabPath());
        return text => tokenizer.CountTokens(text);
    });

    /// <summary>
    ///     Set <paramref name="embedInline" /> false when the caller holds a write transaction: embedding
    ///     runs the engine per chunk, and a lock held that long stalls another process's first bank open.
    /// </summary>
    public async Task<int> IngestFileAsync(SqliteConnection connection, string projectId, string path,
        string? context, CancellationToken cancellationToken, bool embedInline = true)
    {
        await RequireInScopeAsync(connection, projectId, path, cancellationToken).ConfigureAwait(false);

        if (!IsIndexableFile(path, out var handler))
        {
            return 0;
        }

        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return await InsertChunksAsync(connection, projectId, path, content, handler, context, cancellationToken, embedInline)
            .ConfigureAwait(false);
    }

    public async Task<int> IngestDirectoryAsync(SqliteConnection connection, string projectId, string path,
        string? context, CancellationToken cancellationToken)
    {
        var scope = await ReadScopeAsync(connection, projectId, cancellationToken).ConfigureAwait(false);
        RequireInScope(scope, path);

        var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Where(file => !IsHidden(file) && IsInScope(scope, file))
            .OrderBy(file => file, StringComparer.Ordinal);

        var indexed = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsIndexableFile(file, out var handler))
            {
                continue;
            }

            var content = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            indexed += await InsertChunksAsync(connection, projectId, file, content, handler, context, cancellationToken)
                .ConfigureAwait(false);
        }

        return indexed;
    }

    /// <summary>Embeds one freshly inserted row when an engine is configured; deferred otherwise (FR-NM-3 s4; see docs/work/features-native-memory/native-memory.feature).</summary>
    private async Task<int> InsertChunksAsync(SqliteConnection connection, string projectId, string path,
        string content, IFileTypeHandler handler, string? context, CancellationToken cancellationToken, bool embedInline = true)
    {
        var resolvedContext = context ?? ContextNaming.ProjectContext(projectId);
        var bucket = EntryBucket.For(resolvedContext, projectId);
        var (chunkMaxTokens, chunkOverlayTokens, chunkCountTokens) = await ChunkSizeForAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        var chunks = handler.Chunker.Chunk(content, chunkMaxTokens, chunkOverlayTokens, chunkCountTokens);
        if (chunks.Count == 0)
        {
            return 0;
        }

        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();

        // Resolve source once for all chunks from this file.
        var source = await sourceStore.ResolveOrCreateOnConnectionAsync(
            connection, SourceType.File, path, null, null, cancellationToken).ConfigureAwait(false);

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
                            updatedAt = now,
                            sourceId = source.Id
                        },
                        cancellationToken))
                .ConfigureAwait(false);

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

            if (embedInline)
            {
                await embedder.EmbedIfConfiguredAsync(connection, chunkId.Value, chunk, cancellationToken)
                    .ConfigureAwait(false);
            }

            inserted++;
        }

        var ctx = MemorySql.ContextKeyFor(resolvedContext, projectId);
        await connection.ExecuteAsync(
                Def(MemorySql.RecomputeChunkColumnsForContext, new { ctx, sourceFile = path }, cancellationToken))
            .ConfigureAwait(false);

        return inserted > 0 ? 1 : 0;
    }

    /// <summary>
    ///     Resolves the chunk budget from the configured engine (docs/adr/0036): "local" also supplies
    ///     the real BERT tokenizer as the counting override, so the budget and the counter that enforces
    ///     it always agree with what will actually embed the chunk. Other providers keep the default
    ///     o200k counter (unchanged) — the mismatch this fixes is specific to the bundled model.
    /// </summary>
    private static async Task<(int MaxTokens, int OverlayTokens, TokenCount? CountTokens)> ChunkSizeForAsync(
        SqliteConnection connection, CancellationToken cancellationToken)
    {
        var provider = await connection.QuerySingleOrDefaultAsync<string?>(
                Def(MemorySql.SelectSetting, new { key = EmbeddingSettingsKeys.Provider }, cancellationToken))
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(provider))
        {
            return (DefaultMaxTokens, ChunkingDefaults.OverlayTokens, null);
        }

        var model = await connection.QuerySingleOrDefaultAsync<string?>(
                Def(MemorySql.SelectSetting, new { key = EmbeddingSettingsKeys.Model }, cancellationToken))
            .ConfigureAwait(false);
        var maxTokens = Math.Min(DefaultMaxTokens, EmbeddingService.SafeChunkBudgetFor(provider, model));
        var overlayTokens = Math.Min(ChunkingDefaults.OverlayTokens, Math.Max(0, maxTokens - 1));
        var countTokens = provider.Equals("local", StringComparison.OrdinalIgnoreCase) ? LocalCountTokens.Value : null;
        return (maxTokens, overlayTokens, countTokens);
    }

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

    private static bool IsInScope(IReadOnlyList<string> scope, string path) => scope.Any(entry => IngestPath.IsWithinScope(path, entry));

    private static async Task<string?> ReadSettingAsync(SqliteConnection connection, string key,
        CancellationToken cancellationToken) =>
        await connection.QuerySingleOrDefaultAsync<string?>(
                Def(MemorySql.SelectSetting, new { key }, cancellationToken))
            .ConfigureAwait(false);

    private bool IsIndexableFile(string path, [NotNullWhen(true)] out IFileTypeHandler? handler)
    {
        if (!IsHidden(path))
        {
            return fileTypeMatcher.TryGetHandler(path, out handler);
        }

        handler = null;
        return false;
    }

    private static bool IsHidden(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith('.');
    }

    private static CommandDefinition Def(string sql, object? parameters = null,
        CancellationToken cancellationToken = default) =>
        new(sql, parameters, cancellationToken: cancellationToken);
}
