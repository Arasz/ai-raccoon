# Copilot CLI Capabilities (July 2026)

GitHub Copilot CLI (GA Feb 2026) has hooks, skills, and custom agents.

## Hooks

- **Config location**: `.github/hooks/*.json` (any JSON file in the directory)
- **Format**: `{ "version": 1, "hooks": { "<event>": [{ "type": "command", "bash": "...", "powershell": "...", "cwd": ".", "timeoutSec": 10, "env": {} }] } }`
- **Events**: `sessionStart`, `sessionEnd`, `preToolUse`, `postToolUse`, `userPromptSubmitted`, `agentStop`, `subagentStop`, `errorOccurred`
- **Cloud agent**: only `bash` honored (Linux sandbox); `powershell` entries ignored
- **Matcher**: optional regex to filter which tools trigger `preToolUse`/`postToolUse`
- **Tutorial path**: `.github/hooks/copilot-cli-policy.json`
- **Docs**: https://docs.github.com/en/copilot/concepts/agents/hooks

## Skills

- **Location**: `.github/skills/*/SKILL.md`, `.claude/skills/*/SKILL.md`, `.agents/skills/*/SKILL.md`
- **Format**: YAML frontmatter (`name`, `description`, `license`, `argument-hint`) + markdown body
- **Invocation**: `/skill-name` slash command or auto-loaded when relevant
- **Resources**: scripts, examples, and other files in the skill directory
- **Docs**: https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/add-skills

## Custom Agents

- **Location**: `.github/agents/*.agent.md`
- **Format**: YAML frontmatter (`name`, `description`, `tools`, `model`, `mcp-servers`, `user-invocable`, `disable-model-invocation`)
- **Capabilities**: own tool set, MCP servers, behavioral instructions
- **Invocation**: `/agent-name` in chat
- **Docs**: https://docs.github.com/en/copilot/reference/custom-agents-configuration

## Instructions

- **Main**: `.github/copilot-instructions.md` (always loaded)
- **Scoped**: `.github/instructions/*.instructions.md` with `applyTo` frontmatter for file patterns
- **Cross-agent**: also reads `AGENTS.md` and `CLAUDE.md`
- **Include syntax**: `@relative/path` to include another file
- **Docs**: https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/add-custom-instructions

## Plugins

- **Command**: `copilot plugin marketplace add <repo>`, `copilot plugin install <name>@<marketplace>`
- **Awesome Copilot**: `github/awesome-copilot` marketplace (pre-registered)
- **Delivers**: skills, hooks, custom agents, MCP servers

## ai-badger Support Status (v0.9.0)

| Capability | ai-badger support | Implementation |
|-----------|-------------------|----------------|
| Instructions | ✅ scaffolded | `.github/copilot-instructions.md` + scoped via `scaffolding.json` |
| Hooks | ✅ scaffolded | `copilot/adjustments/adjust_hooks.py` → `.github/hooks/ai-badger-hooks.json` |
| Skills | ✅ scaffolded | `copilot/adjustments/adjust_skills.py` → symlinks into `.github/skills/` |
| Custom agents | ✅ scaffolded | `copilot/adjustments/adjust_agents.py` → `.github/agents/*.agent.md` from personas |
| Plugins | ❌ not supported | Copilot plugins are a separate ecosystem |
| MCP servers | ❌ not supported | Opportunity for `adjust_mcp.py` |

## Copilot Hooks Format vs Claude Hooks Format

| Aspect | Claude Code | Copilot CLI |
|--------|------------|-------------|
| Config | `.claude/settings.json` hooks array | `.github/hooks/*.json` standalone files |
| Format | Nested event → matcher → hooks[] | `{ "version": 1, "hooks": { event: [...] } }` |
| Commands | `type: "command", command: "..."` | `type: "command", bash: "...", powershell: "..."` |
| Path vars | `${CLAUDE_PLUGIN_ROOT}` | None — use relative paths |
| Events | SessionStart, UserPromptSubmit, PreToolUse, PostToolUse, Notification | 8 events (see above) |

## Support.json Pattern

Track agent capabilities in `features/common/support.json` — a structured matrix
mapping each agent to its capabilities and what ai-badger scaffolds for it.
Useful for:
- Generating accurate feature-support articles
- Identifying gaps in ai-badger's agent coverage
- Planning which capabilities to scaffold next

Format: `{ "agents": { "<name>": { "capabilities": { "<cap>": { "supported": bool, "mechanism": str, "aiBadgerSupport": bool } } } } }`
