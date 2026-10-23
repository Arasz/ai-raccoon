# Review of 2.0.0, 2.0.1 and 2.1.0

Date: 2026-10-23. Range: `v1.63.0..v2.1.0` (#909 tool rename, #914 unknown-tool guidance,
#913 typed entry search and memory-only TTL). Three read-only lanes (data model, architecture
and code quality, QA with mutation testing) plus a manual pass over the live bank.

## Live bank state (installed 2.1.0+11219f86, `~/.ai-raccoon/memory.db`, read-only)

| Check | Result |
|---|---|
| `doctor` | HEALTHY, user_version 22, 0 pending rows, 0 entries without a kind |
| `entries.entry_type` DDL | `TEXT NOT NULL DEFAULT 'none' CHECK(entry_type IN ('none','memory','file'))` |
| Kind distribution | file 23,323 (all `scope=project`), memory 2,377, none 0, unknown 0 |
| Shape rule (`file` iff `path = source_file`) | holds on every row |
| `vec_entries` / `vec_structure` vs `entries` (one read snapshot) | per-type counts equal, 0 orphans either way, 0 `type` ≠ `entry_type` |
| vec `ctx` vs `MemorySql.ContextKeyExpression` | 0 mismatches |
| `entries_fts` | 25,700 = `entries` |
| `vec_code` vs embedded `code_entries` | 72,560 = 72,560, 0 orphans |
| `ttl_days` set | 0 rows of any kind |
| Memory rows written since 2.1.0 install | 0 — every kind on the bank came from the v20 backfill |

Pre-existing, not 2.x: 36 `Active` workspaces holding 15 unconsolidated rows (newest
2026-09-28); 24 rows in a `custom` context literally labelled `project` (ai-badger, jsaa);
98 promotions waiting, oldest about 69 days.

Counts taken by separate queries drift while the watch re-ingests: compare in one transaction.

## Tests

- Changed-class run at v2.1.0: 1883 total, 1880 passed, **2 failed**, 1 skipped. BDD 187/192
  passed, 5 skipped.
- Both failures are `Speed=Nightly` and were red since the release that broke them; the
  `build-nightly-gates` job runs only on the `run-nightly-gates` label or a manual dispatch,
  and none of the three release PRs carried it:
  - `ProxyForwardTests.BackendError_DoesNotReacquire` still expects an unknown tool to throw
    a protocol exception (2.0.1 made it an `IsError` result).
  - `BackendLaunchIdentityProofE2ETests.FullFlow_ProvenAttach_…` calls `get_entry` without
    the `entryType` 2.1.0 made required.
- Mutation testing: 19 of 24 production mutations went red. Survivors: the doctor CLI's kind
  lines (hard-coded `0` passes), `EntryTypeParameter.Single(None)`, the v20 NULL backfill
  (redundant with v21's), and `get_entry`'s declared-kind re-check (redundant with the SQL
  filter).

## Findings fixed in this change

| Finding | Source |
|---|---|
| Two red Nightly tests | QA |
| `query` hits carry no kind, while `get_entry` requires one | data model |
| `set_memory_ttl` cannot clear a historical TTL on a File row, which the sweep still honours | data model |
| `entryType=memory,file` applies a type filter and drops unclassified rows, contrary to ADR-0141 | data model, architecture |
| A TTL refused for kind reads as `unknown-hash` | data model |
| The reserved project name `shared` is refused only in the MCP tool, not in the registry | architecture |
| `EntryTools` takes its classifier as an optional constructor parameter | data model, architecture, QA |
| `get_quality_report` defaults its window from the wall clock in the tool | architecture |
| Kind literals hand-spelled beside `EntryKindWireNames` | data model, architecture |
| Sed-rename artifacts in doc comments | architecture |
| Doctor CLI kind lines, `Single(None)`, sweep of a historical File TTL: untested | QA |
| Tautological `TheQueryParameterTypes_AreFlags`; env-gated probes report passed instead of skipped | QA |

## Deferred (structural; each needs its own decision)

- `IEntryKindClassifier`: a 6-method interface over pure functions with one implementation,
  injected into about 9 types. A static class beside `EntryKindWireNames` would do.
- Memory-only TTL is enforced at about 5 write paths; a schema CHECK or trigger plus one doctor
  count would hold it once.
- The entry-type selection travels in `SearchQuery.EntryTypes` and a separate dispatcher
  argument and is validated three times.
- `bool preserveMemoryMetadata` is threaded through 7 signatures.
- `GetEntryResult.EntryType` names the corpus while the input `entryType` names the kind
  (renaming the field is breaking).
- No trigger moves a vector when a row's `entry_type` changes in place (latent: only
  migrations update it today).
- Workspace consolidation re-declares every kept row as `memory`.
- Dev banks stamped v20/v21 by unreleased builds may lack the CHECK; the doctor cannot see it.
- Nightly E2E is the only consumer-level check of the tool contract and never runs on a PR
  unless labelled.
