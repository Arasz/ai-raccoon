using System.ComponentModel;
using AiRaccoon.Access;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Observability;
using JetBrains.Annotations;
using ModelContextProtocol;
using ModelContextProtocol.Server;

// ReSharper disable ExplicitCallerInfoArgument

namespace AiRaccoon.Tools;

/// <summary>Thin MCP tools over the shared-extraction pipeline — no business logic here (see docs/work/features-agent-memory/spec-issue-1.md §6.1).</summary>
public sealed class ShareTools(
    IMemoryStore store,
    ToolGate gate,
    ToolCallMetrics observability,
    SharedExtractionRunner extraction,
    IPromotionQueue queue)
{
    private const string TnMemoryShare = "memory_share";
    private const string TnMemoryShareExtract = "memory_share_extract";

    /// <summary>The activity tag is built before validation, and the protocol layer can still hand us a null array.</summary>
    private static string JoinProjectIds(string[]? projectIds) => projectIds is null ? string.Empty : string.Join(",", projectIds);

    [McpServerTool(Name = TnMemoryShare)]
    [Description(
        "Promotes an existing project entry into the flat shared context — the curated, cross-project, sweep-exempt tier. Nothing is shared without this explicit promotion.")]
    public async Task<ApiEnvelope<ShareResult>> Share(
        [Description("The project id.")] string projectId,
        [Description("The content hash to promote.")]
        string hash,
        CancellationToken cancellationToken = default)
    {
        using var activity = new ToolExecutionActivity(observability, TnMemoryShare, projectId);
        try
        {
            await gate.RequireAsync(projectId, AccessRequirement.Write, TnMemoryShare, cancellationToken);
            ArgumentException.ThrowIfNullOrWhiteSpace(hash);

            var entry = await store.ShareAsync(projectId, hash, cancellationToken);
            var result = new ShareResult(true, entry.Context);
            var envelope = await gate.WrapAsync(result, cancellationToken);
            activity.RecordInvocation();
            return envelope;
        }
        catch (Exception ex)
        {
            activity.RecordError(ex);
            throw;
        }
    }

    [McpServerTool(Name = TnMemoryShareExtract)]
    [Description(
        "Checks committed project memories and extracts the ones worth sharing. propose (default) ranks candidates and PERSISTS them into the propose tier — the waiting queue the agent reviews with memory_promotion_list. promote shares the top queued candidates into the curated, sweep-exempt shared tier and drains them. autoPromote (disabled by default) promotes the top queued candidates in the same call — it shares data BETWEEN PROJECTS, so it requires confirm=true as an explicit enable gate. Sharing stays explicit: propose first, review, then promote.")]
    public async Task<ApiEnvelope<ShareExtractResult>> ShareExtract(
        [Description("Project ids to scan (1..8).")]
        string[] projectIds,
        [Description("propose (default) queues ranked candidates; promote shares the top queued candidates.")]
        string mode = "propose",
        [Description("Maximum candidates to queue or promote per project (1..50; default 20).")]
        int? limit = null,
        [Description("Include rows with a TTL (ephemeral by design; promoting makes them sweep-exempt forever).")]
        bool includeTtlRows = false,
        [Description("Promote the top queued candidates in this call. Disabled by default: it shares data between projects.")]
        bool autoPromote = false,
        [Description("Explicit enable gate for autoPromote — acknowledges that candidates become visible to every project.")]
        bool confirm = false,
        CancellationToken cancellationToken = default)
    {
        using var activity = new ToolExecutionActivity(observability, TnMemoryShareExtract,
            JoinProjectIds(projectIds));
        try
        {
            if (projectIds is null || projectIds.Length == 0 || projectIds.Length > 8)
            {
                throw new McpException("invalid-params: projectIds must contain 1..8 project ids");
            }

            var extractMode = mode switch
            {
                "propose" => ExtractMode.Propose,
                "promote" => ExtractMode.Promote,
                _ => throw new McpException("invalid-params: mode must be 'propose' or 'promote'")
            };
            var resolvedLimit = limit ?? SharedExtractionService.DefaultCandidateLimit;
            if (resolvedLimit is < 1 or > 50)
            {
                throw new McpException("invalid-params: limit must be between 1 and 50");
            }

            if (autoPromote && !confirm)
            {
                throw new McpException(
                    "confirm-required: autoPromote shares candidates with ALL projects — pass confirm=true to enable");
            }

            var promotes = extractMode == ExtractMode.Promote || autoPromote;
            foreach (var projectId in projectIds)
            {
                await gate.RequireAsync(projectId,
                        promotes ? AccessRequirement.Write : AccessRequirement.Read,
                        TnMemoryShareExtract, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (promotes)
            {
                var outcome = await queue.PromoteAsync(projectIds, resolvedLimit, cancellationToken)
                    .ConfigureAwait(false);
                var promoteEnvelope = await gate.WrapAsync(new ShareExtractResult([], outcome.PromotedHashes), cancellationToken);
                activity.RecordInvocation();
                return promoteEnvelope;
            }

            var sharedIndex = await store.GetSharedIndexAsync(cancellationToken).ConfigureAwait(false);
            var candidates = new List<ShareCandidate>();
            foreach (var projectId in projectIds)
            {
                candidates.AddRange(await extraction.ProposeAsync(projectId, sharedIndex,
                        includeTtlRows, resolvedLimit, cancellationToken)
                    .ConfigureAwait(false));
            }

            var envelope = await gate.WrapAsync(new ShareExtractResult(candidates, []), cancellationToken);

            activity.RecordInvocation();
            return envelope;
        }
        catch (Exception ex)
        {
            activity.RecordError(ex);
            throw;
        }
    }

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record ShareResult(bool Shared, string Context);
}
