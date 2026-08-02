# Tracking file schemas

Most files live in `.ai-badger/task-tracking/` (gitignored). Writers hold an exclusive flock on
`.write.lock` and replace files atomically, so partial reads are safe. The tracked exceptions
(documented last below) are the project's persistent, committed knowledge log — a three-file
split: `.ai-badger/state.json` (lean always-loaded index), `.ai-badger/status-notes.json`
(per-task verbose notes, on demand), `.ai-badger/status-history.json` (older lean entries, on
demand). Project-specific config (build/test commands, source control, persona routing) lives
separately in `.ai-badger/config.json` — see `schemas/config.schema.json` — and is not part of
this tracking-file set.

## executed-tasks.json

```json
{
  "tasks": [
    {
      "taskId": "T08",
      "title": "Example task title",
      "sessionId": "9fce6c84-…",
      "transcriptPath": "/Users/…/.claude/projects/…/9fce6c84-….jsonl",
      "cwd": "/path/to/project/root",
      "branch": "task/T08-example-slug",
      "startedAt": "2026-07-12T08:30:00+00:00",
      "finishedAt": null,
      "state": "STARTED | IN_PROGRESS | FINISHED",
      "resumeCommand": "claude --resume 9fce6c84-…",
      "stateJsonUpdated": false,
      "stateJsonReminderSent": false,
      "compactionReminderSent": false,
      "resumeAttempts": [{ "at": "2026-07-12T11:00:00+00:00", "dryRun": false }]
    }
  ]
}
```

`transcriptPath` points at the Claude Code CLI's own session transcript under `~/.claude/projects/`
(or wherever `CLAUDE_CONFIG_DIR` puts it) — that location is a Claude Code convention, not part of
this skill's `.ai-badger/` tracking data.

State transitions: `start`/prompt-hook → STARTED; first Stop-hook checkpoint or `reattach`/cron
resume → IN_PROGRESS; `finish` → FINISHED. FINISHED is terminal (start refuses to reopen).

`start` and `reattach` refuse to attach a sessionId that another, not-yet-FINISHED task is
already attached to (exit 2) — this catches a stale `current-session.json` handing a new task
someone else's still-open session (e.g. a hook that hasn't fired yet for the real new session).
Attaching a sessionId that belonged to an already-FINISHED task is allowed — that's legitimate
sequential reuse of one Claude Code session across two backlog tasks.

## token-usage.json

```json
{
  "tasks": [
    {
      "taskId": "T08",
      "sessionId": "9fce6c84-…",
      "checkpoints": {
        "start":  { "timestamp": "…", "contextTokens": 42000, "assistantMessages": 12, "cumulative": { "inputTokens": 0, "outputTokens": 0, "cacheReadTokens": 0, "cacheCreationTokens": 0 } },
        "latest": { "…": "updated on every Stop-hook fire" },
        "finish": { "…": "written by task_tracker.py finish" }
      },
      "subagents": [
        { "description": "implement the feature (TDD)", "totalTokens": 84852, "at": "…" }
      ],
      "usage": {
        "inputTokens": 0, "outputTokens": 0, "cacheReadTokens": 0, "cacheCreationTokens": 0,
        "subagentTokens": 84852,
        "contextTokensAtStart": 42000, "contextTokensAtFinish": 95000, "contextGrowth": 53000,
        "mainSessionTotal": 0, "grandTotal": 84852
      },
      "grade": 4,
      "gradedAt": "…"
    }
  ]
}
```

- `contextTokens` = context-window occupancy of the latest main-chain assistant message
  (input + cache_read + cache_creation), parsed from the session transcript JSONL.
- `cumulative` = sums across all assistant messages in the transcript at checkpoint time.
- `usage` = finish − start deltas plus recorded subagent tokens, recomputed on *every*
  `subagent` call (against the `finish` checkpoint once it exists, else `latest`) — not just
  once at `finish` time. Subagent work dispatched after `finish` (e.g. a review-fix round) still
  lands in `grandTotal`. `grandTotal` is the honest per-task figure to compare across tasks.
- `grade` = user's 0–5 quality grade for the skill run (null until graded).

## current-session.json

Every currently-active session, keyed by sessionId — not a single "most recent" pointer.
Multiple Claude Code sessions run against this repo concurrently in normal use (auto-continue,
manually-opened windows, worktree agents), so a single-slot file would let one session's hook
clobber another's; this is a lock-protected (`locked_store()`) read-modify-write map instead,
written by the SessionStart and UserPromptSubmit hooks on every fire, opportunistically pruning
entries whose recorded pid is no longer alive:

```json
{
  "sessions": {
    "<sessionId>": { "transcriptPath": "…", "cwd": "…", "pid": 12345, "recordedAt": "…" }
  }
}
```

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

## .ai-badger/state.json (tracked, repo root's `.ai-badger/`)

The lean, always-loaded index of the project's per-task knowledge log — the equivalent of what a
CLAUDE.md "Current state" section would otherwise accumulate. Holds the **8 most recent** lean
`completedTasks` entries only; verbose detail lives in the two sibling files below. Updated as
part of every task's Phase 4 finish protocol; freshness since a task's `startedAt` is what
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
- Unlike the files above, this one (and its two siblings) is plain (unlocked) `Read`/`Write` —
  edited by the main agent as ordinary repo content, not by concurrent tracker-CLI processes.

## .ai-badger/status-notes.json (tracked)

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

## .ai-badger/status-history.json (tracked)

Lean entries (same shape as `state.json`'s `completedTasks`) for tasks older than the
most-recent-8. Their notes stay in status-notes.json — history holds lean entries only.

```json
{
  "completedTasks": [
    { "id": "T08", "summary": "Example feature summary", "pr": 38, "hasNotes": false }
  ]
}
```

## Finish-protocol write pattern (Phase 4 step 2)

1. Prepend the finished task's lean entry to `state.json`'s `completedTasks`; refresh
   `next` and `lastUpdated`.
2. Write its verbose notes + `gapsRequiringDecision` to `status-notes.json` under the task id;
   set the lean entry's `hasNotes` to match.
3. If `completedTasks` now exceeds 8, move the oldest overflow lean entries to
   `status-history.json`'s `completedTasks` (prepend, keeping newest-first order). Never trim
   or delete notes text during eviction — it already lives in status-notes.json.

`task_tracker.py finish` gates on `state.json`'s mtime (step 1 always touches it), so the
exit-3 freshness check is unaffected by the split.
