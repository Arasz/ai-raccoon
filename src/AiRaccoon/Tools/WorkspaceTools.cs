using System.ComponentModel;
using System.Runtime.InteropServices;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Isolation;
using AiRaccoon.Core.Memory;
using JetBrains.Annotations;
using ModelContextProtocol.Server;

// ReSharper disable ExplicitCallerInfoArgument

namespace AiRaccoon.Tools;

/// <summary>
///     Thin MCP tools over WorkspaceService — no business logic here (see docs/work/features-agent-memory/spec-issue-1.md §6.1).
///     The whole lifecycle is gated Write, not Destructive: a workspace is a sandbox the caller
///     created and never committed, and both terminal transitions are confined to it (ADR-0052).
/// </summary>
public sealed class WorkspaceTools(
    IWorkspaceService workspaces,
    IToolGate gate)
{
    private const string TnInitializeWorkspace = "initialize_workspace";
    private const string TnGetWorkspaceStatus = "get_workspace_status";
    private const string TnCloseWorkspace = "close_workspace";
    private const string TnDiscardWorkspace = "discard_workspace";

    [McpServerTool(Name = TnInitializeWorkspace)]
    [McpToolScope(ToolScope.Project)]
    [Description(
        "Begins a workspace sandbox: returns a workspace_id whose context is isolated by design. While it is active, write with that workspace_id so notes stay in the outbox.")]
    public async Task<ApiEnvelope<WorkspaceBeginResult>> WorkspaceBegin(
        [Description("The project id.")] string? projectId = null,
        [Description("Provenance only: which agent is working in this workspace.")]
        string? agentId = null,
        [Description("Optional human-readable workspace name.")]
        string? name = null,
        CancellationToken cancellationToken = default)
    {
        var canonical = await gate.RequireAsync(projectId, AccessRequirement.Write, TnInitializeWorkspace, cancellationToken);

        var workspace = await workspaces.BeginAsync(canonical, agentId, name, cancellationToken);
        var result = new WorkspaceBeginResult(workspace.Id, workspace.Context);
        var envelope = await gate.WrapAsync(canonical, result, cancellationToken);
        return envelope;
    }

    [McpServerTool(Name = TnGetWorkspaceStatus)]
    [McpToolScope(ToolScope.Project)]
    [Description("Lists the entries currently in a workspace's outbox.")]
    public async Task<ApiEnvelope<WorkspaceStatusResult>> WorkspaceStatus(
        [Description("The project id.")][Optional][DefaultParameterValue("")] string projectId,
        [Description("The workspace id.")] string workspaceId,
        CancellationToken cancellationToken = default)
    {
        var canonical = await gate.RequireAsync(projectId, AccessRequirement.Read, TnGetWorkspaceStatus, cancellationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        var outbox = await workspaces.GetStatusAsync(canonical, workspaceId, cancellationToken);
        var result = new WorkspaceStatusResult(outbox.Entries, outbox.Entries.Count,
            outbox.Workspace.AgentId, outbox.Workspace.Name);
        var envelope = await gate.WrapAsync(canonical, result, cancellationToken);
        return envelope;
    }

    [McpServerTool(Name = TnCloseWorkspace)]
    [McpToolScope(ToolScope.Project)]
    [Description(
        "Finishes a workspace: promotes the kept hashes (or ['all']) from the workspace outbox into the project's committed memory, then removes the workspace context.")]
    public async Task<ApiEnvelope<ConsolidationToolResult>> WorkspaceConsolidate(
        [Description("The project id.")][Optional][DefaultParameterValue("")] string projectId,
        [Description("The workspace id.")] string workspaceId,
        [Description("Hashes to promote, or ['all'] to promote everything.")]
        string[] keep,
        CancellationToken cancellationToken = default)
    {
        var canonical = await gate.RequireAsync(projectId, AccessRequirement.Write, TnCloseWorkspace, cancellationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentNullException.ThrowIfNull(keep);

        var result = await workspaces.ConsolidateAsync(canonical, workspaceId, keep, cancellationToken);
        var toolResult = new ConsolidationToolResult(result.Promoted, result.Discarded);
        var envelope = await gate.WrapAsync(canonical, toolResult, cancellationToken);
        return envelope;
    }

    [McpServerTool(Name = TnDiscardWorkspace)]
    [McpToolScope(ToolScope.Project)]
    [Description("Discards a workspace without promoting anything: removes its outbox context and all its entries.")]
    public async Task<ApiEnvelope<WorkspaceDiscardResult>> WorkspaceDiscard(
        [Description("The project id.")][Optional][DefaultParameterValue("")] string projectId,
        [Description("The workspace id.")] string workspaceId,
        CancellationToken cancellationToken = default)
    {
        var canonical = await gate.RequireAsync(projectId, AccessRequirement.Write, TnDiscardWorkspace, cancellationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        var discarded = await workspaces.DiscardAsync(canonical, workspaceId, cancellationToken);
        var result = new WorkspaceDiscardResult(discarded);
        var envelope = await gate.WrapAsync(canonical, result, cancellationToken);
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
