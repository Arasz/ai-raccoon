# Code-reviewer findings — combined code-corpus implementation plan (2026-08-21)

**Reviewer:** code-reviewer lane (independent of the architect/engineer/QA/ops authors)
**Scope:** gate honesty, test honesty, safety, consistency, security/privacy
**Sources reviewed:**
- Combined plan: `docs/work/2026-08-21-code-search-implementation-plan.md` (cited as **P**)
- Architecture lane: `docs/work/2026-08-21-code-search-moe-architecture.md` (cited as **A**)
- Engineer lane: `docs/work/2026-08-21-code-search-moe-engineer.md` (cited as **E**)
- QA lane: `docs/work/2026-08-21-code-search-moe-qa.md` (cited as **Q**)
- Ops lane: `docs/work/2026-08-21-code-search-moe-ops.md` (cited as **O**)
- Exploration: `docs/work/2026-08-21-code-search-exploration.md` (cited as **X**)
- Engine plan: `docs/work/2026-08-21-arbitrary-embedding-models-plan.md` (embedding-model-support worktree; cited as **M**)
- Code verified in the plan worktree (`src/…`), paths relative to the worktree root.

Severity: **MUST-FIX** (blocks honest implementation or is unsafe as written) / **SHOULD-FIX** (correctness or honesty gap, implementable around) / **INFO** (document, decide, or accept).

---

## MUST-FIX

