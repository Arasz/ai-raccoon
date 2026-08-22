# Plan — post-delta next steps (rev 2, MoE-reviewed)

**Date:** 2026-08-22 · **Base:** main `771f7762` (delta fix PRs #424–#434, docs #438, rulings #439 merged; v1.30.1 released; PR #440 → 1.31.0 in flight) · **Status:** rev 2 — both MoE findings sets folded (structure REQUEST-CHANGES; facts F-1..F-10); see companion review record
**Sources:** `docs/work/2026-08-21-delta-review-fix-plan.md` (implemented, closed) · `docs/work/2026-08-22-delta-open-items-review.html` (owner gate G1–G6, feedback pending) · `docs/work/2026-08-22-code-mem-owner-gate-feedback.md` (8/8 APPROVE) · cross-session input from #405 and ai-raccoon-cc · MoE review 2026-08-22 (architect + code-reviewer, both opus)

Items marked **[gated: Gn]** proceed only per the owner's verdict on that card. Everything else has
a named owner and trigger. **Status-notes artifact** wherever this plan says "recorded":
`.ai-badger/status-notes.json`, section `postDelta`.

## In-flight / adjacent work

| Work | Owner | State | Notes |
|---|---|---|---|
| **PR #440** — VERSION 1.31.0 + What's-new | #405 session | open; checklist JSON committed but **unfilled** — no in-repo evidence of the 1.31.0 pass yet | What's-new is **three bullets** (#420; #431+#429; one unnumbered hardening line). #423 is never linked; nine delta PRs are not individually covered. S1 carries the coordination note. |
| **#436** — code-corpus prune gap | accepted by ai-raccoon-cc, **NOT started** (session wrapped) | ready-to-start; complete design brief on record (see S8) | cc picks it up next session, or it is reassigned if needed sooner. Not in-progress. |
| **PR #437** — engine output-shape contract + graph-pooled parity gate + tokenizer pin | ai-raccoon-cc (session wrapped) | open, CI green, three mutation-proven gates | disposition step S10. These gates would have caught #416. |
| **Owner gate G1–G6** | owner | form live, watch armed (1 h cap, re-armed on expiry) | decisions only |

## Steps

### S1 — 1.31.0 sync-blob compat note (ungated) + optional named refusal **[G1 gates only the escalation]**
**What ships regardless of G1** (it documents already-merged behavior): a compat note stating —
precisely — that **an encrypted bank pushed by ≥1.31 writes a framed blob that a <1.31 client
cannot pull** (the keyed open fails page authentication: loud refusal via
`SyncCorruptFileException`, never silent corruption, live bank untouched). **Unencrypted banks are
unaffected** — `SyncService` pushes them unwrapped (`SyncService.cs:132-134`). Upgrade both ends
of an encrypted sync pair together.
**Where:** `docs/reference/agent-memory-server.md` (sync section) and
`docs/explanation/architecture.md` (§ sync framing — already documents the mechanism; add the
version-skew sentence). There is **no** `docs/how-to` sync page; none is created for one sentence.
The release-notes half rides PR #440: **#405 owns the `README.md` edit on its branch**; d6 supplies
the exact sentence (this section is that sentence) plus the observation that #440's What's-new
currently leaves #423 unlinked and nine delta PRs aggregated — #405 decides whether to expand it.
**[gated: G1-CHANGE]** escalation: a named format-version refusal on pull, so a future skew fails
with "remote blob requires ≥1.31" instead of a generic corrupt-file error. If taken:
`SyncServiceRemoteBlobTests` case, RED first.
**Owner:** d6 (docs), #405 (README). **Acceptance:** the two doc files carry the corrected
sentence; the README note is in the merged #440; if G1-CHANGE, the refusal test RED→green.
**Trigger:** docs half now; README half before #440 merge; escalation on G1 verdict.
**DEFER path:** escalation question parks in S5's table, re-raised at the next sync-format change.

### S2 — Project-identity ADR (H-C / O2) **[gated: G2]**
Unchanged in substance from rev 1; **all four** scope constraints below must be addressed:
1. The `mcp-token` is per-data-root, minted 0600 **on POSIX** (Windows inherits the data-root ACL,
   ADR-0020 non-goal), at server start — it authenticates **the bank, not the project**; project
   isolation today is a naming convention over a shared credential. The ADR states this plainly.
2. The code corpus is hard project-scoped (`vec_code` partitions on `ctx=project_id`,
   `MemorySchema.cs:481,487`); any redesign preserves per-project vec0 partitioning.
3. The search-quality exclusion's privacy reasoning assumed caller-named projectId; revisit it.
4. O6's `allProjects=true` (verified: no access check runs on that branch,
   `PromotionTools.cs:39-47`) was built without a global mode so as not to pre-empt this design;
   if the ADR lands a real mode, O6's shape is revisited then.
