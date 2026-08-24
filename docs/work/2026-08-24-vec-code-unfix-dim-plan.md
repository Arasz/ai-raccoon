# Plan — vec_code becomes dimension-agnostic (generalize the VecDimensionReconciler to the code corpus)

**Date:** 2026-08-24
**Task:** vec-code-unfix-dim (**plan-only — this task finishes when the plan is ready; no implementation in this task**). Worktree: `.ai-badger/worktrees/vec_code_unfix_dim`, branch `task/vec-code-unfix-dim`, base main @ `7e141c59`.
**Status:** rev 1 (architect draft + review round 1 folded — APPROVE-WITH-CHANGES).

## 1. Objective and the reuse answer

**Objective:** allow the code corpus engine to be any embedding dimension (e.g. Salesforce/SFR-Embedding-Code-400M_R at 1024-dim, refused today) by making `vec_code` dimension-agnostic: the existing memory-bank `VecDimensionReconciler` DROP+CREATE machinery is generalized and reused for `vec_code`, the two configure-time 768 gates are removed, and the code engine's dimension is persisted in a settings row (`embedding.codeDimensions`) that activation, fingerprint-change, and server-open all reconcile against.

**Can we generalize and reuse the existing reconciler? YES.** Four pieces of evidence:

1. **The mechanism is table-agnostic.** `VecDimensionReconciler.ReconcileAsync` (`src/AiRaccoon.Infrastructure/Sqlite/VecDimensionReconciler.cs:29-59`) is a loop over a hardcoded table list (`VecTables = ["vec_entries", "vec_structure"]`, line 27) driving one DDL template — read `sqlite_master`, `DROP TABLE IF EXISTS` + `CREATE VIRTUAL TABLE … vec0(ctx TEXT, embedding float[N] distance_metric=cosine)` — with nothing in `NeedsRecreateAsync`/`RecreateAsync` (lines 61-90) specific to the memory tables. The only per-table input is the name.
2. **`vec_code` shares both properties the mechanism relies on.** The vec0 shape is identical (`MemorySchema.cs:503` — `vec0(ctx TEXT, embedding float[768] distance_metric=cosine)` — vs `:140`/`:144` for the memory tables), and the three `vec_code` triggers (`vec_code_au`/`vec_code_pending`/`vec_code_ad`, `MemorySchema.cs:505-520`) survive the DROP and bind to the recreated table by name (`tbl_name` = `code_entries`) — the exact property the reconciler's own comment cites for the memory six (`VecDimensionReconciler.cs:85-86`), and the property the code-reindex drain's `MarkCodeEmbedded` refills through (`MemorySql.MarkCodeEmbedded`, `CodeEmbedder.cs:165-169`).
3. **Reuse is the recorded design intent, not a retrofit.** The code-search plan's §3.3 states: *"The D3 dimension-reconcile machinery is documented as the extension point, NOT exercised in v1 (review F-18 / external round-1 item 3)"* (`docs/work/2026-08-21-code-search-implementation-plan.md:137-138`), and the v1 scope line repeats it (`:43-44`).
4. **A second reconciler would duplicate the DDL/dimension-regex/transaction logic** and drift from the memory copy — exactly the failure the `derive-or-delete-the-list` invariant exists to prevent.

**Reused:** `VecDimensionReconciler`'s transaction handling, `NeedsRecreate` presence/dimension logic, the `float[N]` regex, and the no-repopulate contract — parameterized by a table list and an optional caller transaction. **New (small, code-shaped):** the `embedding.codeDimensions` settings row, one reconcile entry point on `ICodeEmbedder` (mirroring `IEntryEmbedder.ReconcileVecDimensionsAsync`), the in-transaction reconcile call inside `ActivateCodeEngineAsync`, and the removal of the two 768 gates. No second reconciler.

## 2. Context / evidence (verified 2026-08-24 in the worktree)

