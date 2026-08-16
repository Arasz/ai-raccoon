using System.ComponentModel;
using System.Text.Json.Nodes;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.QueryGuard;
using AiRaccoon.Core.Memory.QueryGuard.Structural;
using AiRaccoon.Core.Metrics;
using AiRaccoon.Core.SearchQuality;
using FluentValidation;
using JetBrains.Annotations;
using ModelContextProtocol;
using ModelContextProtocol.Server;

// ReSharper disable ExplicitCallerInfoArgument

namespace AiRaccoon.Tools;

/// <summary>Thin MCP tools over IMemoryStore — no business logic here (see docs/work/features-agent-memory/spec-issue-1.md §6.1).</summary>
public sealed partial class MemoryTools(
    IMemoryStore store,
    IToolGate gate,
    ISearchQualityService qualityService,
    IQueryGuardService queryGuard,
    IMemoryWriteService writes,
    IMeasurementRecorder measurements,
    ILogger<MemoryTools> logger,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;


    private const string TnMemoryWrite = "memory_write";
    private const string TnMemoryGet = "memory_get";
    private const string TnMemorySearch = "memory_search";
    private const string TnMemoryList = "memory_list";
    private const string TnMemoryStats = "memory_stats";
    private const string TnMemoryDelete = "memory_delete";
    private const string TnMemoryDeleteContext = "memory_delete_context";
    private const string TnMemoryIngestFile = "memory_ingest_file";
    private const string TnMemoryIngestDirectory = "memory_ingest_directory";
    private const string TnMemoryEmbedPending = "memory_embed_pending";

    private static readonly SearchQuery.Validator SearchQueryValidator = new();
    private static readonly MemoryWriteRequest.Validator MemoryWriteRequestValidator = new();

    [McpServerTool(Name = TnMemoryWrite)]
    [Description(
        "Writes content into memory. Writes land in the project's committed context by default; naming a workspace_id routes them into that isolated workspace. A write may be refused (e.g. it matched a noise policy) — check stored: a refused write has stored=false and a reason naming what rejected it, and hash is empty.")]
    public async Task<ApiEnvelope<WriteResult>> Write(
        [Description("The project id; every memory operation is scoped to a project.")]
        string projectId,
        [Description("The content to remember.")]
        string content,
        [Description("When set, the write lands in this workspace's isolated context instead of the project context.")]
        string? workspaceId = null,
        [Description("Provenance only: which agent wrote this.")]
        string? agentId = null,
        [Description("Optional context label for this entry, instead of the default project/workspace context. A context organises entries inside the project; it does not hide them — a plain project search still finds them.")]
        string? context = null,
        [Description("Optional original file path the content came from; chunks of one file share it.")]
        string? sourceFile = null,
        [Description("Optional section slug within the source file (e.g. 'decision'); indexed as a weighted FTS column.")]
        string? section = null,
        CancellationToken cancellationToken = default)
    {
        await gate.RequireAsync(projectId, AccessRequirement.Write, TnMemoryWrite, cancellationToken);

        var request = new MemoryWriteRequest(projectId, content, context, agentId, workspaceId, sourceFile, section);
        await MemoryWriteRequestValidator.ValidateAndThrowAsync(request, cancellationToken);

        var entry = await writes.WriteAsync(request, cancellationToken);
        var result = new WriteResult(entry.Hash, entry.Path, entry.Context, entry.CreatedAt, entry.Stored, entry.Reason);
        var envelope = await gate.WrapAsync(projectId, result, cancellationToken);
        return envelope;
    }

    [McpServerTool(Name = TnMemoryGet)]
    [Description(
        "Reads one entry's full content by its content hash, as returned by memory_write or memory_search. An unknown hash is refused as unknown-hash.")]
    public async Task<ApiEnvelope<GetResult>> Get(
        [Description("The project id; every memory operation is scoped to a project.")]
        string projectId,
        [Description("The content hash to read.")]
        string hash,
        CancellationToken cancellationToken = default)
    {
        await gate.RequireAsync(projectId, AccessRequirement.Read, TnMemoryGet, cancellationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);

        var entry = await store.GetAsync(projectId, hash, cancellationToken)
                    ?? throw new UnknownHashException(hash, projectId);
        var result = new GetResult(entry.Hash, entry.Value, entry.Path, entry.Context, entry.CreatedAt);
        var envelope = await gate.WrapAsync(projectId, result, cancellationToken);
        return envelope;
    }

    [McpServerTool(Name = TnMemorySearch)]
    [Description(
        "Hybrid semantic search over the bank. scope=all (default) searches shared + project (+ workspace when named); scope=project searches the project only; scope=shared searches the shared promotion tier only. A project scope covers every context in the project unless contextLabel narrows it to one.")]
    public async Task<ApiEnvelope<SearchResultList>> Search(
        [Description("The project id.")] string projectId,
        [Description(
            "The search query. Semantic matching only sees roughly the first 254 tokens (~1,000 " +
            "characters of English prose, approximate) — for a long paste (a log, stack trace, test " +
            "output), search its identifying line (exception type, error code, failing test name) " +
            "instead of the whole dump. Keyword matching still covers the query in full.")]
        string query,
        [Description("Search scope: all (default), project, or shared.")]
        string scope = "all",
        [Description("When set, also searches this workspace's isolated context.")]
        string? workspaceId = null,
        [Description("Maximum results (default 20).")]
        int limit = 20,
        [Description(
            "Relative floor: keeps results scoring at least this fraction of THIS response's top hit (default 0, off). " +
            "Ranking is normalized per response, so rank 1 always scores 1.0 even when nothing in the bank answers the " +
            "query — a high score is not evidence of a good match, and this is not an absolute quality bar. Use it to " +
            "keep only hits in the same league as the best one; see ADR-0047.")]
        double minRelativeScore = 0.0,
        [Description("RRF cutoff for the hybrid fusion (default 60); a result scores weight / (k + rank) per modality list.")]
        int rrfK = SearchQuery.DefaultRrfK,
        [Description("Weight of the keyword (FTS5) list in the RRF fusion (default 1).")]
        int ftsWeight = 1,
        [Description("Weight of the semantic (vector) list in the RRF fusion (default 1).")]
        int vectorWeight = 1,
        [Description("Narrows the project scope to one context. Omit it to search every context in " +
                     "the project (the default); memory_stats lists the labels in use.")]
        string? contextLabel = null,
        CancellationToken cancellationToken = default)
    {
        await gate.RequireAsync(projectId, AccessRequirement.Read, TnMemorySearch, cancellationToken);

        var parsedScope = scope.ToLowerInvariant() switch
        {
            "all" => SearchScope.All,
            "project" => SearchScope.Project,
            "shared" => SearchScope.Shared,
            _ => throw new McpException($"invalid-params: Invalid scope '{scope}': expected all, project, or shared.")
        };

        var searchQuery = new SearchQuery(projectId, query, parsedScope, workspaceId, limit, minRelativeScore,
            rrfK, ftsWeight, vectorWeight, contextLabel);

        await SearchQueryValidator.ValidateAndThrowAsync(searchQuery, cancellationToken);

        var guard = await queryGuard.EvaluateAsync(projectId, query, cancellationToken);
        if (guard.Shadowed is { } suppressed)
        {
            Log.QueryGuardShadowVerdict(logger, suppressed.Tier.ToString(), projectId,
                suppressed.PolicyName ?? string.Empty);
        }

        if (guard.Verdict.Tier == QueryGuardTier.Refuse)
        {
            throw new McpException($"invalid-params: {guard.Verdict.Guidance}");
        }

        var searchResults = await store.SearchAsync(searchQuery, cancellationToken);
        var results = searchResults.Results;

        var correlationId = Guid.CreateVersion7().ToString("N");
        await qualityService.RecordSearchSafeAsync(correlationId, query, scope, projectId, results.Count,
            [.. results.Where(r => r.SourceFile is not null).Select(r => r.SourceFile!).Take(5)], cancellationToken);
        RecordPhaseMeasurements(searchResults.Timings, ContentHash.OfValue(query), correlationId, projectId);

        // QueryLengthGuard is always on -- a fact about the embedding window, not a togglable
        // policy like the guard above -- so it is evaluated unconditionally, never through
        // IQueryGuardService (a disabled guard must not silence it).
        var warning = SearchWarnings.Compose(guard.Verdict, QueryLengthGuard.Evaluate(query));
        var result = new SearchResultList(results, warning);
        var envelope = await gate.WrapAsync(projectId, result, cancellationToken);
        return envelope with { Meta = envelope.Meta with { CorrelationId = correlationId } };
    }

    [McpServerTool(Name = TnMemoryList)]
    [Description("Lists the bank's indexed files as a JSON tree (memory_list_files).")]
    public async Task<ApiEnvelope<ListResult>> List(
        [Description("The project id.")] string projectId,
        CancellationToken cancellationToken = default)
    {
        await gate.RequireAsync(projectId, AccessRequirement.Read, TnMemoryList, cancellationToken);
        var files = await store.ListFilesAsync(projectId, cancellationToken);
        var result = new ListResult(JsonNode.Parse(files) ?? new JsonObject());
        var envelope = await gate.WrapAsync(projectId, result, cancellationToken);
        return envelope;
    }

    [McpServerTool(Name = TnMemoryStats)]
    [Description("Reports entry count, pending (deferred-embedding) count, and the bank's committed contexts.")]
    public async Task<ApiEnvelope<StatsResult>> Stats(
        [Description("The project id.")] string projectId,
        CancellationToken cancellationToken = default)
    {
        await gate.RequireAsync(projectId, AccessRequirement.Read, TnMemoryStats, cancellationToken);
        var stats = await store.GetStatsAsync(projectId, cancellationToken);
        var result = new StatsResult(stats.EntryCount, stats.PendingCount, stats.Contexts);
        var envelope = await gate.WrapAsync(projectId, result, cancellationToken);
        return envelope;
    }

    [McpServerTool(Name = TnMemoryDelete)]
    [Description(
        "Deletes a specific memory entry by its content hash. Idempotent: an unknown hash is not an error — it reports deleted=0.")]
    public async Task<ApiEnvelope<DeletedResult>> Delete(
        [Description("The project id.")] string projectId,
        [Description("The content hash to delete.")]
        string hash,
        CancellationToken cancellationToken = default)
    {
        await gate.RequireAsync(projectId, AccessRequirement.Destructive, TnMemoryDelete, cancellationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);

        var deleted = await store.DeleteAsync(projectId, hash, cancellationToken);
        var result = new DeletedResult(deleted ? 1 : 0);
        var envelope = await gate.WrapAsync(projectId, result, cancellationToken);
        return envelope;
    }

    [McpServerTool(Name = TnMemoryDeleteContext)]
    [Description(
        "Deletes every entry stored under a context label (e.g. a project or workspace context). Idempotent: an unknown context is not an error — it reports deleted=0.")]
    public async Task<ApiEnvelope<DeletedContextResult>> DeleteContext(
        [Description("The project id.")] string projectId,
        [Description("The context label to delete.")]
        string context,
        CancellationToken cancellationToken = default)
    {
        await gate.RequireAsync(projectId, AccessRequirement.Destructive, TnMemoryDeleteContext, cancellationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(context);

        var deleted = await store.DeleteContextAsync(projectId, context, cancellationToken);
        var result = new DeletedContextResult(deleted);
        var envelope = await gate.WrapAsync(projectId, result, cancellationToken);
        return envelope;
    }

    [McpServerTool(Name = TnMemoryIngestFile)]
    [Description("Indexes one file from disk into memory. The path must lie inside the project's configured scope (ai-raccoon settings ingest scope add); an unscoped project refuses every ingest.")]
    public async Task<ApiEnvelope<IngestResult>> IngestFile(
        [Description("The project id.")] string projectId,
        [Description("Path of the file to index.")]
        string path,
        [Description("Optional context label.")]
        string? context = null,
        CancellationToken cancellationToken = default)
    {
        await gate.RequireAsync(projectId, AccessRequirement.Write, TnMemoryIngestFile, cancellationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var indexed = await store.IngestFileAsync(projectId, path, context, cancellationToken);
        var result = new IngestResult(indexed);
        var envelope = await gate.WrapAsync(projectId, result, cancellationToken);
        return envelope;
    }

    [McpServerTool(Name = TnMemoryIngestDirectory)]
    [Description(
        "Recursively indexes a directory tree into memory, skipping unchanged files. The path must lie inside the project's configured scope (ai-raccoon settings ingest scope add); an unscoped project refuses every ingest.")]
    public async Task<ApiEnvelope<ScannedResult>> IngestDirectory(
        [Description("The project id.")] string projectId,
        [Description("Path of the directory to index.")]
        string path,
        [Description("Optional context label applied to all files.")]
        string? context = null,
        CancellationToken cancellationToken = default)
    {
        await gate.RequireAsync(projectId, AccessRequirement.Write, TnMemoryIngestDirectory, cancellationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var scanned = await store.IngestDirectoryAsync(projectId, path, context, cancellationToken);
        var result = new ScannedResult(scanned);
        var envelope = await gate.WrapAsync(projectId, result, cancellationToken);
        return envelope;
    }

    [McpServerTool(Name = TnMemoryEmbedPending)]
    [Description("Embeds deferred entries in batches (used when no model was configured at write time).")]
    public async Task<ApiEnvelope<EmbedResult>> EmbedPending(
        [Description("The project id.")] string projectId,
        [Description("Maximum rows to process in this call; omit for all.")]
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        await gate.RequireAsync(projectId, AccessRequirement.Write, TnMemoryEmbedPending, cancellationToken);

        var result = await store.EmbedPendingAsync(projectId, limit, cancellationToken);
        var embedResult = new EmbedResult(result.Processed, result.Pending);
        var envelope = await gate.WrapAsync(projectId, embedResult, cancellationToken);
        return envelope;
    }

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record WriteResult(string Hash, string Path, string Context, long CreatedAt, bool Stored = true, string? Reason = null);

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record GetResult(string Hash, string Value, string Path, string Context, long CreatedAt);

    /// <summary>Warning is set only by the query guard's annotate tier (docs/adr/0040): a non-null value never changes Results.</summary>
    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record SearchResultList(IReadOnlyList<MemorySearchResult> Results, string? Warning = null);

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record ListResult(JsonNode Files);

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record StatsResult(int Entries, int Pending, IReadOnlyList<string> Contexts);

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record DeletedResult(int Deleted);

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record DeletedContextResult(int Deleted);

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record IngestResult(int Indexed);

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record ScannedResult(int Scanned);

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record EmbedResult(int Processed, int Pending);

    /// <summary>
    ///     Tags each of the six search phases with the query hash and correlation id and hands them
    ///     to the recorder — never the query text itself (SqliteMetricsStore's save-time allowlist
    ///     rejects it). Best-effort: a throwing recorder must never fail or slow the search (WP3).
    /// </summary>
    private void RecordPhaseMeasurements(SearchTimings timings, string queryHash, string correlationId, string projectId)
    {
        try
        {
            var recordedAt = _timeProvider.GetUtcNow();
            foreach (var (name, value) in timings.Phases())
            {
                measurements.Record(new Measurement(name, MeasurementKind.Histogram, value.TotalMilliseconds,
                    "ms", recordedAt, projectId, queryHash, correlationId));
            }
        }
        catch (Exception ex)
        {
            Log.PhaseMeasurementRecordingFailed(logger, ex, correlationId);
        }
    }

    private static partial class Log
    {
        /// <summary>Kept here, not in Core: the guard decides, the host reports (docs/adr/0065).</summary>
        [LoggerMessage(EventId = 920, Level = LogLevel.Information,
            Message = "Query guard (shadow) would have returned {Tier} for project {ProjectId} via {PolicyName}")]
        public static partial void QueryGuardShadowVerdict(ILogger logger, string tier, string projectId, string policyName);

        [LoggerMessage(EventId = 921, Level = LogLevel.Warning,
            Message = "Failed to record search phase measurements for correlation {CorrelationId}")]
        public static partial void PhaseMeasurementRecordingFailed(ILogger logger, Exception exception, string correlationId);
    }
}
