# Refresh 0.59.0 → 0.77.3: arasz-home-page (2026-08)

Worked example of a large-version-gap refresh: the project had been scaffolded by the
Claude plugin cache (0.59.0); the running framework checkout was 0.77.3 (~18 minor
versions ahead). Everything below was observed on this run.

## Report shape

- `reScaffolded: true`, `error: null`; 142 scaffold entries. `drift.changed` 22 sources
  (personas, task skill + 4 extensions, welcome-ai-badger scripts, auto-wm, code-review-
  checklist + azure/ts extensions, CLAUDE.md/HERMES.md/AGENTS.md templates, .mcp.json).
- `drift.newItems` 7: five invariants (ask-if-simpler, check-sources-not-yourself,
  measure-when-it-pays, plain-names, proof-of-done), delegator persona, create-task-spec
  skill. New invariants are rendered into agent files under humanized heading names
  ("Done means proven", not `proof-of-done`) — grep for the invariant text, not the slug.
- `drift.locallyModified` 0, `removed` 0, `orphaned` 0, `configChanged` null.
- `frameworkCopies`: 17 stale trees under `~/.claude/plugins/cache/ai-badger/` (0.61.2 →
  0.76.0), all `prunable: false` (Claude Code owns the path). No `~/.ai-badger/framework`
  clone → `cache: null`, no --prune-cache offer.
- `skillUsage`: window 16.7 days, all claims from Claude Code transcripts (1127 sessions).
  9 used (commit-reminder + prompt-markers via hook evidence), 5 unused candidates
  (code-review-checklist, create-task-spec, differential-feature-refactor,
  maintain-agent-instructions, owner-gate-review), 0 cannotTell. The limits section is the
  story: Hermes work is invisible to the channel — report candidates with the limits, never
  as a recommendation.

## Pitfalls hit on this run

- Staged with directory pathspecs (`git add -A .ai-badger .claude .github .junie ...`) —
  root-level `.mcp.json` stayed unstaged; two same-message commits; fixed with
  `git reset --soft HEAD~2 && git commit`. Use a root `git add -A`.
- The scaffold wrote `.mcp.json.bak-<ts>` and `.github/mcp.json.bak-<ts>`. Both were
  byte-identical to `git show HEAD:<file>` → deleted, never committed.
- The report-summary parser crashed on `len(scaffold.entries)` — `entries` is an int.

## What shipped in the diff (61 files, +2411/−578)

New invariants ("Done means proven" is the behavioral headline), delegator agent across
all four agents (.ai-badger, .claude, .github, .junie), create-task-spec skill + discovery
symlinks, `.mcp.json` now launches code-review-graph + a `hermes mcp serve` server, new
task-skill scripts (claude_session_source.py, hermes_session_source.py,
dispatch_gate_hook.py). Seed-once files preserved. lefthook pre-commit runs and skips
everything (no matching staged files) — not a failure.

## Follow-up note

The refresh commit sat un-pushed on main; the next task branched from it, so its PR
carries the refresh commit too. Branch-aware workflows should either push the refresh as
its own PR first or expect (and say) that the task PR includes it.
