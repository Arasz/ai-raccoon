---
name: ai-raccoon-memory
description: >-
  Use when a project needs a memory server — search project and shared memory first, write
  durable facts with source paths, watch a docs directory, or promote facts across projects.
version: 0.2.0
author: ai-badger
license: MIT
platforms: [linux, macos, windows]
scope: default
metadata:
  hermes:
    tags: [memory, retrieval, semantic-search, persistence]
    related_skills: [mcp-index, hermes-mcp-setup]
---

# AiRaccoon Memory

## When NOT to Use

- A one-off lookup ("have we seen X before?") — run `memory_search` and be done, no watch ritual, no write-back
- No docs directory to watch and no durable fact to write — the ritual adds ceremony, not value
- The memory-grade hook when you only need one answer — it is opt-in by env var; don't enable it for a single search

## 1. Watch-on-docs ritual (do this first)

On session start, run `memory_watch_status(projectId)` for this project. If the docs directory
is not in the watched list, run `memory_watch_add(projectId, <absolute path to docs>)` to mirror
it into memory. The watch starts `scanning` and settles to `healthy`; an already-watched path is
a no-op.

Also watch `.semantica/` (one-time per project): `mkdir -p .semantica`, then
`memory_watch_add(projectId, <absolute path to .semantica>)`. Re-adding is a no-op; the durable
record lives in memory, so gitignore `.semantica/` in the consumer repo.

**CLI prerequisite (only when the watch errors):** `watching-disabled` or `path-outside-scope`
means the one-time per-install setup is missing (quote the `*` so the shell does not expand it):
`ai-raccoon watch scope add '<project-id|*>' <path>`, then
`ai-raccoon watch enable '<project-id|*>' true`. If the `memory_watch_*` tools are not listed at
all (older tool build on another machine), update the tool: `dotnet tool update -g arasz.ai-raccoon`.

## 2. Search-first workflow

Always pass `projectId` and `sessionId`. Pass your session id on every search. The server rejects a blank one and stores the value verbatim on the search row. Before web search, code search, or asking the user, run `memory_search(projectId, sessionId, scope=all)` with 2-3 formulations. Try the exact phrase first, then keywords, then a plain restatement. Entries carry source paths. Cite them as evidence. Every reply carries `meta.correlationId`. Keep that id. You need it to grade the search or to record that you opened one of its files.

## 3. Escalation by result

- Decisive hit → use it; cite the source path.
- Partial hit → one targeted external search, then reconcile.
- No hit → search externally, then write the finding back with `memory_write` (source path included).

## 4. Write discipline

Durable facts only, one per entry, source included. Plain writes land in committed project
memory (`project:<id>`). For in-progress notes use workspace isolation:
`memory_workspace_begin` → `memory_workspace_status` → `memory_workspace_consolidate(keep=[...])`
(or `["all"]` to promote everything; `memory_workspace_discard` to drop). Promote durable
cross-project facts with `memory_share` — never automatically. `memory_sweep` removes old
low-rated entries; shared entries are exempt.

## 5. Scopes and kinds

`scope=all` (default: shared + project), `scope=project`, `scope=shared` (the promotion tier only). Kind defaults to `both`. It runs the memory and code hybrids separately and returns both sections. `kind=memory` searches memory only. `kind=code` searches code only. Recording follows the request. Memory and both store the memory leg count and files. Code stores the code count with an empty file list. Code paths never enter the table.

## 6. Gotchas

- `memory_write` has **no `path` param** — the entry path is derived from its content.
- **Never pass `context`** unless workspace isolation is intended: it silently sets
  `scope='custom'`, invisible to project-scoped search.
- `memory_embed_pending`: omit `limit` to process all pending entries.
- `memory_delete_context` requires full access mode.
- When you open a file the search returned, call `memory_record_followthrough` with the same `correlationId`, the file path, and `servedRank` when you saw one. Rank is 1-based. Under `kind=both` a bare rank cannot name its section. That ambiguity is intentional, so send no section qualifier. Grade with `memory_record_grade` (1-5) on the same id when you have a judgment.
- A `memory_search` result (`kind=code` or `kind=both`) carrying a warning starting
  `code engine not configured` means its code section is FTS5 keyword matches only — semantic
  code hits are missing, not absent; don't read it as a complete answer. Tell the user, once
  per session, verbatim: run `ai-raccoon model set code default` (downloads and activates the
  default code embedding model, ~187 MB, one-off) — don't repeat the hint on later searches in
  the same session. Don't re-run the search hoping for vector results: nothing changes until
  that command runs.

## 7. Bulk ops

`memory_ingest_file` / `memory_ingest_directory` bulk-load files; `memory_stats` reports bank
size; `memory_sync` exchanges snapshots with cloud storage when configured.

## 8. Per-prompt memory context (automatic)

This skill ships a hook that searches memory for you before the model sees each prompt. It
runs on Claude Code and Copilot CLI (`UserPromptSubmit` / `userPromptSubmitted`, entry
`scripts/memory_context_hook.py`) and in Hermes CLI sessions (`pre_llm_call`). It never runs
on pi. It is POSIX only: on Windows it stays silent and spawns nothing.

