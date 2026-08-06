# MCP Stack Declarations — Design Pattern

When a stack feature needs MCP servers (e.g. `python` needs `pyright`, `github`
needs the GitHub MCP server), declare them in `features/{stack}/mcp-servers.json`.
This is distinct from `externalTools` in config.json (user-declared, ad-hoc tools).

## `mcp-servers.json` schema

Located at `features/{stack}/mcp-servers.json` or `features/common/mcp-servers.json`.
Schema at `schemas/mcp-servers.schema.json` (Draft 2020-12, `additionalProperties: false`).

```json
{
  "$schema": "../../../schemas/mcp-servers.schema.json",
  "servers": [
    {
      "name": "pyright",
      "command": "uvx mcp-server-pyright",
      "description": "Python type checking and language intelligence",
      "env": {},
      "scope": "project",
      "agentOverrides": {
        "hermes": { "command": "uvx", "args": ["mcp-server-pyright"] }
      }
    }
  ]
}
```

Required fields: `name`, `command`. Optional: `description`, `env`, `scope`
(default "project"), `targetAgents` (array of agent names; omit = all agents),
`agentOverrides` (per-agent: claude, hermes, copilot, junie with `command`/`args`).

## Merge semantics (stack-declared vs user-declared)

Priority order (highest wins on name collision):
1. `externalTools` in config.json — user intent (highest)
2. Stack `mcp-servers.json` — framework recommendation
3. Common `mcp-servers.json` — baseline

Cross-stack dedup: if two stacks declare the same server name, the stack listed
later in `config.json.stacks` wins. Within a single `mcp-servers.json`, names
must be unique (enforced by schema documentation, not schema itself).

## Scope-aware agent config targets

The scaffold splits servers by `scope` field before writing:

| Agent  | `scope: project` target              | `scope: user` target              |
|--------|---------------------------------------|------------------------------------|
| Claude | `.mcp.json`                           | `~/.claude/settings.json` mcpServers |
| Hermes | `.mcp.json` (reads from project root) | `~/.hermes/config.yaml` mcp.servers  |
| Copilot| `.github/copilot/mcp-config.json`     | N/A (project-only)                 |
| Junie  | `.mcp.json` (JetBrains 2024.2+)      | N/A (project-only)                 |

**`scope: user` writes directly** — the scaffold modifies `~/.hermes/config.yaml`
and `~/.claude/settings.json` for user-scoped servers. This is the primary use
case for Hermes MCP (universal, not per-project). Test with
`unittest.mock.patch("pathlib.Path.home")` to isolate from real user config.

**Hermes config.yaml editing**: Use `yaml.safe_load` to read, deep-merge into
`mcp.servers`, `yaml.safe_dump` back. V1 loses comments — V2 can add
string-based comment-preserving editing. Create the file if missing.

**Claude settings.json editing**: JSON merge-only — read existing, add new
`mcpServers` entries, write back. Create `~/.claude/` if missing.

**`targetAgents` field** (optional): If a server declares
`targetAgents: ["hermes"]`, it only scaffolds for those agents. Omit = all.

Copilot V2: per-agent MCP via `.github/agents/*.agent.md` frontmatter
`mcp-servers` field. Not in V1.

Junie V2: may use `.idea/mcp.json` if JetBrains confirms it as canonical.
V1 uses shared `.mcp.json`.

**Pitfall — command parsing heuristic**: The `.mcp.json` format uses `command`
(executable) + `args` (list), but `scaffold.py` does NOT blindly split every
command string. Tests revealed that simple 2-word commands like `"echo v2"` or
`"echo user"` must stay intact, while package-runner commands like
`"uvx mcp-server-pyright"` must be split. The heuristic that resolves this:

```python
parts = command.split()
has_pkg_args = (
    len(parts) >= 2
    and any("-" in p or "@" in p or "/" in p for p in parts[1:])
)
```

- `"uvx mcp-server-pyright"` → hyphen in arg → split → `command: "uvx"`, `args: ["mcp-server-pyright"]`
- `"echo v2"` → no package chars → kept as-is → `command: "echo v2"` (no args field)
- `"npx -y @scope/pkg"` → hyphen + @ → split → `command: "npx"`, `args: ["-y", "@scope/pkg"]`

Rationale: package-runner commands (uvx, npx) always have args with hyphens or
scoped names (@org/pkg). Simple commands should stay intact. When
`agentOverrides` provides explicit `args`, the heuristic is bypassed entirely.

**`args` field presence**: only included when non-empty or explicitly provided by
`agentOverrides`. Absent `args` means the MCP client treats `command` as the
full command line.

## Scaffold integration points

- `_collect_stack_mcp_servers()` — reads `mcp-servers.json` from active stacks + common (last-writer-wins on cross-stack name conflict)
- `_merge_mcp_servers(stack_servers, user_tools)` — merges stack + externalTools (user wins on conflict)
- `_split_servers_by_scope(servers)` — returns `(project_servers, user_servers)` tuple
- `_parse_command(command)` — splits command string into `(executable, args_list)` tuple
- `_resolve_server_for_agent(server, agent_name)` — applies `agentOverrides` for target agent
- `_generate_mcp_json()` — extended to use merged project-scoped servers (not just externalTools)
- `_scaffold_hermes_mcp_user(user_servers)` — writes `scope:user` servers to `~/.hermes/config.yaml`
- `_scaffold_claude_mcp_user(user_servers)` — writes `scope:user` servers to `~/.claude/settings.json`
- `_generate_copilot_mcp_config(servers)` — writes `.github/copilot/mcp-config.json`
- `refresh.py` — no changes needed (re-scaffold picks up new MCP files automatically)

**`run()` call order**: `_generate_mcp_json()` → `_scaffold_hermes_mcp_user()` → `_scaffold_claude_mcp_user()` → `_generate_copilot_mcp_config()`

**Two-layer merge**: (1) In-memory merge of stack + externalTools (user wins). (2) File-level merge with existing `.mcp.json` on disk (existing entries preserved, never overwritten). Current `_generate_mcp_json()` uses `.update()` which overwrites — the refined version must check `if name not in existing["mcpServers"]` before inserting.

## Detection and den-refresh

- Drift detection: new `mcp-servers.json` files appear as `newItems` in drift
- New stacks: `detect_new_stacks()` → re-scaffold reads the new stack's `mcp-servers.json`
- Config changes: adding a stack to `config.json` → next scaffold reads its MCP declarations

## Key decisions

- **No `instructions` field** — stack-declared servers don't inject custom instructions.
  If a server needs instructions, use `externalTools` instead.
- **Transport-agnostic** — declare `command` + `args`; scaffold maps to each agent's format.
- **Merge-only for `.mcp.json`** — never overwrites existing entries (same as externalTools).
- **`support.json` update** — set `aiBadgerSupport: true` for all agents' `mcpServers`.

## Implementation plan

Detailed TDD implementation plan with 48 test cases, phased execution order,
and specific method signatures: `docs/design/mcp-stack-declarations-impl-plan.md`
