# Join review — F22/F25 agent-requested promotion fix (P12)

**Target:** `/Users/arasz/RiderProjects/ai-raccoon/.ai-badger/worktrees/w1-p12`, branch `task/psr-w1-p12` @ `a6d336fd` (`f9835f79` test, `a6d336fd` fix), base `5bca1900`. Working tree clean before and after; no tracked file modified; no commit; no `~/.ai-raccoon` access; no `mcp_ai-raccoon_*` call.
**Method:** diff read, source re-derivation at HEAD, gates run in the worktree, red-at-base and adversarial probes run in a throwaway `/tmp` clone (`docs/work/2026-09-22-project-scope-review/verify/p12-head`) so the review tree stayed read-only.

---

## 1. Diff, stamp currency, and `NotQueued`

### 1a. The stamp is the current version, not a literal — **CONFIRMED**
`MemoryWriteService.cs:71` passes `PromotionScorer.Version` as the seventh `QueueCandidate` argument; `PromotionScorer.cs:16` is `internal const int Version = 2;`. `git diff 5bca1900..HEAD` contains no numeric version in the candidate construction — the symbol is used, so a bump moves the stamp with the scorer.

### 1b. `NotQueued` cannot mis-report — **REFUTED**
`ProposeOutcome.NotQueued` is defined as a **union**: `PromotionQueue.cs:60-64` — *"refused (a remembered discard or an already-shared value twin) **or evicted by the same pass**"*. `MemoryWriteService.cs:80-82` collapses that union to a refusal-only reason:

```csharp
Reason = outcome.NotQueued.Contains(entry.Hash, StringComparer.Ordinal)
    ? PromotionReasons.AgentRequestedRefused
    : $"queued-for-promotion: {PromotionReasons.AgentRequestedShare}"
```

Live evidence (throwaway clone, HEAD source, cap forced to 1, seeded 2.0 row, agent write scores 1.0 and is evicted by its own pass):

```
REASON: not-queued: agent-requested-share refused (discarded earlier or already shared)
```

The queue check in the same probe (`rows.ShouldBe(["seed-hash"])`) proves the new row was the eviction victim, not a refused upsert — yet the response names `(discarded earlier or already shared)`. A second probe, asserting the outcome directly, passed:

```
ProposeOutcome_EvictedHash_IsAlsoInNotQueued  →  passed (1s 184ms)
    outcome.Evicted   == ["low"]
    outcome.NotQueued contains "low"
```

The lane's own `PromotionQueueDiscardTests.cs:164-165` pins exactly this union (`NotQueued == [evicted hash]`), so the conflation is deliberate at the service layer — the write path is the consumer that reads it as a cause.

The queue-state half is honest (the row genuinely is not queued); the **cause** is not.

---

## 2. Gates — run by hand, output pasted

### New tests — **CONFIRMED (2/2 green)**
```
$ dotnet test --project tests/AiRaccoon.Tests --filter "FullyQualifiedName~AgentRequestedPromotionQueueTests" -v m
  AiRaccoon.Core -> .../src/AiRaccoon.Core/bin/Debug/net10.0/AiRaccoon.Core.dll
  AiRaccoon.Infrastructure -> .../src/AiRaccoon.Infrastructure/bin/Debug/net10.0/AiRaccoon.Infrastructure.dll
  AiRaccoon -> .../src/AiRaccoon/bin/Debug/net10.0/AiRaccoon.dll
  AiRaccoon.Tests -> .../tests/AiRaccoon.Tests/bin/Debug/net10.0/AiRaccoon.Tests.dll
Running tests from .../AiRaccoon.Tests.dll (net10.0|arm64)
.../AiRaccoon.Tests.dll (net10.0|arm64) passed (1s 298ms)

Test run summary: Passed!
  total: 2
  failed: 0
  succeeded: 2
  skipped: 0
  duration: 1s 652ms
```

### Promotion/queue consumer filter — **CONFIRMED (283 passed, 1 skipped)**
```
$ dotnet test --project tests/AiRaccoon.Tests --filter "FullyQualifiedName~Promotion|FullyQualifiedName~Queue" -v m
skipped AiRaccoon.Tests.Integration.Memory.PromotionScoringRealDataTests.ScoresCorrelateWithHandLabeledUsefulness
  AIRACCOON_SCORING_EVAL_FIXTURE not set — local-only verification against the real labeled candidate pool ...
.../AiRaccoon.Tests.dll (net10.0|arm64) passed (5s 103ms)

Test run summary: Passed!
  total: 284
  failed: 0
  succeeded: 283
  skipped: 1
  duration: 5s 250ms
```

