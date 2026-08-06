# Consumer-project scaffold operations

Enabling opt-in skills and re-running `welcome-ai-badger`'s `scaffold.py` on a project that
already has `.ai-badger/` (the consumer side of the framework). Session-proven 2026-08-02
(docs-init task on the AiRaccon repo).

## The opt-in skill mechanism

`engine/badger_lib.py` declares every skill's scope: `SKILL_SCOPE_DEFAULT` (ships without
being asked) or `SKILL_SCOPE_OPT_IN` (stays in the catalog until a project names it —
ADR-0005). `SKILL_GROUPS` maps group names to members; naming a group or any member
installs all of them (`expand_skill_groups`):

```python
SKILL_GROUPS = {
    "documentation": ("scaffold-documentation", "update-documentation", "migrate-documentation"),
}
```

Opt-in catalog (framework 0.76.0): `debug-issue`, `evidence-first-research`,
`explore-codebase`, `migrate-documentation`, `refactor-safely`, `review-changes`,
`scaffold-documentation`, `update-documentation`.

**User preference (Rafał): when enabling skills, enable ALL opt-in skills** — not just the
group the current task needs. The answer is never "just documentation"; it is all eight.

## Enabling them

Edit `.ai-badger/config.json`:

```json
{
  "include": {
    "skills": [
      "debug-issue", "evidence-first-research", "explore-codebase",
      "migrate-documentation", "refactor-safely", "review-changes",
      "scaffold-documentation", "update-documentation"
    ]
  }
}
```

The `include` key is schema-valid (`schemas/config.schema.json`, `additionalProperties:
false` with `properties.skills` array). No `--skills` CLI flag needed — scaffold.py reads
`config.include.skills` itself (`inclusions(config)` → `expand_skill_groups` → offered +
addable filter).

## Run the scaffold — on MASTER

**User rule (f: corrections): merge main first, then scaffold on master. Never scaffold
with `--target` pointing at a task worktree.**

Why: `relink_hermes_skills(target, config, skills)` in `skill_delivery.py` rebuilds
`~/.hermes/skills/<project>/` to link into `<target>/.ai-badger/skills/`. A worktree
target puts the links at `.ai-badger/worktrees/<taskId>/...`, and `task_tracker.py
finish` deletes that directory — the project's Hermes skills go dangling. (Repair is
possible: run `relink_hermes_skills(MAIN, config, skills)` afterwards; a broken symlink
still returns true from `is_symlink()` so it is unlinked and re-created pointing at main —
but do not create the problem.)

Command (from the project root, `$AI_BADGER` = the framework checkout, e.g.
`~/RiderProjects/ai-badger`):

```bash
python3 "$AI_BADGER/features/common/skills/welcome-ai-badger/scripts/scaffold.py" \
  --config .ai-badger/config.json --target . --root "$AI_BADGER"
```

Idempotent and safe to re-run: rewrites managed files, refreshes `manifest.json`,
preserves seed-once files, validates config.json against the schema on the way. It also
writes symlinks into `.claude/skills/` and `.github/skills/` (tracked, mode 120000) for
Claude Code / Copilot discovery — include those in the commit.

## Verify after the scaffold

- `ls .ai-badger/skills/` — every requested skill present
- `ls ~/.hermes/skills/<project>/ | wc -l` — matches the on-disk count
- `git status` shows: `M .ai-badger/config.json`, `M .ai-badger/manifest.json`, new
  skill dirs under `.ai-badger/skills/`, new symlinks in `.claude/skills/` +
  `.github/skills/`
- `dotnet build && dotnet test` (or the project's commands) still green — scaffold
  touches no code, but run the gates on the merged result anyway

## Commit hygiene

- **Exclude `.ai-badger/task-tracking/*.json`** from the scaffold commit. `task_tracker.py`
  writes session/token state there on every `start`/`subagent`/`finish`; the finish
  protocol modifies them again. (Some repos track these files via the user's own commits —
  leave them dirty rather than bundling them into your scaffold commit.)
- One coherent commit: `feat: enable all opt-in ai-badger skills (…)` covering
  config.json + manifest.json + skill dirs + both symlink dirs.

## Hermes session-id requirement

`task_tracker.py start` needs `--session-id "$HERMES_SESSION_ID"` — outside Claude Code
there is no auto-detection (no `CLAUDE_CODE_SESSION_ID`, no matching
`current-session.json` entry for a Hermes process).

## Parallel-session awareness

The user runs parallel agent sessions that commit mid-task. Re-check
`git log --oneline -5` before trusting `git status`: your staged commit may silently
contain only part of what you expected (a parallel agent committed the rest), and new
untracked files (their WIP, e.g. a spec dossier) may appear mid-session. Never commit
another session's work-in-progress.

## .gitignore append pitfall

`printf 'pattern\n' >> .gitignore` on a file WITHOUT a trailing newline concatenates onto
the last line (observed: `.DS_Store` + `__pycache__/` merged into
`.DS_Store__pycache__/`). Rewrite the file or ensure the trailing newline, then verify:
`git check-ignore -v <path>` (rc 0 = ignored).

## Verification snippet (config.json validity)

```python
import json, jsonschema
cfg = json.load(open(".ai-badger/config.json"))
schema = json.load(open("$AI_BADGER/schemas/config.schema.json"))
jsonschema.validate(cfg, schema)          # must not raise
# every include.skills name must have a dir on disk:
inc = cfg["include"]["skills"]
assert all((Path(".ai-badger/skills") / n).is_dir() for n in inc)
```

## docs-scaffold notes (when the task is docs-init)

- Canonical tree per scaffold-documentation: `tutorials/ how-to/ reference/ explanation/
  adr/ work/ legacy/ assets/ meta/` + root `README.md` compass. Omit `CHANGELOG.md` when
  the project has no docs ledger; omit `legacy/` when no migration is in progress.
- A pre-existing non-canonical directory (e.g. a user's `docs/features/` dossier) gets an
  honest row in the compass marked "non-canonical, pending migrate-documentation" — never
  a home, never an omission.
- Postcondition checks: every file in a governed directory appears in its parent README;
  every relative link in the written docs resolves. Check both by hand.
