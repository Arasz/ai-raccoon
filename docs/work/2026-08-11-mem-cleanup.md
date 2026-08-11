# Mem cleanup — shared-row migration, file-logging removal, workspace/tombstone sweep (2026-08-11)

Task: `mem-cleanup`. Closes recommendations 5–7 of
`docs/work/2026-08-11-ai-raccoon-diagnostic.md`. Owner brief: all three steps are one
cleanup; for recommendation 6 the decision is to REMOVE the file logging from ai-raccoon
and ai-badger (the replacement approach — server-side quality table / metrics — comes later).

## WP1 — migrate 59 legacy shared rows (recommendation 5)

**Decision: migrate** (not document-as-legacy): value addressing is the only contract since
1.6.3; all 59 rows were verified clean (unique values, no collisions with the 44 value rows,
all embedded); the migration is a path+hash UPDATE that fires no entries trigger.

- `scripts/src/migrate_shared_legacy_rows.py` (+ `scripts/tests/test_migrate_shared_legacy_rows.py`,
  7 tests, TDD RED→GREEN): dry-run by default; pre-verifies the hash formula on every
  value-addressed row; aborts on formula mismatches, value twins or unexpected path shapes.
- Live execution (2026-08-11): WAL-safe backup → `/tmp/mem-cleanup/pre-migration.db`
  (14,108 entries, integrity ok); dry-run report `value_rows=44 legacy_rows=59
  formula_mismatches=0 twins=[] other_shapes=[]`; apply → `migrated=59`.
- Post-verify: 103/103 shared rows value-addressed (`path GLOB 'shared/[0-9a-f]*.md'`),
  0 legacy `shared//%`, 0 hash-formula mismatches, `PRAGMA integrity_check` = ok.

## WP2 — remove ops file logging from the ai-raccoon Hermes provider (recommendation 6a)

The `memory-operations.jsonl` writer lived in the Hermes provider plugin, source of truth in
this repo at `integrations/hermes/ai-raccoon/` (`status.py` `MemoryOperationLog`,
`__init__.py` `_op_log`/`_log`, env `AIRACCOON_MEMORY_LOG`).

- Removed: the class, the env read, the `_log` method and all 8 call sites; status words on
  stderr stay (not file logging). `AIRACCOON_MEMORY_LOG` is inert.
- TDD: `test_no_operation_log_file_created_when_env_set` RED (file created) → GREEN.
  Integrations suite: 69 passed, 7 slow-skipped, 1 pre-existing failure
  (`test_setup_script.py::test_probe_spawns_isolated_server_and_passes` — fails on the base
  tree too; environmental, unrelated).
- Live install synced: `status.py` + `__init__.py` copied to `~/.hermes/plugins/ai-raccoon/`;
  `~/.ai-raccoon/memory-operations.jsonl` deleted (backup `/tmp/mem-cleanup/memory-operations.jsonl.bak`).
  Running sessions keep the old plugin in memory until restart; new sessions write nothing.
- Pre-existing drift noted: the installed plugin's `client.py`/`README.md` lag the repo
  (loopback-token + envelope changes) — a full reinstall via `scripts/hermes-provider-setup.py`
  is a follow-up, out of scope here.

## WP3 — remove memory-grade file logging from ai-badger (recommendation 6b, separate repo)

In the ai-badger repo (branch `task/mem-cleanup-remove-memory-grade-logging`, PR #373):

- Deleted `memory_grade.py` + `memory_grade_hook.py` (the `AI_BADGER_MEMORY_GRADE` →
  `~/.ai-badger/memory-grade/memory-quality.jsonl` + `pending.json` feature) and their 6 test
  files; removed the manifest/hooks.json entries and the plugin wiring (grade-ask injection,
  `_maybe_log_memory_grade`).
- The memory-first gate's consulted-marker recording for Claude/Copilot moved to a new
  `memory_first_gate_post_hook.py` (PostToolUse); the `memory_search` matcher lives inline in
  `ai_badger_hooks.py` and as `is_memory_search` in `memory_first_gate.py`.
- `adjust_hooks.py` gained `REMOVED_MODULES` so stale `memory_grade.py` copies are deleted
  from plugin dirs and project hooks on the next adjust run.
- TDD RED (4 failures) → GREEN; full suite 3759 passed; `sync_plugin_skills` + self
  re-scaffold regenerated the repo's generated copies; changelog entry 0.116.0.
- Live install: `~/.hermes/plugins/ai-badger/ai_badger_hooks.py` updated,
  `memory_grade.py` deleted from the plugin dir and all 4 project `.ai-badger/hooks/` dirs
  (ai-raccoon, ai-badger, jsaa, arasz-home-page); plugin.yaml description refreshed;
  residue check clean. `AI_BADGER_MEMORY_GRADE` (still exported in `~/.zshrc`) is inert.
  `~/.ai-badger/memory-grade/` files left in place as historical data.

## WP4 — sweep 8 stale Active workspaces + 32 tombstones (recommendation 7)

- 8 stale Active workspaces (acme ×2, manual-13x-probe ×5, manual-d1d2d3-verify ×1 —
  08-04/08-08 manual-test artifacts) discarded via `memory_workspace_discard` (app path,
  triggers fire correctly; 3 workspace entries removed with them), then their workspace rows
  deleted (plain-table DELETE).
- `DELETE FROM sync_tombstones` — 32 rows (no sync configured, `sync_meta` empty).
- Post-verify: 0 Active for the 8 ids; workspaces = 24 Closed + 4 Discarded (was 36);
  tombstones = 0; `memory_workspace_status` on a swept id → `unknown-workspace`;
  `PRAGMA integrity_check` = ok; live server healthy (`memory_stats` responds).

## WP5 — docs

- `docs/work/2026-08-11-ai-raccoon-diagnostic.md`: resolution section for recs 5–7
  (tool table is historical: per-tool usage is no longer recorded anywhere).
- `docs/adr/0007-propose-tier.md`: legacy-format migration note (dead `shared/{row.Path}`
  branch kept for defence).
- `docs/plans/2026-08-11-search-quality-metric-plan.md` + `docs/plans/adoption-improvement-plan.md`:
  KPI/backfill sources updated for the removed logs.

## Residual notes

- This session's already-loaded plugins (ai-raccoon provider, ai-badger hooks) keep their
  in-memory behaviour until the next session restart; the removal is effective for new
  sessions.
- The user-profile `memory-quality-logging` skill and the `ai-raccoon-pitfalls` skill still
  describe the removed logs — updated separately (skills, not repo).
- No C# server change → no version bump (consistent with docs-only PR #257).
