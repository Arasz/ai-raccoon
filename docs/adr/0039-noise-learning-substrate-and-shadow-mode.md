# 0039. The Noise-Learning Substrate and Shadow Mode — No Detector Yet

Date: 2026-08-14

## Status
Accepted

## Context
ADR-0033 deleted `ZeroShotEmbeddingNoisePolicy` (three hardcoded anchor vectors, one global cosine
threshold) and the never-wired clustering trio (`OnlineNoiseClusteringService`,
`NoiseFeedbackCollector`, `INoiseClusterStore`), reasoning that the shipped filter scored 0/50
recall on ADR-0029's noise corpus while the deterministic `HermesProcessNoisePolicy` scored 50/50.
That comparison is circular: ADR-0029's corpus *is* Hermes background-process logs, so a policy
built to match that exact shape cannot lose to itself. This task began by restoring the subsystem
on the strength of that circularity argument.

**Mid-task, a research lane measured real traffic** (10,250 sessions, 559k transcript records, 4
projects, one operator/harness) and returned three findings that changed the picture entirely:

1. **The embedding space is fine when the targeted noise is present.** Tool output vs. deliberate
   memory separates at **ROC-AUC 0.946**. This is exactly the case ADR-0029's design was for.
2. **That noise essentially never reaches `memory_write` in this install.** `memory_write` was
   called 66 times against 36,157 `Bash` calls; all 64 substantive writes were sourced findings,
   corrections, rulings, or measurements — zero were tool output.
3. **The noise that *does* reach `memory_write`** — ephemeral agent prose — **is not well-separated**:
   AUC 0.68 against a permutation-null p95 of 0.697 (p = 0.08), concentric classes, centroid
   separation 0.164 against within-class spread ~0.41. A leader-follower centroid learner trained on
   this traffic produced **1,583 clusters from 1,940 items — 93% singletons** (it memorises, not
   generalises); k-means silhouette on noise (0.047–0.142) is no better than on signal
   (0.045–0.075), meaning noise here is a diffuse half-space, not a set of clusters.

**Two follow-on redirects on this same measurement are recorded here, deliberately, so a future
reader sees both were tested and refuted rather than just seeing the code change:**

- **Direction 1 (refuted):** an early argument — made in this task's own instructions — read the
  three anchors' near-orthogonality (mutual cosine similarity 0.12–0.24) as evidence that *multiple*
  centroids were the right shape, and that the deleted leader-follower service was "the standard
  answer to exactly the defect measured." The silhouette/singleton-rate numbers above refute this:
  noise is not cluster-shaped at the granularity a centroid model needs.
- **Direction 2 (not adopted):** a proposal to replace centroids with a linear probe, on the
  strength of the 0.946 AUC figure. That number is **in-sample** — one operator, four repos, one
  harness. A second research lane is running a **leave-one-tool-family-out** evaluation specifically
  to test whether *any* detector generalizes past the tool shapes it has seen. Shipping a probe
  tuned to an in-sample number would be the third time this project shipped a detector validated
  against the shape it was built for (after the three-anchor filter and the implicit fit of
  leader-follower to its own corpus). Both centroid and probe are held to the same bar: **not
  validated on held-out data, not shipped.**

**The capability is still justified**, independent of this one install's numbers: AiRaccoon is a
general-purpose MCP memory server usable by any client, not only the Hermes-integrated harness this
measurement ran against. "Noise can reach `memory_write`" is a design assumption about the
*product*, not a claim that only holds where it has been observed. Absence of tool-output writes in
one install is not evidence of absence in every install — an operator whose agents do paste build
output into `memory_write` would see the 0.946-AUC world this design was built for.

## What was measured, and what was not
| | |
|---|---|
| Tool-output vs. deliberate-memory separability (this install) | **0.946 AUC** — clean, but this class essentially never reaches `memory_write` here |
| Ephemeral-prose-noise vs. signal separability (real `memory_write` traffic) | **0.68 AUC**, not significant (permutation p95 0.697, p = 0.08) |
| Leader-follower centroid learner, real traffic | **93% singleton clusters** (1,583 of 1,940 items); memorises, does not generalise |
| Silhouette, noise vs. signal | 0.047–0.142 vs. 0.045–0.075 — noise is diffuse, not clustered |
| Embed cost, 20-token string (the figure ADR-0033 quoted) | 1.9–5.2 ms |
| Embed cost, realistic 256-token content | **14.6–26.5 ms** — a second inference on the write path is not acceptable at this cost |
| Reused vector (already computed by `EmbedIfConfiguredAsync`), scored against ~50 stored samples | **~0.024 ms** |
| Linear-probe AUC (0.946) | **In-sample only** — one operator, four repos. A leave-one-tool-family-out study is running; not adopted pending its result |

## Decision
Supersedes ADR-0033. Restore and build the **learner-agnostic substrate** only. Ship **no scoring
model** — no centroid assignment, no probe, no similarity threshold. Leave a clearly-marked seam.