**When it fires.** Every prompt that passes pi's `shouldEnrich` gate (long enough, enough
distinct words, not a slash command or a control word like `continue`), in a project where
`.ai-badger/project-id` resolves and the `ai-raccoon` executable is found (`PATH` first, then
`~/.dotnet/tools/ai-raccoon`). Each run spawns the bare `ai-raccoon` binary as a proxy, once,
and sends every search over that one session. The hook never reads the ai-raccoon token; the
proxy does the identity proof. If no serve is running, the proxy may start one, and that serve
outlives the hook under its own idle watchdog. When hits survive pruning, the hook injects a
"Memory context" block identical to pi's `toMemoryContext` output. Otherwise it injects
nothing, and the hook always exits 0. An expected failure (no proxy, a timeout, an error
reply) is silent. A missing or broken `memory_context.py`, or a defect in the hook's own
code, leaves one line in `~/.ai-badger/hook-errors.log` naming the exception type and where it
was raised, never the prompt; under Hermes the same line goes to Hermes's log as a warning.

**Two modes.** Both inject the same block.

- *Pipeline*: runs when `OPENROUTER_API_KEY` is set and `AI_BADGER_MEMORY_CONTEXT_PIPELINE` is
  not `"0"`. An OpenRouter model plans 2 to 6 retrieval queries, each is searched once, the
  Jev decisions endpoint scores the pooled hits, and pi's document-aware merge keeps the best
  five. The run's total is 90 s on Claude and Copilot, split into pi's stage limits
  (planner 15 s, each search 15 s, Jev 8 s). Under Hermes the total is 25 s and the stage
  limits shrink with it, so a planner that uses its whole share still leaves time for two
  full searches and the Jev stage. If the planner fails, the run falls back to one search on
  the prompt.
- *Single search*: every other case. One search on the prompt, capped at 5 s. Nothing leaves
  the machine except the call to the local proxy.

**What leaves the machine.** Only in pipeline mode, and only to OpenRouter: the gated prompt
(the planner's input, and its first 32 000 characters in each Jev request), and for up to 48
pooled hits their file path, kind and a 500-character excerpt of memory *and source code*.
The key is read from the environment only and is never logged or written anywhere. Set
`AI_BADGER_MEMORY_CONTEXT_PIPELINE=0` to keep everything but the ai-raccoon search local.

**Switches.** Only the literal `"0"` counts for either switch.

| Variable | Effect |
|---|---|
| `AI_BADGER_MEMORY_CONTEXT=0` | Hook off: no spawn, no HTTP, no block |
| `AI_BADGER_MEMORY_CONTEXT_PIPELINE=0` | Single search even with a key; no HTTP |
| `AI_BADGER_MEMORY_CONTEXT_PLANNER_MODEL` | Planner model override (an OpenRouter model id) |
| `OPENROUTER_API_KEY` | Turns the pipeline on |
| `AI_BADGER_PROJECT_ID` | Overrides `.ai-badger/project-id`; exported globally, it routes every repo to one project |

Without the override, the planner uses the `medium` tier's preferred model through the `task`
skill's `model_groups.py` and the project's `model-groups.json`. A project that declined the
`task` skill has no resolver, so the planner has no model and every run falls back to a single
search unless the override is set. The planner model comes from project data and is billed to
your key; the override pins it.

**Search log.** Each enriched prompt, and each planned query, is a real `memory_search` and
lands in ai-raccoon's search log like any other search.

**Per agent.**

- *Claude Code*: prints the `hookSpecificOutput` envelope. The hook's `timeout` is 100 s,
  because `UserPromptSubmit` otherwise defaults to 30 s.
- *Copilot CLI*: prints flat `{"additionalContext": ...}`, the only shape Copilot CLI 1.0.88
  consumed when measured (the envelope was ignored). `timeoutSec` is 100. Copilot loads repo
  hooks only for a folder listed in `trustedFolders` in `~/.copilot/config.json`; in an
  untrusted folder the hook never runs, which looks exactly like an empty result.
- *Hermes*: CLI sessions only. The arm runs when `(platform or "cli") == "cli"`, Hermes's own
  normalisation, so a gateway session (Telegram, Discord and the rest) never triggers a search
  or OpenRouter call. It also needs `.ai-badger/skills/ai-raccoon-memory/` in the project. The
  block goes last in the injected context, followed by `(end of memory context)`. Hermes calls
  `pre_llm_call` once per user turn, before its tool loop, so a tool loop searches once.
  Hermes abandons a `pre_llm_call` callback after 30 s by default and then drops that turn's
  whole injection, including a message-bus delivery the callback already consumed. The 25 s
  limits exist to keep a slow run inside that window; a turn that overruns it anyway loses
  everything the callback would have added.

Declining this skill through `config.exclude` removes the Claude wiring and turns the Hermes
arm off. The Copilot hooks file still names the command today; with the skill's scripts absent,
its existence guard answers each prompt with a "hook skipped" system message instead.

**Parity with pi.** The block is byte-identical to pi's for any hit whose path and string rank
hold no line-break or tab characters, whose snippets hold no U+0085, whose rank is a number,
string, boolean or null, and whose text is not cut inside a non-BMP character. Outside that,
this port collapses line breaks in paths and ranks to one space (pi keeps them raw), prints `?`
for a list or object rank, counts truncation in code points, and cuts a path at 300 and a rank
at 32 characters. The "Parity with pi" section of ADR-0031 lists every divergence.

## 9. Verification Checklist

- [ ] `memory_watch_status` shows the docs dir `healthy`
- [ ] `memory_search(projectId, sessionId, scope=all)` returns docs-derived hits
- [ ] A durable finding was written back with `memory_write`, source path included
