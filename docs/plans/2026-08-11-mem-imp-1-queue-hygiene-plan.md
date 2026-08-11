# Queue hygiene: exclude already-shared from propose, persist discards — plan (2026-08-11)

Task: `mem-imp-1`. Implements recommendation 2 of
`docs/work/2026-08-11-ai-raccoon-diagnostic.md`: the propose tier must stop re-queueing
content that is already in the shared tier or that an agent explicitly discarded.

## Evidence (measured 2026-08-11, live bank copy)

- **38 of 1,000 queue rows carry a value already present in the shared tier; 19 of the
  top-50 by score.** The propose-side dedup is structurally broken:
  `SharedExtractionService.IsDuplicate` (src/AiRaccoon.Core/Memory/SharedExtractionService.cs:145)
  tests `sharedValues.Contains(NormalizeWhitespace(row.Value))` — the candidate is
  whitespace-stripped but the shared index (`SharedIndex.Values`, raw values from
  `SelectSharedIndex`) is not, so a multi-word value never matches. The promote path
  (PromotionQueueService.cs:67-69) already normalizes **both** sides; propose is the
  half-normalized outlier.
- **Discards are not persisted.** `memory_promotion_discard` → `IPromotionQueueStore.DiscardAsync`
  (DELETE…RETURNING) only; nothing survives the pass. The 08-10 curation pass discarded 27
  candidates; re-enabling extraction re-queued them (confirmed in the diagnostic queue audit:
  rejected classes — code-internal facts, archived research, changelog entries — back at
  3.0–3.5).

## Design

### WP1 — fix the propose-side shared dedup (root-cause fix)

`SharedExtractionService.RankAll` builds `sharedValueSet` from raw values. Change it to a
whitespace-stripped set, exactly mirroring `PromotionQueueService.PromoteAsync`:

```csharp
var sharedValueSet = sharedValues.Select(v => NormalizeWhitespace(v)).ToHashSet(StringComparer.Ordinal);
```

`IsDuplicate`'s `sharedPath` branch (`shared/{row.Path}`) stays — it catches the 59 legacy
path-addressed shared rows. No other call site changes: `RankAll` is called from
`SharedExtractionRunner.ProposeAsync` (loop + `memory_share_extract` propose) and tests.

This prevents NEW duplicates. Existing residue needs WP2's prune.

### WP2 — persist discards + prune rejected residue

**New table** (additive DDL in `MemorySchema.Ddl`, idempotent `CREATE TABLE IF NOT EXISTS`,
no schema-version bump — same precedent as ADR-0023's unconditional-Ddl trigger and the
`watches`/`watch_files` tables):

```sql
CREATE TABLE IF NOT EXISTS promotion_discards (
    project_id   TEXT NOT NULL,
    hash         TEXT NOT NULL,
    discarded_at INTEGER NOT NULL,
    PRIMARY KEY (project_id, hash)
);
```

Semantics: a discard is the agent's "no" for that content identity
(`hash` = ContentHash.Of(path, content)). It is permanent (no un-discard in v1), keyed per
project, **never synced** (queue rows are per-machine by design — same rule), **never swept**
(curation intent, not data). A changed content produces a new hash and is re-eligible.

**Port additions** (`IPromotionQueueStore`):

- `Task RememberDiscardsAsync(string projectId, IReadOnlyList<string> hashes, ct)` —
  `INSERT OR IGNORE INTO promotion_discards`. Called from
  `PromotionQueueService.DiscardAsync` (the tool path) ONLY — **not** from the store's
  `DiscardAsync`, because `PromoteAsync` claims rows through the same store method (line 85)
  and a promotion must never be remembered as a rejection. `EvictVictimAsync` (capacity) and
  `ClearStaleAsync` (scorer retirement) also must not remember — neither is an agent
  rejection.
- `Task<int> PruneRejectedAsync(string projectId, ct)` — one statement removing queued rows
  that are already shared (exact value twin) or discarded:

```sql
DELETE FROM promotion_queue
WHERE project_id = @ProjectId
  AND (EXISTS (SELECT 1 FROM entries e
               WHERE e.scope = 'shared' AND e.value = promotion_queue.value)
       OR EXISTS (SELECT 1 FROM promotion_discards d
                  WHERE d.project_id = promotion_queue.project_id
                    AND d.hash = promotion_queue.hash))
```