**Owner:** d6 (architect lane drafts; owner ratifies). **Priority:** first (#405 concurs).
**Acceptance:** ADR merged addressing all four; lists what it does NOT decide; H-C recorded closed
as "designed, implementation scoped separately" in the status notes.
**Trigger:** G2 verdict. **DEFER path:** H-C parks in S5 with re-raise at the next security review.

### S3 — Away-mode ratification bookkeeping **[gated: G3]**
On APPROVE: the awm entries for O1/O3/O4/O5/O6 are marked ratified in
`.ai-badger/status-notes.json` § `postDelta.ratifications`. On CHANGE/REJECT of a subset: a
follow-up task is scoped per the owner's note; merged code stands until it lands.
**Acceptance:** the section lists all five flags with their verdict. **Trigger:** G3 verdict.
**DEFER path:** flags stay listed as "made-under-away-mode, unratified".

### S4 — Port-placement rule (H20, re-scoped) **[gated: G4]**
**Corrected inventory:** exactly **three** Infrastructure-declared ports, one per original
finding-line: `IWorkspaceService` (`Infrastructure/Workspace/IWorkspaceService.cs:5` — singular;
`IWorkspaceStore` is already in Core), `IPromotionQueuePruneStore`
(`Infrastructure/Sqlite/IPromotionQueuePruneStore.cs:13`), `IWatchRegisteredStore`
(`Infrastructure/Watch/IWatchRegisteredStore.cs:9`). None is referenced from Core — all consumers
are in the host assembly — so rev 1's "referenced by Core consumers" predicate would never go RED
(both MoE lanes falsified it independently).
**Re-stated predicate:** *any public interface declared in `AiRaccoon.Infrastructure` that is
referenced from the host assembly (`AiRaccoon`) and whose member signatures contain no
Infrastructure-declared type.* Expected RED set is pre-declared as exactly the three ports above —
the observed RED set must equal it; a fourth hit is a finding to report, not a silent move.
**Known non-mechanical part, in scope:** `IPromotionQueuePruneStore` returns
`PromotionQueueOrphanReport`, currently declared inside `SqlitePromotionQueueStore.cs:8` — the
move includes extracting that DTO to Core (plain record, no behavior).
**Fallback (`ask-if-simpler`):** if the predicate cannot be made honest without tuning it to the
known three, do the three moves without the rule and record H20 closed-by-moves.
**Owner:** d6 (dotnet-engineer lane). **Acceptance:** rule RED on today's placement with exactly
the pre-declared set (pasted), green after the moves; suite green; no behavior change.
**Trigger:** G4 verdict; sequenced after #440 merges and after S8 if S8 has started (shared-tree
hygiene). **DEFER path:** H20 parks in S5 with the corrected inventory recorded.

### S5 — Parked-items ledger (ungated bookkeeping; G4 changes only the split)
Recording is unconditional — a stalled gate must not leave items unrecorded. Rows for
`.ai-badger/status-notes.json` § `postDelta.parked`:
- **H21** (`IMemoryStore` decomposition): parked, **quiet-window-only**, defined as: *no open PR
  touching `IMemoryStore`/`SqliteMemoryStore` in any session; re-evaluate at each release
  boundary.* Duplication evidence on record: `IMemoryStore` declares the same four settings
  members as `ISettingsStore` (`Get/Set/GetByPrefix/DeleteSettingAsync`) with no inheritance
  relation (`IMemoryStore.cs:77,80,103,107` vs `ISettingsStore.cs:9-16`). #405 ranks it LAST.
- **H16** (CI matrix): parked; owner = release-checklist owner; review at the next release boundary.
- **H1** (`ranking` rank-derived; λ=0.1): **UNBLOCKED — rev 1 was wrong.** 0814 owner question 7
  was answered on 2026-08-15 (ADR-0058, Accepted; `docs/plans/2026-08-14-…:296`). H1 is now
  plannable: parked to the next tuning round **with M5**, no longer "cannot be planned".
