# Worked example — docs-init on AiRaccon (2026-08-02)

Concrete shapes from the session that produced this skill. Repo:
`~/RiderProjects/ai-raccon/AiRaccon` (ai-badger 0.76.0, stacks dotnet+mcp, agents
claude/copilot/hermes).

## The sequence the user directed

1. Commit pending working-tree state to main (gitignore hygiene, leftover cleanup, a
   parallel agent's spec dossier) — "first merge to main".
2. Then, ON MASTER: edit `.ai-badger/config.json` to add `include.skills`, run
   `python3 $AI_BADGER/features/common/skills/welcome-ai-badger/scripts/scaffold.py
   --config .ai-badger/config.json --target . --root $AI_BADGER`, verify, commit.
3. Docs work in the task worktree (`.ai-badger/worktrees/<taskId>`), merge, then the
   quality gate.

Doing the scaffold in the worktree was considered and rejected for the symlink-lifetime
reason in the SKILL.md — the 21 links under `~/.hermes/skills/AiRaccon/` must point at
the durable checkout.

## Symlink shapes (observed)

```
~/.hermes/skills/AiRaccon/scaffold-documentation ->
  ../../../RiderProjects/ai-raccon/AiRaccon/.ai-badger/skills/scaffold-documentation
.claude/skills/<name>       -> ../../.ai-badger/skills/<name>   (tracked symlinks)
.github/skills/<name>       -> ../../.ai-badger/skills/<name>   (tracked symlinks)
```
A broken symlink still answers `is_symlink()` true — relink repairs dangling links.

## Config change that enabled all 8 opt-in skills

```json
"include": { "skills": [
  "debug-issue", "evidence-first-research", "explore-codebase",
  "migrate-documentation", "refactor-safely", "review-changes",
  "scaffold-documentation", "update-documentation"
]}
```
Verified after scaffold: config.json schema-valid against
`$AI_BADGER/schemas/config.schema.json`, 8/8 skill dirs on disk, manifest updated.

## den-refresh on the fresh scaffold

Report was fully green: 0.76.0 == 0.76.0 == 0.76.0, all drift arrays empty,
`reScaffolded: false`. Notes:
- `skillUsage` had zero evidence channels (no Claude Code transcripts, no audit records) →
  nothing reported unused; `hint` suggested enabling call-behaviorist's audit log.
- 18 stale ai-badger versions in `~/.claude/plugins/cache/ai-badger/` — reported, not
  pruned (Claude Code owns that path).
- `.ai-badger.bckp/` (1.4 MB) appeared despite zero drift — removed as redundant.

## The review that caught the dotnet-run bug (folded into dotnet-mcp-server skill)

A code-reviewer subagent (13 tool calls, built + ran the server) found the README's
`MCP_TRANSPORT=http dotnet run` silently ran stdio: the default "stdio" launch profile
overrides the exported env var. It also found plain `dotnet run` prints
"Using launch settings from src/.../launchSettings.json..." to STDOUT, corrupting the
stdio JSON-RPC stream for MCP clients. Verified independently with a Python probe before
fixing. Fixes: `--launch-profile http` for the HTTP path, `--no-launch-profile` in client
`args`. The probe script lives at
`~/.hermes/skills/software-development/dotnet-mcp-server/scripts/mcp-stdio-probe.py`.

## Other durable observations

- `task_tracker.py start` on Hermes needs `--session-id "$HERMES_SESSION_ID"` or it exits 2.
- `delegate_task` result carries no token counts — `task_tracker.py subagent` got `0` with an
  honest description.
- The task skill's finish reports root `CLAUDE.md` over the 110-line budget — the file is
  ai-badger-generated ("Do not edit this copy by hand"), so the flag is a framework-template
  observation, not a compaction request.
