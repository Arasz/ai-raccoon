# Research: MCP-based skill discovery with a memory skill for AiRaccoon

**Date:** 2026-08-07
**Question:** Should AiRaccoon — already an MCP server — add the SEP-2640 skill-discovery surface (a `skill://index.json` + a served "memory" skill) that Microsoft Agent Framework clients can discover, and what does the server side require?

## Findings

### F1 — The pinned MCP SDK already contains the full server-side resource API; no new package is needed [MEASURED]

AiRaccoon pins `ModelContextProtocol` 2.1.0, and that exact assembly ships `McpServerResourceAttribute` (properties `UriTemplate`, `Name`, `Title`, `MimeType`, `IconSource`), `McpServerResourceTypeAttribute`, `McpServerResource` (with `Create` factories), and the `WithResources<T>()` / `WithResourcesFromAssembly()` builder extensions. The skill server is therefore a pure additive change to `McpServerSetup`: register one resource class next to the existing `WithTools<T>()` / `WithPrompts<MemoryPrompts>()` registrations. The client-side `Microsoft.Agents.AI.Mcp` package is consumed by agent *clients*, not by AiRaccoon.

**Evidence:** Reflection probe (`/tmp/sdkprobe`, net10.0 console app referencing `ModelContextProtocol` 2.1.0, run on this Mac 2026-08-07) enumerating the attribute/resource types; plus `grep -a -o "WithResources[A-Za-z]*" ~/.nuget/packages/modelcontextprotocol/2.1.0/lib/net10.0/ModelContextProtocol.dll` → `WithResources`, `WithResourcesFromAssembly`.

### F2 — The protocol is SEP-2640: a `skill://index.json` discovery document plus per-skill `skill://<name>/SKILL.md` resources [READ]

The server advertises skills through a discovery document at `skill://index.json` and each skill's body via `resources/read`. The canonical index shape (schema 0.2.0):

```json
{ "$schema": "https://schemas.agentskills.io/discovery/0.2.0/schema.json",
  "skills": [ { "name": "unit-converter", "type": "skill-md",
                "description": "…advertisement…", "url": "skill://unit-converter/SKILL.md" } ] }
```

`skill-md` entries are fetched file-by-file; `archive` entries (ZIP/TAR/gzip) are downloaded and unpacked client-side with size/count guardrails. Progressive disclosure: the agent sees only name+description up front, loads the body when a task matches.

**Evidence:** `dotnet/samples/02-agents/AgentSkills/Agent_Step06_McpBasedSkills/Program.cs:109-149` (fetched from microsoft/agent-framework @ main, 2026-08-07) — server class with `[McpServerResourceType]`, index/SKILL.md constants, `[McpServerResource(UriTemplate = "skill://index.json", MimeType = "application/json")]`; and learn.microsoft.com/en-us/agent-framework/agents/skills#mcp-based-skills (skill:// scheme, two entry types, `resources/read`).

### F3 — The `$schema` URL in the official sample is currently a dead link [MEASURED]

`schemas.agentskills.io` has no DNS records from this machine (empty `dig` result), while the apex `agentskills.io` resolves and answers HTTP 200. The sample ships the schema URL anyway and presumably works, so clients tolerate an unresolvable `$schema` — but it is a signal that the discovery spec is young (blog post: 2026-07-28, API explicitly experimental).

**Evidence:** `dig +short schemas.agentskills.io` → no output; `curl -sL -m 8 -w "HTTP %{http_code}" https://agentskills.io/` → `HTTP 200`, run 2026-08-07.

### F4 — Consumption is a Microsoft Agent Framework client feature, experimental, with explicit trust guardrails [READ]

`AgentSkillsProviderBuilder.UseMcpSkills(client)` adds the server as a skill source, composable with local file skills. `AgentMcpSkillsSourceOptions` bounds archive abuse (defaults: 20 files, 1 MB download, 1 MB uncompressed, `.md/.json/.yaml/.yml/.csv/.xml/.txt` resource extensions). Scripts in archive skills are never executed; `load_skill`/`read_skill_resource`/`run_skill_script` require approval by default. Python's `MCPSkillsSource` supports only `skill-md`. Docs warn that an external server controls the instruction content reaching the agent (indirect prompt injection), so only vetted servers should be connected.

