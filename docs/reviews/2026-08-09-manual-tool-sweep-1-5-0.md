# Manual tool sweep — AiRaccoon 1.5.0

**Date:** 2026-08-09
**Target:** `1.5.0+b8f736579ccb7a971adcb44391b523e88c0081de` — pushed to nuget.org (all six RID payloads
plus the shell package) at 14:25 UTC, `dotnet tool update -g ai-raccoon` run once the search API
resolved it, and the shared backend on `:7721` restarted so every stdio proxy on the machine talks
to the new binary. OTLP export was enabled for the restart (`OTEL_EXPORTER_OTLP_ENDPOINT` pointed
at a throwaway local HTTP listener) specifically so this sweep could verify traces/metrics on the
wire rather than trust the CLI's self-report.
**Method:** live MCP tool calls over the running server (this session's own `ai-raccoon` connection,
which routes through the same ADR-0020 proxy every other client on the machine uses), against a
throwaway project id (`manual-150-check`) for mutating calls and the real `ai-raccoon` project for
read-only checks and the promotion-queue/OTLP verification. A raw `sqlite3 -readonly` read against
the live bank confirmed the one finding below that a tool response alone couldn't settle.
**Coverage:** all 22 exposed tools invoked at least once. All six defects from the 1.3.1 sweep
(`docs/reviews/2026-08-08-manual-tool-sweep-1-3-1.md`) re-tested directly.

## Result

Five of six 1.3.1 defects are fixed. One new defect found, more serious than anything in the prior
sweep: the background shared-extraction job's `promote` mode can silently mutate the shared tier
while reporting the call as failed.

| # | Severity | Area | Status |
|---|---|---|---|
| D1 (1.3.1) | High | `memory_workspace_consolidate` schema/description mismatch | **Fixed** |
| D2 (1.3.1) | Medium | workspace family — 4 answers for unknown workspace | **Fixed** |
| D3 (1.3.1) | Low | unknown hash: `memory_share` vs `memory_delete` | **Improved** — `memory_share` now typed `unknown-hash`, no more raw exception. The two-contract asymmetry itself is unchanged and was already ruled a product question, not a defect, in 1.3.1. |
| D4 (1.3.1) | Low | `memory_stats` leaking every project's contexts | **Fixed** |
| D5 (1.3.1) | High | one-entry shared tier capturing every `scope=all` search | **Fixed** |
| D6 (1.3.1) | Low | `consolidate` reporting promoted entries as also discarded | **Fixed** |
| D7 (new) | **High** | `memory_share_extract(mode=promote)` | Throws `unknown-hash` on a stale queued hash **after** already promoting and dequeuing other candidates — the error response misrepresents what the call did |

## D1, D2, D4, D5, D6 — re-verified fixed

Reproduced each 1.3.1 repro step against 1.5.0:

- `memory_workspace_consolidate(keep: ["all"])` on a real workspace entry → `{"promoted":1,"discarded":0}`. The array form works (it always did); the description now correctly reads `"promotes the kept hashes (or ['all'])"` — matches the schema, no more scalar-string trap. `discarded` correctly reports 0 when nothing was thrown away (D6).
- `memory_workspace_status` and `memory_workspace_discard` against a workspace id that was never created now both return `unknown-workspace: Workspace '…' does not exist for project '…'` — the same typed refusal `memory_write` already had. All four workspace tools now agree.
- `memory_stats(manual-150-check)` returns `"contexts":["project:manual-150-check"]` only — no other project's names leak through anymore.
- Shared a probe entry, then ran the four 1.3.1 off-topic queries (`"banana pancake recipe"`, `"kubernetes ingress controller TLS termination"`) through `scope=all` against the real `ai-raccoon` bank (2,687 entries). The probe did not appear in any result; top hits were genuinely on-topic project documents. The per-context-then-global-fuse fix described as shipped in the 1.3.1 doc holds under this sweep.