- **M5** (out-of-sample retrieval control): next tuning round; reuse the four OQ5 repos named in
  code-mem gate D2 (vscode, aspnetcore, deepseek-harness, semantica).
- **M10** (README 1.29.0 What's-new): **CLOSED now** — `README.md:37` already carries the 1.29.0
  entry; no #440 dependency (rev 1's supersede condition was measured false: #440 backfills
  nothing).
- **arch F2** (RRF/affinity logic outside Core): carried from the delta plan's table, dropped by
  rev 1 — restored; parked with H20's disposition (a placement concern; if S4's rule lands, extend
  it or record why not).
- **QA F3** (full-suite seed-embed slowdown mechanism): carried, still-open diagnostic; parked;
  re-raise on next occurrence (Q1's budget now makes it diagnosable).
- **#435** (bge-m3 weak vector leg): **OPEN, not closed** — rev 1 was wrong. Code-mem gate P3
  approved a *conditional* proceed; the condition has since resolved **negative** (cc's cheap
  comparison refuted the pooling hypothesis, cosine 1.00000000; the 2.5 h re-embed was never
  spent; bge-m3 verified simply weaker on this corpus, −0.233 nDCG@5, p=0.001). Action: post that
  evidence to #435 and recommend closure to the owner; cc's kept `bank-fixed` artifact + model
  path (issue thread) preserved for any OQ5 re-measurement.
**Acceptance:** every row above present in the status notes with its condition; #435 comment posted.

### S6 — jsaa-memory.db removal, BOTH halves of #414 **[gated: G5]**
**S6a — immediate half (any PR, before the rewrite; rev 1 omitted it entirely):** the fixture is
still tracked (`tests/AiRaccoon.Tests/AiRaccoon.Tests.csproj:40`, `CopyToOutputDirectory`) and
consumed by 5+ test files (`StructureFusionGateTests.cs:60`, `SectionTargetedRetrievalTests.cs:364`,
`QueryConstructionTests.cs:348`, `RelativeScoreFloorTests.cs:44`, `BaselineMetricsTests.cs:408`).
Replace with a synthetic fixture, delete the file from HEAD, suite green — #414's own
done-criterion. Without S6a the rewrite reds the suite and cannot close the issue.
**S6b — history rewrite:** `git filter-repo` + force-push. **Hard precondition (MoE blocker):**
*no open PR or branch based on pre-rewrite main* — #436, #437, #440 all merged or explicitly
re-planted; a branch merged after the rewrite would resurrect the blob. Window: after #405's
checklist broadcast AND after S8/S10 resolve. Force-push is hook-blocked for agents: the owner
executes the push or explicitly lifts the block for the one operation. All sessions re-clone.
**Acceptance (object-level, on a FRESH clone of the rewritten remote — not a reset local):**
first, the RED: `git rev-list --objects --all | grep jsaa-memory.db` on a pre-rewrite clone prints
the blob (recorded); then on the fresh clone the same command prints nothing AND
`git cat-file -e <old-blob-sha>` fails; suite green (S6a's synthetic fixture); every surviving
branch re-planted (`rebase --onto` or patches) with its diff verified identical; #414 closed.
**Trigger:** G5 verdict + the precondition. **DEFER path:** S6a still happens (it is an any-PR
item); only S6b parks, tracked by #414.

### S7 — 1.29.0 server-lifecycle: time-boxed diagnosis or evidence-based close **[gated: G6]**
Unchanged framing (unexplained, not diagnosed; two hypotheses falsified; peers' negative repros
never exercised a fast rebind). **Acceptance added (rev 1 had none):**
- *Subject:* the released 1.29.0 binary (by SHA, per the checklist-proving memory), scratch
  data-root, scratch ports only (never 7721).
- *Grid:* stop-to-bind delay ∈ {0, 50, 100, 250, 500, 1000, 2000} ms × ≥20 iterations per cell,
  same port re-bound each time.
- *Positive control first (`prove-the-check-fails`):* deliberately hold the port with a second
  process and record that the harness reports the failure signature (probe timeout / EXIT=18);
  no "not reproducible" claim is valid before this control is on record.
- *Artifact:* results table in `docs/work/2026-08-22-1290-lifecycle-repro.md` — reproduced (file
  the defect with the trigger) or bounded-negative (fast rebind exercised, N runs, no repro).
**REJECT branch:** close as not-reproducible-on-1.30.x citing both falsified hypotheses and the
fast-rebind caveat. **DEFER:** stays open, unexplained, listed in S5.
**Owner:** d6 (dotnet-engineer lane). **Trigger:** G6 verdict.

### S8 — #436 code-corpus prune gap (approved as code-mem P2; NOT started)
Accepted by ai-raccoon-cc, deliberately not begun (session wrapped). Design brief on record from
cc — mirror #420's ingest-then-prune onto `code_entries`: `ICodeIngestor.IngestFileAsync` reports
chunk hashes; memory/code hash sets stay disjoint; `PruneChunksNotIn` gains a `code_entries` leg
using `DeleteCodeBySourcePath`'s predicate (exact path OR subtree prefix, `MemorySql.cs:780-784`);
**the prune keys on `FingerprintEligible`/whitespace-only** (`IFileIngestor.cs:24`), never chunk
count — `NoOpCodeChunker`'s zero rows must not read as "delete everything", pinned by its own test;
B1-shrink + B2-sibling mirrors minimum; RED-first + mutation-proven to #420's bar. All design
premises source-verified by the MoE facts lane.
**Owner:** cc next session, or reassigned by d6 if needed sooner. **Acceptance:** per the brief;
PR merged after pulling main past #440. **Not in progress — do not record it as such.**

### S9 — #422 ONNX token-cap re-measurement (code-mem gate D1, APPROVE — dropped by rev 1)
Re-measure the bundled/downloaded ONNX graphs' true token cap; the S4 activation gate and the
chunk budget follow the measurement (the chunker-budget-vs-real-512-ctx question of #422).
**Owner:** d6 (dotnet-engineer lane). **Acceptance:** measurement recorded on #422 with method;
activation gate + budget constants updated to follow it (or recorded as already correct); tests
pinning the budget relationship RED-first if constants change. **Trigger:** after #440 merges
(shares the embedding surface the checklist exercises).

### S10 — PR #437 disposition (engine contract gates)
cc's parting PR: engine output-shape contract, graph-pooled parity gate (skips without the
2.27 GB weights), tokenizer reference pin — three mutation-proven gates that would have caught
#416. **Recommendation: land it.** **Owner:** d6 reviews at the seam and merges under the P1
standing policy, or flags substantive findings back to a follow-up.
**Acceptance:** review record on the PR; merged or findings posted. **Trigger:** now.

## Sequencing

**Hard constraints (dependency, not preference):**
```
S1(README half) ─before→ #440 merge
S9, S4          ─after→  #440 merge
S6b             ─after→  S6a AND #440 AND S8 AND S10 all merged/closed (no pre-rewrite branches)
S8              ─whenever cc (or a reassignee) starts; independent of everything except #440 pull
```
**d6's ordered queue (the scarce resource is this session, not files):**
S10 → S1(docs half) → S5 (+#435 comment) → then by gate verdicts: S2 → S4 → S7 → S9, with S6a as
the first coding slot after G5-APPROVE and S6b at the very end. Steps marked (lane) run as
delegated subagent lanes; the queue orders their dispatch, not their wall clock.

## What this plan explicitly does not do

- Re-open any merged delta decision (G3 covers ratification; overturns become new tasks).
- Implement a project-identity mechanism — S2 is the design only.
- Decompose `IMemoryStore` (H21: quiet-window-only, defined above).
- Spend the #435 re-embed (P3's condition resolved negative; S5 posts the evidence and
  recommends closure).
- Create a sync how-to page (no such page exists; one sentence does not justify one).

## Owner flags

- **F1:** G-card verdicts drive S1–S7; the gate form is the single decision surface.
- **F2 (drift guard, corrected):** the sync framing is load-bearing in **four** artifacts — the
  wrap header in `SyncBlobAuthenticator.cs`, `docs/explanation/architecture.md:535-590` (full
  mechanism description), the release-notes compat sentence, and
  `docs/reference/agent-memory-server.md`'s sync section. A v2 wrap moves all four in one PR.
- **F3 (was: re-ask question 7) — withdrawn:** question 7 is answered (ADR-0058). Nothing to ask.