**Evidence:** learn.microsoft.com/en-us/agent-framework/agents/skills#mcp-based-skills (C# and Python zones, lines 1453-1568 of the fetched page); devblogs.microsoft.com/agent-framework/discover-agent-skills-from-mcp-servers-in-net/ (2026-07-28).

### F5 — AiRaccoon currently exposes no MCP resources; this would be a new, additive surface [READ]

The server registers 22 tools (`19 memory_*` + `3 watch_*`) and prompts only — `grep -rn "Resource" src/AiRaccoon --include="*.cs"` finds zero resource code. The E2E tool-surface test enumerates exactly the 22 tools; resources appear under `resources/list`, invisible to clients that do not ask, so the existing surface tests keep passing untouched (a resources test would be new).

**Evidence:** `src/AiRaccoon/Setup/McpServerSetup.cs:54-64` (`.WithTools<…>()` x7, `.WithPrompts<MemoryPrompts>()`); `tests/AiRaccoon.Tests/E2E/McpServerToolSurfaceE2ETests.cs:30-56` (ExpectedToolNames, 22 entries).

### F6 — A memory-skill content seed already exists: the `ai-raccoon-memory` Hermes skill [READ]

The skill teaches the exact behaviors a served memory skill should advertise — watch-on-docs ritual, search-first workflow (`memory_search` with 2-3 formulations), escalation by result, write discipline, scopes, and pitfalls (no `path` param, `context` sets `scope='custom'`, etc.). It is Hermes-host-specific in parts (plugin/hook instructions), so the served SKILL.md would be a trimmed, client-neutral derivative; keeping the two in sync is a drift decision to make explicitly.

**Evidence:** `/Users/arasz/.hermes/skills/AiRaccoon/ai-raccoon-memory/SKILL.md:1-114` (frontmatter + sections 1-8).

### F7 — Resources are transport-agnostic, so one resource class serves both stdio and HTTP [INFERRED]

The sample registers `WithResources<T>()` on the same `AddMcpServer()` builder AiRaccoon uses, and MCP treats `resources/list` + `resources/read` as core protocol capabilities independent of transport. Reasoning from: the sample's stdio wiring (`Program.cs:97-99`), AiRaccoon's existing builder usage (`McpServerSetup.cs:55-64`), and the protocol's transport independence. No per-transport branch is expected in `McpServerSetup`.

### F8 — The simplest v1 shape is static resources compiled into the server, decoupled from the memory bank [INFERRED]

Serving the index + one memory SKILL.md (+ optionally 1-2 reference pages) as static constants — exactly the sample's shape — costs a single resource class and no new dependencies. A dynamic variant (skills derived from bank contents or per-project conventions) has no caller today and would violate the "ask if a simpler shape would do" invariant. Reasoning from: the sample's static implementation, the index format being a fixed advertisement list, and the repo invariant against speculative abstraction.

## Still open

- **End-to-end proof:** no Microsoft Agent Framework client has consumed AiRaccoon resources yet. Settles by running the sample's client path (`Agent_Step06_McpBasedSkills`) against an AiRaccoon instance exposing the resources — needs a Foundry/Azure OpenAI endpoint.
- **Is the `$schema` link required?** F3 shows it currently dead; whether any client validates against it is unverified. Settles by testing without it.
- **Beyond Agent Framework:** the `skill://` convention is a Microsoft-side convention; Hermes/Claude clients would see plain MCP resources. Whether any non-Microsoft agent consumes SEP-2640 is unknown.
- **Content source of truth:** the served memory skill vs the Hermes `ai-raccoon-memory` skill — duplicate maintenance or a single source (same file, generated)? Not settled.
- **Static vs dynamic index:** F8 argues static-first; the moment someone wants per-project skill sets (skills in the memory bank itself), the index must become dynamic — the extension path is not yet designed.
