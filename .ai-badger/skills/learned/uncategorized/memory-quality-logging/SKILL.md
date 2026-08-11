---
name: memory-quality-logging
description: While dogfooding, grade memory_search results to JSONL.
version: 1.0.0
author: ai-badger
license: MIT
platforms: [linux, macos, windows]
metadata:
  hermes:
    tags: [memory, ai-raccoon, dogfooding, jsonl, grading, telemetry]
    related_skills: [ai-raccoon-integration, hermes-session-telemetry, ingest-docs-to-memory]
---

# Memory-quality logging (dogfooding a memory store)

Log every `memory_search` result with a usefulness grade so retrieval quality is
correlatable per project/workspace across sessions. User-established workflow
(2026-08-05, ai-badger dogfooding session): the quality log is a first-class
deliverable while dogfooding, not an afterthought.

## The JSONL line shape (match it exactly)

One JSON object per line, one line per `memory_search`:

```json
{"ts": "2026-08-05T19:47:00Z", "query": "...", "scope": "all", "projectId": "ai-badger",
 "workspaceId": null, "result": {"results": [{"hash": "...", "ranking": 0.98,
 "sourceFile": "/abs/path.md", "snippet": "..."}]}, "usefulness": 4, "note": "optional"}
```

- `ts` — ISO timestamp (microseconds in the automated hook).
- `workspaceId` — ALWAYS present: the workspace id when one is active, else `null`.
  User requirement: every result must be correlatable by project id AND workspace id.
- `host` / `sessionId` (hook lines since 0.80.0) — the transport
  (hermes/claude/copilot) and the session; both `null` in manual lines. They make
  "no usage" vs "no capture" answerable from the log itself per host.
- `result` — the full tool result; if it is not valid JSON, store `{"raw": <payload>}`
  rather than dropping it.
- `usefulness` — agent's grade 1-5 (5 = best). Unanswered asks stay `null` — honest
  data, never fabricate a grade.
- Session-scoped manual logs go in the repo (`docs/work/<date>-ai-raccoon-memory-quality.jsonl`);
  the automated hook logs machine-wide to `~/.ai-badger/memory-grade/memory-quality.jsonl`
  so it never pollutes a repo's `git status`. Keep the two shapes compatible (hook line
  is a superset: adds `workspaceId` since 0.79.0, `host` + `sessionId` since 0.80.0).

## The grade round-trip (hook design, decided 2026-08-05)

- The hook appends the FULL line at `post_tool_call` time (`usefulness: null`) — the
  result payload is only reliably available to the hook; never relay it through the
  prompt (that is the prompt-bloat risk).
- A one-line grade ask is stashed and injected into the NEXT LLM turn (stash/pop,
  commit-reminder precedent in `features/common/hooks/ai_badger_hooks.py`), or returned
  as `additionalContext` from Claude's PostToolUse hook.
- The agent fills the grade in place via a helper, so the line keeps query + full
  result + ids + grade together:
  `python3 <path>/memory_grade.py grade <ts> <1-5> [note]`
- Rejected alternatives: agent writes the whole line via the helper (relays the big
  payload through the prompt); grading via `memory_write` into the bank (grades are
  telemetry, not memory content — would corrupt retrieval).

## Config surface (default OFF, machine-wide override)

`AI_BADGER_MEMORY_GRADE=1` enables the hook; absent/unset/0/garbage → fully inert (no
reads, no writes, no injection). Env var chosen over a bank settings row: bank rows
silently read OFF on SQLCipher-encrypted banks and couple the framework to AiRaccoon's
schema; env vars are the repo's existing hook-knob shape and inherit into every agent
host. Machine-wide enable:

```sh
echo 'export AI_BADGER_MEMORY_GRADE=1' >> ~/.zshrc
launchctl setenv AI_BADGER_MEMORY_GRADE 1   # GUI-launched hosts
```

Per-project opt-out later = `.ai-badger/config.json` check applied only when the env is on.

## Live-verification recipe (prove the pipeline, don't assume it)

1. Watch setup (one-time per install): `ai-raccoon watch scope add <project> <abs-path>`
   then `ai-raccoon watch enable <project> true`; register with `memory_watch_add`,
   poll `memory_watch_status` until `Healthy`.
2. Embed check: `memory_stats` → `pending` must reach 0; run `memory_embed_pending`
   (`processed: N, pending: 0` is the healthy result).
3. Search + grade: run `memory_search`, append the JSONL line, grade it 1-5, confirm
   the line now carries query + result + projectId + workspaceId + usefulness.

## Coverage audit — "what % of searches are graded?" has three answers

Run `scripts/audit_coverage.py` (no args; optional paths). It joins the grade log against
`~/.ai-raccoon/memory-operations.jsonl` and prints three denominators:

