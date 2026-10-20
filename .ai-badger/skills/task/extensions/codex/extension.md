## Codex execution

Use Codex's native collaboration tools with the delivered `.codex/agents/*.toml` personas when available. Omitted model and reasoning settings inherit the parent; Claude lane aliases (`opus`, `sonnet`, `haiku`) in the framework's delegation map are not Codex model identifiers. Select only models exposed by the active harness.

Repository skills are discovered from `.agents/skills/`, linking the selected `.ai-badger/skills/` copies. Keep task progress and acceptance evidence in the framework's task graph. Native Codex transcript usage accounting, session lifecycle task hooks and delegated-tool policy gates are not ported; do not claim automatic token totals or hook enforcement on Codex. Report that accounting is unavailable when no verified usage source exists.

Project configuration and command hooks require operator trust. The scaffold supplies bounded prompt marker, Git config health and Bash Git storage hooks; patch, MCP, Stop, and delegation hooks require separate verified adapters.
