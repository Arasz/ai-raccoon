using System.ComponentModel;
using ModelContextProtocol.Server;

namespace AiRaccoon.Resources;

/// <summary>
///     SEP-2640 skill-discovery surface: advertises the ai-raccoon-memory skill via
///     skill://index.json so Agent Framework clients (UseMcpSkills) can load it on demand.
///     Static by design — the index and skill body are constants, no bank coupling.
/// </summary>
[McpServerResourceType]
internal sealed class SkillResources
{
    public const string IndexUri = "skill://index.json";
    public const string MemorySkillUri = "skill://ai-raccoon-memory/SKILL.md";

    private const string SkillName = "ai-raccoon-memory";
    private const string SkillDescription =
        "Use when a project needs a memory server — search project and shared memory first, write durable facts with source paths, watch a docs directory, or promote facts across projects.";

    private const string IndexJson =
        """
        {
          "$schema": "https://schemas.agentskills.io/discovery/0.2.0/schema.json",
          "skills": [
            {
              "name": "ai-raccoon-memory",
              "type": "skill-md",
              "description": "Use when a project needs a memory server — search project and shared memory first, write durable facts with source paths, watch a docs directory, or promote facts across projects.",
              "url": "skill://ai-raccoon-memory/SKILL.md"
            }
          ]
        }
        """;

    private const string MemorySkillMd =
        """
        ---
        name: ai-raccoon-memory
        description: Use when a project needs a memory server — search project and shared memory first, write durable facts with source paths, watch a docs directory, or promote facts across projects.
        ---

        # AiRaccoon Memory

        ## 1. Watch-on-docs ritual (do this first)

        On session start, run `memory_watch_status(project_id)` for this project. If the docs directory
        is not in the watched list, run `memory_watch_add(project_id, <absolute path to docs>)` to mirror
        it into memory. The watch starts `scanning` and settles to `healthy`; an already-watched path is
        a no-op.

        If the watch errors with `watching-disabled` or `path-outside-scope`, the one-time per-install
        CLI setup is missing: `ai-raccoon watch scope add '<project-id|*>' <path>`, then
        `ai-raccoon watch enable '<project-id|*>' true`.

        ## 2. Search-first workflow

        Always pass `project_id`. Before web search, code search, or asking the user, run
        `memory_search(project_id, scope=all)` with 2-3 formulations: exact phrase → keywords →
        plain-English restatement. Entries carry source paths — cite them as evidence.

        ## 3. Escalation by result

        - Decisive hit → use it; cite the source path.
        - Partial hit → one targeted external search, then reconcile.
        - No hit → search externally, then write the finding back with `memory_write` (source path included).

        ## 4. Write discipline

        Durable facts only, one per entry, source included. Plain writes land in committed project
        memory. For in-progress notes use workspace isolation:
        `memory_workspace_begin` → `memory_workspace_status` → `memory_workspace_consolidate(keep=[...])`
        (or `["all"]` to promote everything; `memory_workspace_discard` to drop). Promote durable
        cross-project facts with `memory_share` — never automatically. `memory_sweep` removes old
        low-rated entries; shared entries are exempt.

        ## 5. Scopes

        `scope=all` (default: shared + project), `scope=project`, `scope=shared` (the promotion tier only).

        ## 6. Pitfalls

        - `memory_write` has **no `path` param** — the entry path is derived from its content.
        - **Never pass `context`** unless workspace isolation is intended: it silently sets
          `scope='custom'`, invisible to project-scoped search.
        - `memory_embed_pending`: omit `limit` to process all pending entries.
        - `memory_delete_context` requires full access mode.

        ## 7. Bulk ops

        `memory_ingest_file` / `memory_ingest_directory` bulk-load files; `memory_stats` reports bank
        size; `memory_sync` exchanges snapshots with cloud storage when configured.

        ## Verification Checklist

        - [ ] `memory_watch_status` shows the docs dir `healthy`
        - [ ] `memory_search(project_id, scope=all)` returns docs-derived hits
        - [ ] A durable finding was written back with `memory_write`, source path included
        """;

    [McpServerResource(UriTemplate = IndexUri, Name = "Skill Index", MimeType = "application/json")]
    [Description("SEP-2640 skill discovery index")]
    public static string GetIndex() => IndexJson;

    [McpServerResource(UriTemplate = MemorySkillUri, Name = "AiRaccoon Memory Skill", MimeType = "text/markdown")]
    [Description("Instructions for using the AiRaccoon memory tools")]
    public static string GetMemorySkillMd() => MemorySkillMd;
}
