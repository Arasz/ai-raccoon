# H9 — memories should decay, not expire: design brief

**Owner ruling, 2026-08-09:** the reaper's purpose is to prune memories that are *not used*, and
memories should **decay** rather than disappear after a fixed time. There should be a real decay
algorithm in place.

This supersedes the "needs a ruling" placeholder in `2026-08-09-1-6-0-high-fix-briefs.md`. It is a
design change, not a bug fix, so it is written as a design brief with a staged implementation plan.

## What exists today, measured

A decay **formula** exists. A decay **system** does not.

`RatingPolicy.Rating` implements Ebbinghaus half-life decay with an access boost
(`src/AiRaccoon.Core/Rating/RatingPolicy.cs:23`):

```csharp
rating = baseScore * Math.Pow(0.5, ageDays / halfLifeDays) * (1 + accessCount * accessMultiplier);
//        0.5                    halfLife = 30 days                        0.1
```

Now the part that matters. Counted across `src/`:

| | Reality |
|---|---|
| Call sites of `RatingPolicy.Rating(...)` | **exactly one** — `SqliteMemoryStore.BumpAccessAsync:994`, reached only on a search hit |
| Readers of the stored `rating` column | **two** — `SweepService:40` (the delete gate) and `ForgettingPolicyService:54` (the `canEverExpire` report) |
| Influence of rating on search ranking | **none.** `SharedExtraction.cs:24` states it outright: *"Rating/AccessCount are informational context v3 does not read (ADR-0018)"* |

Three consequences follow, and together they are why the feature does not do what it was meant to:

1. **Decay is never evaluated for an entry nobody touches.** The curve is only walked when a search
   hit rewrites the column. An entry nobody searches keeps the schema default `rating = 0.5`
   forever — above the `0.3` threshold — so *disuse actively protects it*. That is the exact
   inverse of the goal.
2. **The clock runs from creation, not from last use.** `SweepService:41` computes
   `ageDays = (now - entry.CreatedAt)`, so a heavily used memory decays anyway. With a 30-day
   half-life the access boost cannot save it: at 400 days you would need ~27,000 accesses to climb
   back over `0.3`. Use does not protect; only recency-of-creation does.
3. **Decay has no gradient — only a cliff.** Because rating feeds nothing but the delete gate, a
   memory is at full strength right up until it is deleted outright. Nothing degrades; something
   vanishes. That is the "disappear after a time" behaviour the ruling rejects.

And the gate itself is a fixed-time cliff (`DegradationPolicy.cs:10`):

```csharp
ShouldDegrade = ttlDays.HasValue && rating < threshold && ageDays > ttlDays;
```

`ttlDays.HasValue` makes an explicit per-entry TTL **mandatory**, so the whole mechanism is inert
until someone sets one — and when they do, deletion is governed by a fixed deadline rather than by
decay.

## Blast radius, measured on the live bank (read-only)

| | project-scope rows |
|---|---|
| total | 13,289 |
| never accessed (`last_accessed_at IS NULL`) | 12,339 — **92.8%** |
| carrying a TTL | **0** |
| below the 0.3 threshold today, stored **or** idle-based | **0** |
| idle days | median 0.9, p90 2.2, **max 3.9** |

Two things fall out. **Nothing is prunable today** — no TTLs exist, so the reaper is inert (which
independently confirms the upgrade-safety verdict). And **nothing becomes prunable on landing**:
with a 30-day half-life the `0.3` threshold is crossed at

```
0.5 · 0.5^(d/30) < 0.3   ⇒   d > 30 · log₂(0.5/0.3) ≈ 22.1 idle days
```

and the most idle row on the bank is 3.9 days old. So this change can be made now with **zero
immediate deletions** — the safest possible moment to land it. The cost arrives ~3 weeks later,
which is precisely why staging (below) matters.

## The design

Three changes, in dependency order. Only the first is required for the ruling; the second is what
makes it "decay, not disappear"; the third is what makes it safe.

### 1. Rating becomes a derived value, computed from idle time

Stop reading a stale column. Compute at evaluation time from data the schema **already carries** —
`access_count`, `last_accessed_at` and `created_at` all exist on `entries`, and `BumpAccess`
already maintains the first two:

```
idleDays = (now - COALESCE(last_accessed_at, created_at)) / 86400
rating   = RatingPolicy.Rating(DefaultBaseScore, accessCount, idleDays, halfLifeDays)
```

Keying decay on **idle time** rather than creation age is what makes use protective and disuse
corrosive — the ruling's actual requirement. A memory read yesterday sits near full strength no
matter how old it is; one abandoned for a year decays regardless of how often it was read before.

Keep the stored column as a **cache**, not a source of truth — writing it on access is still useful
for cheap reads, but nothing should branch on it without recomputing.

