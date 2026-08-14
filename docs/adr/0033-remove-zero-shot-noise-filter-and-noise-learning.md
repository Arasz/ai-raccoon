# 0033. Remove the Zero-Shot Noise Filter and the Noise-Learning Subsystem

Date: 2026-08-14

## Status
**Superseded** — 2026-08-14 by ADR-0039, which restores the noise-learning substrate without a scoring model.

## Context
A 2026-08-14 MoE codebase review, independently reproduced and adversarially re-verified
(`docs/reviews/2026-08-14-moe-codebase-review.md`), found:

- `ZeroShotEmbeddingNoisePolicy` **is registered** (`AppRegistrations.cs`) and runs on every write,
  ahead of `HermesProcessNoisePolicy` — this is a behaviour change on a live path, not dead-code
  removal. It scores **0/50 recall** against the noise set its own ADR (0029) credits it with, at
  1.9–5.2 ms per valid write, while the deterministic `HermesProcessNoisePolicy` scores 50/50 at
  0.0001 ms.
- `OnlineNoiseClusteringService`, `NoiseFeedbackCollector` and `INoiseClusterStore` — the
  noise-learning subsystem `ZeroShotEmbeddingNoisePolicy`'s vectors were meant to feed — have no
  caller and no implementation registered anywhere. They are genuinely unreachable.
- `SqliteNoiseStore`/`noise_entries` persists what a rejected write's content becomes when a policy
  matches, but nothing in `src/` ever reads `noise_entries` — the only references are the INSERT
  and the CREATE TABLE. ADR-0029's promised 14-day purge was never implemented, so rows accumulate
  forever. It cannot serve as "a high-quality dataset of true negatives" (ADR-0029) while nothing
  reads it.

**Before deleting `SqliteNoiseStore`**, the live bank (`~/.ai-raccoon/memory.db`) was queried
read-only:

```
sqlite3 "file:$HOME/.ai-raccoon/memory.db?mode=ro" "SELECT COUNT(*) FROM noise_entries;"
```

Result: **0 rows.** Nothing is lost by this deletion on the reference bank. This also answers the
review's own top open question — the filter's false-positive rate on real traffic — since the
filter's own reject log is empty; there was nothing to answer either way.

## What was measured, and what was not

*Added 2026-08-14 after the repository owner asked whether removal was chosen over repair, and
whether the approach had been measured to fail. The original text below conflated two different
claims. They are separated here because only one of them is backed by measurement.*

**Measured — the shipped filter.** `ZeroShotEmbeddingNoisePolicy` as configured (three hardcoded
anchor vectors, one global cosine threshold of 0.20) was evaluated twice, by independent lanes,
against the real bundled MiniLM model:

| | |
|---|---|
| Recall on the 50 noise strings ADR-0029 credits it with | **0/50** (min distance 0.244 vs threshold 0.20) |
| Recall of the deterministic Hermes policy on the same set | **50/50** |
| Cost per *valid* write | 1.9–5.2 ms, length-dependent |
| Cost of the deterministic policy | 0.0001 ms (~17,000× cheaper) |
| Mutual cosine distance between the three anchors | 0.757 / 0.821 / 0.881 |

That is a sufficient basis to remove **this implementation**: it never fired on the traffic its
own ADR cites, while charging every good write for the privilege.

**Not measured — the learned, multi-cluster approach.** `OnlineNoiseClusteringService` and
`NoiseFeedbackCollector` were **never evaluated, and could not be**: `INoiseClusterStore` had no
implementation, nothing registered them, nothing called them. They never ran. Removing them is
justified as *half-built unreachable code*, which is a maintenance argument — **not** as "the
approach was tried and failed."

**And the measurement cuts the other way here.** The near-orthogonality of the three anchors
(0.757/0.821/0.881) says "noise" is not one region of embedding space, so *no* single global
threshold can cover it. The deleted service was a leader-follower design maintaining **many**
centroids — assign to the nearest cluster within 0.12, else spawn a new one. That is the standard
answer to exactly the defect measured. **The strongest evidence against the shipped filter is
evidence in favour of the design that was deleted alongside it.** This ADR should not be read as
retiring the idea.

