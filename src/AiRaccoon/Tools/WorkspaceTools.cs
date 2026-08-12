using System.ComponentModel;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Workspace;
using JetBrains.Annotations;
using ModelContextProtocol.Server;

// ReSharper disable ExplicitCallerInfoArgument

namespace AiRaccoon.Tools;

/// <summary>Thin MCP tools over WorkspaceService — no business logic here (see docs/work/features-agent-memory/spec-issue-1.md §6.1).</summary>
public sealed class WorkspaceTools(
    WorkspaceService workspaces,
    ToolGate gate)
{
    private const string TnMemoryWorkspaceBegin = "memory_workspace_begin";
    private const string TnMemoryWorkspaceStatus = "memory_workspace_status";
    private const string TnMemoryWorkspaceConsolidate = "memory_workspace_consolidate";
    private const string TnMemoryWorkspaceDiscard = "memory_workspace_discard";

    [McpServerTool(Name = TnMemoryWorkspaceBegin)]
    [Description(
        "Begins a workspace sandbox: returns a workspace_id whose context is isolated by design. While it is active, write with that workspace_id so notes stay in the outbox.")]
    public async Task<ApiEnvelope<WorkspaceBeginResult>> WorkspaceBegin(
        [Description("The project id.")] string projectId,
        [Description("Provenance only: which agent is working in this workspace.")]
        string? agentId = null,
        [Description("Optional human-readable workspace name.")]
        string? name = null,
        CancellationToken cancellationToken = default)
    {
        await gate.RequireAsync(projectId, AccessRequirement.Write, TnMemoryWorkspaceBegin, cancellationToken);

        var workspace = await workspaces.BeginAsync(projectId, agentId, name, cancellationToken);
        var result = new WorkspaceBeginResult(workspace.Id, workspace.Context);
        var envelope = await gate.WrapAsync(projectId, result, cancellationToken);
        return envelope;
    }

    [McpServerTool(Name = TnMemoryWorkspaceStatus)]
    [Description("Lists the entries currently in a workspace's outbox.")]
    public async Task<ApiEnvelope<WorkspaceStatusResult>> WorkspaceStatus(
        [Description("The project id.")] string projectId,
        [Description("The workspace id.")] string workspaceId,
        CancellationToken cancellationToken = default)
    {
        await gate.RequireAsync(projectId, AccessRequirement.Read, TnMemoryWorkspaceStatus, cancellationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        var outbox = await workspaces.GetStatusAsync(projectId, workspaceId, cancellationToken);
        var result = new WorkspaceStatusResult(outbox.Entries, outbox.Entries.Count,
            outbox.Workspace.AgentId, outbox.Workspace.Name);
        var envelope = await gate.WrapAsync(projectId, result, cancellationToken);
        return envelope;
    }

    [McpServerTool(Name = TnMemoryWorkspaceConsolidate)]
    [Description(
        "Finishes a workspace: promotes the kept hashes (or ['all']) from the workspace outbox into the project's committed memory, then removes the workspace context.")]
    public async Task<ApiEnvelope<ConsolidationToolResult>> WorkspaceConsolidate(
        [Description("The project id.")] string projectId,
        [Description("The workspace id.")] string workspaceId,
        [Description("Hashes to promote, or ['all'] to promote everything.")]
        string[] keep,
        CancellationToken cancellationToken = default)
    {
        await gate.RequireAsync(projectId, AccessRequirement.Destructive, TnMemoryWorkspaceConsolidate, cancellationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentNullException.ThrowIfNull(keep);

        var result = await workspaces.ConsolidateAsync(projectId, workspaceId, keep, cancellationToken);
        var toolResult = new ConsolidationToolResult(result.Promoted, result.Discarded);
        var envelope = await gate.WrapAsync(projectId, toolResult, cancellationToken);
        return envelope;
    }

    [McpServerTool(Name = TnMemoryWorkspaceDiscard)]
    [Description("Discards a workspace without promoting anything: removes its outbox context and all its entries.")]
    public async Task<ApiEnvelope<WorkspaceDiscardResult>> WorkspaceDiscard(
        [Description("The project id.")] string projectId,
        [Description("The workspace id.")] string workspaceId,
        CancellationToken cancellationToken = default)
    {
        await gate.RequireAsync(projectId, AccessRequirement.Destructive, TnMemoryWorkspaceDiscard, cancellationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        var discarded = await workspaces.DiscardAsync(projectId, workspaceId, cancellationToken);
        var result = new WorkspaceDiscardResult(discarded);
        var envelope = await gate.WrapAsync(projectId, result, cancellationToken);
        return envelope;
    }

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record WorkspaceBeginResult(string WorkspaceId, string Context);

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record WorkspaceStatusResult(
        IReadOnlyList<MemoryEntry> Entries,
        int Count,
        string? AgentId,
        string? Name);

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record ConsolidationToolResult(int Promoted, int Discarded);

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record WorkspaceDiscardResult(int Discarded);
}
