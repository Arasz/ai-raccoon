using System.Security.Cryptography;
using System.Text;
using AiRaccoon.Core.EventPump;
using AiRaccoon.Core.Isolation;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Core.Memory.Fusion;
using AiRaccoon.Core.Metrics;
using AiRaccoon.Core.Projects;
using AiRaccoon.Core.Rating;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Ingestion;
using CommunityToolkit.Diagnostics;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using static AiRaccoon.Infrastructure.Sqlite.Sql;

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
    ISettingsStore settings,
    IEventPump<EmbedDrainRequest> embedDrainPump,
    IMeasurementRecorder measurements,
    ISharedProjectScope scope,
    IEntryKindClassifier entryTypeClassifier)
    : IMemoryStore
{
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
        IEventPump<EmbedDrainRequest> embedDrainPump,
        IMeasurementRecorder measurements,
        INoiseShadowObserver noiseShadowObserver,
        INoiseEntryStore noiseEntryStore,
        ISharedProjectScope scope,
        IEntryKindClassifier entryTypeClassifier)
        : this(factory, sourceStore, fileIngestor, embedder, timeProvider, logger, noiseFilteringService, settings,
            embedDrainPump, measurements, scope, entryTypeClassifier)
    {
        _noiseShadowObserver = noiseShadowObserver;
        _noiseEntryStore = noiseEntryStore;
    }

    public async Task<MemoryEntry> WriteAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default)
    {
        scope.EnsureCanExecuteInSharedScope(request);

        if (request.ProjectId != ProjectId.Canonicalize(request.ProjectId))
        {
            throw new InvalidOperationException($"ai-raccoon: refusing to write under non-canonical project id '{request.ProjectId}' (canonicalize at the ToolGate first)");
        }

        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();

        await using var connection = await factory.OpenBankAsync(cancellationToken);

        var noise = await IdentifyNoise(request, connection, now, cancellationToken);
        if (noise is not null)
        {
            return noise;
        }

        var context = ContextResolver.Resolve(request);
        var bucket = EntryBucket.For(context, request.ProjectId);

        var path = WritePathFor(request.Content);

        if (bucket.WorkspaceId is not null)
        {
            await RequireActiveWorkspaceAsync(connection, bucket.WorkspaceId, request.ProjectId, cancellationToken);
        }

        if (bucket.WorkspaceId is null)
        {
            var existing = await connection.QueryFirstOrDefaultAsync<EntryRow>(
                Def(MemorySql.SelectCommittedByValue,
                    new { value = request.Content, projectId = request.ProjectId }, cancellationToken));

            if (existing is not null)
            {
                return existing.ToEntry();
            }
        }

        var source = await ResolveSourceAsync(connection, request.SourceFile, request.Section, cancellationToken);

        var chunks = await GetChunks(connection, request, bucket, source, path, now, cancellationToken);

        var firstChunk = chunks[0];
        var hash = ContentHash.Of(path, firstChunk);

        var row = await GetRow(connection, bucket, path, hash, context, cancellationToken);

        if (request.SourceFile is not null)
        {
            await RecomputeChunkColumnsAsync(connection, context, request.ProjectId, request.SourceFile, cancellationToken);
        }

        await embedder.EmbedIfConfiguredAsync(connection, row.Id, firstChunk, cancellationToken);

        await _noiseShadowObserver.ObserveStoredWriteAsync(connection, bucket.ProjectId, request.AgentId, request.Content, hash, cancellationToken);

        return row.ToEntry();
    }

    public async Task<Core.Memory.SearchResults> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        scope.EnsureCanExecuteInSharedScope(query);
        var searchTimingsCollector = new SearchTimingsCollector(timeProvider.GetTimestamp());

        var openStart = timeProvider.GetTimestamp();
        await using var connection = await factory.OpenBankAsync(cancellationToken);
        await connection.RequireSharedAvailableAsync(query.ProjectId, scope, cancellationToken);
        searchTimingsCollector.Open = timeProvider.GetElapsedTime(openStart);

        // Resolve the full parameter set on the search's own connection: the query's values
        // win where provided, the settings snapshot (one batched read) fills the rest.
        var parameters = SearchParameters.FromSources(query,
            await GetSearchParameterDefaultsAsync(connection, cancellationToken));

        var embedStart = timeProvider.GetTimestamp();
        var plan = await PlanAsync(connection, query.Query, cancellationToken);
        var queryVector = await embedder.EmbedQueryAsync(connection, query.Query, cancellationToken);
        queryVector = queryVector with { Alpha = parameters.StructureAlpha };

        searchTimingsCollector.Embed = timeProvider.GetElapsedTime(embedStart);

        return await ExecuteSearchPipeline(connection, query, parameters, plan, queryVector, searchTimingsCollector, cancellationToken);
    }

    /// <summary>get_entry (ADR-0035): the caller's own rows plus the cross-project shared tier; null when no such hash is reachable.</summary>
    public async Task<MemoryEntry?> GetAsync(string projectId, string hash,
        CancellationToken cancellationToken = default, EntryKind? entryKind = null)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken);
        await connection.RequireSharedAvailableAsync(projectId, scope, cancellationToken);
        var row = entryKind is { } selectedKind
            ? await connection.QueryFirstOrDefaultAsync<EntryRow>(
                Def(MemorySql.SelectTypedEntryByHashForRead,
                    new { hash, projectId, entryKind = entryTypeClassifier.ToStoredValue(selectedKind) }, cancellationToken))
            : await connection.QueryFirstOrDefaultAsync<EntryRow>(
                Def(MemorySql.SelectEntryByHashForRead, new { hash, projectId }, cancellationToken));
        return row?.ToEntry();
    }

    public async Task<MemoryEntryResult> ShareAsync(string projectId, string hash, CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken);

        var source = await connection.QueryFirstOrDefaultAsync<SourceRow>(Def(MemorySql.SelectSourceByHashAndProject, new { hash, projectId }, cancellationToken));
        return source is null
            ? throw new UnknownHashException(hash, projectId)
            : await AddContentAsync(projectId, $"shared/{ContentHash.OfValue(source.Value)}.md",
                source.Value, ContextNaming.SharedContext, entryTypeClassifier.Resolve(source.EntryType, source.Path, source.SourceFile),
                source.SourceFile, source.Section, cancellationToken);
    }

    public async Task<IReadOnlyList<ExtractionCandidateRow>> ExtractCandidatesAsync(string projectId,
        bool includeTtlRows, CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken);
        var commandDefinition = Def(MemorySql.SelectExtractionCandidates, new { projectId, includeTtlRows = includeTtlRows ? 1 : 0 }, cancellationToken);
        var extractionRows = await connection.QueryAsync<ExtractionRow>(commandDefinition);

        var excludePrefixesValue = await connection.ReadSettingAsync(ExtractionConfigKeys.ExcludePrefixesGlobal, cancellationToken);
        var excluded = ExtractionConfigKeys.ParseExcludePrefixes(excludePrefixesValue);
        return
        [
            .. extractionRows
                .Where(extractionRow => extractionRow.SourceFile is null || !excluded.Any(p => extractionRow.SourceFile.StartsWith(p, StringComparison.Ordinal)))
                .Select(static extractionRow => extractionRow.ToCandidate())
        ];
    }

    public async Task<SharedIndex> GetSharedIndexAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken);
        var rows = await connection.QueryAsync<SharedRow>(Def(MemorySql.SelectSharedIndex, cancellationToken));
        var indexed = rows.ToList();
        return new SharedIndex([.. indexed.Select(static sharedRow => sharedRow.Value)], [.. indexed.Select(static sharedRow => sharedRow.Path)]);
    }

    public async Task<IReadOnlyList<string>> GetProjectIdsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken);
        var rows = await connection.QueryAsync<string>(
            Def(MemorySql.SelectProjectIds, cancellationToken));
        return [.. rows];
    }

    public async Task<MemoryStats> GetStatsAsync(string projectId, CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken);
        await connection.RequireSharedAvailableAsync(projectId, scope, cancellationToken);

        if (scope.IsReserved(projectId))
        {
            var count = await connection.ExecuteScalarAsync<int>(Def(MemorySql.CountEntries, cancellationToken));
            var pending = await connection.ExecuteScalarAsync<int>(Def(MemorySql.CountPendingEntries, cancellationToken));
            return new MemoryStats(count, pending, count == 0 ? [] : [ContextNaming.SharedContext]);
        }

        var entries = await connection.ExecuteScalarAsync<int>(
            Def(MemorySql.CountProjectEntries, new { projectId }, cancellationToken));
        var pendingCount = await connection.ExecuteScalarAsync<int>(
            Def(MemorySql.PendingCount, new { projectId }, cancellationToken));
        var contextList = (await connection.QueryAsync<string>(
            Def(MemorySql.CommittedContexts, new { projectId }, cancellationToken))).ToList();

        return new MemoryStats(entries, pendingCount, contextList);
    }

    public async Task<string> ListFilesAsync(string projectId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        await using var connection = await factory.OpenBankAsync(cancellationToken);
        await connection.RequireSharedAvailableAsync(projectId, scope, cancellationToken);

        var paths = await connection.QueryAsync<string>(
            Def(MemorySql.DistinctFilePaths, new { projectId }, cancellationToken));
        return FileTree.Build(paths);
    }

    public Task<int> IngestFileAsync(string projectId, string path, string? context,
        CancellationToken cancellationToken = default) =>
        ReplaceForDirectIngestAsync(projectId, path, context, cancellationToken);

    public async Task<int> IngestDirectoryAsync(string projectId, string path, string? context,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken);
        var result = await fileIngestor.IngestDirectoryAsync(connection, projectId, path, context, cancellationToken);

        foreach (var file in result.Files)
        {
            await PruneChunksNotIn(connection, projectId, file.Path, file.ChunkHashes, file.CodeChunkHashes, cancellationToken);
        }

        return result.Indexed;
    }

    public async Task<EmbedPendingResult> EmbedPendingAsync(string projectId, int? limit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken);

        var embeddingSettings = await embedder.ReadSettingsAsync(connection, cancellationToken);
        if (string.IsNullOrWhiteSpace(embeddingSettings.Provider))
        {
            var pendingCount = await connection.ExecuteScalarAsync<int>(
                Def(MemorySql.PendingCount, new { projectId }, cancellationToken));
            return new EmbedPendingResult(0, pendingCount);
        }

        var processed = await embedder.EmbedPendingAsync(connection, projectId, limit, cancellationToken);
        var remaining = await connection.ExecuteScalarAsync<int>(
            Def(MemorySql.PendingCount, new { projectId }, cancellationToken));
        return new EmbedPendingResult(processed, remaining);
    }

    public async Task<MemoryEntryResult> AddContentAsync(
        string projectId, string path, string content, string? context, EntryKind entryKind,
        string? sourceFile = null, string? section = null, CancellationToken cancellationToken = default)
    {
        scope.EnsureCanExecuteInSharedScope(projectId, "direct content writes");

        var resolvedContext = context ?? ContextNaming.ProjectContext(projectId);
        var bucket = EntryBucket.For(resolvedContext, projectId);
        var bucketParams = new { path, scope = bucket.Scope, projectId = bucket.ProjectId, contextLabel = bucket.ContextLabel, workspaceId = bucket.WorkspaceId };

        await using var connection = await factory.OpenBankAsync(cancellationToken);

        var existing = await connection.QueryFirstOrDefaultAsync<EntryRow>(
            Def(MemorySql.SelectEntryByPathInBucket, bucketParams, cancellationToken));
        if (existing is not null)
        {
            return new MemoryEntryResult(existing.ToEntry(), false);
        }

        var hash = ContentHash.Of(path, content);
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();

        var source = await ResolveSourceAsync(connection, sourceFile, section, cancellationToken);

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
                    entryType = entryTypeClassifier.ToStoredValue(
                        entryKind == EntryKind.None ? entryTypeClassifier.Classify(path, sourceFile) : entryKind),
                    chunkIndex = -1,
                    totalChunks = 0
                },
                cancellationToken));

        var inserted = (bucket.Scope == MemoryScopes.SharedScope
            ? await connection.QueryFirstOrDefaultAsync<EntryRow>(
                Def(MemorySql.SelectSharedEntryByPathAndHash,
                    new { path, hash }, cancellationToken))
            : await connection.QueryFirstOrDefaultAsync<EntryRow>(
                Def(MemorySql.SelectEntryByPathInBucket, bucketParams, cancellationToken))) ?? throw new InvalidOperationException(
            $"memory_add_content stored no row for '{path}' in '{resolvedContext}'.");

        if (sourceFile is not null)
        {
            await RecomputeChunkColumnsAsync(connection, resolvedContext, projectId, sourceFile, cancellationToken);
        }

        await embedder.EmbedIfConfiguredAsync(connection, inserted.Id, content, cancellationToken);
        return new MemoryEntryResult(inserted.ToEntry(), affected == 1);
    }

    public async Task<IReadOnlyList<MemoryEntry>> ListContextAsync(string projectId, string context,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(context);

        var (filter, values) = ContextFilterProvider.For(context, projectId, "");
        var parameters = new DynamicParameters();
        foreach (var (key, value) in values)
        {
            parameters.Add(key, value);
        }

        await using var connection = await factory.OpenBankAsync(cancellationToken);

        var rows = await connection.QueryAsync<EntryRow>(
            new CommandDefinition(
                MemorySql.SelectEntriesByContext.Replace("{filter}", filter), parameters,
                cancellationToken: cancellationToken));
        return [.. rows.Select(static entryRow => entryRow.ToEntry())];
    }

    public async Task<EntryMetadata?> GetMetadataAsync(string projectId, string hash,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);

        await using var connection = await factory.OpenBankAsync(cancellationToken);

        var row = await connection.QueryFirstOrDefaultAsync<MetadataRow>(
            Def(MemorySql.SelectEntryMetadata, new { projectId, hash }, cancellationToken));
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

        await using var connection = await factory.OpenBankAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(
            Def(MemorySql.UpdateEntryTtl, new { projectId, hash, ttlDays }, cancellationToken));
        return affected > 0;
    }

    private async Task<IReadOnlyList<string>> GetChunks(SqliteConnection connection, MemoryWriteRequest request, BucketColumns bucket, MemorySource source, string path, long now,
        CancellationToken cancellationToken)
    {
        var chunks = await fileIngestor.ChunkToBudgetAsync(connection, request.Content, cancellationToken);
        foreach (var chunk in chunks)
        {
            await connection.InsertAsync(ContentHash.Of(path, chunk), path, chunk, source, request, bucket, now, entryTypeClassifier, cancellationToken);
        }

        return chunks;
    }

    private static async Task<EntryRow> GetRow(SqliteConnection connection, BucketColumns bucket, string path, string hash, string context, CancellationToken cancellationToken) =>
        (bucket.Scope == MemoryScopes.SharedScope
            ? await connection.QueryFirstOrDefaultAsync<EntryRow>(
                Def(MemorySql.SelectSharedEntryByPathAndHash,
                    new { path, hash }, cancellationToken))
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
                    }, cancellationToken))) ?? throw new InvalidOperationException($"Insert stored no row for context '{context}'.");

    private async Task<MemoryEntry?> IdentifyNoise(MemoryWriteRequest request, SqliteConnection connection, long now, CancellationToken cancellationToken)
    {
        var noiseEnabled = NoiseConfigKeys.ParseEnabled(await connection.ReadSettingAsync(NoiseConfigKeys.EnabledGlobal, cancellationToken));
        if (!noiseEnabled)
        {
            return null;
        }

        var noiseResult = await noiseFilteringService.EvaluatePreWriteAsync(request, cancellationToken);
        if (!noiseResult.IsNoise)
        {
            return null;
        }

        var retentionDaysValue = await connection.ReadSettingAsync(NoiseConfigKeys.RetentionDaysGlobal, cancellationToken);
        var retentionDays = NoiseConfigKeys.ParseRetentionDays(retentionDaysValue);
        var expiresAt = timeProvider.GetUtcNow().AddDays(retentionDays).ToUnixTimeSeconds();
        await _noiseEntryStore.RecordAsync(request, noiseResult.PolicyName ?? ThrowHelper.ThrowArgumentNullException<string>(), expiresAt, now, cancellationToken);

        return new MemoryEntry("", "", request.Context ?? "", request.Content, now, false, $"rejected by noise policy '{noiseResult.PolicyName}'");
    }

    private async Task<Core.Memory.SearchResults> ExecuteSearchPipeline(SqliteConnection connection, SearchQuery query, SearchParameters parameters, FtsQueryPlan plan, QueryVector queryVector,
        SearchTimingsCollector searchTimingsCollector, CancellationToken cancellationToken)
    {
        var searchResults = await ExecuteSearchForContexts(connection, query, parameters, plan, queryVector, cancellationToken);
        searchTimingsCollector.Fts = searchResults.FtsTotalTiming;
        searchTimingsCollector.Vector = searchResults.VectorTotalTiming;

        var fusedResults = SearchResultFusion(parameters, plan, searchResults);
        searchTimingsCollector.Fusion = fusedResults.SearchTiming;

        var mergedResults = SearchResultMerge(query, parameters, plan, fusedResults);
        searchTimingsCollector.Merge = mergedResults.SearchTiming;

        var adjustedResults = AdjustMergedResults(query, parameters, plan, queryVector, fusedResults, mergedResults);
        searchTimingsCollector.Adjustment = adjustedResults.SearchTiming;

        var deferredResults = await ResolveDeferredSnippetsAsync(connection, query, adjustedResults, searchResults.Index, cancellationToken);
        searchTimingsCollector.Snippets = deferredResults.SearchTiming;

        var bumpStart = timeProvider.GetTimestamp();
        await BumpAccessAsync(connection, deferredResults, query.ProjectId, cancellationToken);
        searchTimingsCollector.Bump = timeProvider.GetElapsedTime(bumpStart);

        return new Core.Memory.SearchResults(deferredResults.Results, searchTimingsCollector.ToCollected(timeProvider), deferredResults.FusionDiff,
            deferredResults.EvidenceByHash, deferredResults.Stats, deferredResults.DroppedByFloor, searchResults.AllTermsMatched,
            queryVector.RelevanceFloor);
    }

    private AdjustedSearchResult AdjustMergedResults(SearchQuery query, SearchParameters parameters, FtsQueryPlan queryPlan, QueryVector queryVector,
        FusedSearchResult fusedSearchResult,
        MergedSearchResult mergedSearchResult)
    {
        var adjustmentStart = timeProvider.GetTimestamp();

        var legs = LegsFor(parameters, queryPlan, queryVector, fusedSearchResult.FtsCandidates, fusedSearchResult.VectorCandidates);
        var merged = mergedSearchResult.Results;

        if (legs.Count(static leg => leg.Contributes) < 2 || !parameters.FusionNoRegressionEnabled)
        {
            return new AdjustedSearchResult(merged, timeProvider.GetElapsedTime(adjustmentStart))
            {
                EvidenceByHash = fusedSearchResult.EvidenceByHash,
                Stats = fusedSearchResult.Stats,
                DroppedByFloor = mergedSearchResult.DroppedByFloor
            };
        }

        var outcome = SearchResultMerger.MergeCounting(NoFusionRegression.Reorder(merged, legs), query, parameters, queryPlan, fusedSearchResult.Leader);
        return new AdjustedSearchResult(outcome.Results, timeProvider.GetElapsedTime(adjustmentStart))
        {
            FusionDiff = FusionDiff.Between(merged, outcome.Results),
            EvidenceByHash = fusedSearchResult.EvidenceByHash,
            Stats = fusedSearchResult.Stats,
            DroppedByFloor = mergedSearchResult.DroppedByFloor + outcome.DroppedByFloor
        };
    }

    private MergedSearchResult SearchResultMerge(SearchQuery query, SearchParameters parameters, FtsQueryPlan queryPlan, FusedSearchResult fusedResult)
    {
        var mergeStart = timeProvider.GetTimestamp();
        var outcome = SearchResultMerger.MergeCounting(fusedResult.Results, query, parameters, queryPlan, fusedResult.Leader);
        return new MergedSearchResult(outcome.Results, timeProvider.GetElapsedTime(mergeStart)) { DroppedByFloor = outcome.DroppedByFloor };
    }

    private FusedSearchResult SearchResultFusion(SearchParameters parameters, FtsQueryPlan plan, SearchResults searchResults)
    {
        var fusionStart = timeProvider.GetTimestamp();
        var ftsCandidates = ModalityCandidates.ByBm25(searchResults);
        var vectorCandidates = ModalityCandidates.ByCosine(searchResults);
        // S1 capture (Stage 1): FuseWithEvidence fuses exactly like Fuse while attaching each
        // served hash's pre-normalization evidence. Leg names reuse the ModalityLeg vocabulary
        // from LegsFor ("fts"/"vector") — "vector" is the name the seam reads the content cosine by.
        // The carry below is an O(1) reference handoff, keyed by hash: the Merger's
        // reorder/consolidation/drop needs no remapping, and no SQL is issued anywhere on it.
        var fused = ReciprocalRankFusion.FuseWithEvidence(
            [
                new NamedWeightedCandidates(ftsCandidates, parameters.FtsWeight, "fts"),
                new NamedWeightedCandidates(vectorCandidates, parameters.VectorWeight, "vector")
            ],
            parameters.RrfK, 0, int.MaxValue);
        var results = plan.IsPathQuery ? AnchorMatchesFirst(fused.Results, searchResults.AllTermsMatched) : fused.Results;
        return new FusedSearchResult(results, timeProvider.GetElapsedTime(fusionStart))
        {
            VectorCandidates = vectorCandidates,
            FtsCandidates = ftsCandidates,
            Leader = FusionLeader.Of(results, ftsCandidates, vectorCandidates, searchResults.AllTermsMatched),
            EvidenceByHash = fused.EvidenceByHash,
            Stats = fused.Stats
        };
    }

    private async Task<SearchResults> ExecuteSearchForContexts(SqliteConnection connection, SearchQuery query, SearchParameters parameters, FtsQueryPlan plan, QueryVector queryVector,
        CancellationToken cancellationToken)
    {
        var contexts = await SearchContexts.ResolveAsync(connection, query, scope, cancellationToken);
        var searchResults = new SearchResults();

        foreach (var context in contexts)
        {
            var ctxFilter = ContextFilterProvider.For(context, query.ProjectId, "e.");
            var ctx = MemorySql.ContextKeyFor(context, query.ProjectId);
            var byHashIndex = searchResults.Index;

            var vectorResults = await VectorSearch(connection, query, parameters, queryVector, byHashIndex, ctx, cancellationToken);
            var ftsResults = await FtsSearch(connection, query, parameters, plan, queryVector, ctxFilter, byHashIndex, cancellationToken);

            searchResults.AddResults(vectorResults, ftsResults);
        }

        return searchResults;
    }

    private async Task<VectorSearchResult> VectorSearch(SqliteConnection connection, SearchQuery query, SearchParameters parameters, QueryVector queryVector, ByHashIndex byHashIndex, string ctx,
        CancellationToken cancellationToken)
    {
        if (!parameters.IsVectorQueried(queryVector))
        {
            return new VectorSearchResult([], TimeSpan.Zero);
        }

        var vectorStart = timeProvider.GetTimestamp();
        var vectorResults = await QueryDualVectorsAsync(connection, query, parameters, queryVector, byHashIndex, ctx, cancellationToken);
        return new VectorSearchResult(vectorResults, timeProvider.GetElapsedTime(vectorStart));
    }

    private async Task<FtsSearchResult> FtsSearch(SqliteConnection connection, SearchQuery query, SearchParameters parameters, FtsQueryPlan plan, QueryVector queryVector, ContextFilter contextFilter,
        ByHashIndex byHashIndex, CancellationToken cancellationToken)
    {
        if (!parameters.IsFtsQueried(plan))
        {
            return new FtsSearchResult([], TimeSpan.Zero);
        }

        var ftsStart = timeProvider.GetTimestamp();
        var ftsResults = await QueryFtsBatchAsync(connection, query, parameters, plan, queryVector, contextFilter, byHashIndex, cancellationToken);
        if (plan.AnchorFile is { } anchorFile)
        {
            ftsResults = [.. ftsResults.Where(result => SourcePathQuery.NamesFile(anchorFile, result.SourceFile))];
        }

        IReadOnlyList<string> allTermsMatched = plan.MatchesAllTerms ? [.. ftsResults.Select(static result => result.Hash)] : [];

        if (plan.Fallback is null || ftsResults.Count > Math.Max(plan.TokenCount, query.Limit))
        {
            return new FtsSearchResult(ftsResults, timeProvider.GetElapsedTime(ftsStart)) { AllTermsMatched = allTermsMatched };
        }


        ftsResults = await QueryFallbackFtsBatchAsync(connection, query, parameters, plan, queryVector, contextFilter, byHashIndex, cancellationToken);

        return new FtsSearchResult(ftsResults, timeProvider.GetElapsedTime(ftsStart)) { AllTermsMatched = allTermsMatched };
    }

    /// <summary>
    ///     A workspace row survives discard/consolidate as a Closed record (see IWorkspaceStore); only
    ///     Active is a valid write target, so a missing row and a closed one are the same failure to a caller.
    /// </summary>
    private static async Task RequireActiveWorkspaceAsync(SqliteConnection connection, string workspaceId,
        string projectId, CancellationToken cancellationToken)
    {
        var status = await connection.QueryFirstOrDefaultAsync<string?>(Def(MemorySql.SelectWorkspaceStatus, new { workspaceId, projectId }, cancellationToken));
        if (status != nameof(WorkspaceStatus.Active))
        {
            throw new UnknownWorkspaceException(workspaceId, projectId);
        }
    }

    /// <summary>
    ///     Dual-vector modality: content and structure KNN lists fused by fixed alpha
    ///     (docs/adr/0004-dual-vector-structure-signal.md); banks without structure vectors degrade
    ///     to content-only order.
    /// </summary>
    private static async Task<IReadOnlyList<MemorySearchResult>> QueryDualVectorsAsync(SqliteConnection connection, SearchQuery query, SearchParameters parameters, QueryVector queryVector,
        ByHashIndex byHashIndex, string ctx, CancellationToken cancellationToken)
    {
        var vectorParameters = DynamicParameters.VectorParameters(query, parameters, queryVector, ctx);
        var contentRows = (await connection.QueryAsync<VectorRow>(
                new CommandDefinition(MemorySql.VectorSearchByTypes(query.EntryTypes), vectorParameters,
                    cancellationToken: cancellationToken)))
            .ToList();
        var structureRows = (await connection.QueryAsync<VectorRow>(
                new CommandDefinition(MemorySql.VectorSearchByTypes(query.EntryTypes, true), vectorParameters,
                    cancellationToken: cancellationToken)))
            .ToList();

        var limit = parameters.CandidateWindowFor(query.Limit);
        var fused = StructureFusion.Rank(
            contentRows.Select(static row => new VectorHit(row.Hash, StructureFusion.SimFromDistance(row.Distance))),
            structureRows.Select(static row => new VectorHit(row.Hash, StructureFusion.SimFromDistance(row.Distance))),
            queryVector.Alpha, limit, queryVector.RelevanceFloor ?? 0.0);

        var byHash = contentRows.Concat(structureRows)
            .GroupBy(static row => row.Hash, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);

        // The content leg's own raw cosine, keyed independently of byHash above: byHash keeps
        // whichever row (content or structure) grouped first, which is never a reliable source
        // for "this hash's content similarity" once a hash appears in both legs.
        var contentSimByHash = contentRows
            .GroupBy(static row => row.Hash, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => StructureFusion.SimFromDistance(group.First().Distance), StringComparer.Ordinal);

        var ranked = fused.Select(rank => new RankedVectorRow(
                byHash[rank.Hash],
                rank.Score,
                contentSimByHash.TryGetValue(rank.Hash, out var contentCosine) ? contentCosine : null))
            .ToList();
        foreach (var (row, _, _) in ranked)
        {
            byHashIndex.ValueByHash[row.Hash] = row.Value;
        }

        return BuildDualVectorResults(ranked);
    }

    /// <summary>
    ///     Maps ranked vector rows to results carrying the alpha-fused score as <see cref="MemorySearchResult.Ranking" />
    ///     (ordering only, see docs/adr/0006-rrf-parameter-optimization.md) and the row's own raw content
    ///     cosine as <see cref="MemorySearchResult.ContentCosine" /> — the two diverge whenever structure
    ///     fusion blends in a non-zero, or absent, structure term. <see cref="MemorySearchResult.Snippet" />
    ///     stays unresolved.
    /// </summary>
    internal static IReadOnlyList<MemorySearchResult> BuildDualVectorResults(
        IReadOnlyList<RankedVectorRow> ranked) =>
    [
        .. ranked.Select(static item => new MemorySearchResult(
            item.Row.Hash, item.Score, item.Row.Path, "",
            item.Row.SourceFile, item.Row.ChunkIndex, item.Row.TotalChunks, item.ContentCosine, item.Row.EntryKind))
    ];

    /// <summary>
    ///     Resolves the deferred snippet for each result still carrying the unresolved placeholder:
    ///     an FTS-originated survivor gets the real FTS5 snippet() text (matching the @query it was
    ///     matched by); everything else falls back to <see cref="SnippetFallback" />, as before deferral.
    /// </summary>
    private async Task<DeferredSearchResult> ResolveDeferredSnippetsAsync(SqliteConnection connection, SearchQuery query, AdjustedSearchResult adjustedSearch, ByHashIndex byHashIndex,
        CancellationToken cancellationToken)
    {
        var deferredSnippetsStart = timeProvider.GetTimestamp();
        var deferred = adjustedSearch.Results.Where(static result => result.Snippet.Length == 0).ToList();
        if (deferred.Count == 0)
        {
            return DeferredSearchResult.Empty;
        }

        var ftsSnippetByHash = await ResolveFtsSnippetsAsync(connection, deferred, byHashIndex, cancellationToken);

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

                return byHashIndex.ValueByHash.TryGetValue(result.Hash, out var value)
                    ? result with { Snippet = SnippetFallback.From(value, result.Hash, query.Query) }
                    : result;
            })
        ], timeProvider.GetElapsedTime(deferredSnippetsStart))
        {
            FusionDiff = adjustedSearch.FusionDiff,
            EvidenceByHash = adjustedSearch.EvidenceByHash,
            Stats = adjustedSearch.Stats,
            DroppedByFloor = adjustedSearch.DroppedByFloor
        };
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
        ByHashIndex byHashIndex,
        CancellationToken cancellationToken)
    {
        var snippetByHash = new Dictionary<string, string>(StringComparer.Ordinal);
        var byQuery = deferred
            .Where(result => byHashIndex.FtsQueryByHash.ContainsKey(result.Hash))
            .GroupBy(result => byHashIndex.FtsQueryByHash[result.Hash], StringComparer.Ordinal);

        foreach (var group in byQuery)
        {
            var ids = group.Select(result => byHashIndex.IdByHash[result.Hash]).ToList();
            var rows = await connection.QueryAsync<SnippetRow>(
                Def(MemorySql.FtsSnippetsForSurvivors, new { query = group.Key, ids }, cancellationToken));
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
        var hashes = deferredSearch.Results.Select(static memorySearchResult => memorySearchResult.Hash).Distinct(StringComparer.Ordinal).ToList();
        try
        {
            foreach (var hash in hashes)
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
                        cancellationToken));
            }
        }
        catch (SqliteException ex) when (ex.IsBankBusy())
        {
            Log.BumpAccessDeferred(logger, hashes.Count);
        }
    }

    /// <summary>add_memory has no caller path; a content-derived name keeps the slot stable (FR-NM-7; see docs/work/features-native-memory/native-memory.feature).</summary>
    private static string WritePathFor(string value) => $"{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))}.md";

    /// <summary>Resolves the memory source for a write, creating the source row if needed.</summary>
    private async Task<MemorySource> ResolveSourceAsync(SqliteConnection connection, string? sourceFile, string? section, CancellationToken cancellationToken)
    {
        var (sourceType, locator) = ClassifySource(sourceFile);
        return await ((SqliteMemorySourceStore)sourceStore).ResolveOrCreateOnConnectionAsync(connection, sourceType, locator, section, null, cancellationToken);
    }

    /// <summary>Classifies source_file into a SourceType and normalized locator.</summary>
    private static SourceClassification ClassifySource(string? sourceFile)
    {
        if (string.IsNullOrEmpty(sourceFile))
        {
            return new SourceClassification(SourceType.Manual, "");
        }

        if (sourceFile.StartsWith("hermes/", StringComparison.Ordinal) ||
            sourceFile.Contains("/hermes/", StringComparison.Ordinal))
        {
            return new SourceClassification(SourceType.Transcript, sourceFile);
        }

        return new SourceClassification(SourceType.File, sourceFile);
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 897, Level = LogLevel.Warning,
            Message = "Access-rating bump skipped for {Count} result(s): the bank is busy (another writer holds the lock)")]
        public static partial void BumpAccessDeferred(ILogger logger, int count);

        [LoggerMessage(EventId = 900, Level = LogLevel.Warning,
            Message = "Keyword search failed; degrading to the vector modality for this query")]
        public static partial void KeywordModalityFailed(ILogger logger, Exception exception);
    }
}