Test entries and the shared-tier probe were deleted afterward — `memory_delete` on both the
project-scope hash and the shared-scope hash (which takes its own hash and a `shared/`-prefixed
path, confirmed still true) is enough to fully unshare; that resolves the "no unshare API" concern
recorded in the 2026-08-09 memory-as-a-communication-layer task for the *manual* `memory_share`
path — this sweep did not touch the auto-extraction irreversibility that task was actually about.

## D3 — improved, not fully closed

```
memory_delete(unknown hash) -> {"deleted": 0}          (unchanged)
memory_share(unknown hash)  -> unknown-hash: No entry…  (was: raw "An error occurred…" exception)
```

The remaining asymmetry (silent-zero vs typed-refusal) is the same open product question 1.3.1
flagged; what mattered — the untyped leaked exception — is gone. Separately, `memory_write` was
found to still leak an untyped `"An error occurred invoking 'memory_write'."` when called with a
parameter name the schema doesn't recognize (`value` instead of `content`) — the same class of bug
as 1.3.1's D1, now observed on a second tool. Not scored as a new defect since it is the exact
mechanism D1 already covers (argument-binding failures pass through `ToolRefusals.Filter`
untyped); recorded here so it isn't rediscovered as new.

## D7 — `memory_share_extract(mode=promote)` silently succeeds, then reports failure

This is the headline finding, and it took a second pass to confirm — the first read looked like a
simple crash.

**The surface symptom.** Calling promote against the real, 248-row `ai-raccoon` queue:

```
mode=promote, limit=3 -> unknown-hash: No entry with hash '2260be96e2265ba9337cf5eee9e1f08321eeba67270ac0325eef81923f340c86' in project 'ai-raccoon'.
mode=promote, limit=1 -> unknown-hash: No entry with hash '9e2b9d975ab75553cd3774ab74b2331e8a3f8c29b1054ddeab4658d9b834bfbf' in project 'ai-raccoon'.
```

Both hashes are genuinely-queued candidates (verified against a `memory_promotion_list` dump taken
minutes earlier) — not bad input. Read alone, this says "promote is broken, nothing gets shared,"
and matches the standing symptom: `extract list` reports `enabled: True, mode: promote, interval:
30 min`, yet before this sweep the shared tier was empty (`scope=shared` search: zero results,
confirmed on four different real-content queries) and the queue sat pinned at the global 1000-row
cap with its oldest row **73,010s and climbing in lockstep with wall-clock time** across five
`memory_search` calls spread over several minutes — i.e., that specific row had not been touched in
over 20 hours.

**What actually happened, confirmed by reading the bank directly.** The `mode=promote, limit=3`
call's queue count dropped `ai-raccoon` from 248 to 245 (net **3** rows gone) despite reporting an
error. A direct `sqlite3 -readonly` query against `~/.ai-raccoon/memory.db` found a new row:

```
2c6cb7cda4f4f15bcd1858895c9b8589ea5f0da789471b55a1775e0983fabf47 | shared/ac50c59….md | scope=shared | embed_state=embedded | created_at=1786286450
```

`ac50c59….md` is the path of the queue's own top-scored candidate (score 3.9, the
"AiRaccoon deployment… one bank per install" note). It is now fully embedded and independently
confirmed retrievable via `memory_search(scope=shared)`. **The call promoted at least one real
candidate — indexed, embedded, shared — while its return value told the caller the operation
failed.**

**The likely mechanism.** `promote` appears to iterate queued candidates score-descending,
promoting/dequeuing each as it goes, and throws on the first one whose hash no longer resolves in
the committed-entries table — plausibly a candidate whose source content was re-chunked or
re-embedded (heal pass, schema migration) after it was queued, leaving the queue holding a hash the
entries table no longer has. The loop does not catch that per-candidate failure; it propagates as
the whole tool call's result, after already committing the earlier candidates' work.

