using System.Security.Cryptography;
using System.Text;
using AiRaccoon.Core.Isolation;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Core.Memory.Fusion;
using AiRaccoon.Core.Rating;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Ingestion;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Sqlite.Memory;

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
    INoiseFilteringService noiseFilteringService,
    ISettingsStore settings)
    : IMemoryStore
{
    private const string SharedScope = "shared";
    private readonly INoiseEntryStore _noiseEntryStore = NoOpNoiseEntryStore.Instance;

    private readonly INoiseShadowObserver _noiseShadowObserver = NoOpNoiseShadowObserver.Instance;

    public SqliteMemoryStore(
        ISqliteConnectionFactory factory,
        IMemorySourceStore sourceStore,
        IFileIngestor fileIngestor,
        IEntryEmbedder embedder,
        TimeProvider timeProvider,
        ILogger<SqliteMemoryStore> logger,
        INoiseFilteringService noiseFilteringService,
        ISettingsStore settings,
        INoiseShadowObserver noiseShadowObserver,
        INoiseEntryStore noiseEntryStore)
        : this(factory, sourceStore, fileIngestor, embedder, timeProvider, logger, noiseFilteringService, settings)
    {
        _noiseShadowObserver = noiseShadowObserver;
        _noiseEntryStore = noiseEntryStore;
    }

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
                var retentionDays = NoiseConfigKeys.ParseRetentionDays(
                    await ReadSettingAsync(connection, NoiseConfigKeys.RetentionDaysGlobal, cancellationToken).ConfigureAwait(false));
                var expiresAt = timeProvider.GetUtcNow().AddDays(retentionDays).ToUnixTimeSeconds();
                await _noiseEntryStore.RecordAsync(request, noiseResult.PolicyName!, expiresAt, now, cancellationToken)
                    .ConfigureAwait(false);

                return new MemoryEntry(string.Empty, string.Empty, request.Context ?? string.Empty, request.Content,
                    now, Stored: false, Reason: $"rejected by noise policy '{noiseResult.PolicyName}'");
            }
        }

        var context = ContextResolver.Resolve(request);
        var bucket = EntryBucket.For(context, request.ProjectId);

        var path = WritePathFor(request.Content);

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

        var chunks = await fileIngestor.ChunkToBudgetAsync(connection, request.Content, cancellationToken)
            .ConfigureAwait(false);
        var hash = ContentHash.Of(path, chunks[0]);
        foreach (var chunk in chunks.Skip(1))
        {
            await WriteChunks.InsertAsync(connection, ContentHash.Of(path, chunk), path, chunk, source, request,
                bucket, now, cancellationToken).ConfigureAwait(false);
        }

        await WriteChunks.InsertAsync(connection, hash, path, chunks[0], source, request, bucket, now,
            cancellationToken).ConfigureAwait(false);

        var row = bucket.Scope == SharedScope
            ? await connection.QueryFirstOrDefaultAsync<EntryRow>(
                    Def(MemorySql.SelectSharedEntryByPathAndHash,
                        new { path, hash }, cancellationToken))
                .ConfigureAwait(false)
            : await connection.QueryFirstOrDefaultAsync<EntryRow>(
                    Def(MemorySql.SelectEntryByPathAndHashInBucket,
                        new
                        {
                            path,
                            hash,
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

        await embedder.EmbedIfConfiguredAsync(connection, row.Id, chunks[0], cancellationToken).ConfigureAwait(false);

        await _noiseShadowObserver.ObserveStoredWriteAsync(connection, bucket.ProjectId, request.AgentId,
            request.Content, hash, cancellationToken).ConfigureAwait(false);

        return ToEntry(row);
    }

    public async Task<Core.Memory.SearchResults> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        var searchTimingsCollector = new SearchTimingsCollector(timeProvider.GetTimestamp());

        var openStart = timeProvider.GetTimestamp();
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        searchTimingsCollector.Open = timeProvider.GetElapsedTime(openStart);

        var embedStart = timeProvider.GetTimestamp();
        var queryVector = await embedder.EmbedQueryAsync(connection, query.Query, cancellationToken).ConfigureAwait(false);
        searchTimingsCollector.Embed = timeProvider.GetElapsedTime(embedStart);

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

        var ftsQueried = plan.Expression.Length > 0 && query.FtsWeight != 0;
        var vectorQueried = queryVector is not null && query.VectorWeight != 0;

        return await ExecuteSearchPipeline(connection, query, queryVector, vectorQueried, alpha, plan, ftsQueried, isPathQuery, searchTimingsCollector, cancellationToken);
    }

    /// <summary>memory_get (ADR-0035): the caller's own rows plus the cross-project shared tier; null when no such hash is reachable.</summary>
    public async Task<MemoryEntry?> GetAsync(string projectId, string hash, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QueryFirstOrDefaultAsync<EntryRow>(
                Def(MemorySql.SelectEntryByHashForRead, new { hash, projectId }, cancellationToken))
            .ConfigureAwait(false);
        return row is null ? null : ToEntry(row);
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

        ContextScope.RequireWithinProject(context, projectId);

        var (filter, values) = ContextFilter.For(context, projectId, "");
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

    public async Task<MemoryStats> GetStatsAsync(string projectId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

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
        return FileTree.Build(paths);
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
        return await embedder.ConfigureAsync(connection, provider, model, baseUrl, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<EmbedPendingResult> EmbedPendingAsync(string projectId, int? limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        var embeddingSettings = await embedder.ReadSettingsAsync(connection, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(embeddingSettings.Provider))
        {
            var pendingCount = await connection.ExecuteScalarAsync<int>(
                Def(MemorySql.PendingCount, new { projectId }, cancellationToken)).ConfigureAwait(false);
            return new EmbedPendingResult(0, pendingCount);
        }

        var processed = await embedder.EmbedPendingAsync(connection, projectId, limit, cancellationToken)
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
                        sourceId = source.Id,
                        chunkIndex = -1,
                        totalChunks = 0
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

        if (sourceFile is not null)
        {
            await RecomputeChunkColumnsAsync(connection, resolvedContext, projectId, sourceFile, cancellationToken)
                .ConfigureAwait(false);
        }

        await embedder.EmbedIfConfiguredAsync(connection, inserted.Id, content, cancellationToken).ConfigureAwait(false);
        return new MemoryEntryResult(ToEntry(inserted), affected == 1);
    }

    public async Task<IReadOnlyList<MemoryEntry>> ListContextAsync(string projectId, string context,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(context);

        var (filter, values) = ContextFilter.For(context, projectId, "");
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

    public Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default) => settings.GetSettingAsync(key, cancellationToken);

    public Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default) => settings.SetSettingAsync(key, value, cancellationToken);

    public Task<IReadOnlyDictionary<string, string>> GetSettingsByPrefixAsync(string prefix,
        CancellationToken cancellationToken = default) =>
        settings.GetSettingsByPrefixAsync(prefix, cancellationToken);

    public Task DeleteSettingAsync(string key, CancellationToken cancellationToken = default) => settings.DeleteSettingAsync(key, cancellationToken);

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

    private async Task<Core.Memory.SearchResults> ExecuteSearchPipeline(SqliteConnection connection, SearchQuery query, byte[]? queryVector, bool vectorQueried, double alpha, FtsQueryPlan plan,
        bool ftsQueried, bool isPathQuery, SearchTimingsCollector searchTimingsCollector, CancellationToken cancellationToken)
    {
        var searchResults = await ExecuteSearchForContexts(connection, query, queryVector, vectorQueried, alpha, plan, ftsQueried, cancellationToken);
        searchTimingsCollector.Fts = searchResults.FtsTotalTiming;
        searchTimingsCollector.Vector = searchResults.VectorTotalTiming;

        var fusedResults = SearchResultFusion(query, searchResults);
        searchTimingsCollector.Fusion = fusedResults.SearchTiming;

        var mergedResults = SearchResultMerge(query, fusedResults, isPathQuery);
        searchTimingsCollector.Merge = mergedResults.SearchTiming;

        var adjustedResults = await AdjustMergedResults(connection, query, fusedResults, mergedResults, ftsQueried, vectorQueried, isPathQuery, cancellationToken);
        searchTimingsCollector.Adjustment = adjustedResults.SearchTiming;

        var deferredResults = await ResolveDeferredSnippetsAsync(connection, adjustedResults, searchResults.Indexes, query.Query, cancellationToken);
        searchTimingsCollector.Snippets = deferredResults.SearchTiming;

        var bumpStart = timeProvider.GetTimestamp();
        await BumpAccessAsync(connection, deferredResults, query.ProjectId, cancellationToken).ConfigureAwait(false);
        searchTimingsCollector.Bump = timeProvider.GetElapsedTime(bumpStart);

        return new Core.Memory.SearchResults(deferredResults.Results, searchTimingsCollector.ToCollected(timeProvider), deferredResults.FusionDiff);
    }

    private async Task<AdjustedSearchResult> AdjustMergedResults(SqliteConnection connection, SearchQuery query, FusedSearchResult fusedSearchResult, MergedSearchResult mergedSearchResult,
        bool ftsQueried, bool vectorQueried, bool isPathQuery, CancellationToken cancellationToken)
    {
        var adjustmentStart = timeProvider.GetTimestamp();
        var legs = LegsFor(fusedSearchResult.FtsCandidates, fusedSearchResult.VectorCandidates, ftsQueried, vectorQueried);
        if (!await NoFusionRegressionEnabledAsync(connection, legs, cancellationToken).ConfigureAwait(false))
        {
            return new AdjustedSearchResult(mergedSearchResult.Results, timeProvider.GetElapsedTime(adjustmentStart));
        }

        var adjustedSea = mergedSearchResult with
        {
            Results = SearchResultMerger.Merge(mergedSearchResult with { Results = NoFusionRegression.Reorder(mergedSearchResult.Results, legs) }, query.Limit,
                query.MinRelativeScore, query.RrfK, isPathQuery ? 0.0 : query.SourceLambda,
                query.ConsolidationThreshold, query.DocScoreFormula)
        };
        return new AdjustedSearchResult(adjustedSea.Results, timeProvider.GetElapsedTime(adjustmentStart))
        {
            FusionDiff = FusionDiff.Between(mergedSearchResult.Results, adjustedSea.Results)
        };
    }

    private MergedSearchResult SearchResultMerge(SearchQuery query, FusedSearchResult fusedResult, bool isPathQuery)
    {
        var mergeStart = timeProvider.GetTimestamp();
        var merged = SearchResultMerger.Merge(fusedResult, query.Limit, query.MinRelativeScore, query.RrfK, isPathQuery ? 0.0 : query.SourceLambda, query.ConsolidationThreshold,
            query.DocScoreFormula);
        return new MergedSearchResult(merged, timeProvider.GetElapsedTime(mergeStart));
    }

    private FusedSearchResult SearchResultFusion(SearchQuery query, SearchResults batch)
    {
        var fusionStart = timeProvider.GetTimestamp();
        var ftsCandidates = ModalityCandidates.ByBm25(batch);
        var vectorCandidates = ModalityCandidates.ByCosine(batch);
        List<WeightedResults> weightedResults = [new(ftsCandidates, query.FtsWeight), new(vectorCandidates, query.VectorWeight)];
        var fused = ReciprocalRankFusion.Fuse(weightedResults, query.RrfK, 0, int.MaxValue);
        return new FusedSearchResult(fused, timeProvider.GetElapsedTime(fusionStart))
        {
            VectorCandidates = vectorCandidates,
            FtsCandidates = ftsCandidates
        };
    }

    private async Task<SearchResults> ExecuteSearchForContexts(SqliteConnection connection, SearchQuery query, byte[]? queryVector, bool vectorQueried, double alpha, FtsQueryPlan plan,
        bool ftsQueried,
        CancellationToken cancellationToken)
    {
        var contexts = await SearchContexts.ResolveAsync(connection, query, cancellationToken).ConfigureAwait(false);
        var batch = new SearchResults();

        foreach (var context in contexts)
        {
            var ctxFilter = ContextFilter.For(context, query.ProjectId, "e.");
            var ctx = MemorySql.ContextKeyFor(context, query.ProjectId);
            var limit = CandidateWindowFor(query.Limit, query.CandidateWindow);

            var vectorResults = await VectorSearch(connection, batch.Indexes, queryVector, ctx, vectorQueried, limit, alpha, cancellationToken);
            var ftsResults = await FtsSearch(connection, query, ctxFilter, plan, batch.Indexes, queryVector, ftsQueried, limit, cancellationToken);

            batch.AddResults(vectorResults, ftsResults);
        }

        return batch;
    }

    private async Task<VectorSearchResult> VectorSearch(SqliteConnection connection, HashIndexes hashIndexes, byte[]? queryVector, string ctx, bool vectorQueried, int limit, double alpha,
        CancellationToken cancellationToken)
    {
        if (!vectorQueried)
        {
            return new VectorSearchResult([], TimeSpan.Zero);
        }

        var vectorStart = timeProvider.GetTimestamp();
        var vectorParameters = DynamicParameters.VectorParameters(ctx, limit, queryVector);
        var vectorResults = await QueryDualVectorBatchAsync(connection, vectorParameters, alpha, hashIndexes, cancellationToken).ConfigureAwait(false);
        return new VectorSearchResult(vectorResults, timeProvider.GetElapsedTime(vectorStart));
    }

    private async Task<FtsSearchResult> FtsSearch(SqliteConnection connection, SearchQuery query, ContextFilterValues contextFilter, FtsQueryPlan plan, HashIndexes hashIndexes,
        byte[]? queryVector,
        bool ftsQueried,
        int limit,
        CancellationToken cancellationToken)
    {
        if (!ftsQueried)
        {
            return new FtsSearchResult([], TimeSpan.Zero);
        }

        var ftsStart = timeProvider.GetTimestamp();
        var mainQueryParameters = DynamicParameters.SearchParameters(plan.Expression, limit, queryVector, contextFilter.Values);
        var ftsResults = await QueryFtsBatchAsync(connection, contextFilter.Filter, plan.Expression, mainQueryParameters, hashIndexes, cancellationToken).ConfigureAwait(false);

        if (plan.Fallback is null || ftsResults.Count > Math.Max(plan.TokenCount, query.Limit))
        {
            return new FtsSearchResult(ftsResults, timeProvider.GetElapsedTime(ftsStart));
        }

        var fallbackQueryParameters = DynamicParameters.SearchParameters(plan.Fallback, limit, queryVector, contextFilter.Values);
        ftsResults = await QueryFtsBatchAsync(connection, contextFilter.Filter, plan.Fallback, fallbackQueryParameters, hashIndexes, cancellationToken).ConfigureAwait(false);

        return new FtsSearchResult(ftsResults, timeProvider.GetElapsedTime(ftsStart));
    }

    /// <summary>
    ///     Entry-delete and sync tombstone in one transaction (ADR-0035/WP5a): a crash between the
    ///     two used to leave the content deleted locally with no tombstone, resurrecting it on the
    ///     next sync. Same BEGIN IMMEDIATE/COMMIT/ROLLBACK shape as <see cref="DeleteSourcePathAsync" />
    ///     and <see cref="SqliteMemoryStore.ReplaceCoreAsync" />; both callers are top-level, so there is no nested-transaction hazard.
    /// </summary>
    private async Task<bool> DeleteCoreAsync(SqliteConnection connection, string projectId, string hash,
        string? scope, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(
                new CommandDefinition("BEGIN IMMEDIATE", cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        try
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
                await CompactChunkColumnsAfterDeleteAsync(connection, context, projectId,
                    recomputeContext.SourceFile, (int)recomputeContext.ChunkIndex, cancellationToken).ConfigureAwait(false);
            }

            await connection.ExecuteAsync(
                    new CommandDefinition("COMMIT", cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            return deleted > 0;
        }
        catch
        {
            await connection.ExecuteAsync(
                    new CommandDefinition("ROLLBACK", cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<string?> ReadSettingAsync(SqliteConnection connection, string key,
        CancellationToken cancellationToken) =>
        await connection.QuerySingleOrDefaultAsync<string?>(
                Def(MemorySql.SelectSetting, new { key }, cancellationToken))
            .ConfigureAwait(false);

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
    ///     to content-only order.
    /// </summary>
    private async Task<IReadOnlyList<MemorySearchResult>> QueryDualVectorBatchAsync(
        SqliteConnection connection, DynamicParameters parameters, double alpha,
        HashIndexes hashIndexes, CancellationToken cancellationToken)
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
            hashIndexes.ValueByHash[row.Hash] = row.Value;
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
            item.Row.Hash, item.Score, item.Row.Path, string.Empty,
            item.Row.SourceFile, item.Row.ChunkIndex, item.Row.TotalChunks))
    ];

    /// <summary>
    ///     Resolves the deferred snippet for each result still carrying the unresolved placeholder:
    ///     an FTS-originated survivor gets the real FTS5 snippet() text (matching the @query it was
    ///     matched by); everything else falls back to <see cref="SnippetFallback" />, as before deferral.
    /// </summary>
    private async Task<DeferredSearchResult> ResolveDeferredSnippetsAsync(
        SqliteConnection connection, AdjustedSearchResult adjustedSearch,
        HashIndexes hashIndexes, string queryText, CancellationToken cancellationToken)
    {
        var deferredSnippetsStart = timeProvider.GetTimestamp();
        var deferred = adjustedSearch.Results.Where(result => result.Snippet.Length == 0).ToList();
        if (deferred.Count == 0)
        {
            return new DeferredSearchResult([], TimeSpan.Zero);
        }

        var ftsSnippetByHash = await ResolveFtsSnippetsAsync(connection, deferred, hashIndexes, cancellationToken).ConfigureAwait(false);

        return new DeferredSearchResult([
                .. adjustedSearch.Results.Select(result =>
                {
                    if (result.Snippet.Length != 0)
                    {
                        return result;
                    }

                    if (ftsSnippetByHash.TryGetValue(result.Hash, out var ftsSnippet) && ftsSnippet.Length > 0)
                    {
                        return result with { Snippet = ftsSnippet };
                    }

                    return hashIndexes.ValueByHash.TryGetValue(result.Hash, out var value)
                        ? result with { Snippet = SnippetFallback.From(value, result.Hash, queryText) }
                        : result;
                })
            ]
            , timeProvider.GetElapsedTime(deferredSnippetsStart));
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
        HashIndexes hashIndexes,
        CancellationToken cancellationToken)
    {
        var snippetByHash = new Dictionary<string, string>(StringComparer.Ordinal);
        var byQuery = deferred
            .Where(result => hashIndexes.FtsQueryByHash.ContainsKey(result.Hash))
            .GroupBy(result => hashIndexes.FtsQueryByHash[result.Hash], StringComparer.Ordinal);

        foreach (var group in byQuery)
        {
            var ids = group.Select(result => hashIndexes.IdByHash[result.Hash]).ToList();
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

    /// <summary>
    ///     Rating-pipeline rewire: search hits bump the on-row access/rating columns (MetaStore is gone).
    ///     Scoped to the searching project's own row or the shared tier — a hash collision in another,
    ///     unrelated project must never be aged or rate-bumped by this search.
    /// </summary>
    private async Task BumpAccessAsync(SqliteConnection connection, DeferredSearchResult deferredSearch, string projectId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        foreach (var hash in deferredSearch.Results.Select(r => r.Hash).Distinct(StringComparer.Ordinal))
        {
            await connection.ExecuteAsync(
                    Def(MemorySql.BumpAccess,
                        new
                        {
                            hash,
                            now,
                            projectId,
                            baseScore = RatingPolicy.DefaultBaseScore,
                            halfLifeDays = RatingPolicy.DefaultHalfLifeDays,
                            accessMultiplier = RatingPolicy.DefaultAccessMultiplier
                        },
                        cancellationToken))
                .ConfigureAwait(false);
        }
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
        if (string.IsNullOrEmpty(sourceFile))
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

    private static CommandDefinition Def(string sql, object? parameters = null,
        CancellationToken cancellationToken = default) =>
        new(sql, parameters, cancellationToken: cancellationToken);

    private static partial class Log
    {
        [LoggerMessage(EventId = 900, Level = LogLevel.Warning,
            Message = "Keyword search failed; degrading to the vector modality for this query")]
        public static partial void KeywordModalityFailed(ILogger logger, Exception exception);
    }
}