### 2. Decay must affect retrievability before it affects existence

This is the half that is missing entirely, and the half the ruling is really about. Rating should
damp the search ranking, so a decaying memory *fades* — it ranks lower, surfaces less, and is
eventually not worth returning — long before anything deletes it. Deletion becomes the tail of a
curve rather than an event.

Concretely: fold rating into the hybrid fusion as a multiplicative damper on the fused score. Two
cautions, both from this codebase's own history:

- ADR-0018 deliberately removed `Rating`/`AccessCount` from the promotion scorer. **Read it before
  re-introducing rating into any ranking path** and say why this case differs (this is retrieval
  ranking, not promotion scoring — but the ADR's reasoning may still apply and should be answered,
  not bypassed).
- There is a feedback loop to avoid: ranking damps by rating, and search hits raise rating. A
  memory that falls slightly can then be surfaced less, decay faster, and fall further — a
  rich-get-richer ratchet. Damp gently (a bounded multiplier, floored well above zero) and do not
  let ranking alone drive an entry to zero.

### 3. Deletion is the tail of the curve, not a TTL deadline

Relax `ttlDays.HasValue` from a **mandatory** conjunct to an optional override:

- *Default path* — an entry is prunable when its decayed rating falls below the threshold and it
  has been idle for at least a grace period. No TTL required. This is what makes the reaper
  actually prune unused memories.
- *Override path* — an explicit `memory_set_ttl` still means "drop this after N idle days",
  independent of rating, for content that is known-ephemeral.

**`memory_set_ttl`'s meaning should shift from creation-age to idle-days** so both gates agree; its
tool description currently tells agents that decay is a matter of time, which is wrong today and
would still be wrong after change 1.

### Watched-file entries are excluded — RULED 2026-08-09

**Decision: exclude file-derived entries from rating-driven deletion.** The reasoning holds: a chunk
indexed from a file on disk is not "unwanted" merely because nobody searched it, the source of truth
still exists, and pruning it is *not self-healing* — the watch digest compares file hashes, so an
unchanged file is never re-ingested and the chunk simply stays missing from the index until someone
edits it.

**RULED, 2026-08-09 (second clarification): decay-driven deletion affects ONLY "manual" memories —
never watch-created, never file-derived.** That makes the predicate simple and strict:

> **An entry is eligible for rating-driven deletion only if it has no file provenance at all:
> `source_file IS NULL`.**

Anything carrying a `source_file` is out of scope for deletion, whether or not a watch currently
covers its path. The 152 rows I earlier proposed keeping in scope (a `source_file` no watch root
covers) are **excluded too** — they name a file, so they are file-derived by the owner's rule. My
earlier "watch-covered" refinement below is superseded; it is kept only to record why the simpler
rule was chosen.

On the live bank this makes the eligible population **30 rows out of 13,289 (0.2%)**.

**Implementer's note — confirm the discriminator before relying on it.** `source_file` is written by
both paths: `FileIngestor` sets it during ingestion, and `MemoryWriteRequest.SourceFile` is an
optional argument on `memory_write`, so an agent *may* set it on a hand-written note. Under this
ruling that is the correct outcome (an agent that declares a source file has declared file
provenance), but if a cleaner ingestion marker exists, prefer it and say so. Do **not** invert the
default: an entry whose provenance cannot be established must be treated as file-derived and left
alone, because the failure mode of over-deleting is unrecoverable and the failure mode of
under-deleting is a slightly larger bank.

**Open sub-decision, flagged not assumed:** this ruling is stated in terms of *deletion*. Whether
decay should also damp the **search ranking** of file-derived entries is a separate question and is
*not* treated as ruled here. My recommendation is yes — damping loses nothing (the row is never
deleted, only ranked lower), it is fully reversible, and it is the only lever that reaches the 98.6%
of the bank that deletion no longer touches. But it needs an explicit yes before it is built.

---

*(Superseded analysis, retained as the record of why the strict rule was chosen.)*

**The obvious predicate needed care.** `source_file` is *not* a
watcher-only column — plain `memory_write` accepts a `sourceFile` argument, so an agent note that
merely cites a path carries one too. Measured on the live bank:

| Predicate | Excluded | Left prunable |
|---|---|---|
| `source_file IS NOT NULL` (naive) | 13,259 (99.8%) | **30 rows** (0.2%) |
| `source_file` lies under a registered watch root | 13,107 (98.6%) | **182 rows** (1.4%) |

Use the second. The 152-row difference is entries naming a path no watch covers — agent notes
citing a source, or stale roots. Those are *not* reproducible from disk, so they must decay
normally; excluding them would be a bug, not caution. The bank has 7 registered watch roots, so
the predicate is cheap to evaluate.

### What this exclusion means for the feature — read this before building it