**What a revival would need**, so the next attempt does not restart from zero:
1. **Labelled data.** Neither approach was ever evaluated against a labelled noise/signal set. That
   absence is the root cause of ADR-0029 shipping a 0-recall filter with green tests.
2. **A feedback source.** This change also removes `noise_entries`, the reject log a learner would
   have trained on. Two signals survive and are arguably better: ADR-0032 now returns
   `stored=false` + `reason` to the agent, and `memory_record_grade` / `memory_record_followthrough`
   already exist as explicit quality feedback.
3. **Per-cluster thresholds**, not one global cut — the orthogonality measurement above.
4. **A gate that can fail**: a recall floor over a labelled set at the *shipped* threshold. The
   deleted tests used a `FakeEmbedder` returning a fixed vector regardless of input, so swapping the
   "noise" and "clean" strings between two tests left both green.

The code is preserved in git at `5cccede7~1` and is recoverable in full. It is not kept in the tree
because unreachable code with two further uncalibrated thresholds (0.12 cluster distance, 0.75
orthogonality cap) reads as maintained when it is not — the same condition that let the shipped
filter survive review.

## Decision
Delete the zero-shot noise filter and the noise-learning subsystem it fed. Keep only the
deterministic `HermesProcessNoisePolicy`.

**Removed:** `ZeroShotEmbeddingNoisePolicy`, `ZeroShotEmbeddingFilter`, `INoiseVectorProvider`,
`BundledNoiseVectorProvider`; the clustering trio `OnlineNoiseClusteringService`,
`NoiseFeedbackCollector`, `INoiseClusterStore`; `SqliteNoiseStore` and `INoiseStore`; and —
dead once their only callers went — `IContentEmbedder` and `EntryEmbedder.EmbedContentAsync`.
`NoiseFilteringService` drops its `INoiseStore?` constructor parameter; `AppRegistrations.cs` drops
the corresponding five DI registrations.

**Kept:** `HermesProcessNoisePolicy`, `INoiseFilterPolicy`, `NoiseFilteringService`,
`INoiseFilteringService` (now returning `NoiseFilterResult` per ADR-0032).

**Schema:** `noise_entries`, `noise_clusters` and `vec_noise` are removed from the fresh-bank DDL
path only. `MemorySchema.MigrateToV6Async` — the ladder step that still creates `noise_clusters`
and `vec_noise` when an existing bank upgrades from schema version &lt;6 — is left in place as a
historical no-op; it is never renumbered or deleted, and `CurrentVersion` is unchanged at 6. A
legacy bank that already has these tables keeps them, inert: nothing reads them, and nothing in
this change touches existing rows.

Lands in the same PR as, and after, ADR-0032's honest write outcome — never before it. Deleting the
noise store while a rejected write still returned a fabricated success would have made a rejected
write both a lie and genuinely unrecoverable, which is strictly worse than the pre-existing bug.

## Consequences
- **Positive:** Every remaining write-time policy is deterministic and measured — no more
  synchronous embedding call, and no more silent 0-recall filter running ahead of the one that
  works.
- **Positive:** ~374 fewer production lines, two fewer schema tables on fresh banks, six fewer test
  files pinning removed behaviour.
- **Negative:** The `noise_entries` reject log — even though nothing read it — is the only record
  a future reviewer could have used to measure the zero-shot filter's real-world false-positive
  rate. That question is now unanswerable for this deployment; it was empty regardless.
- **Negative, and the one to weigh:** this removes both halves of semantic noise filtering at once
  — the (measured, failing) single-threshold policy *and* the (never-run, never-measured)
  multi-cluster learner that was the designed fix for its failure mode. A future attempt starts
  from the ADRs and git history rather than from working code. That is deliberate, but it is a
  cost, and it is the reason the "What was measured, and what was not" section above exists.
- **Risk, noted for future deployments:** other deployments of this server may have accumulated
  `noise_entries` rows before upgrading; this ADR's "0 rows, nothing lost" finding is specific to
  the reference bank measured above, not a general claim. The tables stay inert on legacy banks
  rather than being dropped, so no deployment loses that data by upgrading.