> **Mechanism correction, 2026-08-09 (from the WP1/WP2 follow-up code investigation).** This
> under-diagnosed the defect. At 1.5.0, `PromotionQueueService.PromoteAsync` claims a candidate off
> the queue (`queue.DiscardAsync`) *before* sharing it (`store.ShareAsync`) — two separate
> connections, no enclosing transaction, and no try/catch around the pair. So the candidate whose
> hash no longer resolves is not merely misreported: it is already gone from the queue and was
> never shared — **permanently lost, not retried on the next pass.** The misleading error message
> is the second-order symptom; silent, unrecoverable data loss is the defect.
>
> The root cause this sweep did not identify: nothing invalidated a `promotion_queue` row when the
> `entries` row it referenced was deleted or re-chunked. Confirmed live: 19 orphaned queue rows (17
> `ai-raccoon`, 2 `ai-badger`), every one pointing at a watched ADR that was edited and re-ingested —
> `SqliteMemoryStore.ReplaceFileAsync` deletes the old chunk rows and inserts new ones under new
> content-derived hashes, and nothing dropped the queue's now-dead reference. On the `ai-raccoon`
> queue specifically, the rank-1 candidate (score 3.794) was one of the 17 orphans, along with ranks
> 13, 14, 31, 41, 47, and 55 (and ten more further down the queue) — so with the background job's
> default `limit` of 20, three of those orphans (ranks 1, 13, 14) sat inside a single promote batch,
> and every pass destroyed the top candidate while promoting nothing. That gap was asserted as
> *intended* behavior in a then-green test, `Sweep_NeverTouchesTheQueue`.
>
> Fixed by a later work package: an `AFTER DELETE ON entries` trigger now drops the matching
> `promotion_queue` row (see [ADR-0023](../adr/0023-promotion-queue-entries-delete-invalidation.md)),
> and `PromoteAsync` now wraps the claim-then-share pair in a try/catch that reports a per-candidate
> `stale-hash`/`share-failed` failure instead of aborting the batch
> (`src/AiRaccoon.Infrastructure/Promotion/PromotionQueueService.cs`).

**Why this matters more than a normal crash.** A caller — human, agent, or the 30-minute background
hosted service — sees `unknown-hash` and has no way to tell from the response that partial,
real, cross-project-visible work already happened. For the automated path specifically, this is a
plausible full explanation for the standing symptom: a scheduled pass that hits this exception on
its second or third candidate every time would look, from the outside, exactly like "queue frozen,
nothing shared, mode=promote configured but inert" — which is what this sweep observed on its first
pass, right up until a manual `memory_share_extract` call showed otherwise.

**Action taken, not deferred:** flipped the global extraction mode back to `propose`
(`ai-raccoon extract mode propose`) so the background job stops silently mutating the shared tier
under a misleading error contract while this is triaged. `propose` only ranks and queues candidates
for review — no cross-project sharing, fully reversible. This is a config change, not a code fix;
the stale-hash root cause in the promote path is still open.

**Not yet determined:** whether the stale hash is a re-embed/re-chunk race, a missed
queue-invalidation on entry update, or something else — would need to read
`SharedExtractionService`/the promote code path, out of scope for a wire-level manual sweep.

## OTLP traces and metrics — verified on the wire

The restart enabled export to a throwaway local capture listener rather than trusting
`serve observability otlp`'s self-report. Confirmed:

- `POST /v1/traces` and `POST /v1/metrics` arrived as `application/x-protobuf`, sized 500–5,000
  bytes — consistent with real batches, not empty exports.
- A captured metrics payload decodes (via `strings`) to genuine OTel content: `service.name=
  ai-raccoon`, correct `service.instance.id`/`telemetry.sdk.*` resource attributes, and the
  `System.Runtime` meter's `dotnet.gc.*` instruments with real descriptions and unit strings — the
  .NET built-in telemetry catalog work (2026-08-09) is exporting as designed.
- Traces fired in response to live tool-call activity (`memory_write`, `memory_search`, `memory_list`
  all triggered flushes within the same request window), not on a fixed timer only.

Not checked: full protobuf decode of span names/attributes (no proto codec available in this
environment) — the wire-level POST + payload-size + metrics-content checks above are the
verification depth this sweep reached. A visual check against Rider's own OTLP dashboard (satellite
listening on `:17011`, noted in prior work) was not attempted.

