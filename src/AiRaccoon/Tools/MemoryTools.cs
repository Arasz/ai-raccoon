using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Code;
using AiRaccoon.Core.Memory.Fusion;
using AiRaccoon.Core.Memory.QueryGuard;
using AiRaccoon.Core.Metrics;
using AiRaccoon.Infrastructure.Embedding;
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
    ISearchDispatcher searchDispatcher,
    IQueryGuardService queryGuard,
    IMemoryWriteService writes,
    IMeasurementRecorder measurements,
    ISettingsStore settings,
    ILogger<MemoryTools> logger,
    IEmbeddingService embeddings,
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
        "Writes content into memory. Writes land in the project's committed context by default; naming a workspace_id routes them into that isolated workspace, and workspace_id wins over context when both are supplied (the sandbox has priority). A write may be refused (e.g. it matched a noise policy) — check stored: a refused write has stored=false and a reason naming what rejected it, and hash is empty.")]
    public async Task<ApiEnvelope<WriteResult>> Write(
        [Description("The project id; every memory operation is scoped to a project.")]
        [Optional][DefaultParameterValue("")] string projectId,
        [Description("The content to remember.")]
        string content,
        [Description("When set, the write lands in this workspace's isolated context instead of the project context. Wins over context when both are supplied (the sandbox has priority).")]
        string? workspaceId = null,
        [Description("Provenance only: which agent wrote this.")]
        string? agentId = null,
        [Description(
            "Optional context label for this entry, instead of the default project/workspace context. A context organises entries inside the project; it does not hide them — a plain project search still finds them. Ignored when workspace_id is supplied: the workspace wins (the sandbox has priority).")]
        string? context = null,
        [Description("Optional original file path the content came from; chunks of one file share it.")]
        string? sourceFile = null,
        [Description("Optional section slug within the source file (e.g. 'decision'); indexed as a weighted FTS column.")]
        string? section = null,
        CancellationToken cancellationToken = default)
    {
        var canonical = await gate.RequireAsync(projectId, AccessRequirement.Write, TnMemoryWrite, cancellationToken);

        var request = new MemoryWriteRequest(canonical, content, context, agentId, workspaceId, sourceFile, section);
        await MemoryWriteRequestValidator.ValidateAndThrowAsync(request, cancellationToken);

        var entry = await writes.WriteAsync(request, cancellationToken);
        var result = new WriteResult(entry.Hash, entry.Path, entry.Context, entry.CreatedAt, entry.Stored, entry.Reason);
        var envelope = await gate.WrapAsync(canonical, result, cancellationToken);
        return envelope;
    }

    [McpServerTool(Name = TnMemoryGet)]
    [Description(
        "Reads one entry's full content by its content hash, as returned by memory_write or memory_search. An unknown hash is refused as unknown-hash.")]
    public async Task<ApiEnvelope<GetResult>> Get(
        [Description("The project id; every memory operation is scoped to a project.")]
        [Optional][DefaultParameterValue("")] string projectId,
        [Description("The content hash to read.")]
        string hash,
        CancellationToken cancellationToken = default)
    {
        var canonical = await gate.RequireAsync(projectId, AccessRequirement.Read, TnMemoryGet, cancellationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);

        var entry = await store.GetAsync(canonical, hash, cancellationToken)
                    ?? throw new UnknownHashException(hash, canonical);
        var result = new GetResult(entry.Hash, entry.Value, entry.Path, entry.Context, entry.CreatedAt);
        var envelope = await gate.WrapAsync(canonical, result, cancellationToken);
        return envelope;
    }

    [McpServerTool(Name = TnMemorySearch)]
    [Description(
        "Hybrid semantic search over the bank. scope=all (default) searches shared + project (+ workspace when named); "
        + "scope=project searches the project only; scope=shared searches the shared promotion tier only. A project scope "
        + "covers every context in the project unless contextLabel narrows it to one. sessionId is required: every agent "
        + "has a session, and the id is stored verbatim on the search_quality row. Kind defaults to both, with each "
        + "section ranked by its own hybrid. Every search records a search_quality row and returns meta.correlationId "
        + "for grade and follow-through. Memory and both store the memory leg count and files. Code stores the code "
        + "count with an empty file list. Code paths never enter the table. A result warning of '"
        + SearchWarnings.EngineNotConfiguredPrefix + "' means the memory section is keyword-only because the memory "
        + "embedding engine is not installed: relay '" + EmbeddingEngineSetup.DefaultModelCommand + "' to the user "
        + "once and treat the memory hits as incomplete; re-running the search changes nothing until that command runs. "
        + "A result warning of '"
        + CodeSearchWarnings.EngineNotConfiguredPrefix + "' means the code section is keyword-only because the code "
        + "embedding engine is not installed: relay '" + CodeEngineSetup.DefaultModelCommand + "' to the user once and "
        + "treat the code hits as incomplete; re-running the search changes nothing until that command runs. "
        + "The response may carry an evidenceByHash map (hash → retrieval evidence): fusionStrength (0-1, the fraction of the strongest leg "
        + "agreement this query could have produced — ~0.95 means every firing leg ranked it first, ~0.2 is thin), "
        + "legs (which legs agreed and at which ranks; a single-leg entry is itself a thin-response tell), and cosine "
        + "(the vector leg's raw content-embedding similarity to the query, when a vector leg participated and "
        + "reported one — a hash-comparable magnitude, never the structure-blended score legs/order use). The "
        + "response may carry fusionStats (topMargin/topVsMedian over pre-normalization raws, plus maxPossible "
        + "and participatingLegs). A flat margin plus a single-leg top is the measurable 'best of a bad lot' "
        + "signature — a thin response, not a verdict. "
        + "These signals claim no relevance (no relevance value is computed); margins are computed over the PRE-floor "
        + "candidate population, not the served set. A response with unranked: true ranks rows that carry "
        + "no absolute relevance backing (flat margin, one leg, no row clearing the absolute relevance floor or "
        + "containing every query term) — candidates to verify, not answers. An absolute relevance floor drops "
        + "rows whose fused content cosine is below 0.35 unless the row's text contains every query term (a "
        + "keyword match on an identifier such as a ticket key is kept whatever its cosine), so a zero-overlap "
        + "query comes back empty. A response short of "
        + "its requested limit reports the cuts as truncation:[{floor, threshold, dropped}].")]
    public async Task<ApiEnvelope<SearchResultList>> Search(
        [Description("The project id.")] [Optional][DefaultParameterValue("")] string projectId,
        [Description(
            "The search query. Semantic matching only sees roughly the first N tokens — the active " +
            "memory embedding engine's own window (254 tokens for the bundled model; a manifest " +
            "model's is wider, and a result warning names the real number when a query is long " +
            "enough to hit it) — for a long paste (a log, stack trace, test output), search its " +
            "identifying line (exception type, error code, failing test name) instead of the whole " +
            "dump. Keyword matching still covers the query in full. When kind is code or both, the " +
            "code leg has its own, separately-sized engine window and its own trim warning — a query " +
            "trimmed for one leg may still fit the other in full.")]
        string query,
        [Description(
            "The calling agent's session id. Required attribution, stored verbatim on the search_quality row; " +
            "blank is rejected fail-fast.")]
        string sessionId,
        [Description("Search scope: all (default), project, or shared.")]
        string scope = "all",
        [Description("When set, also searches this workspace's isolated context.")]
        string? workspaceId = null,
        [Description("Maximum results (default 8). The score floors can serve fewer than this even when the bank holds more matches; when that shortens the response, truncation names the floor that dropped rows and how many.")]
        int limit = SearchDefaults.Limit,
        [Description(
            "Relative floor: keeps results scoring at least this fraction of THIS response's top hit (default 0.6). " +
            "Ranking is normalized per response, so rank 1 always scores 1.0 even when nothing in the bank answers the " +
            "query — a high score is not evidence of a good match, and this is not an absolute quality bar. Use it to " +
            "keep only hits in the same league as the best one; see ADR-0047 and ADR-0096. Pass 0 for full recall: " +
            "it disables every score floor, the absolute relevance floor included.")]
        double minRelativeScore = SearchDefaults.MinRelativeScore,
        [Description(
            "RRF cutoff for the hybrid fusion (bank setting retrieval.rrfK, else 60); a result scores weight / (k + rank) per modality list.")]
        int? rrfK = null,
        [Description("Weight of the keyword (FTS5) list in the RRF fusion (bank setting retrieval.ftsWeight, else 1).")]
        int? ftsWeight = null,
        [Description("Weight of the semantic (vector) list in the RRF fusion (bank setting retrieval.vectorWeight, else 1).")]
        int? vectorWeight = null,
        [Description(
            "Adjacent-chunk sibling boost: a same-source chunk at index N±1 adds this much to the chunk's score (bank setting retrieval.sourceLambda, else 0.1; valid range 0..1).")]
        double? sourceLambda = null,
        [Description(
            "Consolidation threshold: sibling visibility floor and the merge gap for weak adjacent siblings (bank setting retrieval.consolidationThreshold, else 0.1; must be >= 0).")]
        double? consolidationThreshold = null,
        [Description("Document-score formula used as the secondary sort key: \"max\" or \"sum\" (bank setting retrieval.docScoreFormula, else \"max\").")]
        string? docScoreFormula = null,
        [Description(
            "Per-modality candidate depth policy before RRF fusion: \"max3x100\" or \"max5x50\" (bank setting retrieval.candidateWindow, else \"max3x100\").")]
        string? candidateWindow = null,
        [Description("Narrows the project scope to one context. Omit it to search every context in " +
                     "the project (the default); memory_stats lists the labels in use.")]
        string? contextLabel = null,
        [Description(
            "Which corpus to search: both (default — the memory bank and the indexed code corpus, " +
            "each ranked by its own hybrid), memory (the memory bank only, today's legacy envelope), " +
            "or code (the indexed code corpus only). Code is always project-scoped: scope=shared " +
            "returns an empty code section. Code hits carry lineStart/lineEnd instead of chunkIndex/totalChunks.")]
        string kind = "both",
        [Description("Code section only: maximum results, overriding limit for the code section (bank setting unaffected).")]
        int? codeLimit = null,
        [Description("Code section only: relative floor, overriding minRelativeScore for the code section.")]
        double? codeMinRelativeScore = null,
        CancellationToken cancellationToken = default)
    {
        var canonical = await gate.RequireAsync(projectId, AccessRequirement.Read, TnMemorySearch, cancellationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var parsedScope = scope.ToLowerInvariant() switch
        {
            "all" => SearchScope.All,
            "project" => SearchScope.Project,
            "shared" => SearchScope.Shared,
            _ => throw new McpException($"invalid-params: Invalid scope '{scope}': expected all, project, or shared.")
        };
        var parsedKind = ParseKind(kind);
        ValidateCodeOverrides(codeLimit, codeMinRelativeScore);

        // Enum wire names are validated here (not silently defaulted) so a typo fails fast,
        // and provided tuning values are range-checked before any bank work.
        var searchQuery = BuildValidatedSearchQuery(canonical, query, parsedScope, workspaceId, limit,
            minRelativeScore, rrfK, ftsWeight, vectorWeight, contextLabel, sourceLambda,
            consolidationThreshold, docScoreFormula, candidateWindow);

        var guard = await queryGuard.EvaluateAsync(canonical, query, cancellationToken);
        if (guard.Shadowed is { } suppressed)
        {
            Log.QueryGuardShadowVerdict(logger, suppressed.Tier.ToString(), canonical,
                suppressed.PolicyName ?? string.Empty, QueryFingerprint(query));
        }

        if (guard.Verdict.Tier == QueryGuardTier.Refuse)
        {
            // The caller gets its own query text back in the error result; the server's own
            // channels (log line, OTLP span) carry only the fingerprint below.
            throw new RefusedQueryException(
                $"invalid-params: {guard.Verdict.Guidance} Refused query: {QuerySnippet(query)}",
                $"invalid-params: {guard.Verdict.Guidance} Refused query fingerprint ({guard.Verdict.PolicyName}): {QueryFingerprint(query)}");
        }

        var correlationId = Guid.CreateVersion7().ToString("N");
        var dispatch = await searchDispatcher.DispatchAsync(searchQuery, parsedKind, scope, correlationId,
            sessionId: sessionId, codeLimit: codeLimit, codeMinRelativeScore: codeMinRelativeScore,
            cancellationToken: cancellationToken);

        if (dispatch.MemorySearchResults is not null)
        {
            // The metrics table never leaves the machine (stripped from every pushed snapshot per ADR-0098,
            // like search_quality), so the query-content hash stays excluded for a code-adjacent kind
            // as defense-in-depth -- only the hash, not the whole recording
            // (integration review S6, unchanged by ADR-0094). search_quality itself now records
            // every kind (ADR-0094), with code paths never stored.
            var queryHash = parsedKind == SearchKind.Memory ? ContentHash.OfValue(query) : null;
            RecordSearchMeasurements(dispatch.MemorySearchResults, queryHash, correlationId, canonical);
        }

        var warning = await ComposeWarningAsync(guard.Verdict, query, parsedKind, dispatch.CodeWarning,
            cancellationToken);
        var result = BuildSearchResultList(dispatch, warning, searchQuery);
        var envelope = await gate.WrapAsync(canonical, result, cancellationToken);

        // ADR-0094: SearchDispatcher records a search_quality row for every kind, so every
        // envelope carries a backed correlation id -- a later memory_record_grade /
        // memory_record_followthrough call always has a row to key on. This supersedes the
        // integration-review S6 withholding rule (which held only while code/both wrote no row).
        return envelope with { Meta = envelope.Meta with { CorrelationId = correlationId } };
    }

    /// <summary>Mirrors the scope validation pattern: normalized case-insensitively, rejected fail-fast on a typo.</summary>
    private static SearchKind ParseKind(string kind) =>
        kind.ToLowerInvariant() switch
        {
            SearchKindWireNames.Memory => SearchKind.Memory,
            SearchKindWireNames.Code => SearchKind.Code,
            SearchKindWireNames.Both => SearchKind.Both,
            _ => throw new McpException($"invalid-params: Invalid kind '{kind}': expected memory, code, or both.")
        };

    /// <summary>S5: codeLimit/codeMinRelativeScore override limit/minRelativeScore for the code
    /// section only — SearchQueryValidator enforces the same two rules for their un-prefixed
    /// counterparts, so a section override must fail exactly the same way, not pass through
    /// silently into an empty section.</summary>
    private static void ValidateCodeOverrides(int? codeLimit, double? codeMinRelativeScore)
    {
        if (codeLimit is <= 0)
        {
            throw new McpException($"invalid-params: codeLimit must be greater than 0 (was {codeLimit}).");
        }

        if (codeMinRelativeScore is < 0.0 or > 1.0)
        {
            throw new McpException(
                $"invalid-params: codeMinRelativeScore must be between 0 and 1 inclusive (was {codeMinRelativeScore}).");
        }
    }

    /// <summary>
    ///     memory_search's one warning string. QueryLengthGuard is always on -- a fact about the
    ///     embedding window, not a togglable policy -- so it is evaluated here unconditionally, never
    ///     through IQueryGuardService (a disabled guard must not silence it), against the active
    ///     memory engine's budget whichever kind was requested.
    /// </summary>
    private async Task<string?> ComposeWarningAsync(QueryGuardVerdict guardVerdict, string query,
        SearchKind kind, string? codeWarning, CancellationToken cancellationToken)
    {
        var lengthBudget = await MemoryQueryBudgetTokensAsync(cancellationToken);
        return SearchWarnings.Compose(guardVerdict, QueryLengthGuard.Evaluate(query, lengthBudget),
            await MemoryEngineWarningAsync(kind, cancellationToken), codeWarning);
    }

    /// <summary>
    ///     The memory leg's engine note: `embedding.provider` unset means FTS5-only memory search
    ///     (F6), the same state doctor reports. Null for kind=code, whose leg never runs — reading
    ///     the setting then would be work with no consumer.
    /// </summary>
    private async Task<string?> MemoryEngineWarningAsync(SearchKind kind, CancellationToken cancellationToken)
    {
        if (kind == SearchKind.Code)
        {
            return null;
        }

        var provider = await settings.GetSettingAsync(EmbeddingSettingsKeys.Provider, cancellationToken);
        return SearchWarnings.MemoryEngineWarning(provider);
    }

    /// <summary>
    ///     QueryLengthGuard's reported budget must track the ACTIVE memory engine, not the bundled
    ///     model's fixed 254 -- resolved via IEmbeddingService.ResolveChunkBudgetFor (D6/D9), the same
    ///     number EntryEmbedder.EmbedQueryAsync will actually trim to. An unconfigured provider
    ///     resolves as "local" (the bundled model the remedy activates), so the guard's number still
    ///     matches what a fresh bank would do once fixed.
    /// </summary>
    private async Task<int> MemoryQueryBudgetTokensAsync(CancellationToken cancellationToken)
    {
        var provider = await settings.GetSettingAsync(EmbeddingSettingsKeys.Provider, cancellationToken);
        var model = await settings.GetSettingAsync(EmbeddingSettingsKeys.Model, cancellationToken);
        var resolvedProvider = string.IsNullOrWhiteSpace(provider) ? "local" : provider;
        return embeddings.ResolveChunkBudgetFor(new EmbeddingSettings(resolvedProvider, model, null, null));
    }

    /// <summary>
    ///     S3 join: the P4 sidecar onto the MCP envelope by hash. Dict-by-hash (not a parallel
    ///     list): the affinity reorder (SearchResultMerger.Merge) and the floor/limit permute
    ///     Results order, so a positional coupling would misalign; a hash key survives reorder,
    ///     gives consumers O(1) lookup, and lets an absent hash mean "no evidence" with no
    ///     placeholder. Iterates memory Results only: code hashes live in a separate namespace
    ///     (§8) and floored-out sidecar entries stay out (S10 bounded payload: returned rows
    ///     only). An empty served set carries no evidence and no stats (G3). Ranking is never
    ///     touched in name, position, or semantics. The absolute-relevance judgement runs first
    ///     (SearchRelevance.Judge): it drops rows below the absolute floor and decides the unranked
    ///     marker, and the join below covers the surviving rows only.
    /// </summary>
    private static SearchResultList BuildSearchResultList(SearchDispatchResult dispatch, string? warning, SearchQuery query)
    {
        if (dispatch.Results.Count == 0)
        {
            return new SearchResultList(dispatch.Results, warning, dispatch.CodeResults);
        }

        var sidecar = dispatch.MemorySearchResults;
        var judgement = SearchRelevance.Judge(dispatch.Results, sidecar?.EvidenceByHash, sidecar?.Stats, query.MinRelativeScore,
            sidecar?.AllTermsMatched);
        var truncation = TruncationFor(judgement.Results.Count, dispatch.Results.Count, query, sidecar?.DroppedByFloor ?? 0);
        if (judgement.Results.Count == 0)
        {
            return new SearchResultList(judgement.Results, warning, dispatch.CodeResults, Truncation: truncation);
        }

        if (sidecar?.EvidenceByHash is not { } evidence)
        {
            return new SearchResultList(judgement.Results, warning, dispatch.CodeResults, null, sidecar?.Stats, judgement.Unranked, truncation);
        }

        var joined = new Dictionary<string, RetrievalEvidence>(judgement.Results.Count, StringComparer.Ordinal);
        foreach (var result in judgement.Results)
        {
            if (evidence.TryGetValue(result.Hash, out var item))
            {
                joined[result.Hash] = item;
            }
        }

        return new SearchResultList(judgement.Results, warning, dispatch.CodeResults,
            joined.Count > 0 ? joined : null, sidecar.Stats, judgement.Unranked, truncation);
    }

    /// <summary>
    ///     A response short of its requested limit reports the floor cuts that shortened it: one
    ///     entry per floor that dropped candidates (name, threshold, count). A response that fills
    ///     the limit stays clean, and so does one that is short because the bank simply ends.
    /// </summary>
    private static IReadOnlyList<FloorTruncation>? TruncationFor(
        int served, int preAbsoluteCount, SearchQuery query, int droppedByRelativeFloor)
    {
        if (served >= query.Limit)
        {
            return null;
        }

        List<FloorTruncation>? truncation = null;
        if (droppedByRelativeFloor > 0)
        {
            (truncation ??= []).Add(new FloorTruncation(SearchRelevance.RelativeFloorName, query.MinRelativeScore, droppedByRelativeFloor));
        }

        var droppedByAbsoluteFloor = preAbsoluteCount - served;
        if (droppedByAbsoluteFloor > 0)
        {
            (truncation ??= []).Add(new FloorTruncation(SearchRelevance.AbsoluteRelevanceFloorName, SearchRelevance.AbsoluteRelevanceFloor, droppedByAbsoluteFloor));
        }

        return truncation;
    }

    /// <summary>
    ///     Builds the query from the wire values: enum strings are parsed and rejected on typo,
    ///     the identity rules run, and the provided tuning values are range-checked fail-fast via
    ///     the resolved record's rule set (unset options fall back to valid constants).
    /// </summary>
    private static SearchQuery BuildValidatedSearchQuery(
        string projectId, string query, SearchScope scope, string? workspaceId, int limit,
        double minRelativeScore, int? rrfK, int? ftsWeight, int? vectorWeight, string? contextLabel,
        double? sourceLambda, double? consolidationThreshold, string? docScoreFormula, string? candidateWindow)
    {
        DocScoreFormula? parsedFormula = null;
        if (docScoreFormula is not null)
        {
            parsedFormula = SearchParameterSettingsKeys.ParseDocScoreFormula(docScoreFormula)
                            ?? throw new McpException($"invalid-params: Invalid docScoreFormula '{docScoreFormula}': expected 'max' or 'sum'.");
        }

        CandidateWindowMode? parsedWindow = null;
        if (candidateWindow is not null)
        {
            parsedWindow = SearchParameterSettingsKeys.ParseCandidateWindow(candidateWindow)
                           ?? throw new McpException($"invalid-params: Invalid candidateWindow '{candidateWindow}': expected 'max3x100' or 'max5x50'.");
        }

        var searchQuery = new SearchQuery(projectId, query, scope, workspaceId, limit, minRelativeScore,
            rrfK, ftsWeight, vectorWeight, contextLabel, sourceLambda, consolidationThreshold, parsedFormula, parsedWindow);

        SearchQueryValidator.ValidateAndThrow(searchQuery);
        SearchParameters.FromSources(searchQuery);
        return searchQuery;
    }

    [McpServerTool(Name = TnMemoryList)]
    [Description("Lists the bank's indexed files as a JSON tree (memory_list_files).")]
    public async Task<ApiEnvelope<ListResult>> List(
        [Description("The project id.")] string? projectId = null,
        CancellationToken cancellationToken = default)
    {
        var canonical = await gate.RequireAsync(projectId, AccessRequirement.Read, TnMemoryList, cancellationToken);
        var files = await store.ListFilesAsync(canonical, cancellationToken);
        var result = new ListResult(JsonNode.Parse(files) ?? new JsonObject());
        var envelope = await gate.WrapAsync(canonical, result, cancellationToken);
        return envelope;
    }

    [McpServerTool(Name = TnMemoryStats)]
    [Description("Reports entry count, pending (deferred-embedding) count, and the bank's committed contexts.")]
    public async Task<ApiEnvelope<StatsResult>> Stats(
        [Description("The project id.")] string? projectId = null,
        CancellationToken cancellationToken = default)
    {
        var canonical = await gate.RequireAsync(projectId, AccessRequirement.Read, TnMemoryStats, cancellationToken);
        var stats = await store.GetStatsAsync(canonical, cancellationToken);
        var result = new StatsResult(stats.EntryCount, stats.PendingCount, stats.Contexts);
        var envelope = await gate.WrapAsync(canonical, result, cancellationToken);
        return envelope;
    }

    [McpServerTool(Name = TnMemoryDelete)]
    [Description(
        "Deletes the whole memory behind a content hash: a multi-chunk memory_write's chunks share one write, so any of its chunk hashes deletes all of them, not just one. Idempotent: an unknown hash is not an error — it reports the true row count, 0 for an unknown hash.")]
    public async Task<ApiEnvelope<DeletedResult>> Delete(
        [Description("The project id.")] [Optional][DefaultParameterValue("")] string projectId,
        [Description("The content hash to delete — any chunk of a multi-chunk write reaches the whole write.")]
        string hash,
        CancellationToken cancellationToken = default)
    {
        var canonical = await gate.RequireAsync(projectId, AccessRequirement.Destructive, TnMemoryDelete, cancellationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);

        var deleted = await store.DeleteAsync(canonical, hash, cancellationToken);
        var result = new DeletedResult(deleted);
        var envelope = await gate.WrapAsync(canonical, result, cancellationToken);
        return envelope;
    }

    [McpServerTool(Name = TnMemoryDeleteContext)]
    [Description(
        "Deletes every entry stored under a context label (e.g. a project or workspace context). Idempotent: an unknown context is not an error — it reports deleted=0.")]
    public async Task<ApiEnvelope<DeletedContextResult>> DeleteContext(
        [Description("The project id.")] [Optional][DefaultParameterValue("")] string projectId,
        [Description("The context label to delete.")]
        string context,
        CancellationToken cancellationToken = default)
    {
        var canonical = await gate.RequireAsync(projectId, AccessRequirement.Destructive, TnMemoryDeleteContext, cancellationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(context);

        var deleted = await store.DeleteContextAsync(canonical, context, cancellationToken);
        var result = new DeletedContextResult(deleted);
        var envelope = await gate.WrapAsync(canonical, result, cancellationToken);
        return envelope;
    }

    [McpServerTool(Name = TnMemoryIngestFile)]
    [Description("Indexes one file from disk into memory. The path must lie inside the project's configured scope (ai-raccoon settings ingest scope add); an unscoped project refuses every ingest.")]
    public async Task<ApiEnvelope<IngestResult>> IngestFile(
        [Description("The project id.")] [Optional][DefaultParameterValue("")] string projectId,
        [Description("Path of the file to index.")]
        string path,
        [Description("Optional context label.")]
        string? context = null,
        CancellationToken cancellationToken = default)
    {
        var canonical = await gate.RequireAsync(projectId, AccessRequirement.Write, TnMemoryIngestFile, cancellationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var indexed = await store.IngestFileAsync(canonical, path, context, cancellationToken);
        var result = new IngestResult(indexed);
        var envelope = await gate.WrapAsync(canonical, result, cancellationToken);
        return envelope;
    }

    [McpServerTool(Name = TnMemoryIngestDirectory)]
    [Description(
        "Recursively indexes a directory tree into memory, skipping unchanged files. The path must lie inside the project's configured scope (ai-raccoon settings ingest scope add); an unscoped project refuses every ingest.")]
    public async Task<ApiEnvelope<ScannedResult>> IngestDirectory(
        [Description("The project id.")] [Optional][DefaultParameterValue("")] string projectId,
        [Description("Path of the directory to index.")]
        string path,
        [Description("Optional context label applied to all files.")]
        string? context = null,
        CancellationToken cancellationToken = default)
    {
        var canonical = await gate.RequireAsync(projectId, AccessRequirement.Write, TnMemoryIngestDirectory, cancellationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var scanned = await store.IngestDirectoryAsync(canonical, path, context, cancellationToken);
        var result = new ScannedResult(scanned);
        var envelope = await gate.WrapAsync(canonical, result, cancellationToken);
        return envelope;
    }

    [McpServerTool(Name = TnMemoryEmbedPending)]
    [Description("Embeds deferred entries in batches (used when no model was configured at write time).")]
    public async Task<ApiEnvelope<EmbedResult>> EmbedPending(
        [Description("The project id.")] string? projectId = null,
        [Description("Maximum rows to process in this call; omit for all.")]
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var canonical = await gate.RequireAsync(projectId, AccessRequirement.Write, TnMemoryEmbedPending, cancellationToken);

        var result = await store.EmbedPendingAsync(canonical, limit, cancellationToken);
        var embedResult = new EmbedResult(result.Processed, result.Pending);
        var envelope = await gate.WrapAsync(canonical, embedResult, cancellationToken);
        return envelope;
    }

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record WriteResult(string Hash, string Path, string Context, long CreatedAt, bool Stored = true, string? Reason = null);

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record GetResult(string Hash, string Value, string Path, string Context, long CreatedAt);

    /// <summary>
    ///     Warning is set only by the query guard's annotate tier (docs/adr/0040) or a corpus's
    ///     degraded-mode note (the memory engine's, or the code section's): a non-null value never
    ///     changes Results. Code is null (and omitted from the wire,
    ///     <see cref="JsonIgnoreCondition.WhenWritingNull" />) for kind=memory — the pinned envelope
    ///     contract (docs/work/2026-08-21-code-search-implementation-plan.md §3.6): kind=memory
    ///     serializes the exact legacy shape, no "code" key at all.
    ///     Unranked is the explicit marker for rankings with no absolute relevance backing, and
    ///     Truncation reports the floor cuts behind a response short of its limit; both are omitted
    ///     from the wire unless set (<see cref="JsonIgnoreCondition.WhenWritingDefault" />).
    /// </summary>
    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record SearchResultList(
        IReadOnlyList<MemorySearchResult> Results,
        string? Warning = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<CodeSearchResult>? Code = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyDictionary<string, RetrievalEvidence>? EvidenceByHash = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        FusionStats? FusionStats = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        bool Unranked = false,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<FloorTruncation>? Truncation = null);

    /// <summary>One score floor's truncation report: which floor, its threshold, and how many candidates it dropped.</summary>
    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record FloorTruncation(string Floor, double Threshold, int Dropped);

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
    ///     Tags each of the nine search phases plus the measured total — plus the fusion diff,
    ///     present only when the no-fusion-regression flag is on (docs/adr/0078) — with the query
    ///     hash and correlation id and hands them to the recorder; never the query text itself
    ///     (SqliteMetricsStore's save-time allowlist rejects it). queryHash is null for a
    ///     code-adjacent kind (kind=both): the metrics table never leaves the machine (stripped
    ///     from every pushed snapshot per ADR-0098, like search_quality), so a code-adjacent
    ///     query's content hash is excluded the same way as defense-in-depth. Best-effort: a
    ///     throwing recorder must never fail or slow the search (WP3).
    /// </summary>
    private void RecordSearchMeasurements(SearchResults results, string? queryHash, string correlationId, string projectId)
    {
        try
        {
            var recordedAt = _timeProvider.GetUtcNow();
            foreach (var (name, value) in results.Timings.Measurements())
            {
                measurements.Record(new Measurement(name, MeasurementKind.Histogram, value.TotalMilliseconds,
                    "ms", recordedAt, projectId, queryHash, correlationId));
            }

            foreach (var (name, value, unit) in results.Fusion?.Measurements() ?? [])
            {
                measurements.Record(new Measurement(name, MeasurementKind.Gauge, value,
                    unit, recordedAt, projectId, queryHash, correlationId));
            }

            foreach (var (name, value, unit) in FusionSignalMeasurements(results))
            {
                measurements.Record(new Measurement(name, MeasurementKind.Gauge, value,
                    unit, recordedAt, projectId, queryHash, correlationId));
            }
        }
        catch (Exception ex)
        {
            Log.PhaseMeasurementRecordingFailed(logger, ex, correlationId);
        }
    }

    /// <summary>
    ///     Stage-1 response-shape series (plan §5, P6b): top_strength is the max fusion strength
    ///     over the served rows that carry evidence — reorder-invariant, equal to rank-1 strength
    ///     when nothing reordered — while top_margin and legs_fired come from Stats. Skip-null:
    ///     a missing margin or leg set emits no series at all (M8 — a 0.0 sentinel would poison
    ///     Stage-2 distributions). Carries the caller's queryHash unchanged, so the
    ///     code-adjacent null rule applies to these series exactly as to the phase series.
    /// </summary>
    private static IEnumerable<(string Name, double Value, string Unit)> FusionSignalMeasurements(SearchResults results)
    {
        double? topStrength = null;
        if (results.EvidenceByHash is { } byHash)
        {
            foreach (var row in results.Results)
            {
                if (byHash.TryGetValue(row.Hash, out var evidence))
                {
                    topStrength = topStrength is null
                        ? evidence.FusionStrength
                        : Math.Max(topStrength.Value, evidence.FusionStrength);
                }
            }
        }

        if (topStrength is { } strength)
        {
            yield return (FusionStats.TopStrengthMetric, strength, "ratio");
        }

        if (results.Stats is { } stats)
        {
            if (stats.TopMargin is { } margin)
            {
                yield return (FusionStats.TopMarginMetric, margin, "ratio");
            }

            yield return (FusionStats.LegsFiredMetric, stats.ParticipatingLegs.Count, "legs");
        }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRunRegex();

    /// <summary>Single-line, whitespace-collapsed echo of the query for the caller's own refusal result, capped at 200 chars.</summary>
    private static string QuerySnippet(string query)
    {
        var collapsed = WhitespaceRunRegex().Replace(query, " ").Trim();
        return collapsed.Length <= 200 ? collapsed : collapsed[..200] + "…";
    }

    /// <summary>The refused query's raw length and content hash — the whole identity server-side channels keep, never its text.</summary>
    private static string QueryFingerprint(string query) =>
        $"{query.Length} chars, sha256 {ContentHash.OfValue(query)}";

    private static partial class Log
    {
        /// <summary>Kept here, not in Core: the guard decides, the host reports (docs/adr/0065).</summary>
        [LoggerMessage(EventId = 920, Level = LogLevel.Information,
            Message = "Query guard (shadow) would have returned {Tier} for project {ProjectId} via {PolicyName}; query fingerprint: {QueryFingerprint}")]
        public static partial void QueryGuardShadowVerdict(ILogger logger, string tier, string projectId, string policyName, string queryFingerprint);

        [LoggerMessage(EventId = 921, Level = LogLevel.Warning,
            Message = "Failed to record search phase measurements for correlation {CorrelationId}")]
        public static partial void PhaseMeasurementRecordingFailed(ILogger logger, Exception exception, string correlationId);
    }
}