### 1. Envelope memory-section key is "results" in the combined plan but "memory" in the QA test contract
The authoritative contract (P §3.6) pins `kind=both` → `{ results: [...memory...], code: [...] }` ("both keys always present"), and A D-I declares `CombinedSearchResultList(Results, Code, Warning)` — the memory section serializes under key `results`. The QA catalog pins the key `memory`: Q WP6-T02 GREEN "`memory` key present and empty", Q §6 G2 "Always both keys (`memory: [], code: []`)", Q WP6-T09, and the exploration's envelope (X §Q3, §5.4 `{ memory: [...], code: [...] }`).
- **Why it matters:** implementers follow the QA catalog for the TDD cases; WP6-T02/T09 as written fail against the combined plan's wire shape (false-fail tests), or the implementation silently ships the `memory` key against the authoritative contract.
- **Fix:** pick one key. Recommend `results` (matches the C# record name and P §3.6); rewrite Q WP6-T02/T09, Q §6 G2, and the BDD scenario table (Q §4) to `{ results: [], code: [...] }`. Also pin where `warning` sits in the `kind=both` envelope (P §3.6 omits it; A D-I keeps a single `Warning`).

### 2. "kind=memory byte-identical" contradicts the architect lane's "always-present code key"
P §3.6 + P §7-2 (ops wins): optional `Code` section serialized **only** for `kind=code|both`; `kind=memory` stays byte-identical (regression-pinned, RG-01/Q WP6-T01). A D-I states the opposite: "the declared return type becomes `CombinedSearchResultList` whose serialized shape for `kind=memory` is byte-compatible with today's `SearchResultList` **except for the added `code` key**", "always present, empty when kind != code|both", and A WP-D's gate says "kind=memory response byte-identical to pre-change **except additive `code: []`**".
- **Why it matters:** an implementer reading A WP-D adds the empty `code` key → the byte-identical golden guards (RG-01, Q WP6-T01, existing GoldenFileTests) fail; the two authoritative documents demand incompatible wire shapes.
- **Fix:** delete the "always present" and "except additive `code: []`" language from A D-I and A WP-D; keep P §7-2's resolution; add a serializer note that `code` is omitted for `kind=memory`.

### 3. Eval floor "pinned from first measurement" is self-referential and gameable; the cited precedent says the opposite
P §3.9 and A D-L: "Acceptance floor: mean nDCG@5 ≥ 0.50, **pinned from first measurement**, then witnessed RED against a deliberately bad arm (ADR-0079/0081 precedent)". Problems:
- If the floor is fixed from the gated arm's own first measurement, the arm trivially clears it on re-run — the gate reduces to "first measurement ≥ 0.50" and the RED witness only proves the harness can score a bad arm low, not that 0.50 means anything. Nothing in P/A defines the bad arm's required failure margin, or makes the bad-arm RED a gate-*failure* condition (P §5 WP8 gate says "witnessed", i.e., recorded).
- ADR-0079/0081 (verified: `docs/adr/0079-…md`, `docs/adr/0081-…md`) contain no pin-then-witness floor procedure at all — they build a corpus and record measurements. The actual precedent is M WP5/G5: "bar FIXED at cosine ≥ 0.999 … **threshold never re-baselined from measurement**" — the opposite of "pinned from first measurement".
- **Fix:** fix the floor **a priori** at 0.50 (never re-baselined from the candidate's measurement); the first measurement only validates feasibility; make the bad-arm RED a gate-failure condition with a defined margin (e.g., bad arm must score < floor − 0.05, bad arm defined in advance: scrambled vectors and/or token-window-only chunker); pre-register the floor and freeze the hash-anchored eval set (Q WP8-T02) before the champion run; report per-category means (already required).

### 4. Code re-embed mechanism is contradictory across lanes: outbox+ToolGate (A/ops/P v11b) vs no-outbox job (E D-E9); the verified code makes the outbox path block memory tools
- A D-D: `model_migration` gains `corpus TEXT` (ladder v11) and "the outbox transaction, lease, relay (ModelMigrationJob), and **ADR-0076 ToolGate semantics are reused unchanged**".
- E D-E9: "Code re-embed has **no outbox, no lease, no ToolGate**"; invalidation UPDATE + `CodeReindexJob` drain. E's file list (E1–E27) contains no `model_migration` change at all — v11b is implemented by nobody on the engineer lane.
- O §3.6 + P §3.3 contract: code re-embed "drains per-corpus and **never blocks memory tools**".
- Verified in code: `src/AiRaccoon/Tools/ToolGate.cs:23` closes tool access whenever `HasOpenModelMigrationAsync` is true — any open `model_migration` row (finished_at IS NULL) blocks **all** tools, memory included. The relay is entries-only: `ModelMigrationJob` → `EntryEmbedder.DrainMigrationAsync` (`src/AiRaccoon.Infrastructure/Embedding/EntryEmbedder.cs:125-150`) drains `SelectAllPendingForEmbed` (`src/AiRaccoon.Infrastructure/Sqlite/MemorySql.cs:354`) with the memory engine; the outbox is a **single-row** table (`MemorySchema.cs:372-382`, `MemorySql.cs:361-397`).
- **Why it matters:** implementing A D-D unchanged (a) blocks memory tools during a code re-embed, directly violating the P §3.3/O §3.6 contract, and (b) requires the relay/drain to select code rows and embed with the code engine — new machinery, contradicting H4's "without new machinery" claim (also finding 14).
- **Fix:** pick **one** mechanism. Recommend E D-E9 (invalidation + `code-reindex` job, no outbox, no ToolGate) for v1 — it is the only one that satisfies the never-block contract; then drop v11b from P §4/§5 WP1 and A D-D (or repurpose v11b as a `code-reindex` ledger row only). If the outbox is kept, ToolGate must skip closing for `corpus='code'`, the relay must branch per corpus, and a test must prove `memory_search` answers mid-code-drain (see finding 18). A WP-C's gate text "ToolGate closed during migration" must be scoped to memory migrations.

### 5. The v11 ladder step (both sub-migrations) has NO test in the QA catalog — the WP1 gate is false-green-able
P §4 + P §5 WP1 gate require "ladder v11a+v11b (fixture bank with nested watches prunes, RED→GREEN)" and "v11 migration RED→GREEN". The QA catalog (Q WP1-T01…T08) contains no v11 test: T01–T08 cover fresh-bank tables, existing-bank migration, digest change, column shape, vec dims, FTS, per-corpus dims, encryption — nothing exercises `MigrateToV11Async` (neither the nested-watch prune nor the `corpus` column + backfill). Q §7 maps WP1 → T01…T08 only.
- **Why it matters:** a broken or skipped ladder step (e.g., prune SQL wrong, column not backfilled) passes every WP1 gate test.
- **Fix:** add WP1-T09 (fixture bank with nested watches → v11 open prunes nested rows + cascades `watch_files`, outermost watch intact, stamp written, re-open idempotent, log lines present) and WP1-T10 (v11b: `model_migration` has `corpus TEXT NOT NULL DEFAULT 'memory'`, legacy rows backfilled 'memory').

### 6. v11a prune leaves the surviving outer watch without a re-scan → stale chunks and fingerprint gaps under pruned paths
O §3.4 / P §4 v11a: "keep the outermost watches, delete nested watch rows + cascade their `watch_files`". Verified: `WatchHostedService.cs:178-182` scans once per registration — `EnqueueChangedSince` uses the stored `lastChangeTs` watermark; there is no periodic re-scan. After the migration deletes the inner watch's `watch_files` (cascade, `WatchStore.cs:46-75`), files under the pruned paths are "never fingerprinted" again only if a scan or digest event reaches them; `ReconcileMissingAsync` (`WatchCatchUp.cs:123-132`) reconciles **only fingerprinted** files — a file deleted during downtime under a pruned path keeps its stale chunks in both corpora indefinitely. The runtime prune path is safe only because a new broader watch is born with `lastChangeTs = 0` (`WatchService.cs:34-39`); the migration does not do this.
- **Fix:** v11a must reset the surviving outer watch's `lastChangeTs` to 0 (or enqueue a full catch-up scan after pruning), and a test must assert a deleted-during-downtime file under a pruned path has its chunks removed after the first post-migration open.

### 7. Crash between prune and register loses watch coverage entirely — "transient overlap — harmless" is wrong
E §5.5: "A crash between (2) and (3) leaves a transient overlap — harmless: … the next `AddAsync` prunes again". O §2.4 claims the add is "server-side, **in one transaction**". Verified: `WatchService.AddAsync` (`WatchService.cs:15-40`) has **no encompassing transaction** — `store.AddWatchAsync` opens its own connection (`WatchStore.cs:36-44`), `RemoveWatchAsync` its own `BEGIN IMMEDIATE` (`WatchStore.cs:46-75`), `pipeline.RegisterWatch` is separate. A crash after the prune transaction commits but before the new watch row inserts leaves **no watch row at all** for that path (pruned row gone, new row never written): not an overlap — a silent, total loss of watch coverage (no digests, no scans, no fingerprints), visible only as a missing row in `memory_watch_status`.
- **Fix:** wrap prune + insert in one `BEGIN IMMEDIATE` transaction (new combined store method), keep `pipeline.RegisterWatch` after commit (self-heals via the `WatchHostedService` registration poll), and add a kill-9 test between prune-commit and insert asserting the new watch row exists and the pruned rows are gone.

### 8. Sync-strip gate contradicts the specified mechanism: "no code_% table" vs row deletion
P §3.7 gate: "pushed snapshot contains **no `code_%` table**". P §3.7/O §3.3 mechanism: `StripNonSyncableAsync` "deletes `code_entries`, `code_fts`, `vec_code` (and shadow tables) **from** every pushed snapshot" — mirroring the settings strip, which **deletes rows and leaves the table** (`SyncService.cs:425-440`: `DELETE FROM settings`, `VACUUM`; both push paths call it, `SyncService.cs:63-79` and `:96-110`). Row deletion leaves empty `code_%` tables in the pushed snapshot → the gate's "no code_% table" assertion fails against the specified implementation (false-fail gate).
- **Fix:** either (a) gate on "pushed snapshot contains zero code rows" (`code_entries`/`code_fts`/`vec_code` counts = 0, FTS shadow tables empty), or (b) specify `DROP TABLE` for the code tables in the strip; add the assertion for both push paths (initial and post-merge).

### 9. `search_quality` recording of code queries is undecided, unmechanized, and untested — privacy-relevant
P §1 declares code `search_quality` rows out of scope, but no mechanism or test enforces it. Verified: `MemoryTools.Search` calls `qualityService.RecordSearchSafeAsync(correlationId, query, scope, projectId, …, topSourceFiles…)` **unconditionally** (`src/AiRaccoon/Tools/MemoryTools.cs:172-175`); the QA catalog's WP6 has no search_quality case. A `kind=code|both` query routed through the same handler records the raw query (which may contain secrets/symbols) and code paths into `search_quality` (default 90-day retention, `src/AiRaccoon.Core/Memory/BankMaintenanceConfigKeys.cs:28-37`).
- **Fix:** explicitly gate the recording to `kind=memory` in v1 (or add a `corpus` tag), document the decision in the ADR, and add a regression test "kind=code and kind=both write no `search_quality` rows".

### 10. `ai-raccoon.ignore` semantics contradict each other across the owner contract, ops, and engineer lanes
- (a) O §2.3: "A previously-indexed file that becomes ignored **stays indexed until removed** … not a deletion pass" vs P §2 requirement 1 ("digest on an ignored path deletes stale chunks … must be cleaned"), P §3.5 ("ignored → delete stale chunks + `last_change_ts`, no fingerprint"), E §5.2 (`DeleteSourcePathAsync` on ignored paths; `ReconcileIgnoredAsync`). The owner contract deletes stale chunks; ops documents the opposite.
- (b) O §2.3: "`memory_ingest_file` on an explicitly-named file is **never ignored** (explicit beats ignore); … Decision D6 flags this as the owner's call" vs E §5.2 ("an ignored path → 0 chunks, same as an unindexable extension") vs P §2 requirement 1 ("ignored files are **never fingerprinted, never chunked**" — unqualified) and P §9 OQ4 (routing of `memory_ingest_file` still open).
- **Fix:** correct O §2.3 to the owner contract (stale chunks are cleaned when an ignore takes effect; ignore applies to explicit single-file ingest as well — or resolve OQ4 explicitly in P §9 with the chosen behavior pinned in a QA case). Add a WP4 case for `memory_ingest_file` on an ignored path so the two lane docs cannot drift again.

### 11. WP3 gate "line_start < line_end" is a false-fail gate
P §5 WP3 gate: "`.cs` → code rows with **line_start<line_end** + FTS + vec0". The chunker spec explicitly emits `LineStart == LineEnd` for single-line overflow hard-splits and one-line files (E §3 Step 3, "each piece becoming its own block with `LineStart == LineEnd`"; Q WP2-T06 pins the hard-split). A one-line `.cs` file or a minified line therefore fails the gate with correct output.
- **Fix:** reword to "line_start ≤ line_end, ranges contiguous and covering the file (WP2-T03 property)".

## SHOULD-FIX

### 12. Ignore-file re-scan trigger condition is unpinned — loop risk if implemented on hash-skip touches
E §5.3's digest pseudocode lists `if file == <watchRoot>/ai-raccoon.ignore → rescanInitiator.EnqueueInitialScan(...)` as a sibling of the `if replaced:` line, and E §5.2 says the trigger fires "after the normal replace handling". The design is loop-free only if the trigger is conditional on the file having been **replaced**: the full re-scan (watermark null) re-enumerates the ignore file itself (never self-ignored, E §5.2), and its digest event is a hash-skip `TouchAsync` after the first replace. If the trigger fires on any digest event for the ignore file, every re-scan re-triggers a re-scan (single-flight coalesces but the scan never stops re-firing).
- **Fix:** pin "trigger only when the ignore file's content changed (replaced branch)"; extend Q WP4-T20 with a no-loop assertion (one edit → exactly one scan; the scan's own digest events do not re-trigger).

### 13. QA WP4-T13 and G17 still pin `!` negation and `?` — stale vs the no-negation decision
P §7-3 resolved "no `!` negation in v1 (engineer wins)"; E §5.2 grammar excludes `?`, `[...]`, escaping. Q WP4-T13 `IgnoreRules_Negation_UnignoresAMatchedPath` (⚠ G17) still pins negation behavior, Q §6 G17 still recommends "`!` negation, last-match-wins", and O §2.3 lists "`*`/`?`/`**`" (ops includes `?`, engineer excludes it).
- **Fix:** rewrite WP4-T13 to pin "a `!` pattern is not negation in v1 (treated as a literal / documented)"; update G17's row to the resolution; align O §2.3's grammar list with E §5.2 (drop `?` or add it to both).

### 14. H4 ("existing outbox/lease machinery expresses a per-corpus drain without new machinery") is overstated
Verified: the outbox is a single-row table (`MemorySchema.cs:372-382`), the relay (`ModelMigrationJob.cs:10-45`) is wired to `IEntryEmbedder` only, and the drain is entries-only (`EntryEmbedder.cs:125-150` + `MemorySql.cs:354,358`). A per-corpus drain needs new SELECTs, an engine branch, and (if the outbox is used) a ToolGate change — that is new machinery in any honest accounting.
- **Fix:** restate H4 as "per-corpus drain needs a small, documented extension of the outbox/job (no new table)" and scope those changes in P §4/§5 (or adopt E D-E9 and delete H4 — see finding 4).

### 15. "Reuse the existing harness unchanged" vs WP8-T01 "harness gains a code-corpus mode"
P §3.9: "Reuse the existing harness unchanged (`evaluate.py` + `scoring.py`)". Q WP8-T01 + A WP-E: "`evaluate.py` gains a code-corpus mode (`--corpus code`)". The harness must change (kind param, code engine config, code corpus entries).
- **Fix:** state precisely: `scoring.py` unchanged; `evaluate.py` gains `--corpus code` (+ code-engine settings application); note the `server.search` shim must pass `kind=code` (verified `evaluate.py:15-40` drives queries through `server.search(entry)`).

### 16. The byte-identical regression guard's RED cannot be witnessed pre-feature
Q §5 requires every test witnessed RED before the production change; Q WP6-T01/RG-01's golden equals today's behavior, so it is GREEN pre-feature by construction. The only honest RED is a deliberate mutation (temporarily add the `code` key / flip the default).
- **Fix:** require a recorded mutation RED for RG-01 (and note it in Q §5's logistics), consistent with the project's "a check you have not seen fail is not a check" invariant.

### 17. Registry pin provenance for code-daemon-embed-v1 is TOFU-as-pin unless established out-of-band
O §2.2/risk 1 and Q WP8-T06 require a "committed SHA-256 pin (D8 pattern)" for `faxenoff/code-daemon-embed-v1`, with O risk 1 also saying "first download TOFU pin re-verified on every load". M D8 distinguishes TOFU (first-download, same-channel trust) from committed registry pins (blessed models, "so the case-study download is never TOFU", M §4/F11). If the code-daemon pin is derived from the first download (or from HF LFS oids fetched by the downloader — same channel), it is TOFU dressed as a pin; a compromised first download gets blessed.
- **Fix:** Q WP8-T06 must require the pin be established out-of-band (owner verifies the artifact hash via the HF web UI / a second channel) and committed **before** the first download; state explicitly the code engine's download is never TOFU (M F11 precedent).

### 18. No test pins the ops §3.6 contract "code re-embed never blocks memory tools"
Q WP7-T01 (fingerprint change → re-embed code only) and WP7-T06 (mixed pending drain) exist, but nothing tests that a code re-embed in flight leaves `memory_search` responsive — the exact contract O §3.6/P §3.3 promise (and the one A D-D's unchanged-ToolGate reuse would violate, finding 4).
- **Fix:** add WP7-T09 `MemorySearch_AnswersDuringACodeDrain`: start a code re-embed with a slow fake embedder, call `memory_search` mid-drain, assert normal results and no refusal.

### 19. Lane/plan test-numbering drift
P §5 WP4 row cites "WP4-T01…T27" (QA has T01…T29, Q §7) and WP8 row cites "WP8-T01…T06" (QA has T01…T07, including the docs-drift T07). P §6 says 95 cases mapped 1:1 — the count is right, the ranges are wrong.
- **Fix:** align the P §5 tables with Q §7 (WP4-T01…T29, WP8-T01…T07).

### 20. Engine-less vs broken-engine behavior contradicts between two QA tests
A D-M: `kind=code` with no `embedding.codeModel` → empty code section + warning. Q WP5-T09 pins "empty corpus → `{ code: [] }`, no exception" (fresh bank, no engine). Q WP7-T08 pins "with the code engine missing: `kind=code` returns an actionable configuration error". "No engine configured" and "engine configured but missing/broken" are different states, but neither test states the distinguishing condition, and WP5-T09's fixture (fresh bank) is exactly the state WP7-T08 calls an error.
- **Fix:** pin the state table explicitly (codeModel unset → empty + warning; codeModel set but unloadable → actionable error), and make WP5-T09 use a bank with `embedding.codeModel` set and zero code rows.

## INFO

### 21. v11a "one implementation, two call sites" is overstated; logging needs new plumbing
Ladder steps run inside `EnsureAsync` on the passed connection (`MemorySchema.cs:500-560`) and take `(SqliteConnection, CancellationToken)` only (`MemorySchema.cs:732`); `WatchStore.RemoveWatchAsync` opens its own connection (`WatchStore.cs:46-75`) and the pipeline is not started during migration. So v11a is the same SQL *pattern*, not the same call, and the "reported, not silent" Information log lines (O §3.4) need a logger threaded into `MigrateToV11Async` (or a returned pruned-list logged by the caller). Shared `MemorySql` constants keep the two paths from drifting.

### 22. Cross-project `code_get` is untested
Q WP6-T08/T09 cover known/unknown hashes within one project; a `code_get(projectId, hash)` where the hash exists only in another project is unspecified. Mirror `memory_get`'s project-scoped lookup (`MemoryTools.cs:78-96`) and add the assertion to WP6-T09.

### 23. Source code (including secrets) becomes searchable through the MCP surface
Whole-repo watch + `kind=code` + `code_get` expose full source chunks under the same ToolGate/QueryGuard as memory (P §3.6, Q WP6-T05/WP6-T07). The plan adds no secret handling; QueryGuard refusal categories are not redaction. Document in ADR-0084/0085 and the how-to that code search can surface credentials/API keys from indexed repos, and that project scope + Read-gate are the only boundaries.

---

## Summary

- **MUST-FIX:** 11 (1–11) — envelope key name; byte-identical vs always-present `code` key; self-referential eval floor; code re-embed mechanism/ToolGate conflict; untested v11 ladder; v11a no-re-scan; prune/register crash window; sync strip gate vs mechanism; search_quality recording; ignore contract contradictions; WP3 gate false-fail.
- **SHOULD-FIX:** 9 (12–20) — re-scan trigger loop pinning; stale negation tests; H4 overstatement; harness "unchanged" wording; RG-01 mutation RED; pin provenance (TOFU); missing non-blocking-drain test; numbering drift; engine-less vs broken-engine states.
- **INFO:** 3 (21–23).