**Upsert refusal — the persistence-layer contract** (review finding F11, deepened): the prune
alone does NOT stop re-queueing — `RankAll` re-ranks a discarded row every pass (it has no
discard input) and `UpsertAsync` would re-insert it in the same pass. The refusal therefore
lives in `UpsertAsync` (the single queue-write chokepoint; `SharedExtractionRunner` is the only
caller of `IPromotionQueue.ProposeAsync`): each candidate insert becomes
`INSERT ... SELECT ... WHERE NOT EXISTS (discarded hash) AND NOT EXISTS (shared exact-value
twin)`. This avoids changing `RankAll`'s signature (which would ripple every
`SharedExtractionServiceTests` call site) and avoids a discard-set read on `IMemoryStore`
(which would ripple its fakes). Consequence: `memory_share_extract propose` may still LIST a
discarded row in its advisory candidate output, but the persisted queue — the review surface
`memory_promotion_list` audits — never receives it. The genuine-new count in `UpsertAsync`
must exclude refused rows (snapshot existing + discarded in the same transaction, diff the
batch).

The shared-twin `NOT EXISTS` at insert is defense-in-depth on top of WP1 (RankAll) — it also
covers whitespace twins that RankAll's normalized set already catches, at the cost of one
`entries(scope='shared')` value scan per candidate insert (~103 rows today; no index — an
index on the large `value` column would bloat the bank, documented tradeoff).

Prune placement (review F5): `PruneRejectedAsync` is called at the top of
`PromotionQueueService.ProposeAsync` (single chokepoint — both the 30-min loop and the
`memory_share_extract propose` tool route through it) and at the top of
`PromotionQueueService.PromoteAsync` (a pre-fix discarded row must not be promotable when the
mode flips). NOT in `SharedExtractionRunner` — it depends on `IPromotionQueue`, not the store
port.

Restore-path guard (review F9b): `MemorySql.RestoreQueueRowsStillBacked` re-inserts captured
queue rows across a watch replace — add `AND NOT EXISTS (SELECT 1 FROM promotion_discards d
WHERE d.project_id = r.project_id AND d.hash = r.hash)` so a discarded row cannot reappear
through the replace round-trip either (the table exists on every open via the Ddl).

**Tool contract**: unchanged. `PromotionDiscardResult(Discarded)` stays; persistence is
internal behavior, documented. Whole-queue clear (`hash` omitted) remembers every removed
row — that is the "this queue is junk" semantic. Unknown-hash discard removes 0, remembers 0
(idempotent, as today). Discarded rows remain absent from the QUEUE even though the propose
tool's advisory candidate list may re-show them — documented in the ADR.

### WP3 — docs, version, live gate

- New ADR: `docs/adr/0026-persistent-discards-and-shared-exclusion.md` (root cause with
  measured numbers, design decisions, the claim-path trap, no-un-discard/never-swept/
  never-synced semantics, exact-value limitation).
- Update `docs/reference/agent-memory-server.md` (propose-tier + discard semantics),
  `docs/adr/0007-propose-tier.md` (one paragraph pointer), `docs/explanation/architecture.md`
  schema table if it lists tables.
- Outcome note: `docs/work/2026-08-11-mem-imp-1-queue-hygiene.md`.
- Version bump **1.6.5** (review F8 — four pins): `src/AiRaccoon/AiRaccoon.csproj`
  (PackageVersion/InformationalVersion/AssemblyVersion),
  `src/AiRaccoon/.mcp/server.json` (lines 5/10 — REQUIRED by
  `VersionContractTests.McpServerJson_Versions_MatchPackageVersion`),
  `tests/AiRaccoon.Tests/Unit/Setup/VersionContractTests.cs` `ExpectedVersion`,
  `README.md` "What's new (from 1.2.0 to 1.6.4)" heading. Grep repo-wide for other `1.6.4`
  pins (obj/bin artifacts excluded).

## Acceptance criteria (gates, TDD RED first)