**On the bank that actually exists, the exclusion makes decay-driven *deletion* nearly inert.**
99.8% of project rows are file-derived; after the ruling, the reaper's entire target population is
**182 rows out of 13,289 — about 1.4%.**

That is not an argument against the ruling. It is an argument about which half of the design carries
the value:

- **Change 2 (decay damps ranking) is the whole benefit for 98.6% of this bank.** A stale doc chunk
  that nobody reads should stop crowding search results — and damping costs nothing, loses nothing,
  and is reversible. For file-derived content this is the *only* mechanism that can help, because
  deletion is now off the table for it by ruling.
- **Change 3 (deletion beyond a TTL) applies to ~1.4% of rows today.** Worth doing, and correct, but
  it is a tail case rather than the headline.

**So the staging below is reordered: ranking first, deletion last.** If only one of the three lands,
it should be change 2. The honest framing for anyone reading this later: the owner asked to prune
unused memories; the measurement says that on this bank "unused" is overwhelmingly *file-derived and
therefore protected*, so the practical form of forgetting here is **fading, not deleting**.

One consequence to keep in view: excluded rows still accumulate. If file-derived content is never
deleted, the bank grows monotonically with the docs it watches, and the ceiling is the corpus size
rather than a retention policy. That is probably fine — it is bounded by disk and by what the watch
roots cover — but it should be a stated position rather than an accident. If it ever stops being
fine, the answer is eviction-with-reingest (drop the chunk *and* clear the watch digest so the
watcher rebuilds it), not deletion.

## Staging — how to land this without a cliff

The blast radius is zero today and non-trivial in three weeks, so land it in observable order:

1. **Compute rating live and expose it. Delete nothing.** Change 1 plus reporting only.
   `memory_sweep(dryRun:true)` and the sweep span (once lane B's H7 fix lands) then show exactly
   what *would* be pruned, on the real bank, for as long as the owner wants to watch it.
2. **Wire decay into ranking** (change 2). Independent of deletion, reversible, and immediately
   useful on its own.
3. **Relax the TTL gate** (change 3) only after step 1 has been observed on real data for at least
   one half-life, and after H2 (scope-blind delete) and H6 (reaper ignores access modes) have
   landed — pruning must be correct and consented before it is broadened.

Step 3 is the only irreversible one. Steps 1 and 2 are how you find out what step 3 would do.

## What to test, and what to assert

Every one of these is red-first; paste the failure.

- **Decay is evaluated without access.** An entry never accessed, idle past the crossover, gets a
  computed rating below the threshold. Fails today — the stored `0.5` is returned. This is the
  ruling in one assertion.
- **Use protects.** Two entries created at the same instant; one accessed yesterday, one never.
  Assert the accessed one's rating is materially higher. Fails today (both decay from creation).
- **Disuse corrodes.** An entry accessed 100 times but idle for a year decays below an entry
  accessed once yesterday. This is the "not used" half of the ruling and it must not be satisfiable
  by access count alone.
- **The curve is continuous, not a step.** Assert monotonic decrease across increasing idle days
  and no discontinuity at the threshold — the threshold selects, it does not cause the decay.
- **Ranking is damped** (change 2): a decayed entry ranks below a fresh one of equal textual
  relevance; and a floor test — a heavily decayed entry is still *retrievable* when nothing else
  matches. Decay must not become censorship.
- **No feedback ratchet:** simulate repeated search rounds and assert a mid-rating entry does not
  spiral to the floor purely because ranking damped it.
- **Watched-file entries survive** rating-driven pruning (or are re-ingested afterwards) — whichever
  the decision is, pin it.
- **Nothing is deleted at step 1.** Assert the step-1 build reports candidates and deletes zero.
- Re-assert the measured invariant as a regression: **an entry with no TTL and idle < 22 days is
  never a candidate** at the default half-life and threshold.

## Files, and the collision to respect

Touches `SweepService.cs`, `DegradationPolicy.cs`, `RatingPolicy.cs` (all currently unowned), plus
`MemorySql.SelectEntryMetadata` — which must be widened to return `access_count` and
`last_accessed_at`, it currently returns only `rating` and `ttl_days`.

**`MemorySql.cs` and `SqliteMemoryStore.cs` are owned by in-flight fix lane A1** (H2/H3). Sequence
this after A1 merges; do not start it in parallel.

Relevant prior art to read first: `docs/explanation/architecture.md` (the rating/degradation
section), ADR-0018, and the archived memory-model research
(`docs/work/archive/2026-08-04-memory-model-gap-analysis.md`), which already identified this gap —
*"we have half-life decay rating… PARTIAL"* — and noted that everything decays at one rate with no
decay-rate classification. That last point is a natural follow-up once the system works at all:
a pinned decision and a transient scratch note should not share a half-life.
