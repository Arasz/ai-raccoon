using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Common;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Rating;
using AiRaccoon.Infrastructure.Embedding;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     IMemoryStore over our own memory.db tables (plan §2.2) with plain SQL: no sqlite-memory
///     functions, FTS5-only interim search (RRF + normalization are P6), on-row metadata and
///     embed_state driven by the configured embedding engine (P4): writes embed synchronously
///     once an engine exists, stay pending otherwise, and engine changes re-embed the bank.
/// </summary>
public sealed class SqliteMemoryStore(
    SqliteConnectionFactory factory,
    TimeProvider timeProvider,
    IChunker chunker,
    EmbeddingService embeddings)
    : IMemoryStore
{
    // Chunk bounds (P6b plan §8): 512 tokens exceeded the bundled all-MiniLM-L6-v2's
    // 256-token window, diluting embeddings via truncation; defaults are now 256/48 and the
    // chunk size is clamped to the configured engine's window where the engine knows it.
    private const int DefaultMaxTokens = 256;
    private const int DefaultOverlayTokens = 48;
    private const int EmbedBatchSize = 32;

    // The remote API key is held for the process lifetime only — never persisted (tool contract).
    // After a restart the operator's env var provides it again at embed time.
    private string? _remoteApiKey;

    private static readonly HashSet<string> IndexableExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".md", ".markdown", ".txt" };

    public async Task<MemoryEntry> WriteAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var context = ContextResolver.Resolve(request);
        var bucket = BucketFor(context, request.ProjectId);

        // memory_write carries no logical path; derive a stable one from the content itself so
        // identical content maps to the same slot, then scope the identity hash to it (FR-NM-7).
        var path = WritePathFor(request.Content);
        var hash = ContentHash.Of(path, request.Content);
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        // Global content dedup within the project's committed set: identical content anywhere in
        // committed rows (workspace_id IS NULL) returns the existing entry — no new row (FR-NM-7).
        var existing = await connection.QueryFirstOrDefaultAsync<EntryRow>(
                Def(MemorySql.SelectCommittedByValue,
                    new { value = request.Content, projectId = request.ProjectId }, cancellationToken))
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return ToEntry(existing);
        }

        await connection.ExecuteAsync(
                Def(MemorySql.InsertEntry,
                    new
                    {
                        hash,
                        path,
                        value = request.Content,
                        scope = bucket.Scope,
                        projectId = bucket.ProjectId,
                        contextLabel = bucket.ContextLabel,
                        workspaceId = bucket.WorkspaceId,
                        agentId = request.AgentId,
                        createdAt = now,
                        updatedAt = now
                    },
                    cancellationToken))
            .ConfigureAwait(false);

        var id = await connection.ExecuteScalarAsync<long>(
                Def("SELECT last_insert_rowid()", cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        await EmbedIfConfiguredAsync(connection, id, request.Content, cancellationToken).ConfigureAwait(false);
        var row = await connection.QueryFirstOrDefaultAsync<EntryRow>(
                Def(MemorySql.SelectEntryById, new { id }, cancellationToken))
            .ConfigureAwait(false);
        return ToEntry(row ?? throw new InvalidOperationException($"Insert stored no row for context '{context}'."));
    }

    public async Task<IReadOnlyList<MemorySearchResult>> SearchAsync(SearchQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        // Hybrid modalities (FR-NM-4): a keyword list from FTS5 and a semantic list from vec0.
        // No engine configured -> the query cannot be embedded, so the vec modality is absent
        // and search degrades to FTS5-only results (never a crash).
        var settings = await ReadEmbeddingSettingsAsync(connection, cancellationToken).ConfigureAwait(false);
        byte[]? queryVector = null;
        if (!string.IsNullOrWhiteSpace(settings.Provider))
        {
            var generator = embeddings.CreateGenerator(settings);
            var embedding = await generator.GenerateAsync([query.Query],
                cancellationToken: cancellationToken).ConfigureAwait(false);
            queryVector = EmbeddingBlob.ToBytes(embedding[0].Vector);
        }

        var plan = FtsQueryNormalizer.BuildPlan(query.Query);
        var batches = new List<IReadOnlyList<MemorySearchResult>>();

        // AND-with-OR-fallback (plan C Wave 1.3): run the primary expression; when it
        // under-matches — fewer rows than the query has terms, or fewer rows than the
        // caller asked for (a list that small cannot be a useful ranked signal on its own;
        // A6/C2 measured cases) — retry with the OR fallback. Keeps AND's precision for
        // short queries without the zero-match or under-match regression.
        async Task<int> RunPassAsync(string ftsExpression)
        {
            var ftsHits = 0;
            foreach (var context in SearchContexts.For(query))
            {
                var (filter, values) = FilterFor(context, query.ProjectId, "e.");
                var parameters = new DynamicParameters();
                parameters.Add("query", ftsExpression);
                // Per-modality candidate window (P6b plan §8): K = max(limit*3, 100) so RRF can
                // fuse overlap candidates ranked 20-100 that a per-modality LIMIT @limit starves;
                // the caller's limit and minScore still apply in the final merger pass.
                parameters.Add("limit", CandidateWindowFor(query.Limit));
                if (queryVector is not null)
                {
                    parameters.Add("queryVector", queryVector);
                }

                foreach (var (key, value) in values)
                {
                    parameters.Add(key, value);
                }

                var ftsResults = ftsExpression.Length == 0
                    ? []
                    : await QueryFtsBatchAsync(connection, filter, parameters, cancellationToken).ConfigureAwait(false);
                ftsHits += ftsResults.Count;
                var vectorResults = queryVector is null
                    ? []
                    : await QueryVectorBatchAsync(connection, filter, parameters, cancellationToken).ConfigureAwait(false);

                // Per-context modality fusion; minScore/limit belong to the final merger pass.
                batches.Add(ReciprocalRankFusion.Fuse(
                    [(ftsResults, query.FtsWeight), (vectorResults, query.VectorWeight)],
                    query.RrfK, minScore: 0, limit: int.MaxValue));
            }

            return ftsHits;
        }

        var ftsHits = await RunPassAsync(plan.Expression).ConfigureAwait(false);
        if (plan.Fallback is not null && ftsHits < Math.Max(plan.TokenCount, query.Limit))
        {
            batches.Clear();
            await RunPassAsync(plan.Fallback).ConfigureAwait(false);
        }

        var merged = SearchResultMerger.Merge(batches, query.Limit, query.MinScore, query.RrfK);
        await BumpAccessAsync(connection, merged, cancellationToken).ConfigureAwait(false);
        return merged;
    }

    /// <summary>
    ///     Per-modality candidate window before RRF fusion (P6b plan §8): K = max(limit*3, 100)
    ///     so overlap candidates ranked 20-100 are not starved by a per-modality LIMIT @limit.
    /// </summary>
    internal static int CandidateWindowFor(int limit) =>
        (int)Math.Clamp((long)limit * 3, 100, int.MaxValue);

    /// <summary>
    ///     Chunk bounds tied to the configured engine's token window (P6b plan §8); defaults
    ///     when no engine is configured. The chunk size never exceeds the engine's documented
    ///     max input tokens, preventing truncation dilution at embed time.
    /// </summary>
    private async Task<(int MaxTokens, int OverlayTokens)> ChunkSizeForAsync(
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

    private async Task<IReadOnlyList<MemorySearchResult>> QueryFtsBatchAsync(
        SqliteConnection connection, string filter, DynamicParameters parameters, CancellationToken cancellationToken)
    {
        try
        {
            // ftsExpression is normalized, but a pathological query can still trip the FTS5
            // tokenizer limits; a failed keyword modality degrades to the vector list.
            return (await connection.QueryAsync<SearchRow>(
                        new CommandDefinition(
                            MemorySql.SearchByFilter.Replace("{filter}", filter), parameters,
                            cancellationToken: cancellationToken))
                    .ConfigureAwait(false))
                .Select(row => new MemorySearchResult(
                    row.Hash, row.Seq, row.Ranking, row.Path,
                    string.IsNullOrEmpty(row.Snippet) ? SnippetFallback.From(row.Value, row.Hash) : row.Snippet))
                .ToList();
        }
        catch (SqliteException)
        {
            return [];
        }
    }

    private async Task<IReadOnlyList<MemorySearchResult>> QueryVectorBatchAsync(
        SqliteConnection connection, string filter, DynamicParameters parameters, CancellationToken cancellationToken)
    {
        var rows = (await connection.QueryAsync<VectorRow>(
                    new CommandDefinition(
                        MemorySql.VectorSearchByFilter.Replace("{filter}", filter), parameters,
                        cancellationToken: cancellationToken))
                .ConfigureAwait(false))
            .ToList();
        return rows
            .Select(row => new MemorySearchResult(
                row.Hash, row.Seq, 0, row.Path, SnippetFallback.From(row.Value, row.Hash)))
            .ToList();
    }

    public async Task<MemoryEntry> ShareAsync(string projectId, string hash,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        var source = await connection.QueryFirstOrDefaultAsync<SourceRow>(
                Def(MemorySql.SelectSourceByHashAndProject, new { hash, projectId }, cancellationToken))
            .ConfigureAwait(false);
        if (source is null)
        {
            throw new InvalidOperationException(
                $"No entry with hash '{hash}' in context '{ContextNaming.ProjectContext(projectId)}'.");
        }

        // Promotion creates a REAL shared-scope row under shared/<path>; the path-scoped hash
        // (FR-NM-7) differs from the source row's by construction. AddContentAsync is idempotent:
        // re-sharing finds the existing shared row.
        return await AddContentAsync(projectId, $"shared/{source.Path}", source.Value,
                ContextNaming.SharedContext, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(string projectId, string hash, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        var deleted = await connection.ExecuteAsync(
                Def(MemorySql.DeleteByHashAndProject, new { hash, projectId }, cancellationToken))
            .ConfigureAwait(false);
        return deleted > 0;
    }

    public async Task<int> DeleteContextAsync(string projectId, string context,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(context);

        var (filter, values) = FilterFor(context, projectId, "");
        var parameters = new DynamicParameters();
        foreach (var (key, value) in values)
        {
            parameters.Add(key, value);
        }

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(
                new CommandDefinition($"DELETE FROM entries WHERE {filter}", parameters,
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<MemoryStats> GetStatsAsync(string projectId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        // Entry count is scoped to this project's committed context: workspace scratch and
        // other projects' rows are excluded. Pending comes from embed_state (P4 embeds them).
        var entries = await connection.ExecuteScalarAsync<int>(
            Def(MemorySql.CountProjectEntries, new { projectId }, cancellationToken)).ConfigureAwait(false);
        var pendingCount = await connection.ExecuteScalarAsync<int>(
            Def(MemorySql.PendingCount, new { projectId }, cancellationToken)).ConfigureAwait(false);
        var contextList = (await connection.QueryAsync<string>(
                Def(MemorySql.CommittedContexts, cancellationToken: cancellationToken))
            .ConfigureAwait(false)).ToList();

        return new MemoryStats(entries, pendingCount, contextList);
    }

    public async Task<string> ListFilesAsync(string projectId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        var paths = await connection.QueryAsync<string>(
                Def(MemorySql.DistinctFilePaths, new { projectId }, cancellationToken))
            .ConfigureAwait(false);
        return BuildJsonTree(paths);
    }

    public async Task<int> IngestFileAsync(string projectId, string path, string? context,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!IsIndexableFile(path))
        {
            return 0;
        }

        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return await InsertChunksAsync(projectId, path, content, context, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> IngestDirectoryAsync(string projectId, string path, string? context,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Where(file => !IsHidden(file) && IsIndexableFile(file))
            .OrderBy(file => file, StringComparer.Ordinal);

        var indexed = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            indexed += await InsertChunksAsync(projectId, file, content, context, cancellationToken)
                .ConfigureAwait(false);
        }

        return indexed;
    }

    public async Task<EmbeddingConfig> ConfigureEmbeddingAsync(
        string projectId, string provider, string? model, string? baseUrl, string? apiKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        if (string.Equals(provider, "openai", StringComparison.OrdinalIgnoreCase))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(model);
        }

        // The API key is never persisted (tool contract); it resolves from arg/env at embed time.
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            _remoteApiKey = apiKey;
        }

        var previous = await connection.QuerySingleOrDefaultAsync<string?>(
                Def(MemorySql.SelectSetting, new { key = EmbeddingSettingsKeys.Engine }, cancellationToken))
            .ConfigureAwait(false);

        await connection.ExecuteAsync(
                Def(MemorySql.UpsertSetting,
                    new { key = EmbeddingSettingsKeys.Provider, value = provider }, cancellationToken))
            .ConfigureAwait(false);
        if (model is not null)
        {
            await connection.ExecuteAsync(
                    Def(MemorySql.UpsertSetting,
                        new { key = EmbeddingSettingsKeys.Model, value = model }, cancellationToken))
                .ConfigureAwait(false);
        }

        if (baseUrl is not null)
        {
            await connection.ExecuteAsync(
                    Def(MemorySql.UpsertSetting,
                        new { key = EmbeddingSettingsKeys.BaseUrl, value = baseUrl }, cancellationToken))
                .ConfigureAwait(false);
        }

        var engine = EmbeddingService.EngineFingerprint(provider, model, baseUrl);
        await connection.ExecuteAsync(
                Def(MemorySql.UpsertSetting, new { key = EmbeddingSettingsKeys.Engine, value = engine },
                    cancellationToken))
            .ConfigureAwait(false);

        // Engine change → re-embed the bank with the new engine (FR-NM-3 s6): previously
        // embedded rows are embedded again; the pending queue is untouched — memory_embed_pending
        // owns it (s5).
        if (!string.Equals(previous, engine, StringComparison.Ordinal))
        {
            var reEmbedRows = (await connection.QueryAsync<EmbedRow>(
                    Def(MemorySql.SelectEmbeddedForProject, new { projectId }, cancellationToken))
                .ConfigureAwait(false)).ToList();
            await EmbedBatchAsync(connection, reEmbedRows, cancellationToken).ConfigureAwait(false);
        }

        return new EmbeddingConfig(provider, model ?? "bundled", engine);
    }

    public async Task<EmbedPendingResult> EmbedPendingAsync(string projectId, int? limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        // No engine configured: nothing can be embedded; pending is reported from embed_state.
        var provider = await connection.QuerySingleOrDefaultAsync<string?>(
                Def(MemorySql.SelectSetting, new { key = EmbeddingSettingsKeys.Provider }, cancellationToken))
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(provider))
        {
            var pendingCount = await connection.ExecuteScalarAsync<int>(
                Def(MemorySql.PendingCount, new { projectId }, cancellationToken)).ConfigureAwait(false);
            return new EmbedPendingResult(0, pendingCount);
        }

        var processed = await EmbedRowsAsync(connection, projectId, limit, cancellationToken).ConfigureAwait(false);
        var remaining = await connection.ExecuteScalarAsync<int>(
            Def(MemorySql.PendingCount, new { projectId }, cancellationToken)).ConfigureAwait(false);
        return new EmbedPendingResult(processed, remaining);
    }

    public async Task<MemoryEntry> AddContentAsync(
        string projectId, string path, string content, string? context, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        var resolvedContext = context ?? ContextNaming.ProjectContext(projectId);
        var bucket = BucketFor(resolvedContext, projectId);
        var bucketParams = new { path, scope = bucket.Scope, projectId = bucket.ProjectId, contextLabel = bucket.ContextLabel, workspaceId = bucket.WorkspaceId };

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        var existing = await connection.QueryFirstOrDefaultAsync<EntryRow>(
                Def(MemorySql.SelectEntryByPathInBucket, bucketParams, cancellationToken))
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return ToEntry(existing);
        }

        var hash = ContentHash.Of(path, content);
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        await connection.ExecuteAsync(
                Def(MemorySql.InsertEntry,
                    new
                    {
                        hash,
                        path,
                        value = content,
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

        var inserted = await connection.QueryFirstOrDefaultAsync<EntryRow>(
                Def(MemorySql.SelectEntryByPathInBucket, bucketParams, cancellationToken))
            .ConfigureAwait(false);
        if (inserted is null)
        {
            throw new InvalidOperationException(
                $"memory_add_content stored no row for '{path}' in '{resolvedContext}'.");
        }

        await EmbedIfConfiguredAsync(connection, inserted.Id, content, cancellationToken).ConfigureAwait(false);
        return ToEntry(inserted);
    }

    public async Task<IReadOnlyList<MemoryEntry>> ListContextAsync(string projectId, string context,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(context);

        var (filter, values) = FilterFor(context, projectId, "");
        var parameters = new DynamicParameters();
        foreach (var (key, value) in values)
        {
            parameters.Add(key, value);
        }

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<EntryRow>(
                new CommandDefinition(
                    MemorySql.SelectEntriesByContext.Replace("{filter}", filter), parameters,
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return [.. rows.Select(ToEntry)];
    }

    public async Task<EntryMetadata?> GetMetadataAsync(string projectId, string hash,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        var row = await connection.QueryFirstOrDefaultAsync<MetadataRow>(
                Def(MemorySql.SelectEntryMetadata, new { projectId, hash }, cancellationToken))
            .ConfigureAwait(false);
        return row is null ? null : new EntryMetadata(row.Rating, row.TtlDays);
    }

    public async Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<string?>(
                Def(MemorySql.SelectSetting, new { key }, cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(
                Def(MemorySql.UpsertSetting, new { key, value }, cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task SetEntryTtlAsync(string projectId, string hash, double ttlDays,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(
                Def(MemorySql.UpdateEntryTtl, new { projectId, hash, ttlDays }, cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>Embeds one freshly inserted row when an engine is configured; deferred otherwise (FR-NM-3 s4).</summary>
    private async Task EmbedIfConfiguredAsync(SqliteConnection connection, long id, string value,
        CancellationToken cancellationToken)
    {
        var provider = await connection.QuerySingleOrDefaultAsync<string?>(
                Def(MemorySql.SelectSetting, new { key = EmbeddingSettingsKeys.Provider }, cancellationToken))
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(provider))
        {
            return;
        }

        var settings = await ReadEmbeddingSettingsAsync(connection, cancellationToken).ConfigureAwait(false);
        var generator = embeddings.CreateGenerator(settings);
        var result = await generator.GenerateAsync([value], cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var blob = EmbeddingBlob.ToBytes(result[0].Vector);
        await connection.ExecuteAsync(
                Def(MemorySql.MarkEmbedded, new { id, embedding = blob }, cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>Embeds a project's pending rows in batches with the configured engine.</summary>
    private async Task<int> EmbedRowsAsync(SqliteConnection connection, string projectId, int? limit,
        CancellationToken cancellationToken)
    {
        var processed = 0;
        while (true)
        {
            var remaining = (limit ?? int.MaxValue) - processed;
            if (remaining <= 0)
            {
                break;
            }

            var batch = (await connection.QueryAsync<EmbedRow>(
                    Def(MemorySql.SelectPendingForEmbed,
                        new { projectId, limit = Math.Min(EmbedBatchSize, remaining) }, cancellationToken))
                .ConfigureAwait(false)).ToList();
            if (batch.Count == 0)
            {
                break;
            }

            processed += await EmbedBatchAsync(connection, batch, cancellationToken).ConfigureAwait(false);
        }

        return processed;
    }

    /// <summary>Embeds a set of rows with the configured engine; missing rows are skipped.</summary>
    private async Task<int> EmbedBatchAsync(SqliteConnection connection, IReadOnlyList<EmbedRow> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return 0;
        }

        var settings = await ReadEmbeddingSettingsAsync(connection, cancellationToken).ConfigureAwait(false);
        var generator = embeddings.CreateGenerator(settings);
        for (var offset = 0; offset < rows.Count; offset += EmbedBatchSize)
        {
            var batch = rows.Skip(offset).Take(EmbedBatchSize).ToList();
            var result = await generator.GenerateAsync(batch.Select(r => r.Value),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < batch.Count; i++)
            {
                var blob = EmbeddingBlob.ToBytes(result[i].Vector);
                await connection.ExecuteAsync(
                        Def(MemorySql.MarkEmbedded, new { id = batch[i].Id, embedding = blob }, cancellationToken))
                    .ConfigureAwait(false);
            }
        }

        return rows.Count;
    }

    private async Task<EmbeddingSettings> ReadEmbeddingSettingsAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var provider = await connection.QuerySingleOrDefaultAsync<string?>(
                Def(MemorySql.SelectSetting, new { key = EmbeddingSettingsKeys.Provider }, cancellationToken))
            .ConfigureAwait(false);
        var model = await connection.QuerySingleOrDefaultAsync<string?>(
                Def(MemorySql.SelectSetting, new { key = EmbeddingSettingsKeys.Model }, cancellationToken))
            .ConfigureAwait(false);
        var baseUrl = await connection.QuerySingleOrDefaultAsync<string?>(
                Def(MemorySql.SelectSetting, new { key = EmbeddingSettingsKeys.BaseUrl }, cancellationToken))
            .ConfigureAwait(false);
        return new EmbeddingSettings(provider ?? "", model, baseUrl, _remoteApiKey);
    }

    private async Task<int> InsertChunksAsync(string projectId, string path, string content, string? context,
        CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        var resolvedContext = context ?? ContextNaming.ProjectContext(projectId);
        var bucket = BucketFor(resolvedContext, projectId);
        var (chunkMaxTokens, chunkOverlayTokens) = await ChunkSizeForAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        var chunks = chunker.Chunk(content, chunkMaxTokens, chunkOverlayTokens);
        if (chunks.Count == 0)
        {
            return 0;
        }

        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var bucketParams = new { path, scope = bucket.Scope, projectId = bucket.ProjectId, contextLabel = bucket.ContextLabel, workspaceId = bucket.WorkspaceId };

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
            var chunkId = await connection.ExecuteScalarAsync<long>(
                    Def("SELECT last_insert_rowid()", cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            await EmbedIfConfiguredAsync(connection, chunkId, chunk, cancellationToken).ConfigureAwait(false);
            inserted++;
        }

        return inserted > 0 ? 1 : 0;
    }

    /// <summary>Rating-pipeline rewire: search hits bump the on-row access/rating columns (MetaStore is gone).</summary>
    private async Task BumpAccessAsync(SqliteConnection connection, IReadOnlyList<MemorySearchResult> results,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        foreach (var hash in results.Select(r => r.Hash).Distinct(StringComparer.Ordinal))
        {
            var row = await connection.QueryFirstOrDefaultAsync<RatingRow>(
                    Def(MemorySql.SelectRatingForBump, new { hash }, cancellationToken))
                .ConfigureAwait(false);
            if (row is null)
            {
                continue;
            }

            var ageDays = Math.Max(0, (now - row.CreatedAt) / 86_400.0);
            var rating = RatingPolicy.Rating(
                RatingPolicy.DefaultBaseScore, row.AccessCount + 1, ageDays, RatingPolicy.DefaultHalfLifeDays);
            await connection.ExecuteAsync(
                    Def(MemorySql.BumpAccess, new { hash, now, rating }, cancellationToken))
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Maps a context string to a row filter. Built only from constant fragments; every value
    ///     goes through parameters, so a user-supplied context string can never inject SQL.
    /// </summary>
    private static (string Filter, IReadOnlyDictionary<string, object?> Values) FilterFor(
        string context, string projectId, string alias)
    {
        if (context == ContextNaming.SharedContext)
        {
            return ($"{alias}scope = 'shared'", new Dictionary<string, object?>());
        }

        if (context.StartsWith("project:", StringComparison.Ordinal))
        {
            return ($"{alias}scope = 'project' AND {alias}project_id = @projectId",
                new Dictionary<string, object?> { ["projectId"] = context["project:".Length..] });
        }

        if (context.StartsWith("workspace:", StringComparison.Ordinal))
        {
            return ($"{alias}workspace_id = @workspaceId AND {alias}project_id = @projectId",
                new Dictionary<string, object?> { ["workspaceId"] = context["workspace:".Length..], ["projectId"] = projectId });
        }

        return ($"{alias}scope = 'custom' AND {alias}context_label = @contextLabel AND {alias}project_id = @projectId",
            new Dictionary<string, object?> { ["contextLabel"] = context, ["projectId"] = projectId });
    }

    private static (string? Scope, string ProjectId, string? ContextLabel, string? WorkspaceId) BucketFor(
        string context, string projectId)
    {
        if (context == ContextNaming.SharedContext)
        {
            return ("shared", projectId, null, null);
        }

        if (context.StartsWith("project:", StringComparison.Ordinal))
        {
            return ("project", context["project:".Length..], null, null);
        }

        if (context.StartsWith("workspace:", StringComparison.Ordinal))
        {
            return (null, projectId, null, context["workspace:".Length..]);
        }

        return ("custom", projectId, context, null);
    }

    private static MemoryEntry ToEntry(EntryRow row) =>
        new(row.Hash, row.Path, ContextStringOf(row), row.Value, row.CreatedAt);

    private static string ContextStringOf(EntryRow row)
    {
        if (row.WorkspaceId is not null)
        {
            return ContextNaming.WorkspaceContext(row.WorkspaceId);
        }

        return row.Scope switch
        {
            "shared" => ContextNaming.SharedContext,
            "project" => ContextNaming.ProjectContext(row.ProjectId),
            "custom" => row.ContextLabel ?? "",
            _ => ""
        };
    }

    /// <summary>memory_write has no caller path; a content-derived name keeps the slot stable (FR-NM-7).</summary>
    private static string WritePathFor(string value) =>
        $"{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))}.md";

    private static bool IsIndexableFile(string path) =>
        !IsHidden(path) && IndexableExtensions.Contains(Path.GetExtension(path));

    private static bool IsHidden(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith('.');
    }

    private static string BuildJsonTree(IEnumerable<string> paths)
    {
        var root = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var node = root;
            var segments = path.Split('/');
            for (var i = 0; i < segments.Length - 1; i++)
            {
                if (!node.TryGetValue(segments[i], out var child) || child is not SortedDictionary<string, object> dir)
                {
                    dir = new SortedDictionary<string, object>(StringComparer.Ordinal);
                    node[segments[i]] = dir;
                }

                node = dir;
            }

            node[segments[^1]] = new object();
        }

        return JsonSerializer.Serialize(root);
    }

    private static CommandDefinition Def(string sql, object? parameters = null,
        CancellationToken cancellationToken = default) =>
        new(sql, parameters, cancellationToken: cancellationToken);

    private sealed class EntryRow
    {
        public long Id { get; set; }

        public string Hash { get; set; } = "";

        public string Path { get; set; } = "";

        public string Value { get; set; } = "";

        public string? Scope { get; set; }

        public string ProjectId { get; set; } = "";

        public string? ContextLabel { get; set; }

        public string? WorkspaceId { get; set; }

        public long CreatedAt { get; set; }
    }

    private sealed class SearchRow
    {
        public string Hash { get; set; } = "";

        public int Seq { get; set; }

        public double Ranking { get; set; }

        public string Path { get; set; } = "";

        public string Snippet { get; set; } = "";

        public string Value { get; set; } = "";
    }

    private sealed class VectorRow
    {
        public string Hash { get; set; } = "";

        public int Seq { get; set; }

        public string Path { get; set; } = "";

        public string Value { get; set; } = "";
    }

    private sealed record SourceRow(string Path, string Value);

    private sealed class EmbedRow
    {
        public long Id { get; set; }

        public string Value { get; set; } = "";
    }

    private sealed class RatingRow
    {
        public long CreatedAt { get; set; }

        public int AccessCount { get; set; }
    }

    private sealed class MetadataRow
    {
        public double Rating { get; set; }

        public int? TtlDays { get; set; }
    }
}