## Promotion queue — quality assessment (pre-D7-fix state)

Measured via `memory_promotion_list(ai-raccoon, limit:1000)` → 249 rows, analyzed with a script
against the saved response rather than read inline (192,957 characters — the "queue cannot be
reviewed in context" observation from 1.3.1 still holds at this row count).

**Scoring is healthy — a real improvement over 1.3.1.** 197 distinct scores over 249 rows (was 59
over 288, with 72% stuck at two values). Range 2.59–3.90, mean 2.98. No trace of the retired v1
vocabulary (`cross-project`, `recent`, `accessed` as sole reasons) in the top reason combinations —
the re-scoring hole 1.3.1 diagnosed (a propose pass only ever re-touching the top 20) appears to be
closed; today's 09:23 log entry recorded exactly that fix landing. Only 5/249 rows lack a
`sourceFile` (was 15/288).

**Still true from 1.3.1: `mid-sentence` labels but does not penalize.** 86 of 249 rows carry that
reason tag; their average score (2.999) is marginally *higher* than the queue overall (2.979), not
lower. Same finding, same magnitude, unchanged by the v3 re-scoring fix.

> **Correction, 2026-08-09 (from the WP5 follow-up code investigation).** This is imprecise:
> `mid-sentence` *does* penalize. `PromotionContentEvidence.Evaluate` subtracts a flat `-0.18`
> after the archetype-adjustment clamp, deliberately, so a saturated-high chunk still gets demoted
> for opening mid-sentence (`src/AiRaccoon.Core/Memory/PromotionContentEvidence.cs`, see the comment
> immediately above the subtraction). But the penalty is wired into the **doc-channel** evaluator
> only — `OrganicRefinement.Apply` and `PromotionContentEvidence.EvaluateAutoMemoryNote` never read
> `CandidateFeatures.MidSentence`, so on the `OrganicNote`/`AutoMemoryNote` channels it is extracted
> and never applied. The aggregate above looked inert because of archetype confounding, not because
> the penalty is missing: restricted to the `adr` channel alone and freshly re-measured against the
> live queue, mid-sentence rows average 2.997 (n=78) against 3.026 (n=76) for the rest — a ~0.03 gap
> where the code applies -0.18, because ADR chunks that open mid-sentence also tend to carry more
> positive rule-language evidence that offsets it in the raw, cross-channel average.

**New observation, not verified further:** one high-scoring row (3.9, the deployment note discussed
above) carries the reason `foreign-subject` despite being centrally about `ai-raccoon` itself — the
value text does enumerate sibling project ids (`ai-raccoon, job-search-ai-assistant, ai-badger`) for
context, which may be what the tagger is keying on. Flagged as a possible false-positive in the
reason-labeling, not confirmed against the scoring code.

> **Correction, 2026-08-09 (from the WP5 follow-up code investigation).** Confirmed against the
> scoring code, and the sign matters: `foreign-subject` is a **bonus**, not a penalty — the scorer
> adds `+0.25` in the doc channel and the auto-memory-note channel, and `+0.20` in the organic
> channel, on the theory that content genuinely about another project is exactly what belongs in the
> shared tier (`PromotionContentEvidence.cs`, `OrganicRefinement.cs`). The defect is that the
> detector pays that bonus to the wrong candidates: it is a case-insensitive substring scan for
> another project's id (or a known alias) within the first 250 characters of the value, with no
> notion of subject or centrality (`CandidateFeatureExtractor.Extract`,
> `src/AiRaccoon.Core/Memory/CandidateFeatures.cs`). The flagged row is centrally about
> `ai-raccoon`'s own deployment; it trips purely on its own parenthetical —
> `(project ids: ai-raccoon, job-search-ai-assistant, ai-badger)` — which the detector had no way to
> distinguish from a chunk actually about `job-search-ai-assistant`. (A later work package strips
> bracketed spans, `(...)`/`[...]`, before the subject scan, so this specific parenthetical would no
> longer trip it — but a plain-text mention outside brackets still would; the underlying
> "no notion of centrality" defect is not fully closed.)

**The queue was not draining — now explained by D7, not a separate defect.** 1000/1000 (global cap)
before this sweep, oldest row 20.5h and growing. This is not a new capacity-tuning problem to chase
separately; it is the observable consequence of D7 above.

## Addendum — promote verified live on the fixed binary (2026-08-09, evening)

D7's mechanical half is fixed and proven against the real bank. The owner authorised installing a
locally built tool so the backend could run the fixed code before 1.6.0 publishes; it was packed as
**1.5.2** (below 1.6.0, so the eventual nuget release supersedes it) and the backend was cycled onto
it — `/observability` reported `1.5.2+b8b12cbf`, confirming the running server, not just the CLI.

`memory_share_extract(ai-raccoon, mode=promote, limit=5)` then returned:

```
promotedHashes: 5   skippedDuplicates: 0   failures: []
```

with perfect conservation on the bank: queue 218 → 213 (−5), shared tier 1 → 6 (+5), all six
`embed_state=embedded`, orphaned queue rows 0 before and after. Nothing was dequeued without being
shared — which is precisely the loss this defect caused, so this is the acceptance test for it.
The earlier orphan backlog was cleared the same evening with the new `ai-raccoon extract prune
--apply` (30 → 0, idempotent on a second run).

**But the ranking is not fixed, and it should not be switched back on yet.** Inspecting what those
five promotions actually were:

| # | opening of the promoted value | shape |
|---|---|---|
| 1 | `# 0013 — Extension host hook surface: drop OnSweepAsync…` | ADR header chunk, project-internal |
| 2 | `\| Invariants \| C1/C2/C5 rank 1 \| **1/1/1** (C2 improves 5→1)` | a bare markdown table row |
| 3 | `> \`AIRACCOON_SCORING_EVAL_FIXTURE\` is set. That is a known…` | blockquote fragment |
| 4 | `measurably less "framework-free" than it was. - **Positive:**…` | **starts mid-sentence** |
| 5 | `identical to the committed ones — the whole 96-point grid is unmoved` | **starts mid-sentence** |

Four of five are chunk fragments and two literally open mid-sentence — the exact signal that is
computed and then dropped in the `OrganicNote` and `AutoMemoryNote` channels, and worth only `-0.18`
in the doc channel against an `adr` prior of 2.55. This is the quality half of the sweep's finding,
unchanged, now demonstrated on live promotions rather than inferred from queue scores.

So `extract.mode.global` stays on `propose`. At the default `limit: 20` per project on a 30-minute
interval, flipping it would push roughly a hundred such fragments per pass into a tier that is
**sweep-exempt and visible to every project** — and this repo has already had to wipe that tier twice
for the same reason (see the 2026-08-08 entries in `.ai-badger/state.json`). The mechanical fix
removed the data loss; it did not make the queue's top-ranked candidates worth sharing.

The five test promotions were deleted afterwards (`memory_delete` on each shared hash); their
project-scope source entries were untouched, so the shared tier is back to its single pre-existing
row and no content was lost.

**What would close this properly:** commit a labeled calibration fixture so
`PromotionScoringRealDataTests` can run, then tune the chunk-boundary signals against it. Until that
exists, any weight change is a guess — which is why this task changed detectors and left every weight
alone.


## Method notes

- Driver: this session's own live MCP connection (same proxy path every other client on the machine
  uses), not a standalone script — every call in this doc is a real tool invocation against the
  shared backend.
- Restarting the shared `:7721` backend to pick up the new binary affects every concurrent session
  on the machine; ADR-0020's probe-and-respawn made reconnection transparent, no session needed
  manual recovery.
- One `sqlite3 -readonly` read against the live bank was used to settle D7, after the tool-level
  evidence (two different error hashes, a queue-count drop the errors didn't explain) was
  suggestive but not conclusive on its own.
