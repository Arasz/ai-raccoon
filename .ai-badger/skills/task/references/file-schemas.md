# Tracking store schemas

Runtime tracking state lives in **SQLite**, behind one store module: `engine/badger_store.py`,
vendored verbatim into every consuming skill's `scripts/` directory (ADR-0009's duplication
discipline). The decision record is `docs/adr/0024-sqlite-runtime-store.md` — two databases,
lazy migration, dual-read, retention; this document is the schema companion, not the decision.

## The databases

| Database | Path | Tables |
|---|---|---|
| project | `.ai-badger/task-tracking/tracking.db` (gitignored via the scaffold's managed block) | `tasks`, `token_usage`, `sessions`, `statusline` (KV), `marker_state` (KV), `meta` |
| user | `~/.ai-badger/ai-badger.db` | `awm_state` (KV), `awm_decisions` (log), `commit_reminder` (KV), `pending_feedback`, `searches` (log), `memory_first`, `semantica_nudge`, `dispatch_lanes`, `dirty_sweeps`, `blast_radius_denials`, `meta` |
| audit | `~/.ai-badger/debug/audit.db` — moved whole by `AI_BADGER_DEBUG_DIR` when that is set | `hook_audit` (log), `hook_state` (KV), `meta` |

Every table keeps normal columns for what is filtered or sorted and JSON columns for
whole-document payloads. All store timestamps are `+00:00` ISO-8601. Files are `0600` and
directories `0700`, re-asserted on every open and write, including the `*.db-wal`/`*.db-shm`
sidecars. Each DB opens with `journal_mode=WAL`, `busy_timeout=5000` (worst case: a contended
per-prompt hook blocks up to five seconds), `synchronous=NORMAL`, and a `meta(schema_version)`
stamp — a store meeting a *newer* schema than it knows fails closed with an actionable error
naming the upgrade path (run den-refresh), never writing an old shape silently.

## Access surface

Nothing outside the store module reads or writes these tables directly. Consumers open the
store (`open_tracking()` / `open_user()`) and go through its accessors (`tasks_all`,
`task_upsert`, `usage_all`, `usage_upsert`, `sessions_map`, `session_upsert`, `kv_get`,
`kv_set`, `kv_all`, `log_append`, …). The user-facing surface is unchanged: `task_tracker.py`,
`awm.py`, and `behaviorist.py` verbs and exit codes are identical to the pre-SQLite CLIs.
Store failures never block a hook — they fail open, logged, per the project's hook convention.

## Lazy migration

Legacy JSON/JSONL files import into the tables on **first write** after an upgrade, then rename
to `*.migrated.*` beside where they lived. The rename happens only after the import transaction
COMMITs: a crash mid-migration leaves both artifacts, the next writer's empty-table re-check
re-imports (per-family natural keys, `INSERT OR IGNORE`, resumable), and a concurrent
first-write migration yields exactly one import and one rename. Recovery is part of the
contract: delete the DB, restore the `*.migrated.*` names, and the lazy import redoes it.

While deployed surfaces update only on den-refresh, old and new code hit the same stores —
that is the steady state, not a window between releases. Readers therefore **dual-read**: DB
rows merged with a still-present legacy file per key, last write wins for map-like stores. A
legacy file whose mtime postdates the recorded migration stamp triggers re-import (append-only
families) or fails closed with an upgrade pointer (map families); silent divergence is never an
option.

## Retention

The three log tables — `hook_audit`, `awm_decisions`, `searches` — delete rows older than
**60 days** (throttled on-open prune inside one `BEGIN IMMEDIATE`; read-only opens degrade to a
logged no-op). This replaces the retired caps (awm's 5000-line `decisions.jsonl` trim, the
audit log's 5000-record trim) and ends the unbounded growth those files measured.

## Table shapes

### tasks (project DB)

One row per tracked task; `task_id` (e.g. `T08`) is the natural key, `session_id` is NOT NULL
and carries a partial unique index (`WHERE state <> 'FINISHED'`) as defense in depth behind
the application-level checks.

| Column | Carries |
|---|---|
| `id` | autoincrement row id |
| `task_id` | the tracked task's id (`T08`) |
| `session_id` | the attached agent session — one active task per session |
| `title`, `cwd`, `branch` | task title, project root, work branch |
| `transcript_path` | the Claude Code CLI's own session transcript under `~/.claude/projects/` (or wherever `CLAUDE_CONFIG_DIR` puts it) — a harness convention, not ai-badger data |
| `resume_command` | how to resume the session (`claude --resume <id>`) |
| `started_at`, `finished_at` | state timestamps |
| `state` | `STARTED`, `IN_PROGRESS`, or `FINISHED` |
| `resume_attempts` | JSON array of `{ "at": "…", "dryRun": false }` |
| `tracking_source` | what wrote the row |
| `state_json_updated`, `state_json_reminder_sent`, `compaction_reminder_sent` | knowledge-log freshness flags (the Phase 5 reminder protocol) |

State transitions: `start`/prompt-hook → STARTED; first Stop-hook checkpoint or
`reattach`/cron resume → IN_PROGRESS; `finish` → FINISHED. FINISHED is terminal (`start`
refuses to reopen).

`start` and `reattach` refuse to attach a sessionId that another, not-yet-FINISHED task is
already attached to (exit 2) — this catches a stale session record handing a new task someone
else's still-open session (e.g. a hook that hasn't fired yet for the real new session).
Attaching a sessionId that belonged to an already-FINISHED task is allowed — that's legitimate
sequential reuse of one Claude Code session across two backlog tasks. Writes go through
`task_upsert` (plain INSERT plus the application-level check — never `INSERT OR REPLACE`,
which would delete the other active row).

### token_usage (project DB)

One row per tracked task, keyed by `task_id`. `checkpoints`, `subagents`, and `usage` are JSON
columns — whole-document payloads (P0.6a ruling: the access pattern never filters per
subagent; the revisit seam is documented in ADR-0024).

| Column | Carries |
|---|---|
| `task_id` | the tracked task (NOT NULL primary key) |
| `session_id` | attached session |
| `checkpoints` | JSON: `start` / `latest` / `finish`, each `{ "timestamp": "…", "contextTokens": 42000, "assistantMessages": 12, "cumulative": { … } }` — `latest` updates on every Stop-hook fire, `finish` is written by `task_tracker.py finish` |
| `subagents` | JSON array of `{ "description": "…", "totalTokens": 84852, "at": "…" }` |
| `usage` | JSON: `inputTokens`, `outputTokens`, `cacheReadTokens`, `cacheCreationTokens`, `subagentTokens`, `contextTokensAtStart`, `contextTokensAtFinish`, `contextGrowth`, `mainSessionTotal`, `grandTotal` |
| `grade`, `graded_at` | the user's 0–5 quality grade, when graded (null until then) |
| `tracking_source` | what wrote the row |

- `contextTokens` = context-window occupancy of the latest main-chain assistant message
  (input + cache_read + cache_creation), parsed from the session transcript JSONL.
- `usage` = finish − start deltas plus recorded subagent tokens, recomputed on *every*
  `subagent` call (against the `finish` checkpoint once it exists, else `latest`) — not just
  once at `finish` time. Subagent work dispatched after `finish` (e.g. a review-fix round) still
  lands in `grandTotal`. `grandTotal` is the honest per-task figure to compare across tasks.

### sessions (project DB)

Every currently-active session, keyed by `session_id` — not a single "most recent" pointer.
Multiple Claude Code sessions run against this repo concurrently in normal use (auto-continue,
manually-opened windows, worktree agents), so a single-slot record would let one session's hook
clobber another's. Columns: `session_id` (PK), `transcript_path`, `cwd`, `pid`,
`recorded_at`. Written by the SessionStart and UserPromptSubmit hooks on every fire,
opportunistically pruning entries whose recorded pid is no longer alive.

Tracker commands resolve *their own* session via `tracker_lib.resolve_own_session()`, never by
grabbing "whatever's most recent":
1. `CLAUDE_CODE_SESSION_ID` env var — Claude Code sets this on every tool subprocess it spawns,
   so it identifies the calling session exactly, with zero ambiguity even under concurrency.
2. This process's PID ancestry matched against a recorded session's `pid` (covers CLI versions
   without the env var).
3. A unique cwd match among active sessions (last resort — only if exactly one active session
   shares this process's cwd; a shared cwd with multiple candidates is left unresolved).

`--session-id`/`--transcript-path` CLI flags always win over auto-resolution when passed
explicitly. `cmd_start`/`cmd_reattach` additionally refuse (exit 2) to attach a sessionId
already claimed by a different, not-yet-FINISHED task — see `find_other_entry_with_session`.

### statusline (project DB, KV)

One table, two rows: key `state` (rate limits, context window %, model info — written by
`statusline_capture.py`, read by `poll_limit.py`) and key `delegate` (the statusline delegate
record, owned by `statusline_wiring.py`). All KV tables share the shape `key` (PK), `value`
(JSON document), `updated_at`.

### marker_state (project DB, KV)

The prompt-markers hook's detection history (capped at the most recent 100 entries). Written
only when an `.ai-badger/` directory already exists above the prompt's cwd — the hook never
creates tracking structure, and the store opens `tracking.db` only under an existing
`.ai-badger/`, matching the directory guard.

### User-DB tables

- `awm_state` (KV) — away/partner mode, one row per project path: `{"enabled": …, "mode": …,
  "enabled_at": …, "duration": …, "expires_at": …}`. Writers: `awm.py`, `awm_gate.py`,
  `awm_context.py`.
- `awm_decisions` (log) — the away-mode audit log: `mode_enabled/disabled/expired`,
  `auto_approve`, `question_denied`, `denylisted`, `out_of_scope`, `decision`. Rows are
  `{ts, payload}`; 60-day retention.
- `commit_reminder` (KV) — per-project commit-reminder entries (uncommitted counts, fire
  counters, at-risk markers).
- `pending_feedback` — the grounded-feedback stash: written by `grounded_feedback.py`, popped
  by the Hermes dispatcher.
- `searches` (log) — memory-grade search records; 60-day retention (grades themselves go to
  the raccoon server's SQLite, not here).
- `memory_first` — ADR-0017's consulted-session markers and denial counters.
- `semantica_nudge` — one row per session the semantica nudge was shown to (the writer is
  framework-side and imports the engine directly, so no vendoring).
- `dispatch_lanes` — delegation lane records (one lane per row; legacy lanes were files of
  `<epoch-float> <tool_use_id>` lines).
- `dirty_sweeps` — cross-worktree dirty-sweep records, keyed by sha1 of the main checkout
  root: all worktrees of a repo deliberately share one record, because the warning is
  repo-wide.
- `blast_radius_denials` — kill-guard denials, keyed by session and project.

## The committed knowledge log (stays JSON, git-tracked)

The tracked exceptions to the store are the project's persistent, committed knowledge log — a
three-file split: `.ai-badger/state.json` (lean always-loaded index),
`.ai-badger/status-notes.json` (per-task verbose notes, on demand),
`.ai-badger/status-history.json` (older lean entries, on demand). These stay plain files on
purpose: they are git-tracked repo content the main agent edits with ordinary Read/Write
(ADR-0024, decision 13). Project-specific config (build/test commands, source control, persona
routing) also stays JSON in `.ai-badger/config.json` — see `schemas/config.schema.json`.

### .ai-badger/state.json (tracked, repo root's `.ai-badger/`)

The lean, always-loaded index of the project's per-task knowledge log — the equivalent of what a
CLAUDE.md "Current state" section would otherwise accumulate. Holds the **8 most recent** lean
`completedTasks` entries only; verbose detail lives in the two sibling files below. Updated as
part of every task's Phase 5 finish protocol; freshness since a task's `startedAt` is what
`state_json_updated_since()` (in `tracker_lib.py`) checks to gate `finish`.

```json
{
  "lastUpdated": "2026-07-18T15:00:00Z",
  "notesRef": ".ai-badger/status-notes.json",
  "historyRef": ".ai-badger/status-history.json",
  "completedTasks": [
    { "id": "T29.1", "summary": "one-liner", "issue": 111, "pr": 119, "hasNotes": true }
  ],
  "next": { "id": "T20", "note": "…" },
  "filedNotStarted": [{ "id": "T28", "issue": 43 }],
  "research": [{ "topic": "…", "date": "…", "docs": "docs/research/…", "notes": "…" }],
  "stillTrue": ["standing caveats a new session must not assume away"]
}
```

- Lean entry fields: `id`, one-line `summary`, `issue`/`pr` (when a source-control extension
  supplies them), `hasNotes` (whether status-notes.json has an entry for this id). NO verbose
  `notes` or `gapsRequiringDecision` here — those live in status-notes.json only.
- Unlike the store, these files are plain (unlocked) `Read`/`Write` — edited by the main agent
  as ordinary repo content, not by concurrent tracker-CLI processes.

### .ai-badger/status-notes.json (tracked)

Per-task verbose detail, keyed by task id — every task that has notes, recent or historical.
Loaded ON DEMAND when a session needs one specific task's detail, never at session start.

```json
{
  "T29.1": {
    "notes": "dense hard-won facts: SDK traps, unverified guesses, deferred-decision pointers…",
    "gapsRequiringDecision": "open decisions for the user, or null"
  }
}
```

### .ai-badger/status-history.json (tracked)

Lean entries (same shape as `state.json`'s `completedTasks`) for tasks older than the
most-recent-8. Their notes stay in status-notes.json — history holds lean entries only.

```json
{
  "completedTasks": [
    { "id": "T08", "summary": "Example feature summary", "pr": 38, "hasNotes": false }
  ]
}
```

## Finish-protocol write pattern (Phase 5 step 2)

1. Prepend the finished task's lean entry to `state.json`'s `completedTasks`; refresh
   `next` and `lastUpdated`.
2. Write its verbose notes + `gapsRequiringDecision` to `status-notes.json` under the task id;
   set the lean entry's `hasNotes` to match.
3. If `completedTasks` now exceeds 8, move the oldest overflow lean entries to
   `status-history.json`'s `completedTasks` (prepend, keeping newest-first order). Never trim
   or delete notes text during eviction — it already lives in status-notes.json.

`task_tracker.py finish` gates on `state.json`'s mtime (step 1 always touches it), so the
exit-3 freshness check is unaffected by the split.

## Legacy JSON shapes (transitional)

The pre-migration shapes of the migrated stores — `executed-tasks.json`, `token-usage.json`,
`current-session.json`, `statusline-state.json`, `statusline-delegate.json`,
`prompt-markers/marker-state.json` (project scope); `~/.claude/awm/state.json`,
`~/.claude/awm/decisions.jsonl`, `~/.ai-badger/commit-reminder/state.json` + `pending.json`,
`~/.ai-badger/pending-feedback.json`, `~/.ai-badger/memory-grade/searches.json`,
`memory-first/`, `semantica-nudge/`, `dispatch-lanes/`, `dirty-sweep-*.json`,
`blast-radius-guard/*.denials`, `debug/state.json` + `debug/audit.jsonl` (user/audit scope) —
matter only until a deployment's first write has migrated them. Their contents survive as the
rows and KV values described above; the store module's family definitions
(`badger_store.Family`) are the authoritative legacy mapping. Do not create or hand-edit these
files in a migrated deployment: a legacy file that reappears after migration is treated as
resurrection — re-imported (append-only families) or failed closed with an upgrade pointer
(map families), per ADR-0024. An old `*.migrated.*` file left in place is inert.
