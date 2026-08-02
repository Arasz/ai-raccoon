---
name: commit-reminder
description: >-
  Use when a project has accumulated uncommitted changes and nobody has said so out loud —
  several edits in a row with no commit in between — or when a subagent may be stuck and about
  to lose its work ("did that agent commit?", "is anything at risk?", "ensure work is
  committed"). A PostToolUse hook watches the live `git status --porcelain` count after every
  edit-shaped tool call and commands a commit once it crosses a threshold; after repeated
  unanswered commands it records the work as at risk, and `scripts/ensure_committed.py` reports
  that to a parent.
---

# Commit reminder

A command, not a gate: this hook only ever adds `additionalContext` to a `PostToolUse` event. It
never blocks, denies, or otherwise gates the tool call that triggered it — no `decision`, no
`permissionDecision`, no `continue` field, on any code path. That distinction is load-bearing:
`docs/changelog/0.33.0-no-third-party-tool-call-interception.md` records ripping out a
third-party plugin that hooked every `Write`/`Edit`/`Bash` call and forced an OAuth login before
letting it through. This skill exists to never repeat that mistake.

It is phrased as an instruction rather than a suggestion. "Consider committing" is advice an
agent mid-task routinely declines; the point of the hook is that the work stops being at risk.

## The commit convention

Commits follow [Conventional Commits 1.0.0](https://www.conventionalcommits.org/en/v1.0.0/),
and the hook's message states the form so nobody has to go and look it up:

```
<type>[optional scope]: <description>

[optional body]

[optional footer(s)]
```

`fix:` patches a bug, `feat:` adds a feature, and either may carry `!` or a
`BREAKING CHANGE:` footer. `build:`, `chore:`, `ci:`, `docs:`, `style:`, `refactor:`, `perf:`
and `test:` are the other types in common use. The hook deliberately does **not** guess the
type from the changed paths — the type states intent, and intent is not recoverable from a
file list.

## What triggers it

After every `Write`/`Edit`/`MultiEdit`/`NotebookEdit` (or a Hermes edit-shaped tool call), the
hook runs `git status --porcelain` in the project root and counts the files it lists. There is no
separately tracked file list — the live count *is* the signal — so the moment a commit happens,
the count drops on its own and the hook needs no cleanup step to notice.

## The debounce ratchet

A per-project entry is persisted between calls. The hook fires when the count first crosses the
threshold and again each time it reaches a **new high**, staying silent while the count holds
flat — otherwise it would repeat itself on every edit past the threshold. As soon as the count
drops below the stored marker, the marker ratchets down immediately, so climbing back past the
threshold later fires again. It is a re-arming debounce, not a one-time flag.

Two consequences worth knowing, because they follow from the count being the only signal:

- The escalation bar is three *new highs*, not three commands ignored over a span of time. An
  agent that edits the same five files repeatedly is never asked twice.
- Anything that lowers the count clears the unanswered counter, including `git stash` or a
  cleaned build directory — not only a commit. The hook cannot tell those apart, and treating a
  dropped count as progress is the same assumption the ratchet already makes.

## Escalation: an agent that never commits

Each firing that is not followed by a commit increments an unanswered count on the entry. A
dropped file count is the only evidence of a commit this hook has, so that is what clears it.

Once the count reaches `ESCALATE_AFTER` (3), two things change. The message escalates — it
states that the work is at risk and asks for a WIP commit rather than a tidy one — and the entry
is reported by:

```console
$ python3 .ai-badger/skills/commit-reminder/scripts/ensure_committed.py
1 project(s) told to commit 3+ times with no commit since:
  /repo/wt-a session sess-7 — 3 unanswered since 2026-07-31T12:00:00Z
Take over the work, commit it yourself, or stop the agent — an agent that ends here
leaves the work unrecoverable.
```

**Why a script and not a message to the parent.** A `PostToolUse` hook can only add context to
the agent that triggered it; it has no channel to that agent's parent. So the hook records and
the parent reads. Run `ensure_committed.py` when a subagent has been working a long time without
landing anything — it names which project, which session, and how long, while there is still
time to take over, commit, or stop it. It exits 0 even when work is at risk — and on malformed
state, which is a report a parent must still be able to read: crashing would be a worse failure
than the one being reported.

A project whose work has since been committed drops out of the report even if its entry has not
been cleared yet, because the entry only clears on a later hook run in that project — otherwise
a finished or deleted worktree would stay "at risk" forever.

The Hermes hook shares the same entry. Both sides read and write it through `advance`, so an
edit on one side cannot silently clear an escalation raised on the other.

Entries are keyed by resolved project root, which separates parallel agents whenever they run in
their own worktrees — the common case for this kind of fan-out.

## Configuration

- `AI_BADGER_COMMIT_REMINDER_THRESHOLD` — uncommitted-file count that triggers the command.
  Defaults to `5`. A non-numeric value falls back to the default rather than erroring.
- `AI_BADGER_COMMIT_ESCALATE_AFTER` — unanswered commands before the work is reported at risk.
  Defaults to `3`.
- `AI_BADGER_COMMIT_REMINDER_IMPACT=graph` — opt into a richer impact estimate backed by the
  `code-review-graph` CLI instead of the cheap default (file count + directory spread). This is
  slower (roughly 15-20 seconds observed per call), so it only runs once the cheap check has
  already decided to fire — never on every edit — and it falls back to the cheap estimate
  silently if the graph call fails or isn't available.

## Observability

Every run logs to the `debug_log`/`call-behaviorist` audit trail under component name
`commit_reminder_hook` (a no-op unless that facility is switched on): `skip` when the hook exits
early, `checked` after computing the uncommitted count, and `fire` when the command is emitted —
carrying `unanswered` and `atRisk` so the escalation is visible in the audit trail too.

## Files

- `scripts/commit_reminder.py` — pure logic: parsing `git status --porcelain`, recognizing an
  edit-shaped tool, the debounce ratchet, the unanswered-command counter, and the message text.
- `scripts/ensure_committed.py` — the read side: which projects are at risk, for a parent.
- `scripts/impact_estimator.py` — the cheap default and optional graph-backed impact summary.
- `scripts/commit_reminder_hook.py` — the `PostToolUse` entry point wiring the above together.
- `scripts/debug_log.py` — a vendored, byte-identical copy of the framework's debug logger (hooks
  run from several deployment shapes and must not depend on the framework being importable).

## Migration

The state file gained a shape. `{root: <int>}` from earlier versions still loads — a bare
integer reads as a marker with no unanswered commands — so an existing state file needs no
migration and no deletion.