| # | Gate | RED shape | GREEN |
|---|---|---|---|
| G1 | Propose dedups against RAW shared values (exact twin) | `SharedExtractionServiceTests`: sharedValues = ["alpha beta"], candidate value "alpha beta" → currently ranked (BUG); test asserts excluded | pass |
| G2 | Whitespace-twin exclusion, shared side carries the whitespace (review F10 — the mirror scenario is green today) | sharedValues = ["a b"], candidate "ab" → currently ranked (BUG); test asserts excluded | pass |
| G3 | Discard persists; re-propose of the same hash is not re-queued | store test (real `SqlitePromotionQueueStore`, mirroring `PromotionQueueInvalidationTests`): discard → `RememberDiscardsAsync` → `UpsertAsync` same hash → queue empty (upsert refused) | pass |
| G4 | Prune removes existing residue: shared-twin rows and discarded rows | seed a queue row with a value equal to a shared row + a discarded row → `PruneRejectedAsync` → both gone; shared rows untouched | pass |
| G5 | Promote claims do NOT write discards (pin, review F2) | — (vacuously green under stubs; stated as a pin in the GREEN commit) | after promote, `promotion_discards` empty |
| G6 | Eviction/ClearStale never write discards (pin) | — (same) | after EvictVictimAsync/ClearStaleAsync, `promotion_discards` empty |
| G7 | Upsert refusal count honesty | store test: upsert batch of discarded hash + fresh hash → fresh counted 1, refused 0 | pass |
| G8 | Queue-level tool gate (review F11): discard via service → propose (service + real store) → `memory_promotion_list` lacks the hash; `promotion_discards` holds it | service-level test | pass |
| G9 | Restore-path guard (review F9b): a discarded row's source file replaced → not restored to the queue | storage test around `ReplaceFileAsync` + capture/restore | pass |
| G10 | Full targeted suite green (build-fast + extraction/promotion cluster), then full `dotnet test` once, re-run if the known cold-start flake fires | | |
| G11 | MANUAL live test on the real bank (user f:-gate): run propose via `memory_share_extract propose` per project → re-run the top-50 audit (`memory_promotion_list limit 50` + SQL value-join) → ~0 already-shared AND ~0 re-discarded; discard one live candidate via the tool → next propose does not re-queue it; `promotion_discards` rows visible in a bank read | | |

## Ripples (known, review-corrected)

- `IPromotionQueueStore` gains two members → CS0535 in the fake stores (4 files / 5 fakes —
  `PromotionQueueServiceTests` uses the REAL store, so it is NOT in the list):
  `PromotionQueueServiceGuardTests` (UnreachableQueueStore + EmptyDiscardQueueStore),
  `PromotionQueueServicePromoteRaceTests`, `PromotionQueueServicePromoteAccountingTests`,
  `ExtractionMetricsTests`.
- **RED-commit shape (review F7):** the stub commit carries the interface change + fake updates
  + `NotImplementedException` stubs in `SqlitePromotionQueueStore` with the NEW store-level
  gates (G3/G4/G7) failing at runtime — but the `ProposeAsync`/`PromoteAsync` prune call sites
  stay UNHOOKED in RED (hooking stubs would throw inside ~40 existing propose/promote tests).
  The hookup + real SQL + the Ddl table + G5/G6/G8/G9 land together in GREEN.
- `PromotionQueueInvalidationTests` (storage) is the model for the new store tests;
  `SqlitePromotionQueueStoreTests` (storage) is the other natural home for G3/G4.
- `SharedExtractionService.RankAll` signature unchanged (fix is internal to the set build).
- `VersionContractTests.McpServerJson_Versions_MatchPackageVersion` REQUIRES the
  `src/AiRaccoon/.mcp/server.json` version pins — the bump must include that file
  (review F8).

## Out of scope

- Scorer improvements (round-3 winner A, ADR-bias) — separate task.
- An un-discard tool, discard expiry/cap, bulk discard — noted as future work in the ADR.
- Migration of the 59 legacy path-addressed shared rows.
- Whitespace-normalized prune (exact-value only, see Design).

## Sequencing

One lane, sequential (single cohesive change over shared files): WP1 (dedup) → WP2 (table +
port + persist + prune + tests) → WP3 (docs + version + live gate). Each WP commits and
pushes separately; draft PR opened after WP1 lands (github extension).
