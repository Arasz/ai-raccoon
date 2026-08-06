---
name: scripts-tooling-refactor
description: >-
  Refactor repo scripts: convert, src/tests, prune dead.
---

# Scripts-tooling refactor

Refactoring a repo's `scripts/` directory into a maintained layout: per-repo
tooling language only, logic in `src/`, tests in `tests/` (TDD), dead scripts
pruned on evidence. The user's convention across repos (ai-raccoon 2026-08,
jsaa earlier): **all non-python scripts converted to python** (jsaa's stated
tooling language is Bun TypeScript — always check the repo's own convention,
never assume), **old/not-used scripts removed**, **src + tests layout**,
**TDD: tests written first**.

Pairs with `ai-badger-project-ops` (refresh/staging hygiene) and the `task`
skill (worktree + orchestration). Run inside the task worktree.

## Step 1 — Inventory with tracking, not `find`

`find scripts -type f` shows gitignored generated files too. Use
`git ls-files scripts/` — the tracked set is the refactor surface. Note
gitignored outputs (e.g. `baseline-results.json`) separately: they're
generated, never commit them, don't move them.

## Step 2 — Usage evidence decides dead vs live (the core technique)

For every file, trace references across the repo. Use a ripgrep-backed search
(grep -rln over the whole repo can time out on ignored dirs).

Classify each reference:

- **LIVE** — CI run lines (`.github/workflows/*.yml` `run:` steps), current
  test code reading a path, READMEs with run instructions, user gate rituals
  (e.g. a fresh-install gate), csproj comments.
- **HISTORICAL** — `docs/plans/*` (implemented plans), `docs/work/*` findings,
  design proposals. A script referenced only there is a removal candidate —
  but first verify the live gate moved elsewhere (e.g. C# integration tests
  replaced a python runner + manual HTML scoring form).
- A script mentioned only in its own docstring or a sibling script's header
  comment is **not** used — but a three-way sync contract between sibling
  scripts (e.g. verify-tool-package ↔ download-embedding-model ↔
  fresh-install-test) means renaming one breaks the others' comments: update
  them all.
- **Data files with hardcoded paths in tests must NOT move.** C# tests
  hardcode `scripts/chunk-hash-map.json`, `scripts/baseline-queries.json` —
  keep such files at `scripts/` root. A file read by 5+ integration tests is
  load-bearing even if no doc mentions it.
- Removal candidates need a written one-line evidence trail each, and the
  "live gate is elsewhere" check.

## Step 3 — Layout: thin wrappers, logic in src

Preserve call sites: existing callers reference `scripts/<name>` (CI,
READMEs, user's gate ritual). Keep thin entrypoints at `scripts/<name>.py`
that import logic from `scripts/src/`:

```python
# scripts/patch-tool-shell.py — thin wrapper
import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parent))
from src.tool_shell_patch import main  # noqa: E402
if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
```

Only `.sh → .py` renames change references. Check stdlib-only before adding
deps: `grep -E "^(import|from) " scripts/*.py` — this repo's scripts were all
stdlib; keep it that way (no new dependencies without need).

## Step 4 — TDD with pytest

- Verify pytest availability first (`python3 -m pytest --version`), then
  write failing tests BEFORE the logic moves.
- Tests live in `scripts/tests/test_*.py`; run: `python3 -m pytest scripts/tests`.
- Testable units: pure functions with in-memory fixtures — zip patching
  (zipfile + io.BytesIO), sha256 verification (tmp fixture files), csproj
  version parsing, RID detection, chunk hashing/dedup. No network, no live
  server, no `dotnet` invocations in unit tests.
- Orchestration scripts (fresh-install gate, golden regen wrapper) are not
  unit-testable — at most smoke coverage; say so explicitly in the plan.

## Step 5 — Reference-update checklist (after conversions)

Grep the OLD script name repo-wide and fix every live mention:
- CI yml run lines: `bash scripts/x.sh` → `python3 scripts/x.py`
- READMEs (benchmarks/, tests/ sub-READMEs), csproj comments
- Test error messages that tell devs to run a script (e.g.
  `GoldenFileTests.cs`: "regenerate with scripts/regenerate-retrieval-golden.sh")
- Sibling-script header comments naming the converted script
- Leave historical docs (plan/findings) untouched — only live references.

## Step 6 — Gates

- `python3 -m pytest scripts/tests` green (the TDD proof)
- Repo's own gate untouched: `dotnet build` + `dotnet test` (or per-repo
  commands) still green
- Each converted script at least runs (`--help` or a dry invocation) — done
  means proven, not written

## Pitfalls

- Whole-repo `grep -rln` can hang on ignored/derived dirs — use the
  ripgrep-backed search tool (respects gitignore).
- Concurrent sessions dirty the tree: snapshot `git status --short` first,
  stage explicit paths, never `git add -A` (see ai-badger-project-ops).
- A refresh/scaffold may overwrite in-place-edited skills — diff before
  mourning (see ai-badger-project-ops: the overwrite can be a stale-edit fix).
- The task's real spec may arrive as a paste (Claude Code `/task` output +
  requirement bullets): map bullets 1:1 to scope items and confirm the
  refresh/stack work was the prerequisite, not the deliverable.
