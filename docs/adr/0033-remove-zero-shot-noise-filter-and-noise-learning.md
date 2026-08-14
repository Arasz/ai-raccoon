# 0033. Remove the Zero-Shot Noise Filter and the Noise-Learning Subsystem

Date: 2026-08-14

## Status
Accepted

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
- **Risk, noted for future deployments:** other deployments of this server may have accumulated
  `noise_entries` rows before upgrading; this ADR's "0 rows, nothing lost" finding is specific to
  the reference bank measured above, not a general claim. The tables stay inert on legacy banks
  rather than being dropped, so no deployment loses that data by upgrading.
