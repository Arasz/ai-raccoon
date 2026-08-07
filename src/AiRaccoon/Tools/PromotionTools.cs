using System.ComponentModel;
using AiRaccoon.Access;
using AiRaccoon.Core;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Observability;
using JetBrains.Annotations;
using ModelContextProtocol;
using ModelContextProtocol.Server;

// ReSharper disable ExplicitCallerInfoArgument

namespace AiRaccoon.Tools;

/// <summary>Thin MCP tools over the propose tier (IPromotionQueue) — no business logic here.</summary>
public sealed class PromotionTools(
    IPromotionQueue queue,
    IMemoryAccessGuard access,
    ToolCallMetrics observability)
{
    private const string TnMemoryPromotionList = "memory_promotion_list";
    private const string TnMemoryPromotionDiscard = "memory_promotion_discard";

    private static void RequireProjectId(string? projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            throw new McpException("invalid-params: project_id is required");
        }
    }

    private async Task RequireAsync(string projectId, AccessRequirement requirement, string toolName,
        CancellationToken cancellationToken) =>
        await access.EnsureAsync(projectId, requirement, toolName, cancellationToken).ConfigureAwait(false);

    [McpServerTool(Name = TnMemoryPromotionList)]
    [Description(
        "Lists the propose tier — candidates waiting for promotion review, ranked by score. Propose with memory_share_extract to fill it; promote the keepers with memory_share_extract (mode=promote) or drop them with memory_promotion_discard.")]
    public async Task<ApiEnvelope<PromotionListResult>> List(
        [Description("The project id; omit to see every project's queue.")]
        string? projectId = null,
        [Description("Maximum rows (default 50).")]
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        using var activity = new ToolExecutionActivity(observability, TnMemoryPromotionList, projectId ?? "all");
        try
        {
            if (projectId is not null)
            {
                RequireProjectId(projectId);
                await RequireAsync(projectId, AccessRequirement.Read, TnMemoryPromotionList, cancellationToken);
            }

            var rows = await queue.ListAsync(projectId, limit, cancellationToken);
            var result = new PromotionListResult(rows);
            var envelope = await WrapAsync(result, cancellationToken);
            activity.RecordInvocation();
            return envelope;
        }
        catch (Exception ex)
        {
            activity.RecordError(ex);
            throw;
        }
    }

    [McpServerTool(Name = TnMemoryPromotionDiscard)]
    [Description(
        "Removes a candidate from the propose tier without promoting it (the agent's 'no'). Omit the hash to clear the whole project's queue.")]
    public async Task<ApiEnvelope<PromotionDiscardResult>> Discard(
        [Description("The project id.")] string projectId,
        [Description("The queued hash to drop; omit to clear the project's whole queue.")]
        string? hash = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = new ToolExecutionActivity(observability, TnMemoryPromotionDiscard, projectId);
        try
        {
            RequireProjectId(projectId);
            await RequireAsync(projectId, AccessRequirement.Write, TnMemoryPromotionDiscard, cancellationToken);

            var discarded = await queue.DiscardAsync(projectId, hash, cancellationToken);
            var result = new PromotionDiscardResult(discarded);
            var envelope = await WrapAsync(result, cancellationToken);
            activity.RecordInvocation();
            return envelope;
        }
        catch (Exception ex)
        {
            activity.RecordError(ex);
            throw;
        }
    }

    private async Task<ApiEnvelope<T>> WrapAsync<T>(T data, CancellationToken cancellationToken) =>
        new(data, await queue.GetMetaAsync(cancellationToken).ConfigureAwait(false));

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record PromotionListResult(IReadOnlyList<PromotionQueueRow> Rows);

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record PromotionDiscardResult(int Discarded);
}
