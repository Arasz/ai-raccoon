# Memory-grade hook: host-coverage diagnostics (verified 2026-08-06)

Question this answers: "the quality log is empty or holds only probe lines — is
nobody searching, or is the hook not firing?"

## Where the pieces live (check the project copies FIRST)

- `<project>/.ai-badger/hooks/{memory_grade.py, ai_badger_hooks.py, hooks.json}` —
  project-scaffolded copies, identical to `~/.hermes/plugins/{memory_grade.py,
  ai_badger_hooks.py}`. `hooks.json` is the Claude Code registration: PostToolUse
  matcher `memory_search` → `.ai-badger/skills/ai-raccoon-memory/scripts/
  memory_grade_hook.py` (script present in every scaffolded project).
- Log: `~/.ai-badger/memory-grade/memory-quality.jsonl`; unanswered asks:
  `~/.ai-badger/memory-grade/pending.json`.
- Enable switch: env `AI_BADGER_MEMORY_GRADE=1` (exact string "1", anything else
  is inert).

## Diagnostic order (each step returns a decisive signal)

1. State: `python3 ~/.hermes/plugins/memory_grade.py probe` → env state, log path,
   last 3 lines. (or the project copy: `.ai-badger/hooks/memory_grade.py probe`)
2. Env reach: `launchctl getenv AI_BADGER_MEMORY_GRADE` (GUI-launched hosts) and
   `grep -c AI_BADGER_MEMORY_GRADE ~/.zshrc` (interactive shells). Observed: all
   three were "1" — env was NOT the problem.
3. Hermes-side execution proof: `ls ~/.hermes/plugins/__pycache__/ | grep -i ai_badger`
   — empty means Hermes never imported the plugin (no .pyc). Cross-check:
   `grep -rh ai_badger_hooks ~/.hermes/logs/` — observed only pyright-lint and
   hardline-block mentions, never execution lines. Hermes has no mechanism that
   auto-loads arbitrary `.py` from `~/.hermes/plugins/`; its plugin surface is the
   builtin `plugins:` config (disabled-list) + `~/.hermes/desktop-plugins/`. The
   ai-badger hooks-manifest declares the Hermes intent ("plugin entry
   ai_badger_hooks.py, method post_tool_call") but nothing invokes it.
4. Claude-side usage proof: real invocations live in `~/.claude/projects/**/*.jsonl`
   as tool_use entries. Observed: `grep -rlo memory_search ~/.claude/projects/`
   matched 63 files but ZERO `"type":"tool_use"` entries — all hits were embedded
   skill/CLAUDE.md context text. Distinguish with:
   `grep -o '"type":"tool_use"[^}]*memory_search[^}]*' <file>`.
   **Gotcha: project dir names start with a dash** (`-Users-arasz-...`), so grep
   parses the glob as an option list and dies with "grep: : No such file" — use
   `grep -- pattern ./-Users-.../*.jsonl` or `./`-prefix the dir. Also count ALL
   ai-raccoon tools, not just memory_search: zero
   `"name":"mcp__ai-raccoon__*"` tool_use across every project log (measured
   2026-08-06: 21,682 Bash / 2,805 Edit / 1,072 Agent calls, zero memory tools) is
   the decisive "Claude never invokes" signal.
5. Live blind-spot proof: run one organic `memory_search` via the MCP tool, then
   `wc -l` the log. Unchanged ⇒ the host you are in does not run the hook (observed:
   Hermes search, log stayed at 1 line).

## Resolved 2026-08-06 (post-0.81.0): the per-host answers, measured

- Hermes capture WORKS since 0.80.0 (directory plugin at
  `~/.hermes/plugins/ai-badger/` + `hermes plugins enable ai-badger`): the log
  carries 10 `host=hermes` lines. Sparse usage is usage, not capture failure.
- Claude zero-lines explanation (two distinct causes, both measured):
  1. jsaa: NO Claude session since 2026-08-03 03:32, but its `.mcp.json` (with
     ai-raccoon) was written 2026-08-05 23:13 — no Claude session has EVER had the
     server declared there. Check `.mcp.json` mtime vs newest session-file mtime
     before concluding anything about a host.
  2. ai-badger: sessions exist and the tools ARE loaded — as
     `deferred_tools_delta` attachments (Claude Code lazy tool loading: names
     appear, but tools are NOT in the active toolset until the model discovers
     them) — yet zero ai-raccoon invocations across all logs. Availability !=
     invocation.
- So the 2026-08-06 log verdict: 10 hermes + 1 manual + 0 claude; 7 tool-test
  probes; 4 organic searches (all in ai-raccoon-repo Hermes sessions — jsaa and
  ai-badger sessions never search); 2 graded (4, 5). Quality is not the problem;
  trigger/habit is. Adoption levers that follow: one-call session-start memory
  brief, a memory step in the task skill's Phase 0, regenerate the project's
  mcp-tools.json when it lacks ai-raccoon entries (ai-badger's own repo index had
  zero — the per-turn recommendation hook can then never suggest memory tools),
  and seed the (empty) shared tier via curated promotion only.

## Interpretation

- A log holding only the WP7 probe line means NO organic capture — not no usage
  (session history showed real memory_search calls in Hermes sessions).
- Consequence (current, post-0.81.0): the machine-wide log measures Hermes usage
  (sparse but real) and zero Claude usage. "No usage" vs "no capture" is answerable
  per host from the log itself (host field) plus the step-4 forensics — never
  assume a missing host means the hook is broken.
- Fallback when the dominant host cannot be wired: session-scoped manual logs
  (`docs/work/<date>-ai-raccoon-memory-quality.jsonl`, same line shape, `usefulness`
  filled by the agent) — already the established dogfooding shape.
- Fixing Hermes-side capture for real = a framework task against a supported Hermes
  extension point (real plugin registration or a cron sampler), not a file drop into
  `~/.hermes/plugins/`.