- **graded/logged** — the naive figure quoted off the log alone (measured 2026-08-11:
  21/172 = 12.2 %). This is usually what "12 % graded" means.
- **logged/searched** — hook capture rate (168/438 = 38 %; per-day 40/24/0/22/41/71 %;
  08-08 captured zero of 26 searches). A coverage problem that is really a capture problem
  cannot be fixed by grading more — check hook/plugin enablement per host first (pitfall
  above).
- **graded/searched** — true coverage (20/438 = 4.6 %).

Also check `pending.json` (stash size: 2 vs 151 ungraded on 08-11 — the stash is NOT the
bottleneck) and the null-projectId share (56 % on 08-11 — per-project correlation is only
possible on the rest; all null lines carry sessionId, so backfill is possible in principle).
Voluntary grades are selection-biased upward (avg 4.29, 19/21 grades >= 4, both sub-4 grades
from structured diagnostics): treat "perceived quality" as an upper bound, and never
validate an auto-grader against it alone.

## Pitfalls (verified 2026-08-05)

- **Empty log ≠ no usage — check host coverage first (diagnosed 2026-08-06, FIXED 0.80.0).**
  The hook only fires on hosts that execute it. Claude Code does (project `hooks.json`
  PostToolUse matcher `memory_search`). The Hermes side was dead from 0.79.0 until
  0.80.0: ai-badger dropped flat `.py` files into `~/.hermes/plugins/` and Hermes only
  loads DIRECTORY plugins (`plugin.yaml` + `register(ctx)`, opt-in via
  `plugins.enabled`), so no Hermes session ever logged. Since 0.80.0 the fix is:
  plugin dir `~/.hermes/plugins/ai-badger/` shipped by the scaffold, then
  `hermes plugins enable ai-badger` + a host restart; the helper now lives at
  `~/.hermes/plugins/ai-badger/memory_grade.py`. Before trusting an empty log: check
  `hermes plugins list` shows ai-badger enabled, `grep "hooks registered"
  ~/.hermes/logs/agent.log`, and run one organic search. Also, naive
  `grep memory_search ~/.claude/projects/` matches embedded skill/CLAUDE.md context
  text — real invocations must be filtered on `"type":"tool_use"`. A log holding only
  probe lines is an instrumentation-coverage gap, NOT a usefulness verdict; fall back
  to session-scoped manual logs (`docs/work/<date>-ai-raccoon-memory-quality.jsonl`)
  when the dominant host can't be wired. Full diagnostic recipe:
  `references/hook-coverage-diagnostics.md`. Status since 0.80.0/0.81.0 (measured
  2026-08-06): the Hermes side captures (host=hermes lines present) and the Claude
  question is resolved — jsaa had no Claude sessions since its `.mcp.json` was
  written, and ai-badger Claude sessions load ai-raccoon tools only as
  `deferred_tools_delta` (lazy loading) and never invoke them. Also: `~/.claude/projects`
  dir names start with a dash — `grep` needs a `--` separator or `./` prefix.
- **Stale embedding settings break search silently.** A bank whose settings carry
  `embedding.model=<name>` + a leftover `baseUrl` while the engine is `local:bundled`
  makes `memory_search` fail with "Load model from <cwd>/<name> failed" (the non-empty
  model name resolves cwd-relative). Fix: `ai-raccoon model set local` — a null model
  DELETES the stale rows (the CLI contract), engine stays `local:bundled`. Verify with
  `ai-raccoon model show` → `model: (unset)`.
- **Bundled ONNX must sit next to the tool.** The resolver walks UP from
  `AppContext.BaseDirectory` checking `Models/model_qint8_arm64.onnx` at each ancestor.
  The installed global tool's store ships `vocab.txt` but NOT the .onnx (gitignored
  pack glob) — copy the SHA-verified model from a dev checkout into the store's
  `Models/` dir (first-hit path). No restart needed when resolution is per-call.
- Tool-name matching must tolerate the family: `mcp__ai_raccoon__memory_search`,
  `mcp__ai-raccoon__memory_search`, `ai-raccoon:memory_search`, bare `memory_search`.
  Only `memory_search` acts; other memory tools are out of scope until a buyer exists.
- `memory_write` has no `path` param; NEVER pass `context` (silently sets
  scope='custom', invisible to project-scoped search); `memory_embed_pending` omit
  `limit` to process all.

## Files

- `scripts/audit_coverage.py` — three-denominator coverage audit (graded/logged vs
  logged/searched vs graded/searched), joined against `memory-operations.jsonl`.
- The full memory-grade-hook implementation plan lives in the ai-badger repo at
  `docs/plans/memory-grade-hook.md` (7 TDD work packages, WP1-WP7, separate PR after
  the ai-raccoon integration #302 merges).
