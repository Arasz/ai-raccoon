# Mem cleanup: shared-row migration, file-logging removal, workspace/tombstone sweep — plan (2026-08-11)

Task: `mem-cleanup`. Implements recommendations 5, 6, 7 of
`docs/work/2026-08-11-ai-raccoon-diagnostic.md` (PR #257), per the owner's brief:
all three steps are one cleanup; the resolution for recommendation 6 is to REMOVE the
file logging from ai-raccoon and ai-badger (approach changes later), not to widen coverage.

Planning ran in-session at reduced rigor (no `delegation.model` in `.ai-badger/config.json` —
subagents inherit the session model; per standing memory note).

## Evidence (re-checked against the live bank 2026-08-11, read-only)

- Shared tier = 103 rows: 44 value-addressed (`shared/<64hex>.md`) + 59 legacy
  `shared//<abs-path>` (double-slash; the pre-1.6.3 `"shared/" + source.Path` shape with
  absolute paths). All 59: `embed_state='embedded'`, zero `hermes/%` source_files, **all
  values unique among legacy AND disjoint from the 44 value rows** (no `uq_entries_shared_bucket`
  collision risk).
- `entries` triggers fire only on `value`/`source_file`/`section`/`embed_state`/
  `structure_embedding` UPDATEs — a path+hash-only UPDATE fires nothing (verified in
  `sqlite_master`). System sqlite3 (no vec0) can therefore run the migration, but NOT
  `DELETE FROM entries` (vec0 AFTER-DELETE trigger) — workspace entries must go through the app.
- Ops log: `~/.ai-raccoon/memory-operations.jsonl` is written by the HERMES provider plugin,
  source of truth in THIS repo at `integrations/hermes/ai-raccoon/status.py`
  (`MemoryOperationLog`) + `__init__.py` (`_op_log`/`_log`, env `AIRACCOON_MEMORY_LOG`,
  call sites at __init__.py:258,262,290,292,331,335,338,359,361). Installed copy at
  `~/.hermes/plugins/ai-raccoon/` differs only by one missing status word (older).
- Quality log: `~/.ai-badger/memory-grade/memory-quality.jsonl` + `pending.json` written by
  the ai-badger plugin `memory_grade.py` (`AI_BADGER_MEMORY_GRADE=1` gate; log_search/grade
  CLI). Source of truth in the **ai-badger repo**:
  `features/common/skills/ai-raccoon-memory/scripts/memory_grade.py` (+ `memory_grade_hook.py`),
  wired in `features/common/hooks/ai_badger_hooks.py` (`_maybe_log_memory_grade` ~855-861,
  `pop_ask` ~602-604). Installed copy at `~/.hermes/plugins/ai-badger/` is byte-identical.
- Stale Active workspaces = 8 (verified): `acme` ×2, `manual-13x-probe` ×5 (3 of which hold
  the only 3 workspace entries in the bank), `manual-d1d2d3-verify` ×1 — all 08-04/08-08
  manual-test artifacts. `sync_tombstones` = 32 rows, `sync_meta` empty (no sync configured).
- Sweep tool: `memory_workspace_discard(projectId, workspaceId)` removes the workspace's
  entries + context through the app (vec/fts triggers fire correctly); requires Destructive
  access (bank is `full`). Tombstones are a plain table — direct SQL is safe.

## Decision — migrate, do not document as legacy (recommendation 5)

Migrate the 59 rows to value format. Rationale: value-addressing is the only current contract
(1.6.3+); the rows are clean (unique values, embedded, no collisions); the migration is a
path+hash UPDATE that touches no trigger; the alternative (document-as-legacy) leaves a
permanent two-format tier and a dead legacy branch in `SharedExtractionService.IsDuplicate`
for rows that will only age. Risk is bounded by: backup copy first, dry-run report, post-run
verification (103/103 value-addressed, hash formula re-verified on every row).

## Work packages

### WP1 — migrate 59 legacy shared rows (live bank, one-off; scripted)

New `scripts/migrate-shared-legacy-rows.py` (python sqlite3, `--bank <path>` default
`~/.ai-raccoon/memory.db`, `--dry-run` default, `--apply`):
1. Pre-verify: every value-addressed row satisfies `hash == sha256(path+value)` (formula
   lock — abort otherwise); count legacy rows.
2. Compute new `path = "shared/" + sha256(value).hexdigest() + ".md"`, new
   `hash = sha256(path+value)`; detect twins (legacy↔legacy or legacy↔value; expect 0).
3. Apply: `UPDATE entries SET path=?, hash=? WHERE id=?` per row (single transaction).
4. Verify: 0 rows `LIKE 'shared//%'`; 103 value-addressed rows; all hashes match the formula;
   `PRAGMA integrity_check`.

TDD: `scripts/tests/test_migrate_shared_legacy_rows.py` — fixture DB (entries + the shared
unique index + fts/vec trigger SURROGATES are unnecessary: assert only path/hash changes and
collision handling). RED: legacy row keeps `shared//` path after a dry-run call against the
fixture (module absent / behavior absent); GREEN after the script implements migration.

Live execution: WAL-safe backup (`~/.ai-raccoon` online-backup API → `/tmp/mem-cleanup/pre-migration.db`),
dry-run report, apply, verify. The live `serve` (7721) may run concurrently (WAL); the
extraction loop only reads the shared tier.

**Gate G1**: pytest (scripts) green; live post-state = 103/103 value-addressed,
`integrity_check` ok, formula holds for every shared row; backup + reports in the outcome doc.

### WP2 — remove ops file logging from the ai-raccoon provider (this repo)

- `integrations/hermes/ai-raccoon/status.py`: delete `MemoryOperationLog` class (and its
  json/threading imports); keep `STATUS_WORDS`/`status_word` (stderr cues are not file logging).
- `integrations/hermes/ai-raccoon/__init__.py`: delete `_op_log` field (161), env read
  (196-197), `_log` method (225-231) and all 8 call sites; drop `MemoryOperationLog` import;
  update the module docstring. `AIRACCOON_MEMORY_LOG` becomes inert.
- Tests (TDD): `integrations/hermes/tests/test_status.py` — replace the four
  `test_operation_log_*` tests with one RED test asserting **no file is created** even when
  `AIRACCOON_MEMORY_LOG` is set; keep status-word tests; drop the now-dead
  `test_all_provider_tools_have_status_words` only if it references the log (it does not —
  keep). Check `conftest.py` fixtures (`status_module` etc.) still load.
- Sync the live install: copy updated `status.py` + `__init__.py` to
  `~/.hermes/plugins/ai-raccoon/` (check `integrations/hermes/ai-raccoon/README.md` for the
  canonical install flow first).
- Delete the accumulated `~/.ai-raccoon/memory-operations.jsonl` (backup to /tmp first).
- Docs: `docs/reference/agent-memory-server.md` + `docs/plans/adoption-improvement-plan.md`
  (KPI sources) — remove/adjust references to the ops log; diagnostic doc resolution note.

**Gate G2**: RED witnessed (file created today) → GREEN (no file); integrations pytest green;
live: one hermes `memory_search` appends nothing to the ops log after the install sync.

### WP3 — remove memory-grade file logging from ai-badger (separate repo, separate PR)

In the **ai-badger repo** (`~/RiderProjects/ai-badger`, its own worktree + branch + PR):
- Delete `features/common/skills/ai-raccoon-memory/scripts/memory_grade.py` and
  `memory_grade_hook.py`; strip the `memory_grade` wiring from
  `features/common/hooks/ai_badger_hooks.py` (`_load_memory_grade`, `_maybe_log_memory_grade`,
  `pop_ask` block); delete `tests/test_memory_grade_*.py`; update the memory-quality-logging /
  ai-raccoon-memory skill docs and any `AI_BADGER_MEMORY_GRADE` mentions (grep repo-wide);
  adjust `features/hermes/adjustments/adjust_hooks.py` if it installs the grade files.
- Sync the live install: remove `memory_grade.py` from `~/.hermes/plugins/ai-badger/`, patch
  `ai_badger_hooks.py` identically.
- `AI_BADGER_MEMORY_GRADE` becomes inert (documented); `~/.ai-badger/memory-grade/` files
  left in place or archived (one-line note in the ai-badger changelog).
- Follow the ai-badger repo's own conventions (its tests, its PR flow).

**Gate G3**: ai-badger pytest green (targeted suites + full quick run); installed plugin has
no `memory_grade` references; a live `memory_search` under `AI_BADGER_MEMORY_GRADE=1` appends
nothing to `memory-quality.jsonl`.

### WP4 — sweep 8 stale Active workspaces + 32 tombstones (live bank, one-off)

- Workspaces: `memory_workspace_discard(projectId, workspaceId)` per stale workspace via the
  live bridge (app path → vec/fts triggers fire correctly). Project ids: `acme` ×2,
  `manual-13x-probe` ×5, `manual-d1d2d3-verify` ×1.
- Tombstones: `DELETE FROM sync_tombstones` (plain table, no triggers) via python sqlite3 on
  the live bank.
- Verify: `workspaces` Active count for those ids = 0; entries with those workspace_ids = 0;
  `sync_tombstones` = 0; `memory_workspace_status` per project no longer lists them; server
  healthy after.

**Gate G4**: SQL verification above + tool-listing verification; counts recorded in the
outcome doc.

### WP5 — docs + outcome (this repo)

- `docs/work/2026-08-11-mem-cleanup.md` — outcome note: evidence, executed operations,
  verification numbers for all three steps.
- Diagnostic doc: append "Resolution (2026-08-11)" section for recommendations 5/6/7.
- `docs/adr/0007-propose-tier.md`: one paragraph — shared tier is value-addressed only since
  the 2026-08-11 migration; the `shared/{row.Path}` legacy branch is dead-but-harmless.
- Grep-repo-wide pass for stale references to `memory-operations.jsonl`,
  `memory-quality.jsonl`, `AIRACCOON_MEMORY_LOG`, `AI_BADGER_MEMORY_GRADE` in docs/scripts.

**Gate G5**: no stale references in this repo's docs; outcome doc committed with the PR.

## Out of scope

- Queue hygiene (already-shared exclusion, persistent discards) — task `mem-imp-1`, parked
  (STARTED; its plan lives in `.ai-badger/worktrees/mem-imp-1/`), its own PR.
- Removing the now-dead `shared/{row.Path}` legacy branch in `SharedExtractionService` (C#
  change; harmless dead branch — noted in ADR-0007).
- New telemetry approach (OTLP-based) — "we will change approach later".
- No C# server change → no 1.6.x version bump (consistent with the docs-only PR #257).
  Provider plugin version stays 0.1.0.

## Sequencing & risks

WP1 → WP2 → WP4 → WP5 in this repo; WP3 is a separate repo and runs after WP2 (needs the
same live-install sync pattern). Executed in-session sequentially (one executor; reduced-rigor
mode) — no parallel lanes.

Risks: live-bank writes (backup + dry-run + verification each step); the bridge/serve may be
mid-call during the migration (WAL, small transaction); `memory_workspace_discard` needs
Destructive access (confirmed full). The 3 workspace entries are in stale workspaces — deleted
with them, by design. `mem-imp-1`'s plan (queue hygiene) explicitly excludes the 59-row
migration — no overlap.

## Acceptance summary

| WP | Gate | Proof |
|---|---|---|
| WP1 | G1 | pytest + live 103/103 value-addressed, integrity ok |
| WP2 | G2 | RED→GREEN tests, integrations pytest, live ops-log static |
| WP3 | G3 | ai-badger pytest, installed plugin clean, live quality-log static |
| WP4 | G4 | SQL + tool-listing counts: 8/32 → 0 |
| WP5 | G5 | repo-wide grep: no stale log references |
