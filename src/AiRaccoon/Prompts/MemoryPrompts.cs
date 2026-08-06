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
                - **Search memory first** — before web search, code search, or asking the user: call memory_search (project_id, scope=all) with 2-3 formulations — exact phrase, keywords, then a plain-English restatement. Entries carry source paths; a hit that answers the question is evidence, so cite its source.
                - Escalate by result: a decisive hit → use it. A partial hit → combine memory with one targeted external search for the missing piece. No hit → search externally, then write the finding back with memory_write (include the source path) so the next lookup is answered from memory.
                - Watch: to mirror a repo or docs directory into memory, watching must be enabled with the path inside the scope allowlist — one-time per install via the CLI: `ai-raccoon watch scope add '<project-id|*>' <path>` and `ai-raccoon watch enable '<project-id|*>' true` (quote the `*` so the shell doesn't expand it into the current directory's files). Then register the path with memory_watch_add (project_id + absolute path); the initial scan runs in the background (memory_watch_status reports scanning → healthy). Watched content becomes searchable automatically; stop with memory_watch_remove.
                - Plain writes (memory_write without a workspace_id) land in the project's committed memory (project:<id>).
                - When a workspace is active, write with its workspace_id so notes stay in the isolated workspace outbox; consolidate when you finish.
                - Promote durable, cross-project knowledge with memory_share — shared entries are curated and never swept. Nothing is shared without an explicit promotion.
                - Proposals wait in the propose tier: memory_share_extract (project_id(s), propose) ranks candidates and QUEUES them; memory_promotion_list shows what is waiting (the queue is capacity-capped — over it, the weakest candidate of the biggest occupier is evicted). Review the queue, promote the keepers with memory_share_extract (mode=promote, drains the queue), drop the rest with memory_promotion_discard. autoPromote exists but stays OFF by default and requires confirm=true — it shares data between projects.
                - Search with scope=all (default) to see shared + project memory; scope=project narrows to the project; scope=shared searches only the promotion tier.
                - Write durable facts, not raw chatter: one entry per fact, source included, so entries stay verifiable.
                - Degradation (memory_sweep) removes old, low-rated project entries; shared entries are exempt.
                - Bulk-load corpora with memory_ingest_file / memory_ingest_directory; memory_stats shows bank size; memory_sync exchanges snapshots with cloud storage when configured.
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