| Fact | Evidence |
|---|---|
| Memory bank has TWO vec0 tables at `float[384]` default, reconciled at runtime, never fixed | `MemorySchema.cs:140,144`; `VecDimensionReconciler.cs` (94 lines) |
| Reconciler: one transaction (deferred `BEGIN`, upgraded at first DDL — `BeginTransactionAsync` at `:35-36`), per-table DROP+CREATE at target dim, no repopulate, six memory triggers survive by name | `VecDimensionReconciler.cs:27,35-59,79-90` |
| Reconciler registered in DI | `AppRegistrations.cs:366` |
| Memory call sites: drain Phase 1 (before first embed) + serverless open | `EntryEmbedder.cs:119-122,150-161`; `NodeRunner.cs:117-124` |
| Code corpus: ONE vec0 table `vec_code`, `float[768]` in Ddl, `ctx = project_id` directly, three triggers with the same DROP-survival property | `MemorySchema.cs:503,505-520` |
| Activation REFUSES any manifest with `Dimensions != 768` and never reconciles ("no reconcile phase needed since vec_code is a fixed float[768]") | `SqliteCodeEngineStore.cs:43-50` (comment `:90-92`); `SettingsCommands.cs:253-260`; CLI help text `CliCommandTree.cs:179` |
| Activation is ONE transaction (settings upserts + `MarkAllCodeEmbeddedPending`), atomicity pinned by an S7-style rollback test | `SqliteCodeEngineStore.cs:78-102`; `MemorySql.cs:404-405`; `CodeEngineActivationTests.cs:86-110` |
| Chunk-budget gate is orthogonal to dimensions and MUST stay | `SqliteCodeEngineStore.cs:59-68` (`CodeChunker.DefaultBudget`) |
| No persisted code dimension; memory persists `embedding.dimensions` only for remote engines | `EmbeddingSettingsKeys.cs:21`; `CodeEmbedder.cs:222-235` |
| Code reindex drain has NO reconcile anywhere in its path (the gap this plan closes) | `CodeReindexJob.cs:40-57`; `CodeEmbedder.cs:171-207` |
| Fingerprint-change path (manifest swapped on disk) runs on every maintenance poll | `CodeReindexJob.HasWorkAsync` → `ReconcileFingerprintAsync`, `CodeReindexJob.cs:42` |
| `ICodeEmbedder` consumers: `CodeReindexJob` (`AppRegistrations.cs:201`), `SqliteCodeSearchService` (`SqliteCodeSearchService.cs:26`), and `EmbedDrainService` (`EmbedDrainService.cs:40`, a hosted BackgroundService). All are server-side infrastructure — `ICodeEmbedder` never appears as a leaf-command method argument (derivation `LayeringRulesTests.cs:248-257`), so Rule 6 is not tripped by adding a member | `LayeringRulesTests.cs:211,263-266`; `AppRegistrations.cs:201,372` |
| Tests pinning the 768 refusal | `ModelSetCodeLocalTests.cs:46-59`; `CodeEngineActivationTests.cs`; `docs/features/code-corpus/code-corpus.feature:175-188`; `CodeCorpusSteps.cs:138-146` |
| Existing reconciler test surface | `VecDimensionReconcileTests.cs`, `VecDimensionReconcileWorkTests.cs`, `DrainReconcilesDimensionsFirstTests.cs`, `VecDimensionReconcileAtStartTests.cs` |
| Ddl is digest-gated; a runtime DROP is NOT healed by the next open and does NOT change the digest | `MemorySchema.cs:524-532`; `VecDimensionReconciler.cs:21-23` (ADR-0075) |
| Malformed settings fall back to constants, never crash | ADR-0083 (`SearchParameters`); precedent for the missing/`embedding.codeDimensions` default |
| v1 code-search plan named the D3 machinery as the extension point | `2026-08-21-code-search-implementation-plan.md:137-138,43-44` |

## 3. Design decisions

### O1 — How `VecDimensionReconciler` gains `vec_code`: parameterize the table list (chosen) — no second reconciler

**Decision.** Generalize the existing reconciler, additively, keeping every memory call site unchanged:

- `VecDimensionReconciler.cs:27` — one const becomes two: `MemoryVecTables = ["vec_entries", "vec_structure"]` and `CodeVecTables = ["vec_code"]`.
- `IVecDimensionReconciler` gains one generalized method; the existing 3-arg method stays and delegates:

```csharp
// Existing shape (unchanged call sites — memory tables, own transaction):
Task<bool> ReconcileAsync(SqliteConnection connection, int targetDimension, CancellationToken cancellationToken);
// Generalized shape (new):
Task<bool> ReconcileAsync(SqliteConnection connection, SqliteTransaction? transaction,
    int targetDimension, IReadOnlyCollection<string> tables, CancellationToken cancellationToken);
```

- Implementation: when `transaction` is null the reconciler begins its own transaction exactly as today and commits/rolls back; when a caller transaction is passed it runs `NeedsRecreate`/`Recreate` inside it and **never** begins, commits or rolls back — the caller owns the transaction. (Microsoft.Data.Sqlite throws on a re-`Begin` with a transaction already open, so "begin only when null" is not just cleaner, it is the only correct shape.) The 3-arg method becomes `ReconcileAsync(connection, null, targetDimension, MemoryVecTables, ct)`.
- The `CREATE` template is unchanged: `vec0(ctx TEXT, embedding float[N] distance_metric=cosine)` already matches `vec_code`'s declared shape (`MemorySchema.cs:503`).

