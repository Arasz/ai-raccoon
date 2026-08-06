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
sys.path.insert(0, str(Path(__file__).resolve().parent / "src"))
from tool_shell import patch  # noqa: E402
if __name__ == "__main__":
    sys.exit(main())
```

Use the `parent / "src"` + flat-module import (verified to work from any
invocation cwd, e.g. run from /tmp). The `from src import …` namespace-package
variant relying on `sys.path[0]` = scripts/ is fragile under different
invocation styles — rejected in the ai-raccoon plan, don't reintroduce it.

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

## Step 4b — Byte-identical splits: derive expected values, don't assume

For work packages that must be behavior "byte-identical to today" (hash maps,
golden files, outputs read by other test suites), expected values in the TDD
tests come from EXECUTING the current script on inline fixtures — never from
reading it and guessing. Write a throwaway derivation script that imports the
legacy module, runs its functions on your fixtures, and dumps expected values
as JSON; paste the dump into the tests verbatim. Reading misses dead code and
quirks; assumption silently "fixes" them and breaks byte-level contracts.

Real P5 finds: an exclusion glob that only matched literally (`.remember/
today-*.md` — no fnmatch branch ever fired) and an ADR preamble chunk whose
content repeats the H1. Both pinned as-is in tests and flagged in the report.

Real P6 finds (benchmark corpus split): parameterized-collector fixtures
must replicate the FULL config shape — `collect_docs` indexes every repo key
(`repos["badger"]`, `repos["home"]`) and `os.listdir`s `docs/adr`
unconditionally, so a "minimal" single-repo fixture crashes (KeyError /
FileNotFoundError) even when the behavior under test is the short-doc skip.
Also pinned: topic keywords are substring matches ("consistently" contains
"ci" → ci-cost topic) and `strip_md` has no fence branch (the inline-code
regex eats fences by accident). P6's byte-identity proof used a NO-TOUCH
smoke (module-global OUT redirected to tmp, diff legacy-vs-new) because
regeneration of the tracked corpus was forbidden — recipe in
`references/behavior-pinning-derivation.md`.

Importing the legacy module for derivation needs `sys.modules[spec.name] = m`
BEFORE `spec.loader.exec_module(m)` — otherwise dataclasses with `from
__future__ import annotations` crash with `AttributeError: 'NoneType' object
has no attribute '__dict__'`.

Full recipe, importlib fix, split-adjustment checklist, and the golden-artifact
smoke compare: `references/behavior-pinning-derivation.md`.

## Step 4c — Shell→Python ports: byte-identical output discipline

For converted scripts whose gates are "same messages, same exit codes"
(verify-tool-package.sh → src/package_verify.py pattern):

- Split streams exactly: FAIL lines go to stderr (`>&2`), present/verified/OK
  lines to stdout; capsys tests assert each stream separately.
- Preserve print-before-fail ordering: a .sh loop that prints
  "present in package: X" for each entry BEFORE failing on the first missing
  one must stay a loop — compute the missing set, then iterate entries in
  order printing present-or-FAIL. Failing on the first item of the missing
  list changes output order.
- `set -e` propagates the child's exit code; Python should
  `subprocess.run(..., check=True)` with `except CalledProcessError: return
  exc.returncode` — not hardcode 1.
- Testability split for fallback chains (dotnet --info then uname mapping):
  the subprocess lives behind a tiny adapter (`_dotnet_rid()` returning None
  on any failure), pure logic separate (`rid_from_uname(system, machine)`).
  Tests monkeypatch the adapter, parametrize the table — no real subprocess
  in unit tests.
- SHA-pin comparisons can't be unit-tested green: no fake content hashes to a
  fixed pin (preimage). Honest coverage = hash fn vs `hashlib` reference on
  known content, the MISMATCH branch asserting the pin text in the message,
  the pin constant in the bundle-contract test, and the green path proven by
  a real end-to-end smoke (pitfall below).
- Report behavior deltas in the handoff even when exit codes match (e.g.
  missing `<PackageVersion>` now fails earlier with a clearer message;
  tempfile default dir ≠ `${TMPDIR:-/tmp}`).

Worked example with message/exit table and smoke recipe:
`references/shell-to-python-port-checklist.md`.

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

## Step 6b — Behavior-parity harness (conversions)

For any wrapper/shell conversion with "behavior identical to today" in its AC,
run old vs new side-by-side on identical fixtures and DIFF the observable
contract: stdout, stderr, exit codes, and output artifacts (patched nupkg
bytes). Extract the old script via `git show HEAD:<path>` even after the
rewrite landed. For .sh → .py that shells out (`dotnet`), put a shim named
EXACTLY like the real binary on PATH to verify cwd/env/args/exit propagation
without running the real tool. Full recipe + pitfalls:
`references/cli-parity-harness.md`. Pairs with the derivation recipe
(`references/behavior-pinning-derivation.md`): derivation pins expected test
values from current code; the harness proves the converted script still
behaves like the original.

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
- When a PORTED module's test fails, probe the original first (`git show HEAD`
  + importlib exec): error-ordering differences are the original's contract,
  and the port is right when it matches byte-for-byte. Fix the test, not the
  port — do not "fix" the port to match an assumed message.
- When a gate says "usage/exit behavior identical to today", the gated
  behavior beats any suggested implementation that changes it (e.g. argparse
  swaps no-args from exit 1 + docstring to exit 2 + usage text). Keep the
  exact shape and flag the deviation in the report.
- Moving a module into `src/` shifts `__file__`-derived path constants:
  add `.parent.parent` and verify the resolved path still targets the
  original location — a smoke run that writes the artifact is the proof
  (P5's HASH_MAP_PATH would otherwise have landed in scripts/src/).
- Run the pytest gate on both the dev python and the oldest supported
  system python — proves the syntax-compat claim instead of asserting it.
- PATH binaries may be shadowed by an UNDELIBERATE test-harness shim (echoes
  args, exits 0, emits nothing on stdout) — `which dotnet` lied while the
  real SDK sat at /opt/homebrew/bin. For a full-green smoke, verify the
  binary is real (`dotnet --info | grep RID`), then run with
  `PATH=/real/bin:$PATH`. (Step 6b's harness builds shims on purpose; this is
  the inverse: bypassing an ambient shim to reach the real tool.)
- Full-green smokes may need gitignored inputs (a 23 MB model): `git
  check-ignore` first (confirms provisioning is tree-safe), fetch via the
  repo's own download script — turns a "usage path" gate into a real proof
  (green OK, exit 0) without dirtying the tree, and double-smokes the P1
  download script.
- Other lanes' files can appear in the shared worktree MID-SESSION (P6/P7
  landed while P2 ran; `git status` was clean at start). Leave them
  untouched, list them in the report so the parent can reconcile — never
  `git add -A` over them.
