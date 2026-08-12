using System.ComponentModel;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using JetBrains.Annotations;
using ModelContextProtocol;
using ModelContextProtocol.Server;

// ReSharper disable ExplicitCallerInfoArgument

namespace AiRaccoon.Tools;

/// <summary>Thin MCP tools over the propose tier (IPromotionQueue) — no business logic here.</summary>
public sealed class PromotionTools(
    IPromotionQueue queue,
    ToolGate gate)
{
    private const string TnMemoryPromotionList = "memory_promotion_list";
    private const string TnMemoryPromotionDiscard = "memory_promotion_discard";

    [McpServerTool(Name = TnMemoryPromotionList)]
    [Description(
        "Lists the propose tier — candidates waiting for promotion review, ranked by score. Propose with memory_share_extract to fill it; promote the keepers with memory_share_extract (mode=promote) or drop them with memory_promotion_discard. Values are previews by default; pass includeFullValue=true for the full text.")]
    public async Task<ApiEnvelope<PromotionListResult>> List(
        [Description("The project id; omit to see every project's queue.")]
        string? projectId = null,
        [Description("Maximum rows (default 50).")]
        int limit = 50,
        [Description("Return each row's full value instead of a preview. Off by default — a full queue can be hundreds of KB.")]
        bool includeFullValue = false,
        CancellationToken cancellationToken = default)
    {
        if (limit < 1)
        {
            throw new McpException("invalid-params: limit must be at least 1");
        }

        if (projectId is not null)
        {
            await gate.RequireAsync(projectId, AccessRequirement.Read, TnMemoryPromotionList, cancellationToken);
        }

        var rows = await queue.ListAsync(projectId, limit, cancellationToken);
        var result = new PromotionListResult([.. rows.Select(r => ToView(r, includeFullValue))]);
        var envelope = await gate.WrapAsync(projectId, result, cancellationToken);
        return envelope;
    }

    private static PromotionQueueRowView ToView(PromotionQueueRow row, bool includeFullValue) =>
        new(row.ProjectId, row.Hash, row.Path, includeFullValue ? row.Value : Truncate(row.Value),
            row.SourceFile, row.Score, row.Reasons, row.CreatedAt, row.UpdatedAt);

    private static string Truncate(string value) =>
        value.Length <= SharedExtractionService.PreviewLength
            ? value
            : value[..(SharedExtractionService.PreviewLength - 1)] + "…";

    [McpServerTool(Name = TnMemoryPromotionDiscard)]
    [Description(
        "Removes a candidate from the propose tier without promoting it (the agent's 'no'). Omit the hash to clear the whole project's queue. Idempotent: an unknown hash is not an error — it reports discarded=0.")]
    public async Task<ApiEnvelope<PromotionDiscardResult>> Discard(
        [Description("The project id.")] string projectId,
        [Description("The queued hash to drop; omit to clear the project's whole queue.")]
        string? hash = null,
        CancellationToken cancellationToken = default)
    {
        await gate.RequireAsync(projectId, AccessRequirement.Write, TnMemoryPromotionDiscard, cancellationToken);

        var discarded = await queue.DiscardAsync(projectId, hash, cancellationToken);
        var result = new PromotionDiscardResult(discarded);
        var envelope = await gate.WrapAsync(projectId, result, cancellationToken);
        return envelope;
    }

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record PromotionListResult(IReadOnlyList<PromotionQueueRowView> Rows);

    /// <summary>A queued row for review — Value is a preview unless includeFullValue was set.</summary>
    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record PromotionQueueRowView(
        string ProjectId,
        string Hash,
        string Path,
        string Value,
        string? SourceFile,
        double Score,
        IReadOnlyList<string> Reasons,
        long CreatedAt,
        long UpdatedAt);

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record PromotionDiscardResult(int Discarded);
}