The lane's "176 changed-surface tests" is not reproduced by this filter (284 total, 283 green); the surface is green either way. The single skip is environmental (`AIRACCOON_SCORING_EVAL_FIXTURE`), not a regression.

### Red-at-base (the gates discriminate) — **CONFIRMED**
In the `/tmp` clone, `git checkout 5bca1900 -- src/ tests/` then restoring only the new test file; same filter:
```
failed AgentRequestedPromotionQueueTests.AgentRequestedRow_SurvivesAProposePass_StampedWithTheCurrentScorerVersion
  Shouldly.ShouldAssertException : surviving.Count should be 1 but was 0
  Additional Info: the propose pass's stale-scorer clear must not destroy an agent-requested row

failed AgentRequestedPromotionQueueTests.RewriteAfterDiscard_ReportsRefusal_AndLeavesTheQueueEmpty
  Expected Value | not-queued: agent-requested-share refused (discarded earlier or already shared)
  Actual Value   | queued-for-promotion: agent-requested-share
  Additional Info: F25: the queue refused the upsert, so the response must not claim a queued review

Test run summary: Failed!  total: 2  failed: 2  succeeded: 0
```

### Real store, not a fake — **CONFIRMED**
`AgentRequestedPromotionQueueTests.cs:41-47` constructs `SqlitePromotionQueueStore` → `PromotionQueueService` → `MemoryWriteService`/`SharedExtractionRunner`. No `FakePromotionQueue` anywhere in the class (its own doc comment forbids it).

---

## 3. Attacks

### (a) Refused vs evicted separation, and the write response — **REFUTED**
There is no separation: `NotQueued` is the union (`PromotionQueue.cs:60-64`) and the write maps all of it to `"refused (discarded earlier or already shared)"` (`MemoryWriteService.cs:80-82`). A capacity eviction is therefore reported with two false causes. Evidence: §1b probes (live, HEAD).

The eviction arm is not exotic. The premise written into the code and the ADR — `MemoryWriteService.cs:39-41` and `docs/adr/0067-...md:44`: *"`AgentRequestedScore = 1.0`, above the scorer's range, so an explicit request outranks every inference"* — is false for the Version-2 scorer: the scorer clamps to `0.0–4.0` (`PromotionScorer.cs:57,72,...`) and `OrganicNote`'s fitted prior is **2.00** (`ProvenanceArchetype.cs:53`); G2 measured a live re-score of 2.1567, and my embedded probe re-scored the agent row to **1.37**. An agent row at 1.0 is a *low*-score row, so under capacity pressure it is the natural eviction victim — and the write then claims a discard/shared-twin refusal.

Smallest fix: `SqlitePromotionQueueStore.UpsertAsync` already computes the genuine refusal set (`SqlitePromotionQueueStore.cs:50-77`) and discards it at `:82` (`return hashes.Count(...) - refused.Count`). Surface it (e.g. `ProposeOutcome.Refused`) and let the write report the real cause; keep `NotQueued` as the state.

### (b) The extra `ListAsync` under the cap — **CONFIRMED (ordering correct), with a cost note**
The read is at `PromotionQueueService.cs:68`, **after** the eviction loop (`:46-64`). A just-evicted row cannot be reported as queued — the question's failure mode does not occur; the row is reported as `NotQueued`, which is what feeds (a). `PromotionQueueSql.List` has no `LIMIT`, so there is no truncation-driven false "not queued". Cost: one full project-queue `SELECT` per propose pass, and the extraction path already lists the queue once in `SharedExtractionRunner.cs:51-53`, so that path now pays two full reads (plus `GetStatsAsync`). Not a hot path; worth a note only.

### (c) Durability at a bumped scorer version — **REFUTED (generation-scoped)**
`ClearStale` deletes `scorer_version != @CurrentVersion` (`PromotionQueueSql.cs:124-127`). The stamp is current at *write* time, so a later bump makes it stale. Live probe at HEAD:

```
VersionBump_ClearsTheStampedAgentRow  →  passed
    after ClearStaleAsync(projectId, PromotionScorer.Version + 1): rows == 0
```