1. **`INoiseClusterStore` / `SqliteNoiseClusterStore`** (`noise_clusters` + `vec_noise`, restored to
   the fresh-bank DDL — the ladder step that already created them for legacy banks,
   `MigrateToV6Async`, is unchanged and unrenumbered). Round-trip tested against a real scratch bank.
   **Known limitation, not resolved here:** the persisted shape (`cluster_label`, `frequency`,
   `centroid_embedding`, `status`) bakes in "a cluster is the unit." It cannot cleanly hold a single
   project-wide weight vector the way a linear probe would need. This is inherited from the original
   (pre-ADR-0033) design and this task's own instruction to restore that exact schema without
   renumbering the ladder; redesigning it is out of this pass's scope and is flagged here for
   routing, not decided unilaterally.
2. **`NoiseFeedbackCollector`**, append-only: a confirmed-noise sample (a rejected write, or a
   `HermesProcessNoisePolicy` hit specifically — where Hermes is present, its confident matches are
   free labelled data, which is exactly what the research found missing everywhere) becomes its own
   row. No nearest-neighbour assignment, no merging — that decision belongs to whichever detector
   eventually plugs into the seam, not to this collector.
3. **`INoiseDetector`** — the seam. Pure and synchronous: vector + whatever samples the store holds
   for the caller's scope in, `NoiseFilterResult` out. No I/O, no embedding call, nothing
   probability- or distance-shaped baked into the interface. `NoOpNoiseDetector` — always clean — is
   the only implementation shipped and the only one registered in DI.
4. **Shadow/dry-run mode** (`NoiseShadowObserver`), gated by `noise.learner.shadow.enabled.global`
   (mirrors `noise.enabled`/`sweep.enabled`; **default off**). This is the highest-value piece here:
   it turns "we assume noise can reach the input" from an assumption into a per-install measurement.
   An operator runs shadow mode, reads what the detector *would* have flagged, and only then decides
   whether enforcement is worth it — and it generates exactly the labelled data the research named
   as the thing that would change its verdict (it wants a 200-item labelled set; this produces one
   from real traffic). Two hooks in `SqliteMemoryStore.WriteAsync`:
   - `ObserveStoredWriteAsync` reuses the vector `EmbedIfConfiguredAsync` already persisted for the
     row (one extra `SELECT`, not a second inference) and hands it to the detector. Never affects
     `Stored`/`Reason`.
   - `ObserveRejectedWriteAsync` feeds a confirmed rejection to the feedback collector.
   Shadow observations are never fed back into the store as training data themselves — only
   externally-confirmed rejections are — so nothing here can reinforce its own guesses.
5. **`noise.learner.enabled.global`** stays reserved and **unread** — a future enforce mode, not
   built until shadow mode (or the held-out study) produces evidence to justify it.
6. `SqliteMemoryStore` gains an 8-argument constructor overload carrying `INoiseShadowObserver`; the
   original 7-argument primary constructor is unchanged and its (private) observer field defaults to
   `NoOpNoiseShadowObserver.Instance` — a genuine, always-non-null Null Object, not a nullable
   injected parameter — so `TestData.CreateMemoryStore` (outside this task's file ownership) keeps
   compiling. Production DI resolves the 8-arg constructor because `INoiseShadowObserver` is
   registered; `NoiseLearningRegistrationTests` verifies this by reflection, not by assuming .NET's
   constructor-selection behaviour.

**Explicitly not built:** `ZeroShotEmbeddingNoisePolicy`'s three anchors as a live write-path policy
(0/50 recall, ADR-0033's original and still-valid finding on that specific implementation);
leader-follower centroid assignment as a write-path detector (93% singletons, this ADR); a linear
probe (0.946 AUC, in-sample only); any similarity-threshold setting (a threshold is itself a scoring
decision — removed from `NoiseConfigKeys` after being drafted mid-task, before it was ever read by
production code); orthogonality-gated promotion from "candidate" to "active" cluster status (no
caller without a promotion mechanism this pass does not build).

## Consequences
- **Positive:** the substrate — store, feedback path, settings gate, shadow-mode plumbing — is real,
  registered, and tested end-to-end (including against the real bundled MiniLM model, not a fixed-
  vector fake), independent of which detector eventually ships. Whichever wins (centroid, probe, or
  neither) plugs into `INoiseDetector` without touching the store, the feedback path, or the write-path
  hook.
- **Positive:** shadow mode is genuinely low-risk (never rejects, never mutates `Stored`/`Reason`)
  and is the mechanism that converts an assumption ("noise reaches the input on some install") into
  installation-specific evidence, for this operator or any other.
- **Positive:** the reused-vector design means shadow-mode evaluation cost is unrelated to the
  256-token-content embed cost (14.6–26.5 ms) that made a second inference a non-starter.
- **Negative:** as shipped, the subsystem does nothing observable — `NoOpNoiseDetector` never flags
  anything, and the shadow-mode switch defaults off. This is deliberate, not an oversight; it is
  discoverable by reading `INoiseDetector`'s doc comment and this ADR, not left implicit.
- **Negative, flagged for routing:** `noise_clusters`' schema assumes "a cluster is the unit." A
  linear-probe detector (or anything else that isn't sample-clustering) would need either a schema
  change or an awkward encoding (e.g., one row holding a weight vector under a sentinel label). Not
  resolved in this pass.
- **Negative:** two research verdicts and one architectural redirect happened mid-task on this
  branch. Both wrong turns are recorded above rather than silently corrected, because a future
  reader deciding whether to build the detector needs to see that the multi-centroid argument was
  tested and refuted by measurement, not merely superseded by a different opinion.
