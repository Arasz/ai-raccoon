using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiRaccoon.Core.Isolation;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Core.Rating;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Ingestion;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     IMemoryStore over the single-file memory.db. Plain SQL, FTS5 + vec0
///     hybrid search, on-row metadata, embed_state driven by the configured engine.
/// </summary>
public sealed partial class SqliteMemoryStore(
    ISqliteConnectionFactory factory,
    IMemorySourceStore sourceStore,
    IFileIngestor fileIngestor,
    IEntryEmbedder embedder,
    TimeProvider timeProvider,
    ILogger<SqliteMemoryStore> logger,
    INoiseFilteringService noiseFilteringService)
    : IMemoryStore
{
    private const string SharedScope = "shared";
    private readonly IEntryEmbedder _embedder = embedder;

    public async Task<MemoryEntry> WriteAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        var noiseEnabled = NoiseConfigKeys.ParseEnabled(
            await ReadSettingAsync(connection, NoiseConfigKeys.EnabledGlobal, cancellationToken).ConfigureAwait(false));
        if (noiseEnabled)
        {
            var noiseResult = await noiseFilteringService.EvaluatePreWriteAsync(request, cancellationToken)
                .ConfigureAwait(false);
            if (noiseResult.IsNoise)
            {
                return new MemoryEntry(string.Empty, string.Empty, request.Context ?? string.Empty, request.Content,
                    now, Stored: false, Reason: $"rejected by noise policy '{noiseResult.PolicyName}'");
            }
        }

        var context = ContextResolver.Resolve(request);
        var bucket = EntryBucket.For(context, request.ProjectId);

        // memory_write carries no logical path; derive a stable one from the content itself so
        // identical content maps to the same slot, then scope the identity hash to it (FR-NM-7; see docs/work/features-native-memory/native-memory.feature).
        var path = WritePathFor(request.Content);
        var hash = ContentHash.Of(path, request.Content);

        if (bucket.WorkspaceId is not null)
        {
            await RequireActiveWorkspaceAsync(connection, bucket.WorkspaceId, request.ProjectId, cancellationToken)
                .ConfigureAwait(false);
        }

        if (bucket.WorkspaceId is null)
        {
            var existing = await connection.QueryFirstOrDefaultAsync<EntryRow>(
                    Def(MemorySql.SelectCommittedByValue,
                        new { value = request.Content, projectId = request.ProjectId }, cancellationToken))
                .ConfigureAwait(false);
            if (existing is not null)
            {
                return ToEntry(existing);
            }
        }

        var source = await ResolveSourceAsync(connection, request.SourceFile, request.Section, cancellationToken)
            .ConfigureAwait(false);

        await connection.ExecuteAsync(
                Def(MemorySql.InsertEntry,
                    new
                    {
                        hash,
                        path,
                        value = request.Content,
                        sourceFile = source.SourceLocator is { Length: > 0 } ? source.SourceLocator : null,
                        section = request.Section,
                        scope = bucket.Scope,
                        projectId = bucket.ProjectId,
                        contextLabel = bucket.ContextLabel,
                        workspaceId = bucket.WorkspaceId,
                        agentId = request.AgentId,
                        createdAt = now,
                        updatedAt = now,
                        sourceId = source.Id
                    },
                    cancellationToken))
            .ConfigureAwait(false);

        var row = bucket.Scope == SharedScope
            ? await connection.QueryFirstOrDefaultAsync<EntryRow>(
                    Def(MemorySql.SelectSharedEntryByPathAndHash,
                        new { path, hash }, cancellationToken))
                .ConfigureAwait(false)
            : await connection.QueryFirstOrDefaultAsync<EntryRow>(
                    Def(MemorySql.SelectEntryByPathInBucket,
                        new
                        {
                            path,
                            scope = bucket.Scope,
                            projectId = bucket.ProjectId,
                            contextLabel = bucket.ContextLabel,
                            workspaceId = bucket.WorkspaceId
                        }, cancellationToken))
                .ConfigureAwait(false);

        if (row is null)
        {
            throw new InvalidOperationException($"Insert stored no row for context '{context}'.");
        }

        if (request.SourceFile is not null)
        {
            await RecomputeChunkColumnsAsync(connection, context, request.ProjectId, request.SourceFile,
                cancellationToken).ConfigureAwait(false);
        }

        await _embedder.EmbedIfConfiguredAsync(connection, row.Id, request.Content, cancellationToken).ConfigureAwait(false);
        return ToEntry(row);
    }

    public async Task<IReadOnlyList<MemorySearchResult>> SearchAsync(SearchQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        var queryVector = await _embedder.EmbedQueryAsync(connection, query.Query, cancellationToken)
            .ConfigureAwait(false);

        var plan = FtsQueryNormalizer.BuildPlan(query.Query);
        var isPathQuery = SourcePathQuery.TryBuild(query.Query, out var pathExpression);
        if (isPathQuery)
        {
            plan = plan with { Expression = pathExpression, Fallback = null };
        }


        var alpha = StructureFusion.DefaultAlpha;
        if (queryVector is not null)
        {
            alpha = await ReadStructureAlphaAsync(connection, cancellationToken).ConfigureAwait(false);
        }

        var ftsBatches = new List<IReadOnlyList<MemorySearchResult>>();
        var vectorBatches = new List<IReadOnlyList<MemorySearchResult>>();
        var contexts = SearchContexts.For(query).ToList();
        var valueByHash = new Dictionary<string, string>(StringComparer.Ordinal);
        var ftsQueryByHash = new Dictionary<string, string>(StringComparer.Ordinal);
        var idByHash = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var context in contexts)
        {
            var (filter, values) = FilterFor(context, query.ProjectId, "e.");
            var ctx = MemorySql.ContextKeyFor(context, query.ProjectId);
            var limit = CandidateWindowFor(query.Limit, query.CandidateWindow);

            IReadOnlyList<MemorySearchResult> ftsResults = [];
            if (plan.Expression.Length > 0 && query.FtsWeight != 0)
            {
                ftsResults = await QueryFtsBatchAsync(connection, filter, plan.Expression,
                        SearchParameters(plan.Expression), valueByHash, ftsQueryByHash, idByHash, cancellationToken)
                    .ConfigureAwait(false);
                if (plan.Fallback is not null && ftsResults.Count <= Math.Max(plan.TokenCount, query.Limit))
                {
                    ftsResults = await QueryFtsBatchAsync(connection, filter, plan.Fallback,
                            SearchParameters(plan.Fallback), valueByHash, ftsQueryByHash, idByHash, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            var vectorResults = queryVector is null || query.VectorWeight == 0
                ? []
                : await QueryDualVectorBatchAsync(connection,
                    VectorParameters(), alpha, valueByHash, cancellationToken).ConfigureAwait(false);

            ftsBatches.Add(ftsResults);
            vectorBatches.Add(vectorResults);
            continue;

            DynamicParameters SearchParameters(string ftsExpression)
            {
                var parameters = new DynamicParameters();
                parameters.Add("query", ftsExpression);
                parameters.Add("limit", limit);
                if (queryVector is not null)
                {
                    parameters.Add("queryVector", queryVector);
                }

                foreach (var (key, value) in values)
                {
                    parameters.Add(key, value);
                }

                return parameters;
            }

            DynamicParameters VectorParameters()
            {
                var parameters = new DynamicParameters();
                parameters.Add("ctx", ctx);
                parameters.Add("limit", limit);
                parameters.Add("queryVector", queryVector);
                return parameters;
            }
        }

        var fused = ReciprocalRankFusion.Fuse(
            [
                (ModalityCandidates.ByBm25(ftsBatches), query.FtsWeight),
                (ModalityCandidates.ByCosine(vectorBatches), query.VectorWeight)
            ],
            query.RrfK, 0, int.MaxValue);
        var merged = SearchResultMerger.Merge([fused], query.Limit, query.MinScore, query.RrfK,
            isPathQuery ? 0.0 : query.SourceLambda, query.ConsolidationThreshold, query.DocScoreFormula);
        merged = await ResolveDeferredSnippetsAsync(connection, merged, valueByHash, ftsQueryByHash, idByHash,
            cancellationToken).ConfigureAwait(false);
        await BumpAccessAsync(connection, merged, query.ProjectId, cancellationToken).ConfigureAwait(false);
        return merged;
    }

    public async Task<MemoryEntryResult> ShareAsync(string projectId, string hash,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        var source = await connection.QueryFirstOrDefaultAsync<SourceRow>(
                Def(MemorySql.SelectSourceByHashAndProject, new { hash, projectId }, cancellationToken))
            .ConfigureAwait(false);
        return source is null
            ? throw new UnknownHashException(hash, projectId)
            : await AddContentAsync(projectId, $"shared/{ContentHash.OfValue(source.Value)}.md",
                    source.Value, ContextNaming.SharedContext, source.SourceFile, source.Section, cancellationToken)
                .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ExtractionCandidateRow>> ExtractCandidatesAsync(string projectId,
        bool includeTtlRows, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<ExtractionRow>(
                Def(MemorySql.SelectExtractionCandidates,
                    new { projectId, includeTtlRows = includeTtlRows ? 1 : 0 }, cancellationToken))
            .ConfigureAwait(false);

        // Operator-configured source_file exclusions (extract.exclude.prefixes): rows whose
        // source_file starts with an excluded prefix never become shared-extraction candidates.
        var excluded = ExtractionConfigKeys.ParseExcludePrefixes(
            await connection.QueryFirstOrDefaultAsync<string?>(
                    Def(MemorySql.SelectSetting,
                        new { key = ExtractionConfigKeys.ExcludePrefixesGlobal }, cancellationToken))
                .ConfigureAwait(false));
        return
        [
            .. rows
                .Where(r => r.SourceFile is null ||
                            !excluded.Any(p => r.SourceFile.StartsWith(p, StringComparison.Ordinal)))
                .Select(r => r.ToCandidate())
        ];
    }

    public async Task<SharedIndex> GetSharedIndexAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<SharedRow>(
                Def(MemorySql.SelectSharedIndex, cancellationToken, cancellationToken))
            .ConfigureAwait(false);
        var indexed = rows.ToList();
        return new SharedIndex(
            [.. indexed.Select(r => r.Value)],
            [.. indexed.Select(r => r.Path)]);
    }

    public async Task<IReadOnlyList<string>> GetProjectIdsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<string>(
                Def(MemorySql.SelectProjectIds, cancellationToken))
            .ConfigureAwait(false);
        return [.. rows];
    }

    public async Task<bool> DeleteAsync(string projectId, string hash, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        return await DeleteCoreAsync(connection, projectId, hash, null, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     The sweep's own delete (H2): restricted to the one scope it enumerated, so a
    ///     project-scoped pass cannot also remove a sibling row that shares this hash in another
    ///     scope or workspace — hash alone is not a unique row (ContentHash.Of has no scope
    ///     input). Unlike <see cref="DeleteAsync" />/memory_delete, which targets a hash wherever
    ///     it lives; this is a narrower, internal verb, not a public tool.
    /// </summary>
    public async Task<bool> DeleteInScopeAsync(string projectId, string hash, string scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        return await DeleteCoreAsync(connection, projectId, hash, scope, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    ///     Removes every committed chunk of one source path and its subtree (directory delete
    ///     cascades), plus the per-path watch fingerprints, in the same transaction — a
    ///     delete-then-recreate cycle must not hash-skip back to stale chunks. Watch registration survives.
    /// </summary>
    public async Task<int> DeleteSourcePathAsync(string projectId, string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(
                new CommandDefinition("BEGIN IMMEDIATE", cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        try
        {
            var pathPrefix = LikePattern.Escape(path) + "/%";
            var deleted = await connection.ExecuteAsync(
                    Def(MemorySql.DeleteBySourcePath, new { projectId, path, pathPrefix }, cancellationToken))
                .ConfigureAwait(false);
            await connection.ExecuteAsync(
                    Def(MemorySql.DeleteWatchFilesByProjectPathCascade,
                        new { projectId, path, pathPrefix }, cancellationToken))
                .ConfigureAwait(false);
            await connection.ExecuteAsync(
                    new CommandDefinition("COMMIT", cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            return deleted;
        }
        catch
        {
            await connection.ExecuteAsync(
                    new CommandDefinition("ROLLBACK", cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    ///     Watch-digest replace-by-path, all in one transaction so no reader ever sees the file
    ///     chunkless and a crash rolls the whole replace back: fingerprint re-check (the write lock
    ///     makes it the authoritative one, so a concurrent process re-does no chunking or embedding),
    ///     delete, re-ingest, fingerprint write. Embedding is left pending for the caller to run
    ///     after the commit — the engine is far too slow to hold the bank's write lock through.
    /// </summary>
    public async Task<bool> ReplaceFileAsync(string projectId, string path, string fileHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileHash);

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(
                new CommandDefinition("BEGIN IMMEDIATE", cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        try
        {
            var stored = await connection.ExecuteScalarAsync<string?>(
                    Def(MemorySql.SelectWatchFile, new { projectId, path }, cancellationToken))
                .ConfigureAwait(false);
            var replaced = !string.Equals(stored, fileHash, StringComparison.Ordinal);
            if (replaced)
            {
                var pathPrefix = LikePattern.Escape(path) + "/%";
                await connection.ExecuteAsync(
                        Def(MemorySql.CreateQueueRestoreTable, null, cancellationToken))
                    .ConfigureAwait(false);
                await connection.ExecuteAsync(
                        Def(MemorySql.CaptureQueueRowsForSourcePath, new { projectId, path, pathPrefix },
                            cancellationToken))
                    .ConfigureAwait(false);
                await connection.ExecuteAsync(
                        Def(MemorySql.DeleteBySourcePath, new { projectId, path, pathPrefix }, cancellationToken))
                    .ConfigureAwait(false);
                await fileIngestor
                    .IngestFileAsync(connection, projectId, path, null, cancellationToken, false)
                    .ConfigureAwait(false);
                await connection.ExecuteAsync(
                        Def(MemorySql.RestoreQueueRowsStillBacked, null, cancellationToken))
                    .ConfigureAwait(false);
                await connection.ExecuteAsync(
                        Def(MemorySql.UpsertWatchFile,
                            new
                            {
                                projectId, path, fileHash,
                                updatedAt = timeProvider.GetUtcNow().ToUnixTimeSeconds()
                            }, cancellationToken))
                    .ConfigureAwait(false);
            }

            await connection.ExecuteAsync(
                    new CommandDefinition("COMMIT", cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            return replaced;
        }
        catch
        {
            await connection.ExecuteAsync(
                    new CommandDefinition("ROLLBACK", cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            throw;
        }
    }

    public async Task<MemoryStats> GetStatsAsync(string projectId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        // Entry count is scoped to this project's committed context: workspace scratch and
        // other projects' rows are excluded. Pending comes from embed_state, filled by the embed pipeline.
        var entries = await connection.ExecuteScalarAsync<int>(
            Def(MemorySql.CountProjectEntries, new { projectId }, cancellationToken)).ConfigureAwait(false);
        var pendingCount = await connection.ExecuteScalarAsync<int>(
            Def(MemorySql.PendingCount, new { projectId }, cancellationToken)).ConfigureAwait(false);
        var contextList = (await connection.QueryAsync<string>(
                Def(MemorySql.CommittedContexts, new { projectId }, cancellationToken))
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

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        return await fileIngestor.IngestFileAsync(connection, projectId, path, context, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<int> IngestDirectoryAsync(string projectId, string path, string? context,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        return await fileIngestor.IngestDirectoryAsync(connection, projectId, path, context, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<EmbeddingConfig> ConfigureEmbeddingAsync(
        string provider, string? model, string? baseUrl, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        if (string.Equals(provider, "openai", StringComparison.OrdinalIgnoreCase))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(model);
        }

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        return await _embedder.ConfigureAsync(connection, provider, model, baseUrl, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<EmbedPendingResult> EmbedPendingAsync(string projectId, int? limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        // No engine configured: nothing can be embedded; pending is reported from embed_state.
        var settings = await _embedder.ReadSettingsAsync(connection, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(settings.Provider))
        {
            var pendingCount = await connection.ExecuteScalarAsync<int>(
                Def(MemorySql.PendingCount, new { projectId }, cancellationToken)).ConfigureAwait(false);
            return new EmbedPendingResult(0, pendingCount);
        }

        var processed = await _embedder.EmbedPendingAsync(connection, projectId, limit, cancellationToken)
            .ConfigureAwait(false);
        var remaining = await connection.ExecuteScalarAsync<int>(
            Def(MemorySql.PendingCount, new { projectId }, cancellationToken)).ConfigureAwait(false);
        return new EmbedPendingResult(processed, remaining);
    }

    public async Task<MemoryEntryResult> AddContentAsync(
        string projectId, string path, string content, string? context, string? sourceFile = null,
        string? section = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        var resolvedContext = context ?? ContextNaming.ProjectContext(projectId);
        var bucket = EntryBucket.For(resolvedContext, projectId);
        var bucketParams = new { path, scope = bucket.Scope, projectId = bucket.ProjectId, contextLabel = bucket.ContextLabel, workspaceId = bucket.WorkspaceId };

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        var existing = await connection.QueryFirstOrDefaultAsync<EntryRow>(
                Def(MemorySql.SelectEntryByPathInBucket, bucketParams, cancellationToken))
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return new MemoryEntryResult(ToEntry(existing), false);
        }

        var hash = ContentHash.Of(path, content);
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();

        var source = await ResolveSourceAsync(connection, sourceFile, section, cancellationToken)
            .ConfigureAwait(false);

        var affected = await connection.ExecuteAsync(
                Def(MemorySql.InsertEntry,
                    new
                    {
                        hash,
                        path,
                        value = content,
                        sourceFile = source.SourceLocator is { Length: > 0 } ? source.SourceLocator : null,
                        section,
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

        var inserted = bucket.Scope == SharedScope
            ? await connection.QueryFirstOrDefaultAsync<EntryRow>(
                    Def(MemorySql.SelectSharedEntryByPathAndHash,
                        new { path, hash }, cancellationToken))
                .ConfigureAwait(false)
            : await connection.QueryFirstOrDefaultAsync<EntryRow>(
                    Def(MemorySql.SelectEntryByPathInBucket, bucketParams, cancellationToken))
                .ConfigureAwait(false);
        if (inserted is null)
        {
            throw new InvalidOperationException(
                $"memory_add_content stored no row for '{path}' in '{resolvedContext}'.");
        }

        // Chunk-column maintenance (docs/plans/2026-08-08-search-knn-perf.md §3.3): scoped to the
        // TARGET context — ShareAsync/promotion write into a different context than the source
        // row, and only the target's group changed.
        if (sourceFile is not null)
        {
            await RecomputeChunkColumnsAsync(connection, resolvedContext, projectId, sourceFile, cancellationToken)
                .ConfigureAwait(false);
        }

        await _embedder.EmbedIfConfiguredAsync(connection, inserted.Id, content, cancellationToken).ConfigureAwait(false);
        return new MemoryEntryResult(ToEntry(inserted), affected == 1);
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
        return await ReadSettingAsync(connection, key, cancellationToken).ConfigureAwait(false);
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

    public async Task<IReadOnlyDictionary<string, string>> GetSettingsByPrefixAsync(string prefix,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<SettingRow>(
                Def(MemorySql.SelectSettingsByPrefix, new { prefix }, cancellationToken))
            .ConfigureAwait(false);
        return rows.ToDictionary(row => row.Key, row => row.Value, StringComparer.Ordinal);
    }

    public async Task DeleteSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(Def(MemorySql.DeleteSetting, new { key }, cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<bool> SetEntryTtlAsync(string projectId, string hash, int? ttlDays,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        var affected = await connection.ExecuteAsync(
                Def(MemorySql.UpdateEntryTtl, new { projectId, hash, ttlDays }, cancellationToken))
            .ConfigureAwait(false);
        return affected > 0;
    }

    private async Task<bool> DeleteCoreAsync(SqliteConnection connection, string projectId, string hash,
        string? scope, CancellationToken cancellationToken)
    {
        var rowScope = await connection.QueryFirstOrDefaultAsync<string?>(
                Def(MemorySql.SelectScopeByHashAndProject, new { hash, projectId, scope }, cancellationToken))
            .ConfigureAwait(false);

        var recomputeContext = await connection.QueryFirstOrDefaultAsync<DeleteRecomputeRow>(
                Def(MemorySql.SelectDeleteRecomputeContext, new { hash, projectId, scope }, cancellationToken))
            .ConfigureAwait(false);

        var deleted = await connection.ExecuteAsync(
                Def(MemorySql.DeleteByHashAndProject, new { hash, projectId, scope }, cancellationToken))
            .ConfigureAwait(false);

        if (deleted > 0 && rowScope is not null)
        {
            await connection.ExecuteAsync(
                    Def(MemorySql.UpsertTombstone,
                        new { hash, scope = rowScope, deletedAt = timeProvider.GetUtcNow().ToUnixTimeSeconds() },
                        cancellationToken))
                .ConfigureAwait(false);
        }

        if (deleted > 0 && recomputeContext?.SourceFile is not null)
        {
            var context = ContextStringOf(recomputeContext.Scope, recomputeContext.ContextLabel,
                recomputeContext.WorkspaceId, projectId);
            await RecomputeChunkColumnsAsync(connection, context, projectId, recomputeContext.SourceFile,
                cancellationToken).ConfigureAwait(false);
        }

        return deleted > 0;
    }

    private static async Task<string?> ReadSettingAsync(SqliteConnection connection, string key,
        CancellationToken cancellationToken) =>
        await connection.QuerySingleOrDefaultAsync<string?>(
                Def(MemorySql.SelectSetting, new { key }, cancellationToken))
            .ConfigureAwait(false);

    /// <summary>
    ///     Per-modality candidate window before RRF fusion (see docs/adr/0006-rrf-parameter-optimization.md):
    ///     the default max(limit*3, 100) keeps overlap candidates ranked 20-100 from being
    ///     starved by a per-modality LIMIT.
    /// </summary>
    internal static int CandidateWindowFor(int limit, CandidateWindowMode mode = CandidateWindowMode.Max3X100) =>
        mode == CandidateWindowMode.Max5X50
            ? (int)Math.Clamp((long)limit * 5, 50, int.MaxValue)
            : (int)Math.Clamp((long)limit * 3, 100, int.MaxValue);

    /// <summary>
    ///     Keyword modality: FTS5 candidates without snippet() — deferred (see <see cref="BuildFtsResults" />).
    ///     <paramref name="valueByHash" />, <paramref name="ftsQueryByHash" /> and <paramref name="idByHash" />
    ///     carry each candidate's raw value, matching @query text and row id, for snippet resolution after ranking.
    /// </summary>
    private async Task<IReadOnlyList<MemorySearchResult>> QueryFtsBatchAsync(
        SqliteConnection connection, string filter, string ftsExpression, DynamicParameters parameters,
        IDictionary<string, string> valueByHash, IDictionary<string, string> ftsQueryByHash,
        IDictionary<string, long> idByHash, CancellationToken cancellationToken)
    {
        try
        {
            // FtsQueryNormalizer sanitizes its own expressions, but SourcePathQuery bypasses it,
            // so a malformed expression can still reach FTS5; a failed keyword modality degrades
            // to the vector list.
            var rows = (await connection.QueryAsync<SearchRow>(
                    new CommandDefinition(
                        MemorySql.SearchByFilter.Replace("{filter}", filter), parameters,
                        cancellationToken: cancellationToken))
                .ConfigureAwait(false)).ToList();

            foreach (var row in rows)
            {
                valueByHash[row.Hash] = row.Value;
                ftsQueryByHash[row.Hash] = ftsExpression;
                idByHash[row.Hash] = row.Id;
            }

            return BuildFtsResults(rows);
        }
        catch (SqliteException ex)
        {
            // Deliberate degradation, but never a silent one: the caller just gets a shorter
            // list, and a broken index looks exactly like "nothing matched" without this.
            Log.KeywordModalityFailed(logger, ex);
            return [];
        }
    }

    /// <summary>Maps FTS candidate rows to results with <see cref="MemorySearchResult.Snippet" /> left unresolved.</summary>
    internal static IReadOnlyList<MemorySearchResult> BuildFtsResults(IReadOnlyList<SearchRow> rows) =>
    [
        .. rows.Select(row => new MemorySearchResult(
            row.Hash, row.Seq, row.Ranking, row.Path, string.Empty,
            row.SourceFile, row.ChunkIndex, row.TotalChunks))
    ];

    /// <summary>
    ///     A workspace row survives discard/consolidate as a Closed record (see IWorkspaceStore); only
    ///     Active is a valid write target, so a missing row and a closed one are the same failure to a caller.
    /// </summary>
    private static async Task RequireActiveWorkspaceAsync(SqliteConnection connection, string workspaceId,
        string projectId, CancellationToken cancellationToken)
    {
        var status = await connection.QueryFirstOrDefaultAsync<string?>(
                Def(MemorySql.SelectWorkspaceStatus, new { workspaceId, projectId }, cancellationToken))
            .ConfigureAwait(false);
        if (status != WorkspaceStatus.Active.ToString())
        {
            throw new UnknownWorkspaceException(workspaceId, projectId);
        }
    }

    /// <summary>
    ///     Dual-vector modality: content and structure KNN lists fused by fixed alpha
    ///     (docs/adr/0004-dual-vector-structure-signal.md); banks without structure vectors degrade
    ///     to content-only order. Snippet computation is deferred; <paramref name="valueByHash" /> carries each survivor's raw value out for resolution after ranking.
    /// </summary>
    private async Task<IReadOnlyList<MemorySearchResult>> QueryDualVectorBatchAsync(
        SqliteConnection connection, DynamicParameters parameters, double alpha,
        IDictionary<string, string> valueByHash, CancellationToken cancellationToken)
    {
        var contentRows = (await connection.QueryAsync<VectorRow>(
                    new CommandDefinition(MemorySql.VectorSearchByFilter, parameters,
                        cancellationToken: cancellationToken))
                .ConfigureAwait(false))
            .ToList();
        var structureRows = (await connection.QueryAsync<VectorRow>(
                    new CommandDefinition(MemorySql.StructureVectorSearchByFilter, parameters,
                        cancellationToken: cancellationToken))
                .ConfigureAwait(false))
            .ToList();

        var limit = parameters.Get<int>("limit");
        var fused = StructureFusion.Rank(
            contentRows.Select(row => new VectorHit(row.Hash, StructureFusion.SimFromDistance(row.Distance))),
            structureRows.Select(row => new VectorHit(row.Hash, StructureFusion.SimFromDistance(row.Distance))),
            alpha, limit);

        var byHash = contentRows.Concat(structureRows)
            .GroupBy(row => row.Hash, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var ranked = fused.Select(rank => (Row: byHash[rank.Hash], rank.Score)).ToList();
        foreach (var (row, _) in ranked)
        {
            valueByHash[row.Hash] = row.Value;
        }

        return BuildDualVectorResults(ranked);
    }

    /// <summary>
    ///     Maps ranked vector rows to results carrying the fused cosine score as <see cref="MemorySearchResult.Ranking" />
    ///     (see docs/adr/0006-rrf-parameter-optimization.md); <see cref="MemorySearchResult.Snippet" /> stays unresolved.
    /// </summary>
    internal static IReadOnlyList<MemorySearchResult> BuildDualVectorResults(
        IReadOnlyList<(VectorRow Row, double Score)> ranked) =>
    [
        .. ranked.Select(item => new MemorySearchResult(
            item.Row.Hash, item.Row.Seq, item.Score, item.Row.Path, string.Empty,
            item.Row.SourceFile, item.Row.ChunkIndex, item.Row.TotalChunks))
    ];

    /// <summary>
    ///     Resolves the deferred snippet for each result still carrying the unresolved placeholder:
    ///     an FTS-originated survivor gets the real FTS5 snippet() text (matching the @query it was
    ///     matched by); everything else falls back to <see cref="SnippetFallback" />, as before deferral.
    /// </summary>
    private static async Task<IReadOnlyList<MemorySearchResult>> ResolveDeferredSnippetsAsync(
        SqliteConnection connection, IReadOnlyList<MemorySearchResult> results,
        IReadOnlyDictionary<string, string> valueByHash, IReadOnlyDictionary<string, string> ftsQueryByHash,
        IReadOnlyDictionary<string, long> idByHash, CancellationToken cancellationToken)
    {
        var deferred = results.Where(result => result.Snippet.Length == 0).ToList();
        if (deferred.Count == 0)
        {
            return results;
        }

        var ftsSnippetByHash = await ResolveFtsSnippetsAsync(connection, deferred, ftsQueryByHash, idByHash,
            cancellationToken).ConfigureAwait(false);

        return
        [
            .. results.Select(result =>
            {
                if (result.Snippet.Length != 0)
                {
                    return result;
                }

                if (ftsSnippetByHash.TryGetValue(result.Hash, out var ftsSnippet) && ftsSnippet.Length > 0)
                {
                    return result with { Snippet = ftsSnippet };
                }

                return valueByHash.TryGetValue(result.Hash, out var value)
                    ? result with { Snippet = SnippetFallback.From(value, result.Hash) }
                    : result;
            })
        ];
    }

    /// <summary>
    ///     Batches the deferred FTS-native snippet lookup by matching @query text — most searches
    ///     use one context, so this is one small query restricted to <paramref name="deferred" />'s
    ///     row ids, not the full candidate window. Filters by id (entries_fts.rowid), not hash:
    ///     measured (session scratchpad, live-bank copy) — a hash-filtered MATCH forces FTS5 into a
    ///     full corpus-wide scan for the term (hash isn't an FTS5-indexed column), 5-6x slower than
    ///     the rowid-restricted lookup.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, string>> ResolveFtsSnippetsAsync(
        SqliteConnection connection, IReadOnlyList<MemorySearchResult> deferred,
        IReadOnlyDictionary<string, string> ftsQueryByHash, IReadOnlyDictionary<string, long> idByHash,
        CancellationToken cancellationToken)
    {
        var snippetByHash = new Dictionary<string, string>(StringComparer.Ordinal);
        var byQuery = deferred
            .Where(result => ftsQueryByHash.ContainsKey(result.Hash))
            .GroupBy(result => ftsQueryByHash[result.Hash], StringComparer.Ordinal);

        foreach (var group in byQuery)
        {
            var ids = group.Select(result => idByHash[result.Hash]).ToList();
            var rows = await connection.QueryAsync<SnippetRow>(
                    Def(MemorySql.FtsSnippetsForSurvivors, new { query = group.Key, ids }, cancellationToken))
                .ConfigureAwait(false);
            foreach (var row in rows)
            {
                snippetByHash[row.Hash] = row.Snippet;
            }
        }

        return snippetByHash;
    }

    private async Task<double> ReadStructureAlphaAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var raw = await connection.QuerySingleOrDefaultAsync<string?>(
                Def(MemorySql.SelectSetting, new { key = StructureFusion.AlphaSettingKey }, cancellationToken))
            .ConfigureAwait(false);
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var alpha)
               && alpha is >= 0.0 and <= 1.0
            ? alpha
            : StructureFusion.DefaultAlpha;
    }

    /// <summary>
    ///     Rating-pipeline rewire: search hits bump the on-row access/rating columns (MetaStore is gone).
    ///     Scoped to the searching project's own row or the shared tier — a hash collision in another,
    ///     unrelated project must never be aged or rate-bumped by this search.
    /// </summary>
    private async Task BumpAccessAsync(SqliteConnection connection, IReadOnlyList<MemorySearchResult> results,
        string projectId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        foreach (var hash in results.Select(r => r.Hash).Distinct(StringComparer.Ordinal))
        {
            var row = await connection.QueryFirstOrDefaultAsync<RatingRow>(
                    Def(MemorySql.SelectRatingForBump, new { hash, projectId }, cancellationToken))
                .ConfigureAwait(false);
            if (row is null)
            {
                continue;
            }

            var ageDays = Math.Max(0, (now - row.CreatedAt) / 86_400.0);
            var rating = RatingPolicy.Rating(
                RatingPolicy.DefaultBaseScore, row.AccessCount + 1, ageDays, RatingPolicy.DefaultHalfLifeDays);
            await connection.ExecuteAsync(
                    Def(MemorySql.BumpAccess, new { hash, now, rating, projectId }, cancellationToken))
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

        if (context.StartsWith("label:", StringComparison.Ordinal))
        {
            // Label context (see docs/plans/retrieval-improvement-c.md §3 2e): contributes the
            // label's custom-scoped rows only — project-scoped rows are already in the project batch (SearchContexts),
            // and a union here would double-count them in RRF.
            var rest = context["label:".Length..];
            var colon = rest.IndexOf(':');
            if (colon > 0)
            {
                var label = rest[(colon + 1)..];
                return (
                    $"{alias}scope = 'custom' AND {alias}context_label = @contextLabel AND {alias}project_id = @projectId",
                    new Dictionary<string, object?> { ["projectId"] = projectId, ["contextLabel"] = label });
            }
        }

        return ($"{alias}scope = 'custom' AND {alias}context_label = @contextLabel AND {alias}project_id = @projectId",
            new Dictionary<string, object?> { ["contextLabel"] = context, ["projectId"] = projectId });
    }

    private static MemoryEntry ToEntry(EntryRow row) => new(row.Hash, row.Path, ContextStringOf(row), row.Value, row.CreatedAt);

    private static string ContextStringOf(EntryRow row) => ContextStringOf(row.Scope, row.ContextLabel, row.WorkspaceId, row.ProjectId);

    /// <summary>Inverse of <see cref="EntryBucket.For" />: the bucket columns a row was stored under, back to the context string that would retrieve it.</summary>
    private static string ContextStringOf(string? scope, string? contextLabel, string? workspaceId, string projectId)
    {
        if (workspaceId is not null)
        {
            return ContextNaming.WorkspaceContext(workspaceId);
        }

        return scope switch
        {
            SharedScope => ContextNaming.SharedContext,
            "project" => ContextNaming.ProjectContext(projectId),
            "custom" => contextLabel ?? "",
            _ => ""
        };
    }

    /// <summary>
    ///     Recomputes chunk_index/total_chunks for one (ctx, sourceFile) group after a write that
    ///     can change its membership (docs/plans/2026-08-08-search-knn-perf.md §3.3).
    /// </summary>
    private static async Task RecomputeChunkColumnsAsync(SqliteConnection connection, string context,
        string projectId, string sourceFile, CancellationToken cancellationToken)
    {
        var ctx = MemorySql.ContextKeyFor(context, projectId);
        await connection.ExecuteAsync(
                Def(MemorySql.RecomputeChunkColumnsForContext, new { ctx, sourceFile }, cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>memory_write has no caller path; a content-derived name keeps the slot stable (FR-NM-7; see docs/work/features-native-memory/native-memory.feature).</summary>
    private static string WritePathFor(string value) => $"{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))}.md";

    /// <summary>Resolves the memory source for a write, creating the source row if needed.</summary>
    private async Task<MemorySource> ResolveSourceAsync(
        SqliteConnection connection, string? sourceFile, string? section, CancellationToken cancellationToken)
    {
        var (sourceType, locator) = ClassifySource(sourceFile);
        return await ((SqliteMemorySourceStore)sourceStore).ResolveOrCreateOnConnectionAsync(
                connection, sourceType, locator, section, null, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Classifies source_file into a SourceType and normalized locator.</summary>
    private static (SourceType Type, string Locator) ClassifySource(string? sourceFile)
    {
        if (sourceFile is null || sourceFile.Length == 0)
        {
            return (SourceType.Manual, "");
        }

        if (sourceFile.StartsWith("hermes/", StringComparison.Ordinal) ||
            sourceFile.Contains("/hermes/", StringComparison.Ordinal))
        {
            return (SourceType.Transcript, sourceFile);
        }

        return (SourceType.File, sourceFile);
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

    private static partial class Log
    {
        [LoggerMessage(EventId = 900, Level = LogLevel.Warning,
            Message = "Keyword search failed; degrading to the vector modality for this query")]
        public static partial void KeywordModalityFailed(ILogger logger, Exception exception);
    }

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

    internal sealed class SearchRow
    {
        public string Hash { get; set; } = "";

        public int Seq { get; set; }

        public double Ranking { get; set; }

        public string Path { get; set; } = "";

        public string Value { get; set; } = "";

        public string? SourceFile { get; set; }

        public int ChunkIndex { get; set; }

        public int TotalChunks { get; set; }

        /// <summary>entries.id — the FTS5 rowid, used to resolve the deferred snippet by row identity rather than hash.</summary>
        public long Id { get; set; }
    }

    /// <summary>Row shape for <see cref="MemorySql.FtsSnippetsForSurvivors" />.</summary>
    private sealed class SnippetRow
    {
        public string Hash { get; set; } = "";

        public string Snippet { get; set; } = "";
    }

    internal sealed class VectorRow
    {
        public string Hash { get; set; } = "";

        public int Seq { get; set; }

        public string Path { get; set; } = "";

        public string Value { get; set; } = "";

        public double Distance { get; set; }

        public string? SourceFile { get; set; }

        public int ChunkIndex { get; set; }

        public int TotalChunks { get; set; }
    }

    private sealed record SourceRow(string Path, string Value, string? SourceFile, string? Section, string? SourceType, string? HeadingPath);

    private sealed record DeleteRecomputeRow(string? Scope, string? ContextLabel, string? WorkspaceId, string? SourceFile);

    private sealed record SharedRow(string Path, string Value);

    private sealed record ExtractionRow(
        string Hash,
        string Path,
        string Value,
        string? SourceFile,
        string? SourceType,
        double Rating,
        long AccessCount,
        long CreatedAt,
        long? TtlDays)
    {
        public ExtractionCandidateRow ToCandidate() =>
            new(Hash, Path, Value, SourceFile, Rating, (int)AccessCount,
                DateTimeOffset.FromUnixTimeSeconds(CreatedAt), (int?)TtlDays, SourceType);
    }

    private sealed record SettingRow(string Key, string Value);

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