The fix works only while the stamp equals the current version. The new test pins `PromotionScorer.Version` dynamically (`AgentRequestedPromotionQueueTests.cs:74`), so a future bump keeps the test green and this fragility unpinned. This is the ruling's chosen mechanism — K2 chose *stamp*, explicitly over exempting the reason from `ClearStale` — but F22's terminal case (a below-floor note that can never re-earn admission) returns at every scorer generation.

### (d) F25 wire string vs production constant — **CONFIRMED (exact)**
Mechanical comparison of `MemoryWriteService.cs:17-18` and the test literal at `AgentRequestedPromotionQueueTests.cs:100`:

```
src literal:  'not-queued: agent-requested-share refused (discarded earlier or already shared)'
test literal: 'not-queued: agent-requested-share refused (discarded earlier or already shared)'
EXACT MATCH: True
```

The test pins the literal rather than the constant — correct for a wire contract.

---

## 4. The accepted residual: a re-score replaces the agent reason

**CONFIRMED live, and it is the residual the lane named.** With the row flipped to `embed_state='embedded'` (the extraction query requires it, `MemorySql.cs:48`), the next propose pass includes the already-queued hash in `toQueue` (`SharedExtractionRunner.cs:60-65`) and the upsert's `ON CONFLICT DO UPDATE SET reasons = excluded.reasons` (`PromotionQueueSql.cs:20-25`) replaces the agent's reasons:

```
REASONS: [organic-note] SCORE: 1,37 VERSION: 2
    (probe expected ["agent-requested-share"] and failed — deliberately)
```

A below-floor note is *not* re-scored (it never enters `ranked`, `SharedExtractionService.cs:91`), so it keeps `agent-requested-share`; above-floor notes lose it on the first pass.

**Judgement:** this is consistent with K2's letter — the consequence text says *"any re-score is honest"* — and with the stamp-not-exempt mechanism the owner chose. It is in tension with ADR-0067's stated consequence at `docs/adr/0067-...md:61`: *"An agent-requested candidate is distinguishable in `memory_promotion_list` by its reason"* — after one propose pass on an embedded bank it is not. The promotion *request* survives (the row stays queued; `PromoteAsync` does not filter by reason), but the provenance label and the priority score change. The lane disclosed this accurately.

---

## Residual risk

1. **Eviction is reported as a refusal** (live, §1b/§3a). The write response's *state* is true, its *cause* is false for the capacity arm. K2's consequence — "the write response's reason must reflect the real enqueue outcome" — is only met for discard/shared-twin.
2. **The "agent request outranks every inference" premise is stale** for the Version-2 scorer (`MemoryWriteService.cs:39-41`, ADR-0067:44 vs `ProvenanceArchetype.cs:53`, OrganicNote prior 2.00). Agent rows at 1.0 are systematically the lowest-priority rows, which makes residual 1 routine under pressure.
3. **Version-bump fragility** (§3c): below-floor agent requests are terminally cleared at each scorer-generation bump, with no test pinning the behavior.
4. **Provenance erasure on embedded banks** (§4): the agent reason is transient; the row's score/priority is replaced by the scorer's.
5. **Snapshot staleness**: `NotQueued` is computed from a post-pass read; a concurrent pass/process can evict between the read and the response, so the outcome can be stale in either direction. Inherent to the "final queue read" design.
6. **Cost**: one extra full project-queue `SELECT` per propose pass; two on the extraction path.
7. **Hygiene**: the new test file has no final newline, against `.editorconfig:10` (`insert_final_newline = true`).
8. The lane's "176 changed-surface tests" was not reproduced; the measured filter is 284 total (283 passed, 1 environmental skip).

## Still open

1. **Owner call on the refusal text**: is "not-queued" alone sufficient, or must the write distinguish evicted from refused by surfacing the store's already-computed `refused` set (`SqlitePromotionQueueStore.cs:50-77`)? Under K2's own wording this reads as required, not optional.
2. **Owner call on re-score provenance**: preserve/merge `agent-requested-share` when refreshing an already-queued row, or amend ADR-0067:61 to say the label is transient. K2's "any re-score is honest" leans to the amendment; the ADR intent leans to preservation.
3. **Bump policy**: K2 rejected exempting the reason from `ClearStale`; if agent requests must survive scorer generations, that rejected option (or a re-stamp on read) is the only mechanism that does.
4. **Tests**: the shared-twin refusal arm of F25 is untested (discard arm only); the version-bump behavior is unpinned; the new file needs its final newline.
