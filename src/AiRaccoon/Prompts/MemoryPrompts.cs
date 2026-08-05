using System.ComponentModel;
using ModelContextProtocol.Server;

namespace AiRaccoon.Prompts;

/// <summary>Agent-facing usage guides; the text is the protocol contract (see docs/work/features-agent-memory/spec-issue-1.md §4.2).</summary>
public sealed class MemoryPrompts
{
    [McpServerPrompt(Name = "memory-usage-guide")]
    [Description("How to use the ai-raccoon memory server: project ids, workspace isolation, promotion to shared, and search scopes.")]
    public string MemoryUsageGuide(
        [Description("Optional project id to tailor the guide to.")]
        string? projectId = null)
    {
        var projectLine = string.IsNullOrWhiteSpace(projectId)
            ? "Always pass the project_id of the project you are working in — every memory operation is scoped to a project."
            : $"You are working in project '{projectId}' — pass project_id={projectId} on every memory call.";

        return $"""
                {projectLine}
                - Plain writes (memory_write without a workspace_id) land in the project's committed memory (project:<id>).
                - When a workspace is active, write with its workspace_id so notes stay in the isolated workspace outbox; consolidate when you finish.
                - Promote durable, cross-project knowledge with memory_share — shared entries are curated and never swept. Nothing is shared without an explicit promotion.
                - Search with scope=all (default) to see shared + project memory; scope=project narrows to the project; scope=shared searches only the promotion tier.
                - Search before asking the user; write durable facts, not raw chatter.
                - Degradation (memory_sweep) removes old, low-rated project entries; shared entries are exempt.
                """;
    }

    [McpServerPrompt(Name = "workspace-consolidation-guide")]
    [Description("Ritual for finishing a workspace: review the outbox, promote durable facts, drop noise.")]
    public string WorkspaceConsolidationGuide(
        [Description("Optional workspace id being finished.")]
        string? workspaceId = null,
        [Description("Optional project id.")] string? projectId = null)
    {
        var workspaceLine = string.IsNullOrWhiteSpace(workspaceId)
            ? "Before declaring a task done, finish your workspace:"
            : $"Before declaring the task done, finish workspace '{workspaceId}':";

        return $"""
                {workspaceLine}
                1. Call memory_workspace_status to list what your workspace outbox holds.
                2. Decide which entries are durable facts worth keeping in the project's committed memory.
                3. Promote the keepers with memory_workspace_consolidate keep=[...] (or ["all"]), and promote anything that belongs to every project with memory_share.
                4. Anything you do not keep is discarded when the workspace context is removed.
                """;
    }
}
