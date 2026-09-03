---
name: test-economy
description: >-
  Use when a change's tests are about to run — deciding what to run locally (the modified
  surface and its consumers, once), what to leave to CI (the full suite, when CI is alive),
  and what to do when CI is dead (hooked-up local gates once, else one manual full-suite run
  before push) — or when full-suite runs start repeating and something must say stop. A
  PostToolUse hook counts shell test-runner commands, classifies full vs filtered, and
  commands the economy on the run past the session's budget; deliberate flake diagnosis is
  the one exempted repetition.
version: 1.0.0
author: ai-badger
license: MIT
platforms: [linux, macos, windows]
scope: default
metadata:
  hermes:
    tags: [testing, ci, hooks, discipline]
    related_skills: [commit-reminder, task, review-tests, dotnet-flaky-test-diagnosis]
---

# Test economy

A command, not a gate: this hook only ever adds `additionalContext` to a `PostToolUse` event.
It never blocks, denies, or otherwise gates the tool call that triggered it — no `decision`,
no `permissionDecision`, no `continue` field, on any code path (changelog 0.33.0: no
third-party tool-call interception).

## The economy

Normal flow runs each affected suite once: the modified surface's tests, plus the suites of
anything that consumes the changed behavior — an API change pulls the frontend end-to-end
that exercises it. The full suite is CI's job on push. The sequence to eliminate is the
triple — the agent's manual full suite, then the gate's full suite, then CI's full suite,
all three proving the same commit. The whole budget is one minimal manual run plus CI.

**The CI variable decides what proves a push:**

- **CI alive** (public repos always; check `gh run list`, the repo's CI badge, or the
  project's workflow files): skip every slow local gate by design and treat CI's run as the
  verdict — then read it. Local work is the touched surface's suite, once.
- **CI dead or absent**: run the project's hooked-up local gates once before push; when
  nothing is hooked up, one manual full-suite run before push is the gate.

Docs checks, comment and documentation discipline, and lint sit on no skip side of this
trade — they run on every change, locally or via CI, in both branches.

A suite is never re-run to see whether it passes again. Repetition is measurement, and
measurement belongs to a named diagnosis — flake triage runs a suite deliberately, in
isolation, and reports the distribution. A re-run after a code fix is a new run, not a
repetition.

## What triggers it

After every shell-shaped tool call, the hook classifies the command: a test run names a
known runner (`pytest`, `python -m pytest`/`unittest`, `dotnet test`, `npm`/`yarn`/`pnpm`/
`bun test`, `npx jest`/`vitest`/`playwright`, `go test`, `cargo test`, `mvn test`,
`gradle test`, `mix test`, `phpunit`, `rake test`, `swift test`). It is **filtered** when
the command carries a selector — a path, `-k`, `--filter`, `--tests`, `-Dtest=`, `-run` —
and **full** otherwise (`go test ./...` counts full; an unrecognized flag never demotes a
run to filtered).

Counts are per project per session. Filtered runs never count against anything. A full run
past the budget (`MAX_FULL = 2` allowed, so the nudge lands on the 3rd) fires once; from the
escalation bar (`ESCALATE_AT = 5`) every further full run nags with the STOP form. Runs
between nudge and bar stay silent — the agent was told; give it room to act.

The fire message carries the **local gate wiring** this project has (lefthook, pre-commit,
husky, a real git pre-push hook), so the CI-dead branch is actionable on the spot: wired
gates run once before push; none wired, one manual full-suite run.

## The diagnosis exception

The hook cannot read intent, so it fires during deliberate flake diagnosis too. When the
repetition is the diagnosis — a measured distribution for a suspected flake, per
`review-tests` Pass 2 and `dotnet-flaky-test-diagnosis` — say so and continue. The nudge
governs normal flow; naming the diagnosis is what takes a run out of it.

## State

One row per project in the user store (`test_economy` family), holding the last eight
sessions' counters — full runs, filtered runs, fires, first-fire timestamp. Sessions are
independent budgets: a fresh session starts clean. A store that cannot be opened fails open
to empty state; the hook stays silent rather than wrong.

## Gotchas

- **The hook only ever adds `additionalContext`** — no `decision`/`permissionDenied`/
  `continue` on any code path (changelog 0.33.0).
- **Filtered runs never fire the nudge** — a path or selector argument is the touched
  surface running, exactly what the economy wants. Only full runs count.
- **`go test ./...` is a full run; `go test ./pkg/...` is filtered.** The whole-tree form is
  the suite; a package path is a surface.
- **The diagnosis exception is named, not assumed.** "Say so and continue" means state the
  diagnosis in the response — an unspoken repetition is just repetition.
- **CI-alive must be checked, not assumed.** Public repos usually have CI; private ones may
  not. `gh run list --limit 3` (or the project's equivalent) is the check; a dead or absent
  CI moves the gate back local.

## Configuration

- `AI_BADGER_TEST_ECONOMY_MAX_FULL` — full-suite runs a session may make before the nudge.
  Defaults to `2` (nudge on the 3rd). A non-numeric value falls back to the default.
- `AI_BADGER_TEST_ECONOMY_ESCALATE_AT` — the run count from which every full run nags with
  the STOP form. Defaults to `5`.

## Observability

Every run logs to the `debug_log`/`call-behaviorist` audit trail under component name
`test_economy_hook` (a no-op unless that facility is switched on): `skip` with the reason
when the hook exits early, `checked` after classifying the command, and `fire` with the
runner and escalation state when the command is emitted.

## Verification Checklist

- [ ] Hook verified counting: two full runs silent, third fires, message names the local gate wiring (or its absence)
- [ ] A filtered run (path or `-k`) leaves the counters untouched
- [ ] CI status checked and the branch taken matches it (CI alive → read the run; CI dead → gates once, or manual full suite once)
- [ ] Lint and docs gates ran — they are on no skip side of the economy

## Files

- `scripts/suite_economy.py` — pure logic: runner recognition, full-vs-filtered
  classification, the per-session counting rule, the message text, and local-gate detection.
- `scripts/suite_economy_hook.py` (not `test_*`: the scaffold's delivery ignores test_*.py — a production hook must not wear a test's name) — the `PostToolUse` entry point wiring the above together.
- `scripts/debug_log.py` — the canonical logger re-export shim (hooks run from several
  deployment shapes and must not depend on the framework being importable).
- `scripts/badger_store.py` — the user-store module (byte-identical to the engine copy).