**Rationale.** The mechanism is name-driven already (evidence #1); a table-list parameter is the smallest delta. Memory call sites (`EntryEmbedder.cs:159`, DI registration, all four reconciler test files) compile and behave identically. A separate small reconciler was rejected: it duplicates the transaction/regex/presence logic, and the two copies would drift — the repo's `derive-or-delete-the-list` invariant.

### O2 — Where the code engine's target dimension comes from: a new settings row, `embedding.codeDimensions`

**Decision.**

- New key `EmbeddingSettingsKeys.CodeDimensions = "embedding.codeDimensions"` (`EmbeddingSettingsKeys.cs`, beside `CodeModel`/`CodeEngine`).
- **Written at activation**: `ActivateCodeEngineAsync` upserts `embedding.codeDimensions = descriptor.Dimensions` in the same transaction as `codeModel`/`codeEngine` (the manifest is already in hand — no resolution needed).
- **Written at fingerprint change**: `CodeEmbedder.ReconcileFingerprintAsync`'s change branch upserts `codeDimensions` in the same transaction as the `codeEngine` fingerprint update and the invalidation. The value resolves via `embeddings.ResolveDimensions(SettingsFor(codeModel))` — note this expression's fallback is the **bundled descriptor's 384**, not 768 (`EmbeddingService.cs:221`); that fallback is unreachable in the change branch because the manifest load for `EngineFingerprint` precedes it, so the resolved value is always the manifest's dimension. State this explicitly in the implementation comment rather than relying on the reader to know.
- **Read at open**: `ICodeEmbedder.ReconcileVecCodeDimensionsAsync` reads `codeModel` (blank → no-op, mirroring `EntryEmbedder.ReconcileVecDimensionsAsync`'s "a bank with no engine is left alone", `EntryEmbedder.cs:154-157`); reads `codeDimensions`; **missing or unparseable → `CodeCorpusSchema.EmbeddingDimensions` (768)**.
- **Deleted by** `ModelCodeResetAsync` (`SettingsCommands.cs:306-314`) alongside `CodeModel`/`CodeEngine` — D2's key-hygiene precedent (`ModelResetAsync` deletes the memory `Dimensions` key, `SettingsCommands.cs:268-283`). WP4 adds a line asserting the reset deletes all three keys.

**Legacy-bank case (codeModel present, no codeDimensions row).** Default to 768, **without reading the manifest at open**. This is correct by construction: before this change, the 768 gate (`SqliteCodeEngineStore.cs:43-50`) made every existing `codeModel` 768-dimension — there is no legacy bank with a non-768 engine. Deriving from the manifest at open was rejected: it makes server start depend on a model file that may be missing/moved/corrupt, and the manifest-less default is exactly the value the Ddl block already declares. A malformed row defaults to 768 per ADR-0083's "malformed settings fall back to constants, never crash" precedent; the 15s fingerprint poll corrects the row the moment a real manifest disagrees.

### O3 — Reconcile ordering inside `ActivateCodeEngineAsync`'s transaction: BEFORE `MarkAllCodeEmbeddedPending`, in the SAME transaction

**Decision.** The activation transaction becomes (all in the one transaction, `SqliteCodeEngineStore.cs:78-102`):

1. upsert `embedding.codeModel` (existing)
2. upsert `embedding.codeEngine` (existing)
3. upsert `embedding.codeDimensions` (new, from `descriptor.Dimensions`)
4. **reconcile `vec_code`** via the new transaction-aware overload: `reconciler.ReconcileAsync(connection, transaction, descriptor.Dimensions, VecDimensionReconciler.CodeVecTables, ct)` — DROP+CREATE when missing or mismatched
5. `MarkAllCodeEmbeddedPending` (existing — the `vec_code_pending` trigger's deletes hit the already-empty recreated table as no-ops)

Commit. The fingerprint-change path (`ReconcileFingerprintAsync`, `CodeEmbedder.cs:186-204`) uses the same ordering: reconcile + `codeDimensions` upsert before `MarkAll` in its existing transaction (verified: that method already has a transaction, `CodeEmbedder.cs:189-204`).

**Why inside the transaction (not before/after, as memory's drain does).** The memory drain reconciles in its own transaction because its settings write and reconcile are separated by the outbox (ADR-0076); the code activation has no outbox — settings, index shape, and row state can and should commit atomically. If the reconcile ran in a separate transaction, a crash between the two commits would leave either (a) a 1024 engine activated against a 768 `vec_code` — the reindex job then embeds 1024-dim blobs into a `float[768]` table, every row hits `MaxEmbedAttempts` and is **abandoned** (`CodeCorpusSchema.MaxEmbedAttempts = 3`) — or (b) a 1024 `vec_code` with the old 768 settings. Single-transaction makes the crash state the old, fully-consistent bank. This extends the S7 atomicity guarantee `CodeEngineActivationTests` already pins (`CodeEngineActivationTests.cs:86-110`) to the DDL itself.

**Why before `MarkAll`, not after.** End state is identical either way (rows end pending regardless); before is chosen because it mirrors the drain's Phase-1 ordering — "bring vec0 to the new engine's dimension BEFORE the first row is [embedded/invalidated]" (`EntryEmbedder.cs:119-122`) — and because the trigger deletes during `MarkAll` then touch an empty table. The S7-extension rollback test stays honest under this ordering: the witness trigger fires on `UPDATE OF embed_state`, which is the last statement, so a forced failure genuinely rolls back the DDL.

### O4 — Reconcile-at-open wiring: extend `NodeRunner`'s existing open path (chosen) — server-only by construction

**Decision.**

- `ICodeEmbedder` gains `Task<bool> ReconcileVecCodeDimensionsAsync(SqliteConnection connection, CancellationToken cancellationToken)` — a direct parallel of `IEntryEmbedder.ReconcileVecDimensionsAsync` (`EntryEmbedder.cs:150-161`). Implementation in `CodeEmbedder` (ctor gains `IVecDimensionReconciler`): read `codeModel` → blank ⇒ `false`; resolve target (`codeDimensions` ?? 768 per O2); `reconciler.ReconcileAsync(connection, null, target, VecDimensionReconciler.CodeVecTables, ct)`. **DDL-only — it never touches row state**, mirroring the memory open reconcile.
- `NodeRunner`'s constructor (`NodeRunner.cs:20-29`) gains `ICodeEmbedder`; the open path (`:121-124`) calls it immediately after the existing `entryEmbedder.ReconcileVecDimensionsAsync(connection, ctx)`. `ICodeEmbedder` is resolvable in NodeRunner's serve graph (`AppRegistrations.cs:372` registers `ICodeEmbedder → CodeEmbedder`; `NodeRegistration.cs:32` composes NodeRunner).

**Why the open path at all** (activation is already atomic, so there is no serverless-stale state for code): create-if-missing healing — a `vec_code` that is missing or mismatched (manual drop, a crashed legacy flow, hand-edited bank) is healed at every server open, the same guarantee memory's D3-at-open provides; and symmetry with the memory path keeps exactly one narrative ("reconcile at open, reconcile at activation, reconcile on fingerprint change").

**Why `NodeRunner` and not a code-engine-store open hook.** Rule 6 (`LayeringRulesTests.cs:211`, detector `:263-266`) forbids a Reconcile/vec-DDL-shaped member on any port reachable from a CLI command — and `ICodeEngineStore` IS CLI-reachable (`SettingsCommands.ModelSetCodeLocalAsync` takes it, `SettingsCommands.cs:211`). A public `Reconcile…` member on the store would trip the gate; an internal one would be unreachable from the server's DI graph cleanly. `ICodeEmbedder` is infrastructure-only (consumers: `CodeReindexJob`, `SqliteCodeSearchService`, `EmbedDrainService` — none are leaf CLI commands; it never appears as a leaf-command method argument), so adding the member there is safe, and `NodeRunner` is the sanctioned server path (its own comment: "Server-only by construction (`cli-asks-the-server-acts`): NodeRunner is the one path that becomes the server", `NodeRunner.cs:119-120`).

### O5 — Fate of `CodeCorpusSchema.EmbeddingDimensions` and the Ddl block's `float[768]`

**Decision.** Both stay, re-documented as the *default* rather than the *only* dimension:

- `MemorySchema.cs:503` keeps `float[768]` in the Ddl block → **no digest change** (`ComputeSchemaDigest` derives from `Ddl`, `MemorySchema.cs:524-532`), **no `CurrentVersion` bump, no ladder step** — exactly how memory works: the Ddl declares 384, the reconciler moves the table at runtime, and the digest-gated Ddl never undoes a runtime reconcile (ADR-0075; `VecDimensionReconciler.cs:21-23`). Fresh banks are created at 768; existing banks reconcile only when a different-dimension engine activates.
- `CodeCorpusSchema.EmbeddingDimensions = 768` stays (it is still the fresh-bank/legacy default, the O2 missing-row default, and the BDD fixture selector `CodeCorpusSteps.cs:143-145`); its doc comment changes from "the only gate protecting vec_code" to "the dimension a fresh code corpus is created at and legacy banks default to". It stops being a refusal input.

**Confirmed:** no digest change means existing banks are not forced through the Ddl block on next open, and a runtime reconcile to 1024 persists across opens.

### O6 — Removal of both 768 gates

**Decision.**

- `SqliteCodeEngineStore.cs:43-50` — delete the dimensions refusal block and the `:25-29`/`:90-92` comment claims that protect a "fixed 768-dimension index". **The refusal becomes: nothing — activation accepts any declared dimension and reconciles `vec_code` in the same transaction.** `CodeEngineActivationRefusedException` remains for its other two legs: manifest load failure (`:38-41`) and the chunk-budget gate (`:59-68`), which is orthogonal to dimensions and **stays** (a narrower-than-510-token window still silently truncates every chunk; that defect is unchanged by dimensionality).
- `SettingsCommands.ActivateCodeDirectoryAsync:253-260` — delete the pre-flight dims refusal. The manifest pre-flight load stays (missing-manifest refusal with the loader's own message); the doc comment at `ModelSetCodeLocalAsync:205-210` is rewritten.
- **CLI help text** `CliCommandTree.cs:179` — rewrite `"Refuses a manifest whose dimensions are not 768: vec_code is fixed float[768]."` (the `model code set local` description) to state that any manifest dimension is accepted and `vec_code` is reconciled to it.
- The HTTP settings endpoint (`SettingsEndpoint.MapSettings`) needs no change: it calls `ActivateCodeEngineAsync` directly and inherits the new behavior.

### O7 — Test plan (design-tests discipline: each test names its failure mode and the red-proof mutation)

**Reconciler level** — `tests/AiRaccoon.Tests/Integration/Storage/VecDimensionReconcileTests.cs` (extended):

| Test | Failure mode it targets | Red-proof mutation | Oracle |
|---|---|---|---|
| `ReconcileAsync_CodeTables_TargetDiffers_RecreatesVecCodeOnly` | The code tables are forgotten or the memory tables are touched — vec_code stays 768 while vec_entries moves | Change `CodeVecTables` to `["vec_entries"]`; or swap the consts | Hand-derived from the DDL template: `vec_code` DDL contains `float[1024]`, `vec_entries`/`vec_structure` DDL unchanged |
| `ReconcileAsync_CodeTables_TargetMatches_IsANoOp` | Unconditional DROP+CREATE on a matching dimension loses populated rows | Delete the `NeedsRecreateAsync` dimension check | Hand-derived: `changed == false`, `vec_code` DDL still `float[768]` |
| `ReconcileAsync_CodeTables_TableMissing_CreatesIt` | Presence inferred from a dimension read (the 384-for-missing trap `VecDimensionReconciler.cs:68-72` comment warns about) | Replace the explicit `sql is null` presence check with a dimension-only comparison | Hand-derived: dropped `vec_code` is recreated at the target dim |
| `ReconcileAsync_CodeTables_InCallersTransaction_DoesNotCommitItself` | The reconcile begins its own transaction and commits independently — outer rollback no longer undoes the DDL (the atomicity O3 depends on) | Make the transaction-aware overload begin its own transaction (nested `BEGIN` throws inside `ActivateCodeEngineAsync`; here: pass a transaction, then roll it back and observe the table still changed) | Hand-derived: after `RollbackAsync`, `vec_code` DDL still `float[768]` |
| Existing four memory tests | Memory regressions | — (must stay green unchanged) | Existing |

**Activation level** — `tests/AiRaccoon.Tests/Integration/Embedding/CodeEngineActivationTests.cs` (extended; `SqliteCodeEngineStore` is constructed directly at `:44,:135,:173` — all three sites gain the reconciler param):

| Test | Failure mode | Red-proof mutation | Oracle |
|---|---|---|---|
| `ActivateCodeEngineAsync_Non768Manifest_ReconcilesVecCodeAndWritesCodeDimensions` | The reconcile is skipped — a 1024 engine activates against a 768 `vec_code` and the reindex job wedges (rows abandoned after `MaxEmbedAttempts`) | Delete the reconcile call from the activation transaction | Hand-derived: `vec_code` DDL contains `float[1024]`, `embedding.codeDimensions` row == `1024`, rows `pending`, `vec_code` empty |
| `ActivateCodeEngineAsync_768Manifest_NoDdlAndWritesCodeDimensions` | A matching-dimension activation drops the populated table | Reconcile unconditionally without the dimension check | Hand-derived: `vec_code` stays `float[768]`, `codeDimensions` row == `768` |
| `ActivateCodeEngineAsync_ReconcileFailure_RollsBackSettingsAndDdl` (S7 extension) | The reconcile runs OUTSIDE the activation transaction — its DDL survives the rollback (the exact O3 decision) | Move the reconcile call before `BeginTransactionAsync` (its own committed tx) | Hand-derived: force the last statement to fail via the existing witness-trigger technique (`CodeEngineActivationTests.cs:101-108`); assert `codeModel`/`codeEngine`/`codeDimensions` rows absent AND `vec_code` DDL still `float[768]` |
| Existing `…_RollsBackTheEarlierSettingsWritesToo` | — (must stay green) | — | Existing |

**Fingerprint path** — `tests/AiRaccoon.Tests/Integration/Embedding/CodeEmbedderTests.cs` (extended; constructors gain the reconciler param):

| Test | Failure mode | Red-proof mutation | Oracle |
|---|---|---|---|
| `ReconcileFingerprintAsync_ManifestDimensionsChangedInPlace_ReconcilesVecCodeAndUpdatesCodeDimensions` | A manifest swapped on disk (new dims, same `codeModel` path) invalidates rows but leaves `vec_code` at 768 — the drain re-embeds 1024-dim blobs into a 768 table | Remove the reconcile + `codeDimensions` upsert from the change branch | Hand-derived: fingerprint differs ⇒ `vec_code` DDL `float[1024]`, `codeDimensions` == `1024`, rows `pending` |
| `ReconcileFingerprintAsync_UnchangedFingerprint_PerformsNoDdl` | The reconcile runs on every 15s poll — DDL on a match drops the populated table | Call the reconcile unconditionally in `ReconcileFingerprintAsync` | Hand-derived: seeded `vec_code` row survives, DDL `float[768]` |

**Open path** — `tests/AiRaccoon.Tests/Integration/Setup/Serve/VecDimensionReconcileAtStartTests.cs` (extended with two code scenarios + code seeding helpers; the memory scenarios are untouched):

| Test | Failure mode | Red-proof mutation | Oracle |
|---|---|---|---|
| `AServerlessCodeActivationChangingDimensions_IsReconciledBeforeTheFirstToolCall` | The open path forgets `vec_code` — a bank activated without a server (or hand-seeded) serves with a stale-dim table | Remove the `codeEmbedder.ReconcileVecCodeDimensionsAsync` call from `NodeRunner.cs:121-124` | Hand-derived: seed `codeModel`+`codeEngine`+`codeDimensions=1024` + embedded row, then `serve`; `vec_code` DDL contains `float[1024]` |
| `ALegacyCodeBankWithoutCodeDimensionsRow_DefaultsTo768AndPerformsNoDdl` | The missing-row default throws (e.g. `int.Parse` on null) or resolves wrong — serve fails or drops the populated table | Change the missing-row default to `0` / remove the `TryParse` | Hand-derived: `codeModel` set, no `codeDimensions` row, seeded `vec_code` row at 768; after `serve`, row survives and DDL is `float[768]` |
| `CliReachablePorts_ExposeNoReconcileOrVecDdlMember` | The new `ICodeEmbedder.Reconcile…` member leaks onto a CLI-reachable port (Rule 6) | (negative control — no mutation; must stay green as-is) | Existing derivation — **this is the real control for the new member**; `NoLeafCommandTypeOtherThanServe_HoldsALiveEntryEmbedderAsAConstructedField` scans for `IEntryEmbedder` only and cannot see a NodeRunner→ICodeEmbedder injection, so it is NOT a control for this change (kept green, no claims made from it) |

**CLI + BDD** — `tests/AiRaccoon.Tests/Unit/Setup/ModelSetCodeLocalTests.cs` and the BDD suite:

| Test / scenario | Failure mode | Red-proof mutation | Oracle |
|---|---|---|---|
| `ModelSetCodeLocal_Non768Manifest_RefusesBeforeActivation…` → **FLIP to** `…_Non768Manifest_ActivatesAndReconciles` | The old gate survives in either copy (CLI or store) — a 1024 manifest still refuses | Re-add the `Dimensions != CodeCorpusSchema.EmbeddingDimensions` check to `ActivateCodeDirectoryAsync` | Hand-derived: exit 0, `store.CodeActivated` set |
| `ModelSetCodeLocal_DirectoryWithoutManifest_RefusesWithTheLoadersOwnMessage` | Gate removal accidentally deletes the manifest pre-flight | Delete the loader call in `ActivateCodeDirectoryAsync` | Hand-derived: missing-manifest refusal names `EmbeddingManifest.FileName`, nothing activated (unchanged) |
| `ModelSetCodeLocal_Valid768Manifest_ActivatesTheCodeEngine` | — (must stay green) | — | Existing |
| `ModelCodeReset_DeletesCodeDimensionsRow` (new) | Code reset leaves `embedding.codeDimensions` behind — the next open reconciles to a stale dim | Drop the `codeDimensions` delete from `ModelCodeResetAsync` | Hand-derived: after reset, all three of `codeModel`/`codeEngine`/`codeDimensions` absent |
| Feature `Rule: The code corpus's vector index accepts only 768-dimension manifests` → **flip both scenarios**: "…accepts any-dimension manifest and reconciles vec_code" (Given 1024 manifest ⇒ engine activated AND `vec_code` declares 1024) / "accepts a 768-dimension manifest" (unchanged) | The behavioral contract still promises a refusal | Keep the old feature text | Spec: this plan §3; steps in `CodeCorpusSteps.cs` (`SeedManifestDirectory(dir, 1024)` already exists at `:138-146`) |

**Mechanical member additions** (compile-break surface, enumerated so no lane discovers them by CI failure):

- `ICodeEmbedder` fakes gaining the new member: `TestHelpers/FakeCodeEmbedder.cs:12`, `EmbedDrainServiceTests.cs:343` (RecordingCodeEmbedder), `EmbedDrainContinuousTests.cs:156` (SequencedCodeEmbedder), `EmbedDrainContinuousTests.cs:190`, `EmbedDrainMetricsTests.cs:137` (StubCodeEmbedder).
- `new CodeEmbedder(...)` sites (ctor gains `IVecDimensionReconciler`): `CodeCorpusFeatureContext.cs:60`, `CodeReindexJobTests.cs` (all sites), `RowBudgetTests.cs:141`, `CodeEmbedderPoisonLoggingTests.cs:46,65,89`.
- `new SqliteCodeEngineStore(...)` sites (ctor gains `IVecDimensionReconciler`): `CodeEngineActivationTests.cs:44,135,173`; `CodeReindexJobTests.cs:107,140,166,195,250`; `CodeCorpusFeatureContext.cs:65-67`.
- **Seeding helper:** `TestData.SeedCodeManifestDirectory(string dir)` has no dimensions parameter (`TestData.cs:337-348`); only `CodeCorpusSteps.SeedManifestDirectory(dir, int)` does. WP3 needs a dims-parameterized `TestData` variant (or a fixture copy) for the 1024-dim open/fingerprint scenarios.

**Statement-count traces: deliberately omitted.** The S7-extension rollback test observes the same property the trace would assert — the reconcile's DDL lives inside the activation transaction — behaviorally (a separately-committed reconcile survives the rollback). A statement-count assertion would pin the mechanism rather than the contract (ADR-0065's lesson); `measure-when-it-pays`: the rollback test is cheaper and strictly stronger.

### O8 — Docs + release

**Doc-comment sites (rewrite the "fixed 768 / no reconcile phase" claims):** `CodeCorpusSchema.cs:3-8`, `SqliteCodeEngineStore.cs:25-29,90-92`, `SettingsCommands.cs:205-210`, `VecDimensionReconciler.cs:8-14` (interface doc names the table list now), `SettingsProtocol.cs:37` ("validated (manifest present, 768 dims) by the CLI" → manifest present only), `ICodeEngineStore.cs:17` ("the 768-dim refusal happens before this is called" → drop), `CodeEngineActivationTests.cs:17-31`, `ModelSetCodeLocalTests.cs:11-17`, `VecDimensionReconcileAtStartTests.cs:23-29` (unchanged but verify), `NodeRunner.cs:117-120` (extend to name both corpora), `CliCommandTree.cs:179` (help text, WP4).

**`.md` files:** `docs/reference/agent-memory-server.md` (vec_code section, ~`:185-193` — "fixed `float[768]` index with no dimension-reconcile phase" → "reconciled like the memory bank; `embedding.codeDimensions` row"), `docs/how-to/configure-embedding-engines.md` (vec_code section, ~`:175-179`), `docs/how-to/search-the-code-corpus.md` (`:13` mentions "187 MB, 768-dim" — still factually true as a default-model fact; verify no other 768-claim rows), `docs/explanation/architecture.md` (`:203` — "`vec_code` — vec0 virtual table, `float[768]` (`code-daemon-embed-v1`'s dimension…" → re-document as the fresh-bank default), `docs/features/code-corpus/code-corpus.feature` (header `:11-14` + the flipped Rule), `docs/work/README.md` (add this plan to the Active records table — the repo's doc-drift gate requires every doc named in a README), root `README.md` "## What's new" entry (per `whats-new-update` skill: compact, user-facing — "code corpus supports any embedding dimension").

**ADR.** **New ADR-0093** — "vec_code is dimension-agnostic through the shared D3 reconciler" (next number after 0092; the README index `docs/adr/README.md` row is enforced by `AdrIndexTests`). The record **amends**: ADR-0084 (the D3 reconcile now also covers `vec_code`), ADR-0085 (the `vec_code float[768]` corpus shape becomes a default, not a fixed contract), ADR-0087 (the configure-transaction now also reconciles), ADR-0088 (the sentence *"Non-768 manifests are refused … the only dimension gate"* is reversed). ADRs themselves are immutable — the amendment is recorded in 0093, not edited into the old files.

**Release.** `VERSION` 1.34.4 → **1.35.0** (`scripts/version-bump.py minor` — user-facing feature per the `version-bump` skill), `VersionContractTests` gate, README What's new entry.

### O9 — Parallelism

| Lane | Files | Depends on |
|---|---|---|
| WP1 — reconciler generalization + settings key (foundation) | `VecDimensionReconciler.cs`, `EmbeddingSettingsKeys.cs`, `VecDimensionReconcileTests.cs` | — |
| WP2 — activation reconcile + store ctor | `SqliteCodeEngineStore.cs`, `CodeEngineActivationTests.cs` (all three sites), `CodeCorpusFeatureContext.cs` (**wholly in WP2** — it touches both the store ctor `:65-67` and the embedder ctor `:60`), the `new SqliteCodeEngineStore(...)` sites in `CodeReindexJobTests.cs:107,140,166,195,250` (their shape WP2 defines) | WP1 |
| WP3 — open + fingerprint paths | `ICodeEmbedder.cs`, `CodeEmbedder.cs`, `NodeRunner.cs`, `VecDimensionReconcileAtStartTests.cs`, `CodeEmbedderTests.cs`, `RowBudgetTests.cs`, `CodeEmbedderPoisonLoggingTests.cs`, the five `ICodeEmbedder` fakes, the `new CodeEmbedder(...)` sites in `CodeReindexJobTests.cs` | WP1 + **WP2's shape** (CodeReindexJobTests store-ctor edits land with WP2; the embedder-ctor edits in the same file serialize after) — WP3 runs parallel to WP2 only after WP2's ctor shape is fixed |
| WP4 — gate removal + CLI + BDD | `SettingsCommands.cs`, `CliCommandTree.cs` (help text), `ModelSetCodeLocalTests.cs`, `CodeCorpusSteps.cs`, `code-corpus.feature` | WP2 (the CLI gate removal is only safe once the store reconciles) — can start BDD edits once WP2's shape is fixed |
| WP5 — docs + ADR + version | the `.md` list in O8, `docs/adr/0093-*.md`, `docs/adr/README.md`, `VERSION`, `README.md` | WP2/WP3 shapes fixed (doc drafting may overlap WP4 — all decisions are fixed by this plan) |

## 4. Work packages (acceptance criteria + quality gate)

### WP1 — Generalize `VecDimensionReconciler` (table list + optional transaction)

- Split `VecTables` into `MemoryVecTables`/`CodeVecTables`; add the transaction-aware tables overload; 3-arg method delegates; add `EmbeddingSettingsKeys.CodeDimensions`.
- **Acceptance criteria:** (a) every existing `IVecDimensionReconciler` call site compiles unmodified; (b) the four existing `VecDimensionReconcileTests` pass unchanged; (c) the four new reconciler tests in O7 pass with their red-proofs witnessed.
- **Quality gate:** `dotnet build` clean; `dotnet test --filter "FullyQualifiedName~VecDimensionReconcileTests"` green with the new tests counted (> 0) and each new test's red-proof run pasted per the `design-tests` skill.

### WP2 — Activation reconciles `vec_code` in-transaction

- `SqliteCodeEngineStore`: ctor + `IVecDimensionReconciler`; delete the dims refusal; upsert `codeDimensions`; reconcile before `MarkAll`; rewrite comments. Update all nine `new SqliteCodeEngineStore(...)` sites + `CodeCorpusFeatureContext.cs`.
- **Acceptance criteria:** (a) a 1024 manifest activates with `vec_code` at `float[1024]`, `embedding.codeDimensions == "1024"`, rows pending, `vec_code` empty; (b) a 768 manifest activates with no DDL; (c) the S7-extension rollback test proves reconcile + settings roll back together; (d) the chunk-budget gate still refuses a narrow-window manifest.
- **Quality gate:** `dotnet test --filter "FullyQualifiedName~CodeEngineActivationTests"` green; red-proof runs on the new tests' mutations.

### WP3 — Open + fingerprint paths reconcile `vec_code`

- `ICodeEmbedder.ReconcileVecCodeDimensionsAsync`; `CodeEmbedder` ctor + reconciler, open-reconcile implementation, fingerprint-change branch reconcile; `NodeRunner` ctor + call; all five `ICodeEmbedder` fakes + all `new CodeEmbedder(...)` sites gain the member/param; add the dims-parameterized `TestData` manifest seeder.
- **Acceptance criteria:** (a) a serverless-seeded 1024 code bank is reconciled at serve open (AtStart code scenario); (b) a legacy bank (no `codeDimensions` row) no-ops with its populated `vec_code` intact; (c) an in-place manifest dimension swap reconciles via the fingerprint poll; (d) an unchanged fingerprint performs no DDL; (e) `CliReachablePorts_ExposeNoReconcileOrVecDdlMember` stays green.
- **Quality gate:** `dotnet test --filter "FullyQualifiedName~VecDimensionReconcileAtStartTests|FullyQualifiedName~CodeEmbedderTests|FullyQualifiedName~CodeReindexJobTests|FullyQualifiedName~LayeringRulesTests"` green with red-proofs.

### WP4 — Remove both 768 gates; flip CLI + BDD contracts

- Delete the two refusal blocks; rewrite the `model code set local` help text (`CliCommandTree.cs:179`); add the code-reset key-deletion assertion; update doc comments; flip `ModelSetCodeLocalTests` + the feature Rule/scenarios.
- **Acceptance criteria:** (a) `model set code local <1024-manifest>` exits 0 and activates; (b) missing-manifest refusal unchanged; (c) BDD suite green with the flipped scenarios; (d) the chunk-budget refusal text still names `CodeChunker.DefaultBudget`; (e) `ModelCodeReset` deletes `codeModel`/`codeEngine`/`codeDimensions` together.
- **Quality gate:** `dotnet test --filter "FullyQualifiedName~ModelSetCodeLocalTests"` + the Reqnroll BDD run green; build clean.

### WP5 — Docs, ADR-0093, version

- Update the O8 `.md`/doc-comment sites; write ADR-0093 + README index row; `scripts/version-bump.py minor` (1.34.4 → 1.35.0); What's new entry.
- **Acceptance criteria:** (a) no **non-ADR prose or doc comment** anywhere still claims a fixed `float[768]` or a 768 **refusal** — grep `fixed float\[768\]|refuses a non-768|only dimension gate|no dimension-reconcile phase` over `docs/` (excluding `docs/adr/` bodies and index) and `src/` doc comments; factual default-model mentions (e.g. "187 MB, 768-dim") and ADR bodies (immutable; amended only in 0093) are allowed and expected; (b) `AdrIndexTests` green; (c) `VersionContractTests` green including the packed `.mcp/server.json`; (d) the docs drift gate (docs.ts scan; every doc named in a README — `docs/work/README.md` lists this plan) passes.
- **Quality gate:** the three named test classes green + the repo's docs drift gate.

## 5. Out of scope / deferred

- **Remote code embedding engines** (openai for code): the code corpus stays local-manifest-only (§3.3 v1 scope); `embedding.codeDimensions` has no remote branch.
- **Default model change:** 768 stays the fresh-bank default; no default-engine flip.
- **Schema versioning:** no digest change, no `CurrentVersion` bump, no ladder step (O5).
- **Row-state repair on open-reconcile:** the open path is DDL-only; it does not mark rows pending when it recreates `vec_code` (mirrors memory D3). A changed-dim open leaves embedded rows with stale blobs and an empty `vec_code` → FTS-only until the next activation/fingerprint change (see Risks).
- **Memory engine behavior:** untouched — memory call sites and semantics are unchanged.
- **Sync/sweep/TTL for the code corpus:** unchanged (ADR-0085 lifecycle boundaries).

## 6. Risks and mitigations

| Risk | Assessment | Mitigation |
|---|---|---|
| Crash mid-reconcile leaves `vec_code` dropped | **Does not occur.** The DROP+CREATE run inside the same transaction in every path (deferred `BEGIN`, upgraded at first DDL — still fully atomic); a kill-9 mid-transaction rolls back to the pre-transaction table. In the activation path the whole sequence (settings + DDL + invalidation) is one transaction, so the crash state is the old, fully-consistent bank — strictly stronger than memory's D3 (whose reconcile is its own transaction, with the drain's open migration providing the retry). Post-commit is always consistent. | Transactional design (O3); S7-extension rollback test. |
| Open-reconcile drops `vec_code` on a dim change while rows are still `embedded` → empty `vec_code`, rows not re-driven | Narrow, degraded-but-not-wedged: this state can only be reached by an inconsistent bank (no `codeDimensions` row + mismatched table), not by any supported flow (activation is atomic; fingerprint changes mark rows pending in the same transaction). Search degrades to FTS-only per the existing feature contract; the next activation or fingerprint change heals it. Memory has the identical property (DDL-only open reconcile). | Documented in ADR-0093; the FTS-degrade contract (`code-corpus.feature`) already covers it. Rejected alternative: marking rows pending from the open reconcile — breaks the "reconcile is DDL-only" mirror with memory and would re-embed a corpus the user never asked to re-embed. |
| Missing/malformed `embedding.codeDimensions` row defaults to 768 while the manifest says otherwise | Only reachable by hand-editing or a pre-change bank; search then errors actionably (`CodeEngineUnloadableException` naming the engine) until re-activation; the 15s fingerprint poll self-heals in a live host. | ADR-0083 fallback precedent; O2 default rule; WP3 legacy-bank test. |
| Reconcile/vec-DDL member leaks onto a CLI-reachable port | `ICodeEmbedder` is infrastructure-only today (consumers: `CodeReindexJob`, `SqliteCodeSearchService`, `EmbedDrainService`); Rule 6 derives ports dynamically, so any future CLI use is caught at test time. | Layering guard tests stay green (WP3 acceptance e); ADR-0093 records the constraint. |
| Concurrent sessions: two processes activate different-dim engines | Last-writer-wins on the settings rows; rows end pending; `vec_code` reconciles to the final dim at each activation; the fingerprint poll + open-reconcile converge. Same concurrency semantics as code today (no outbox — ADR-0087's existing property); no new race is introduced because every mutation is a single transaction. | Covered by existing activation/invalidation tests; no new locking. |
| The fingerprint poll loads the manifest every 15s (now also reconciles) | The manifest load already happens today (`EngineFingerprint`); the added reconcile runs **only in the change branch** — an unchanged fingerprint performs zero DDL (WP3 test). | `ReconcileFingerprintAsync_UnchangedFingerprint_PerformsNoDdl`. |

## 7. Amendments

**Round 1 (plan review, 2026-08-24, verdict APPROVE-WITH-CHANGES — all MUSTs folded):**

- M1 — §2/O4 now name `EmbedDrainService` as a third `ICodeEmbedder` consumer and why the layering conclusion is unchanged.
- M2 — O7 gains the enumerated five `ICodeEmbedder` fakes under "Mechanical member additions".
- M3 — O7 gains `RowBudgetTests.cs:141` + `CodeEmbedderPoisonLoggingTests.cs:46,65,89` under "Mechanical member additions".
- M4 — O7/WP2 now enumerate all nine `new SqliteCodeEngineStore(...)` sites.
- M5 — O9 no longer claims WP2 ‖ WP3 disjoint: `CodeCorpusFeatureContext.cs` moves wholly into WP2; WP3 depends on WP2's ctor shape.
- M6 — WP4 gains the `CliCommandTree.cs:179` help-text rewrite.
- M7 — O8 gains `SettingsProtocol.cs:37` + `ICodeEngineStore.cs:17`.
- M8 — WP5 acceptance (a) scoped to non-ADR prose + gate-claims; ADR bodies/index and factual default-model sentences excluded.
- S9 — O8 gains `docs/explanation/architecture.md:203`.
- S10 — O8 drops the phantom troubleshooting-table row for search-the-code-corpus.md; keeps the `:13` factual check.
- S11 — §2/§6 reworded "BEGIN IMMEDIATE" → "one transaction (deferred BEGIN, upgraded at first DDL)".
- S12 — O2 states the fingerprint expression's 384 fallback explicitly and why it is unreachable in the change branch.
- I13 — O7 open-path row now states only `CliReachablePorts_ExposeNoReconcileOrVecDdlMember` is a real control for the new member.
- I16 — O7 gains the dims-parameterized `TestData` seeder requirement.
- I18 — WP4 gains the `ModelCodeReset` three-key deletion assertion + test.

**Confirmed by review, no change:** I14 (trace omission sound), I15 (no doctor/MCP/checklist 768 claims), I17 (rollback test honest under O3 ordering).
