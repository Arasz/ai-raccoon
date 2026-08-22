# Plan — post-delta session 3 (rev 1.2 — WP11's gate answered; G1–G15 still pending)

**Date:** 2026-08-22 · **Base:** main `021d6c17` (v1.32.0 released 14:00:31Z **and published to
nuget.org**; PRs #463/#464/#466/#467/#468 merged) · **Status:** rev 1.2 — **G1–G15 still
pending**; **G16–G19 answered** (`docs/work/2026-08-22-post-delta-3-wp11-feedback.md`), so WP11 is
open. No other gated work package starts until its gate is answered, except the two marked *ungated* ·
**Task:** `post-delta-3` · **Lane:** architect (plan + gate), Opus.

**rev 1.2 — gate answered (WP11 only).** The owner ruled on G16–G19 in
`docs/work/2026-08-22-post-delta-3-wp11-feedback.md`: **G16 APPROVE** (ORT intra-op cap at
`max(1, cores/2)` via `embedding.threads`, gated on `MiniLmGoldenVectorTests`), **G18 APPROVE**
(one key `maintenance.embed-rows-per-run.global`, default 128, no pacing delay), **G19 APPROVE**
(bank settings on the existing `/settings` route) — all three stand exactly as this plan first wrote them.
**G17 CHANGE**, verbatim: *"We want to use one system, based on bounded channels - not a semaphore -
exactly the same solution as for metrics. It should be a separate 'topic' than for metrics - but I
want you to extract the channel based events pump. We can even use single pump with round robin
consumers with a limited processing budget"*. §WP11's design and PR split are rewritten to follow it:
the `SemaphoreSlim` is gone, an `EventPump<T>` is extracted (Finding (c) records what the metrics
path actually is today — **not** a `Channel<T>`, but a bounded queue with drop-on-full, which is the
same contract), metrics becomes one topic and embed another, and WP11 is now **four** PRs (A, B1, B2,
C) instead of two. The round-robin variant the owner offered is evaluated and **rejected for two
topics**, with the reason recorded — see §WP11 *Chosen / Rejected*.

**Sources** (all re-verified against the tree/API today, not quoted from a prior document):
`gh issue view 414/436/454/455/459/465`; `docs/adr/0089-the-project-id-is-a-guidv7-and-that-is-not-access-control.md`;
`tests/AiRaccoon.Tests/Integration/ParityGateTests.cs`; `src/AiRaccoon.Core/Memory/SearchResults.cs`;
`src/AiRaccoon.Infrastructure/Embedding/Download/ModelDownloadPlanner.cs`;
`gh api repos/Arasz/ai-raccoon/activity?activity_type=force_push`; `gh run view 32577511356`;
nuget.org flat-container index for `ai-raccoon`; `docs/work/2026-08-22-post-delta-next-steps-plan.md` (rev 4, shape);
`docs/work/2026-08-22-post-delta-continuation-plan.md` (parked-items ledger).

## Session todo

1. Open the draft PR carrying this plan; generate the owner gate form from §*Owner gate*.
2. WP7 — ref-rewrite investigation + mitigation draft (ungated; starts now).
3. WP5 — S6b runbook + verification script with a recorded failing run (ungated).
4. Fold the gate answers into rev 2; architect re-reviews the plan before any gated WP opens.
5. WP1 — #465 export `search.adjustment` (G11, shaped by G8).
6. WP2 — #459 a graph-baked pooled output beats the ST flags (G10).
7. WP4 — split the parity gate's p95 leg into its own benchmark fact (G7).
8. *(WP6 moved to the end — owner ruling 2026-08-22 ~19:38: it runs **LAST**.)*
9. WP3 — #436 the re-ingest prune reaches `code_entries` (G12).
10. WP8 — #454 AND-under-match anchor (G13); WP9 — EventId disposition (G6).
11. WP11 — the embed/ingest load governor (**gate answered**, rev 1.2): four PRs in order —
    **A** the ORT thread cap (waits for #475), **B1** extract the bounded-channel `EventPump<T>` and
    move metrics onto it with its existing tests unmodified, **B2** the embed topic, its producers
    and the deleted inline watch drain (waits for B1 **and** WP3 #436), **C** the
    `maintenance.embed-rows-per-run.global` key (waits for B2).
12. WP12 — code-ingestion performance (profile PR #508; ingest is 0.34 % of the clock, the embed
    drain 99.66 %, and 99.6 % of the drain is `InferenceSession.Run`): **A** length-sorted batching
    in `CodeEmbedder`/`EntryEmbedder` (**dispatched**, ungated, independent of WP11) — **C** rows /
    batches / elapsed on the EventId-525 job line (ungated) — **B** no 15 s gaps while the backlog
    is non-empty, *inside* WP11-B2's consumer (**G20**; after B2 and C) — **D** directory-ingest
    ignore root (**G21**; after WP3 #436 and WP11-B2) — **E** research: quantized / CoreML
    inference for the code engine (**G22**; architect, research only).
13. **WP6 — #455 re-derive corpus, queries and the parity golden (G14, scoped by G9). LAST**, per
    the owner's ruling of 2026-08-22 ~19:38: everything else finishes before the corpus moves under it.

---

## Session handoff — read this first in a fresh session

**Where the tree stands.** main `021d6c17`, `VERSION` = 1.32.0. **Zero open PRs.** Three worktrees:
`/Users/arasz/RiderProjects/ai-raccoon` (main), `.ai-badger/worktrees/1.32.0-check`
(`task/1.32.0-check`, at `021d6c17`, clean) and this task's worktree. No branch predates a
prospective history rewrite.

**Corrections to the pre-session evidence — read before acting on it:**

1. **The v1.32.0 publish gate is not pending. It passed, and the package is live.** Run
   `32577511356` is `completed/success`; its `publish` job (environment `production`) ran
   14:08:06→14:09:15Z. `gh release list` shows `v1.32.0` (Latest). nuget.org now lists
   `ai-raccoon` 1.27.0, 1.27.1, 1.28.0, 1.28.1, 1.29.0, **1.32.0** — 1.30.x and 1.31.x were never
   published, consistent with the owner's earlier "no publish for now". The open question is
   therefore no longer *approve or decline*; it is whether that publish was intended and what the
   standing policy is (gate **G4**).
2. **A third private-prose path exists at HEAD and is in no issue.**
   `tests/AiRaccoon.Tests/Unit/Retrieval/assets/reference-topk.json` (506,884 B, committed) is the
   parity gate's vendored golden, derived from `RealWorldCorpus`. **434 of its 680 golden hits, and
   35 of its 68 query ids, are `jsaa-*` documents carrying verbatim ADR prose** from the private
   job-search-ai-assistant tree (e.g. `jsaa-adr-0067-registry-driven-erasure-with-runtime-verification`,
   snippet begins "ADR-0067: Erasure is a registry-driven fan-out with runtime completeness
   verification…"). No email address is present (`grep -c 'araszkiewicz\|@gmail'` = 0). #455 names
   only `RealWorldCorpus.cs` and the two Python paths; this file is the same harm class and it is
   what the parity gate measures against, so it cannot be re-derived independently of #455 (gate **G9**).
3. **The ref-rewriting actor is not foreign.** Every one of the 100 force-push events the GitHub
   activity API returns (2026-08-06 → 2026-08-22) is under the login `Arasz`. Six occurred today.
   Details and mechanism in WP7 and gate **G5**.

**The order the session runs in** is the Session todo above and §*Sequencing* below; nothing gated
starts before the gate comes back.

---

## How work lands (stated once; every WP inherits it)

- **One work package = one PR.** Branch `task/pd3-<slug>` in its **own worktree**
  (`.ai-badger/worktrees/<slug>`), never in the main checkout.
- **Draft at the first commit.** `gh pr create --draft` on commit #1, not at the end. Push after
  **every** commit — the owner may squash-merge at any moment and unpushed commits are lost.
- **RED first.** Every WP names, above, the test that must be *seen failing* before the production
  edit, and the exact failure text expected. A gate that has only ever passed is not a gate
  (`.ai-badger/invariants/prove-the-check-fails.md`).
- **Review loop.** After each change the coordinator posts a comment prefixed `@{agent}` reading
  `Ready to review` and marks the PR ready (`gh pr ready <n>`). A **5-minute poll** —
  `gh pr view <n> --json comments,reviews` plus `gh api repos/Arasz/ai-raccoon/pulls/<n>/comments`,
  filtered to activity after the latest `Ready to review` — watches for the reviewer agent's verdict
  (`approve` / `APPROVED` / `LGTM` / `VERDICT: approve`). On approve →
  `gh pr merge <n> --squash --delete-branch --admin` (`--admin` past the same-account gate is
  standing policy). On change requests → triage, route to the owning lane, push the fix, post
  `Ready to review` again. **This plan's own PR goes through the same loop.**
- **Lanes never run the unfiltered suite.** A lane runs `dotnet build` plus the single
  `--filter` named in its WP. CI owns everything else. Full-scope fast is a per-packet checkpoint,
  not a per-commit one (`.ai-badger/invariants/minimal-test-runs.md`).
- **Merge `origin/main`, never rebase a pushed branch.** Before every integration step,
  `git fetch origin && git merge origin/main`. Never `git pull` (global `pull.rebase=true` turns it
  into a rebase — see WP7), and never any force variant of a push.
- **Broadcast when main moves**; message every peer session after pushing, naming the collision.
- Models: **Sonnet** implements, **Opus** plans and reviews. The reviewer is a different agent from
  the implementer in every case.

---

## Work packages

### WP1 — #465: export `search.adjustment` **[gated: G11; naming ruled by G8]**

**Scope.** `SearchTimings` measures nine phases; `Phases()` exports eight. `PhaseNames`
(`src/AiRaccoon.Core/Memory/SearchResults.cs:47-52`) lists eight names, and `PhaseNames[5]` —
`"search.affinity"` — is bound to the `Merge` field (`Phases()`, `:57-67`); `Adjustment` has no
name and no series, so Σ(phases) never included it and `memory_performance` cannot report it.

**Files / symbols.** `src/AiRaccoon.Core/Memory/SearchResults.cs` (`PhaseNames`, `SeriesNames`,
`Phases()`); `tests/AiRaccoon.Tests/Unit/Storage/SearchPhaseClosureTests.cs`; the test that pins the
name list (`SearchResultsTests`); `docs/plans/2026-08-17-search-phase-attribution.md` (amend).
`SearchTimingsCollector.cs` needs no change — it already carries `Adjustment`.

**RED first.** Extend the name-pinning test to expect nine names including `"search.adjustment"`.
Expected failure: the assertion reports 8 returned vs 9 expected. Then extend the closure test's
expected phase list; expected failure: residual/closure mismatch. Only then edit `SearchResults.cs`.

**Acceptance** (checkable by someone not here): `SearchResults.PhaseNames.Count == 9`;
`Phases()` returns a `("search.adjustment", …)` pair mapped to the `Adjustment` field;
`SeriesNames.Count == 10`; the closure test's expected list is derived from `PhaseNames`, not a
second hand-written copy (`derive-or-delete-the-list`); the attribution plan names the new series.

**Gate.** `dotnet test --filter "FullyQualifiedName~SearchResults|FullyQualifiedName~SearchPhaseClosure" --nologo -v m`

**Lane / model.** dotnet-engineer / Sonnet. Review: code-reviewer / Opus.

**Parallelism.** Independent of every other WP. No shared file.

---

### WP2 — #459: a graph-baked pooled output beats the sentence-transformers flags **[gated: G10]**

> **Sequencing update (2026-08-22 16:55, peer session ai-raccoon-fd):** PR **#475** (fix for #470, in
> flight) touches the same files — `ModelDownloadService.cs`, `OnnxEmbeddingGenerator.cs`, a new
> `OnnxOutputRanks` extension, and makes `IOnnxSmokeTester.Verify` return each output's declared
> rank. #470 is the **rank-2** case (the sole output is already pooled); #459 is the **rank-3 plus a
> second pooled-shaped output** case (bge-m3) and #475 states "rank-3 graphs are untouched", so the
> two are adjacent, not duplicates. WP2 **starts only after #475 merges**, builds on its rank seam
> rather than re-probing, and its RED test must still be the bge-m3 manifest fixture. main also
> moves to **VERSION 1.32.1** shortly (peer bump) — WPs that touch `VERSION` merge `origin/main` first.

**Scope.** `ModelDownloadPlanner.PoolingDecision`
(`src/AiRaccoon.Infrastructure/Embedding/Download/ModelDownloadPlanner.cs:345-366`) returns from the
`1_Pooling/config.json` branch **before it ever consults `probe`** — the `OnnxGraphProbe?` parameter
is accepted and ignored on that path. For `BAAI/bge-m3` this writes `pooling.mode: "cls"` while the
checked-in fixture `tests/AiRaccoon.Tests/Resources/ManifestFixtures/bge-m3.full.json` asserts
`model-output`. It is right today only by luck: BGE-M3's dense recipe genuinely is CLS, so
client-side CLS over `token_embeddings` reproduces the baked `sentence_embedding` (cosine 1.0,
nightly run 32567884467). A model whose baked output is not CLS-pooled gets a silently wrong manifest.

**Files / symbols.** `ModelDownloadPlanner.PoolingDecision` (+ its `probe` argument at `:119`);
`tests/AiRaccoon.Tests/Unit/Embedding/Download/ModelDownloadPlannerTests.cs`;
`tests/AiRaccoon.Tests/Resources/ManifestFixtures/bge-m3.full.json` (read only — it is the oracle).

**RED first.** New planner test: `rawFiles` carries a `1_Pooling/config.json` with CLS flags **and**
`modules.json`, and the probe reports a second pooled-shaped output. Expect
`(PoolingMode.ModelOutput, …, provenance "graph")` with the output names carried through. Expected
failure today: returns `cls` / provenance `sentence-transformers`. Keep the ST flags as the fallback
when the probe sees no pooled output — a second test pins that path stays green.

**Acceptance.** With a graph-baked pooled output present, the planned manifest matches
`bge-m3.full.json` (`embedding: sentence_embedding`, `tokenEmbeddings: token_embeddings`) without a
hand patch; with no pooled output, the ST-flag path is unchanged; provenance distinguishes the two.

**Gate.** `dotnet test --filter "FullyQualifiedName~ModelDownloadPlannerTests" --nologo -v m`

**Lane / model.** dotnet-engineer / Sonnet. Review: code-reviewer / Opus.

**Parallelism.** Independent. Touches the same file as the merged #453/#466 work but nothing else
in session 3 goes near `Embedding/Download`.

---

### WP3 — #436: the re-ingest prune reaches `code_entries` **[gated: G12]**

**Scope.** #420 made a re-ingested file drop its stale chunk set for the memory corpus.
`ICodeIngestor` never reports its chunk hashes, so a code file that re-chunks to **fewer** chunks
strands every `code_entries` row the new set does not overwrite. Owner already ruled this NEAR-TERM
(code-mem gate P2 APPROVE); accepted by ai-raccoon-cc, never started.

**Files / symbols.** `ICodeIngestor.IngestFileAsync` / `CodeIngestResult` (add the chunk-hash set);
`FileIngestor` (`IngestAsCodeAsync` leg surfaces them); `SqliteMemoryStore.PruneChunksNotIn` (new
`code_entries` leg using `MemorySql.DeleteCodeBySourcePath`'s predicate — exact path OR subtree
prefix, inert for a file path); new
`tests/AiRaccoon.Tests/Integration/Ingestion/CodeIngestReplacesStaleChunksTests.cs` mirroring
`DirectIngestReplacesStaleChunksTests`.

**RED first.** Three cases, each seen red before the production edit: **B1 shrink** — ingest a code
file at N chunks, re-ingest the shrunken file at M<N, assert `code_entries` holds exactly M rows
(today: N). **B2 sibling** — a sibling code file's rows survive a single-file re-ingest.
**S3 guard** — a non-empty code file whose chunker returned zero rows (`NoOpCodeChunker` /
`FingerprintEligible == false`) must **not** be treated as "no chunks, delete everything". Pin that
one explicitly; it is the way this fix goes wrong.

**Acceptance.** The three cases pass; `DirectIngestReplacesStaleChunksTests` (B1/B2/B3/B4/B6) still
passes unchanged; the memory-corpus and code-corpus hash sets stay disjoint per file.

**Gate.** `dotnet test --filter "FullyQualifiedName~CodeIngestReplacesStaleChunks|FullyQualifiedName~DirectIngestReplacesStaleChunks|FullyQualifiedName~FileIngestorCodeRouting|FullyQualifiedName~CodeChunkerFingerprintGate" --nologo -v m`

**Lane / model.** dotnet-engineer / Sonnet, with test-engineer / Sonnet writing the RED set first.
Review: code-reviewer / Opus.

**Parallelism.** **Serialise against nothing in this plan**, but it is the widest blast radius here
(`IFileIngestor`/`ICodeIngestor` contracts + `SqliteMemoryStore`). If any WP is added later that
touches ingestion or the store, it queues behind WP3.

---

### WP4 — ParityGate p95 leg: its own benchmark-tagged fact **[gated: G7]**

**Scope.** `ParityGateTests.FusedSearch_NdcgParityWithinDelta_AtEverySweepPoint_AndP95WithinBudget`
(`tests/AiRaccoon.Tests/Integration/ParityGateTests.cs:29-73`) asserts nDCG parity **and** a
host-speed p95 budget (`P95LatencyBudgetMs = 1000.0`, `:24`) in one `[Fact]`. The class is
`Speed=Nightly`, so it is outside PR lanes today, but it has already gone red environmentally
(1672 ms vs 1000). Today's owner ruling (#464): wall-clock budgets live only in a benchmark lane,
`Performance=Benchmark`.

**Files / symbols.** `ParityGateTests.cs` only — split the p95 assertion into
`FusedSearch_P95LatencyWithinBudget` carrying `[Trait(TestCategories.Performance, TestCategories.Benchmark)]`;
the parity fact keeps the nDCG sweep and drops the latency lines and the p95 argument to
`WriteReportIfRequested`.

**Mechanism worth knowing.** `build.yml`'s nightly-gates lane filters
`Speed=Nightly&Performance!=Benchmark`, so the tag removes the p95 leg from that lane;
`nightly.yml` runs **unfiltered**, so it still runs there and on demand with
`--filter "Performance=Benchmark"`. Nothing is lost, only re-homed.

**RED first.** After the split, set `P95LatencyBudgetMs` to `0.0` on a scratch commit, run the
benchmark filter, observe `FusedSearch_P95LatencyWithinBudget` fail with the p95 message while the
parity fact stays green; revert the scratch commit before review. Record the failure text in the PR.

**Acceptance.** Two facts; the parity fact contains no wall-clock assertion; the p95 fact carries
the `Performance=Benchmark` trait; both still read from the same harness run
(`fixture.Harness.QueryLatenciesMs`) so the split costs no extra corpus run.

**Gate.** `dotnet test --filter "FullyQualifiedName~ParityGateTests" --nologo -v m`
(Nightly-tagged; run explicitly, not via a PR lane.)

**Lane / model.** test-engineer / Sonnet. Review: code-reviewer / Opus.

**Parallelism.** **Serialises with WP6** — both edit the parity gate's world (`ParityGateTests.cs`
vs `RealWorldCorpus`/`reference-topk.json`). Run WP4 first; it is hours, WP6 is days.

---

### WP5 — S6b: the history-rewrite runbook and its verification script (prepare only) **[ungated]**

**Scope.** The agent prepares everything the rewrite needs and **executes none of it**. The
history-rewriting push is the owner's, deliberately.

**Verified inputs.** `tests/AiRaccoon.Tests/Resources/jsaa-memory.db` — 7 objects still reachable
(`git rev-list --objects --all | grep -c`), 7 commits in `git log --all`, left HEAD in #450.
`benchmarks/AiRaccoon.Benchmarks/Corpus/RealWorldCorpus.cs` — **exactly one** commit touches it
(`e303b4bb`, "bench: real-world corpus from user repos + generator script"), **one** reachable
blob. `tests/AiRaccoon.Tests/Unit/Retrieval/assets/reference-topk.json` — three commits
(`6a52f976` create, `9a53d63e` move, `5005a05b` case-rename) under **three path spellings**
(`tests/AiRaccoon.Tests/Retrieval/assets/…`, `…/unit/retrieval/assets/…`, `…/Unit/Retrieval/assets/…`),
so the rewrite needs **five** `--path` arguments (WP5 lane correction, PR #473; rev 1 said two commits
`2499ca59`/`155f281e` — neither touches the file). **The coupling in the brief is real:** one
`git filter-repo` invocation takes all five `--path` arguments and scrubs them together; doing S6b first means a second rewrite
later for the other two.

**Deliverables.** `docs/work/2026-08-22-414-s6b-history-rewrite-runbook.md` containing: the exact
`git filter-repo --invert-paths --path … --path … --path …` invocation; a pre-rewrite record of
every blob SHA to be removed and the branch tips to be re-planted; the session stop list and
worktree-prune commands; the hook/AWM lift the owner must make and undo; the post-rewrite push
sequence for all branches and tags; and the rollback (a `git clone --mirror` taken immediately
before). Plus `scripts/verify-history-scrubbed.py` — clones the remote fresh into a temp dir and
exits non-zero if `git log --all -- <path>` returns anything for any of the three paths.

**RED first.** The script is the gate, so it must be **seen failing**: run it against the current
origin *before* the rewrite and record its non-zero exit naming all three paths. That output goes in
the PR body. (A verification script that has only ever printed "clean" proves nothing.)

**Acceptance.** The runbook names all three paths; every command is copy-pasteable with no
placeholder; the script has a recorded RED run against today's origin; the runbook states the
precondition "zero open PRs, no worktree other than main" and how to check it.

**Gate.** `python3 scripts/verify-history-scrubbed.py` — expected **FAIL** now, expected PASS only
after the owner's rewrite. No `dotnet` gate; this WP touches no C#.

**Lane / model.** architect (runbook, Opus) + dotnet-engineer (script, Sonnet).
Review: code-reviewer / Opus.

**Parallelism.** Independent. But **the rewrite itself must not happen before WP6 lands** if G1
chooses the one-rewrite path — that is the whole point of G1/G9.

---

### WP6 — #455: re-derive the benchmark corpus, its queries and the parity golden **[gated: G14, scoped by G9]**

**Scope.** Three committed artefacts carry private prose, and they are one unit because the third is
derived from the first: `benchmarks/AiRaccoon.Benchmarks/Corpus/RealWorldCorpus.cs` (294 lines, 107
`jsaa` mentions, ~420-char verbatim excerpts, no email); `RealWorldQueries.cs` (its ground-truth
judgments); `tests/AiRaccoon.Tests/Unit/Retrieval/assets/reference-topk.json` (434/680 golden hits
and 35/68 query ids are `jsaa-*`, with prose in the snippets). Plus the hardcoded private paths in
`scripts/src/benchmark_corpus.py:19-25` (`REPOS`, `OUT`) and
`scripts/tests/test_benchmark_corpus.py:25-31` — whose test is literally named
`test_hardcoded_paths_preserved_verbatim` and **asserts the private path**, so it must be rewritten,
not merely de-referenced.

**Consumers that move with it.** `tests/AiRaccoon.Tests/Integration/ManagedHarness.cs`,
`ParityGateTests`, `HarnessSmokeTests`, `Unit/Retrieval/ReferenceRunCache.cs`,
`Integration/Retrieval/GoldenFileTests.cs`, `benchmarks/…/BenchmarkCorpus.cs`, `SyntheticCorpus.cs`,
`AiRaccoon.Tests.csproj`.

**Method.** Follow ADR-0090's precedent: re-derive from this repository's own public docs.
Regenerate `reference-topk.json` with the vendored `ReferenceRunner` (pinned sqlite-memory 1.3.5 +
the pinned `all-MiniLM-L6-v2.Q5_K_M.gguf`), which re-baselines the parity gate self-consistently —
the gate measures the new side *against that golden*, so nDCG parity is preserved by construction;
what must be re-confirmed is that the ground-truth judgments in `RealWorldQueries` still hold on the
new corpus.

**RED first.** Extend `tests/AiRaccoon.Tests/Unit/Retrieval/CorpusFixtureGuardTests.cs` (the
ADR-0090 guard, `Category=Unit&Speed=Fast`) with a fact asserting that no corpus document id, path
or snippet — in `RealWorldCorpus`, `RealWorldQueries` **or** `reference-topk.json` — carries the
private-repo prefix, composed from fragments the way the existing guard composes
`"jsaa" + "-memory"`. Expected failure today: **471** matches in `reference-topk.json` and **107** in
`RealWorldCorpus.cs`. That guard is the acceptance oracle, not a grep in a PR description.

**Acceptance.** The new guard is green; `benchmark_corpus.py` takes its roots from environment
variables (mirroring `AIRACCOON_JSAA_ROOT` from S6a) with no absolute path in tracked source; the
Python test asserts the env-var contract instead of the private path; `documentCount` in the
regenerated golden matches the new corpus; the parity gate and harness smoke tests pass; the PR
records the regeneration command and the machine it ran on (the golden carries `Provenance`).

**Gate.** `dotnet test --filter "FullyQualifiedName~CorpusFixtureGuardTests" --nologo -v m` (fast,
the RED/GREEN oracle) then `dotnet test --filter "FullyQualifiedName~ParityGateTests|FullyQualifiedName~HarnessSmokeTests|FullyQualifiedName~GoldenFileTests" --nologo -v m`
(Nightly-tagged, run explicitly). Python side: `uv run pytest scripts/tests/test_benchmark_corpus.py`.

**Lane / model.** dotnet-engineer / Sonnet (regeneration + C#), test-engineer / Sonnet (the guard).
Review: code-reviewer / Opus. **This is the largest WP in the plan — size it as a full session's
work on its own, not a side task.**

**Parallelism.** Serialises **after WP4** (both touch the parity gate) and **before the S6b
rewrite** if G1 picks the single-rewrite path.

---

### WP7 — the ref-rewriting investigation and its mitigations **[ungated for investigation; mitigations gated: G5]**

**Scope.** Establish what issues the history-rewriting pushes and put mitigations to the owner. Do
not name a culprit without evidence.

**What is measured** (`gh api repos/Arasz/ai-raccoon/activity?activity_type=force_push&per_page=100`,
re-run today): 100 events returned, **all under login `Arasz`** — one account runs every lane, so the
login identifies nothing. By day: 08-06 ×1, 08-07 ×12, 08-08 ×22, 08-09 ×20, 08-13 ×6, 08-14 ×4,
08-15 ×12, 08-16 ×3, 08-17 ×3, 08-19 ×5, 08-20 ×1, 08-21 ×5, **08-22 ×6**. Today's six, with local
objects inspected: `task/pd-s2-project-identity-adr` 08:54:05Z, `task/pd-s6a-public-docs-corpus`
08:54:30Z, `task/pd-s9-default-code-model` 09:16:41Z, `task/fix-host-coupled-e2e-tests` 11:51:52Z,
`task/release-1-32-0` 12:54:10Z, `task/fix-code-engine-unloadable` 12:54:39Z. Two pairs land ~25-30 s
apart on *different* branches.

**Shape of each rewrite** (verified by reading both commits): four preserve subject **and author
date** exactly (`3fa32704→d8f3db58`, `60221e11→248e078a`, `894b1b12→a4fc27f0`, `5b0b52d0→12ed394d`)
— a replay, not an amend. One replaced a merge commit with an earlier non-merge commit
(`363adce9` "Merge remote-tracking branch 'origin/main'…" → `f0d174cc`, author date 4 minutes
*earlier*) — a rebase flattening a merge. One is a genuine reset: `f7e73e98→097c7e23`, and
`097c7e23` **is an ancestor of `origin/main`** (the ADR-0089 commit) — the branch's own commit
vanished, which is what a rebase does to a commit already present upstream.

**The mechanism that explains all six.** `git config --global pull.rebase=true` (no repo-local
override; `.git/hooks` is empty, `core.hooksPath` unset). Any `git pull` replays local commits →
same-subject rewrites → the next push is non-fast-forward → whoever is pushing reaches for a force
variant. The morning rule "rebase on main before pushing" made that the *expected* path; the later
rule is "merge, never rebase".

**Why the guardrail did not stop it.** `~/.claude/settings.json` `autoMode.soft_deny` contains only
`Bash(git push:* --force)` — which does **not** match `--force-with-lease` or the `-f` short form.
The AWM hook's regex does cover them, but AWM arms per directory, so a worktree created after arming
is uncovered. The resume cron (`*/30`, `resume_cron.py`) has **no 2026-08-22 log entries** — ruled out.

**Attribution (corrected by the WP7 lane, PR #474).** Grepping every transcript under
`~/.claude/projects` modified today (subagents included) finds **exactly one** force command — a
`--force-with-lease` push in `.ai-badger/worktrees/post-delta-next-steps` at 08:50:06Z — and it was
**blocked by the dotnet-claude-kit `pre-bash-guard.sh` hook and never ran**; that lane then
fast-forwarded with a plain push at 08:51:03Z. Twenty `git pull`s ran today, every one with
`--no-rebase` or `--ff-only`, so `pull.rebase=true` never fired. **All six events appear in no agent
transcript,** so they were issued outside a Claude lane — an interactive
shell or the IDE's git integration (Rider's push dialog offers a multi-branch force push, which
would explain two events ~25 s apart on different branches). **This is a hypothesis, not a finding**;
only the owner can confirm or deny that they, or Rider on their behalf, pushed those branches (G5).

**Remaining check the agent can run.** Re-grep transcripts for the other five branch names to
confirm no lane touched them, and note that remote-ref reflogs are gone with the deleted branches,
so nothing further is recoverable from the API.

**Deliverables.** The findings section above, folded into the session report; a one-line rule in
`CLAUDE.md` and the standing lane brief ("merge `origin/main`; never rebase a pushed branch; never
`git pull` — `pull.rebase` is true globally"); a drafted GitHub ruleset payload + `gh api` command
for `refs/heads/task/**` with `non_fast_forward` blocked; and the exact `git config --global` and
`settings.json` edits for the owner to apply.

**Acceptance.** The report states, per event, agent-attributable or not, with the transcript
evidence; the mitigation commands are copy-pasteable; nothing is asserted about who pushed.

**Gate.** No test. The proof is the per-event table with its evidence column, reviewed by
code-reviewer / Opus against the raw API output.

**Lane / model.** architect / Opus. **Config and ruleset changes are the owner's** — the agent
proposes, never applies (standing rule: no agent changes its own permission settings).

**Parallelism.** Independent of everything.

---

### WP8 — #454: an AND-under-match retrieval anchor on the public corpus **[gated: G13]**

**Scope.** `QueryConstructionTests.AndPrimary_UnderMatchedRows_DocumentsKnownRankRegression`
(`tests/AiRaccoon.Tests/Integration/QueryConstructionTests.cs:102`, `Category=Retrieval`,
`Speed=Nightly`) is `Assert.Skip`-ped: its measurement ("data erasure" → AND primary matched only
ADR-0068 rows, ADR-0067 restored at exactly rank 8 under `Limit: 30`) was jsaa-specific and was
**skipped rather than re-pinned to an invented number**. The sibling
`AndPrimary_ZeroMatch_RetriesWithOrFallback` is green — the *trigger* is gated, the *under-match*
case is not.

**Note the corpus distinction** (easy to get wrong): this test runs on the ADR-0090 public-docs
fixture via `CorpusHashMap` (`:60-61`), **not** on `RealWorldCorpus`. It is therefore independent of
WP6 and does not queue behind it.

**Method.** Search the public docs corpus for a (query, under-matched file) pair: a query whose FTS
AND primary returns a non-empty but incomplete row set, where a relevant file is restored only by
the OR fallback. Measure the restored file's rank. This is research; it may find nothing, and
"nothing found" is a legitimate outcome that keeps the skip and says so in the issue.

**RED first.** Once a pair is measured, prove the assertion can fail by disabling the OR fallback
and observing the restored file drop out of the ranked window. Record that output. Only then remove
the `Skip`.

**Acceptance.** Either the skip is removed with a measured rank, a recorded RED observation and the
measurement method written into the test's doc comment; **or** the issue is updated with the
searched query set and the evidence that no under-matching pair exists on this corpus, and the skip
stays with a sharper reason. Not acceptable: a green re-pin with no recorded red.

**Gate.** `dotnet test --filter "FullyQualifiedName~QueryConstructionTests" --nologo -v m`

**Lane / model.** test-engineer / Sonnet (measurement), architect / Opus (verdict on "no pair
exists"). Review: code-reviewer / Opus.

**Parallelism.** Independent.

---

### WP9 — EventId 416→418 disposition **[gated: G6]**

**Scope.** Documentation only, and only if G6 says *invert*. If G6 says *keep* (recommended), the WP
is already satisfied: the event-id register is `docs/reference/logging-event-ids.md`, and it records
416 as "a hole in this block, not a free id … retired, never reused" (lines 45-46). The one remaining
drift — `docs/adr/README.md`'s ADR-0071 summary row still reading "event **416**" — is fixed in PR
#471 itself (review round 1), so the keep branch is a no-op.

**Files / symbols.** `src/AiRaccoon.Infrastructure/Embedding/EmbeddingService.cs:365-368`
(the `QueryTrimmedToWindow` `[LoggerMessage(EventId = 418…)]` and its explanatory comment);
`docs/adr/0071-a-query-is-trimmed-deliberately-and-said-so.md`,
`docs/adr/0072-a-term-budget-for-long-queries-is-not-adjudicable.md`, `docs/adr/README.md` (the
three documents that name 416), and the register `docs/reference/logging-event-ids.md`. Inverting instead means moving `OnnxEmbeddingGenerator`'s 414-415
block, and 414 is named in three ADRs plus a test — five documents to change against these three.

**RED first.** n/a for the *keep* branch (no behaviour). For the *invert* branch the gate is the
event-id uniqueness test: change one id to a duplicate, watch it go red, revert.

**Acceptance.** No event id is used twice; 416 appears in the register as retired; every document
naming an id names the id the code actually emits.

**Gate.** `dotnet test --filter "FullyQualifiedName~Logging|FullyQualifiedName~EventId" --nologo -v m`
plus the docs-drift check on `docs/adr/`.

**Lane / model.** dotnet-engineer / Sonnet. Review: code-reviewer / Opus. Cost: under an hour
either way — the decision is about which document set stays true, not about effort.

**Parallelism.** Independent.

---

### WP10 — ADR-0089 implementation: **sized, gated, NOT started** **[gated: G2 then G15]**

**Status.** `docs/adr/0089-…md` is `Status: Proposed — the owner ratifies this one`. Verified: the
design touched zero source files. `grep -rn "project_id_token_get" src tests` returns **nothing**;
there is no `projects` table. Nothing here starts until G2 flips the ADR to Accepted **and** G15
schedules it.

**Sizing (for G15, so the owner can rule on a number).** Five parts, each its own PR:
(a) `projects` table + migration (id / nullable name / created_at) and the registration write path;
(b) `project_id_token_get` MCP tool minting a sortable guidv7 and registering it — thin, per
`mcp-thin`; (c) `ToolGate` refusal for an unregistered id, guid or not, with warn-but-work for
legacy raw ids the bank already knows; (d) CLI `project id generate` / `project id convert`
(one-way), System.CommandLine, options keep their `--` prefix at `GetValue`; (e) the storage
instructions plus the `ai-raccoon.ignore` entry (**not** `.gitignore` — the owner reversed that in
the #448 review). Every existing caller passes a raw id today, so (c) is the risk-bearing part and
needs its RED set written first: a legacy id that the bank knows keeps working; one it does not know
is refused; a guidv7 that was never registered is refused.

**Acceptance / gate** are deferred to the implementation plan, which the architect writes **after**
ratification. This WP's only session-3 deliverable, if G15 says *park*, is that sizing paragraph.

**Lane / model.** architect / Opus to plan; dotnet-engineer / Sonnet ×5 to build.

**Parallelism.** If scheduled, (a)→(b)/(c)→(d) serialise; (e) is parallel. **It will not fit
alongside WP6 in one session** — that is the substance of G15.

---

### WP11 — embed/ingest load governor **[scope extension; G16/G18/G19 APPROVED, G17 answered CHANGE — design below is rev 1.2]**

**Trigger.** The owner's live server today: `pending-embed` ran **13,307 ms**; a watch digest's
best-effort embed failed with `SQLite Error 5: 'database is locked'` at `EntryEmbedder.EmbedAsync`
(`:302`) ← `EmbedPendingAsync` (`:218`) ← `SqliteMemoryStore.EmbedPendingAsync` (`:431`/`:435`) ←
`WatchDigestExecutor.TryEmbedPendingAsync` (`:116`); `code-reindex` started in the same window.
Owner's words: *"it seems the code ingestion is saturating the machine CPU — we need to tune how
often and how much work at once we do with code ingestion to limit the impact."*

*Not in scope:* the `[418]` "Search query was shortened … 1095 tokens exceeded the 254-token window"
warning in the same log is a 1,095-token **agent query** being trimmed deliberately and said so
(ADR-0071); it is informational, costs one inference, and has nothing to do with the load problem.

#### Finding (a) — CPU: unbounded ORT threads × unserialised drains

- **Nothing in this repository ever constructs a `SessionOptions`.** The one ONNX session in the
  tree is `OnnxEmbeddingGenerator.cs:53` — `_session = new InferenceSession(modelPath);`, no options
  argument. Verified: `grep -rn "SessionOptions\|IntraOpNumThreads\|InterOpNumThreads\|ExecutionMode"
  src/ tests/ benchmarks/` returns **0 lines**, and `grep -rn "new InferenceSession" src/` returns
  exactly that one line.
- **ORT's documented default is one intra-op thread per physical core.** onnxruntime.ai's
  threading page (re-fetched today, not recalled): *"Default: (not specified or 0)
  `sess_options.intra_op_num_threads = 0`"* → *"INTRA Threads Total = Number of physical CPU Cores."*
  The owner's host reports `hw.physicalcpu` = **10** (arm64). So each session runs a 10-thread
  intra-op pool.
- **There are two such sessions, not one.** `EmbeddingService.CreateGenerator`
  (`EmbeddingService.cs:85-96`) caches one generator per engine fingerprint
  (`_engines.GetOrAdd(EngineFingerprint(provider, model, baseUrl), …)`), and the code corpus has its
  own engine (`EmbeddingSettingsKeys.CodeModel`/`CodeEngine`, `:25`/`:28`; `CodeEmbedder.cs:70-74`
  builds its settings from `codeModel`). Memory drain + code drain = **2 sessions × 10 threads = 20
  intra-op threads on 10 cores**, before counting the ingest and query paths that use the same
  cached sessions concurrently.
- **`ExecutionMode.ORT_SEQUENTIAL` is already the default** (same page: *"Default:
  `sess_options.execution_mode = rt.ExecutionMode.ORT_SEQUENTIAL`"*). Setting it explicitly changes
  nothing — dropped from the design below on `ask-if-simpler` grounds.
- **Batch sizes are the same on both corpora and are not the lever.** `EntryEmbedder.BatchSize` = 32
  (`EntryEmbedder.cs:21`), `CodeEmbedder.BatchSize` = 32 (`CodeEmbedder.cs:18`); each drain runs
  4 × 32 = 128 rows per run (`PendingEmbedJob.cs:36`, `CodeReindexJob.cs:31`). A batch of 32 short
  strings is a small amount of *work*; what makes it saturating is that each batch is handed to a
  10-thread pool and several batches are in flight at once.

#### Finding (b) — lock: three unserialised writers, and a holder that can exceed 5 s

- **Nothing serialises the embed paths.** `grep -rn "SemaphoreSlim\|Mutex\|lock (" ` over
  `Infrastructure/Embedding/`, `Infrastructure/Maintenance/` and `Infrastructure/Watch/` finds no
  gate over any embedder. The only semaphore in the neighbourhood is `WatchScheduler`'s **per-project
  digest gate**, whose default limit is **4** (`WatchScheduler.cs:10`, `:31-33`) — it permits
  concurrency, it does not restrain embedding.
- **The maintenance service itself runs two independent loops that both call the same runner.**
  `BankMaintenanceHostedService.ExecuteAsync` does
  `await Task.WhenAll(RunHeavyPassLoopAsync(...), RunOnDemandPollLoopAsync(...))` (`:112-114`); the
  heavy pass calls `_jobRunner.RunDueAsync(connection, _jobs, …)` at `:249-251` on its own
  connection, and the on-demand poll calls the same method at `:163-164` on a *different* connection
  every `OnDemandPollInterval` = **15 s** (`:79`). `MaintenanceJobRunner.RunDueAsync`
  (`MaintenanceJobRunner.cs:22-99`) holds no lock and takes no lease. **The same `PendingEmbedJob`
  can therefore be running in both loops at once.**
- **And when it is, both runs embed the same rows.** `MemorySql.SelectAllPendingForEmbed`
  (`MemorySql.cs:354-355`) is `SELECT id, value FROM entries WHERE embed_state = 'pending' ORDER BY
  id LIMIT @limit` — **no claim, no lease, no state transition to 'embedding'**. Two overlapping
  drains select the identical first 128 ids and pay for every inference twice.
- **The watch-digest leg is an *unbounded* drain, unlike the job.**
  `WatchDigestExecutor.cs:88` calls `TryEmbedPendingAsync` after every successful replace, which
  calls `store.EmbedPendingAsync(projectId, **null**, …)` (`:116`) — `limit: null`, i.e.
  `EntryEmbedder.EmbedPendingAsync`'s `while (true)` loop (`EntryEmbedder.cs:202-219`) runs until the
  project has no pending row left, *plus* an equally unbounded structure heal (`:223-227`). The watch
  pipeline ticks every **1 s** (`WatchPipeline.cs:61`) and admits up to 4 digests per project, so up
  to **four unbounded drains** can be in flight while `PendingEmbedJob` runs its own.
- **Neither embedder embeds inside a write transaction — that part is already right.** `grep -rn
  "BeginTransaction" src/` shows the only transactions near embedding are
  `EntryEmbedder.cs:50` (the `model set` outbox, ADR-0076) and `CodeEmbedder.cs:189` (the fingerprint
  reconcile, ADR-0087); `EntryEmbedder.EmbedAsync` (`:276-316`) and `CodeEmbedder.EmbedPendingBatchAsync`
  (`:47-93`) run the generator first and then write per row. The watch replace path deliberately
  passes `embedInline: false` (`SqliteMemoryStore.Replace.cs:185`; the contract is spelled out at
  `IFileIngestor.cs:35-40` — *"Set embedInline false when the caller holds a write transaction"*).
  `docs/work/2026-08-07-moe-b-persistence.md:218-220` recorded the same conclusion: *"Embedding is
  **not** awaited under a write lock … The most likely hazard in this design is genuinely absent."*
  **So "never embed inside a write transaction" is a rule to keep, not a fix to make** — with one
  loose end below.
- **The statement that threw is a write, and the timeout is 5 s.** `EntryEmbedder.cs:302` is the
  `MarkEmbedded` UPDATE (`MemorySql.cs:328-330`), which fires the vec0 triggers. It waited on
  another writer for the full `DefaultTimeout = 5` / `PRAGMA busy_timeout=5000`
  (`SqliteConnectionFactory.cs:295-299`, `:346-348`) and gave up.
- **Which writer held it that long — not established.** Two candidates, both consistent with the
  log, and the log alone cannot separate them:
  1. `ReplaceCoreAsync` (`SqliteMemoryStore.Replace.cs:146-210`) issues `BEGIN IMMEDIATE` at
     `:152-154` and holds it across the queue capture, `DeleteBySourcePath`, `DeleteCodeBySourcePath`,
     the **full re-ingest and re-chunking** of the file (`:184-186`) and the fingerprint upsert,
     committing only at `:206-208`. Chunking a large code file inside that span, on a box whose
     cores are all busy running inference, is the obvious way to exceed 5 s — and three more digests
     may be queued behind it (concurrency 4).
  2. Write **starvation** rather than a single long holder: SQLite's busy handler is not fair, so a
     128-row `MarkEmbedded` loop of short autocommit writes from one connection can keep a waiter
     spinning for its whole 5 s without any one transaction lasting that long.
  **Settling it is one instrumentation change, not a guess:** a `[LoggerMessage]` around
  `ReplaceCoreAsync`'s BEGIN…COMMIT recording the held span, or `sqlite3_profile`. WP11's first
  commit should add that log line so the fix is aimed rather than assumed.
- **One loose end worth closing while here.** `FileIngestor.IngestDirectoryAsync`
  (`FileIngestor.cs:132-168`) opens **no** transaction and calls `InsertChunksAsync` with
  `embedInline` at its default `true` (`:286-290` → `EntryEmbedder.EmbedIfConfiguredAsync`,
  `EntryEmbedder.cs:164-195`), so a directory ingest embeds **one generator call per chunk**,
  serially, on the same shared session. It is not a lock hazard (no transaction), but it is a third
  uncoordinated consumer of the inference pool, and B2 turns it into a producer like the rest.

#### Finding (c) — what the metrics path actually is (the mechanism G17 asks to extract)

The owner's ruling calls it "the channel based events pump … exactly the same solution as for
metrics". Re-read today, and the wording needs one correction before anything is extracted:

- **The metrics path does not use `Channel<T>` today.** `grep -rn "Channel<\|CreateBounded\|BoundedChannelOptions" src/`
  returns **exactly one line in the whole tree**, and it is not metrics:
  `src/AiRaccoon.Infrastructure/Watch/WatchPipeline.cs:56` —
  `private readonly Channel<WatchEvent> _events = Channel.CreateUnbounded<WatchEvent>();`.
- **What metrics has is a bounded queue with drop-on-full plus a timed drain** — semantically the
  same object, hand-rolled. `MeasurementBuffer.cs:9` holds a `ConcurrentQueue<Measurement>`;
  `TryEnqueue` (`:21-36`) reserves a slot with `Interlocked.Increment` **before** enqueuing (`:25`),
  and on overflow decrements, increments `_dropped` and **returns false** (`:26-31`) — it never
  blocks and never grows. `DrainAll()` (`:38-48`) dequeues everything. `ApplyCapacity` (`:50`)
  changes the cap at runtime with a `Volatile.Write`.
- **Port:** `IMeasurementBuffer.cs:10-27` — `Capacity`, `EnqueuedCount`, `DroppedCount`,
  `TryEnqueue` (false = dropped), `DrainAll`, `ApplyCapacity`.
- **Producer:** `MetricsRecorder.cs:14-24` — one `buffer.TryEnqueue(...)` inside a `try`, exception-proof,
  return value deliberately ignored; a failure logs EventId **960** (`:28-30`) and the caller's search
  is unaffected. Fire-and-forget, never awaited.
- **Consumer:** `MetricsFlusher.cs:18-51` — a `BackgroundService` with a `PeriodicTimer` on the
  injected `TimeProvider` (`:43-44`), period read from `metrics.flush-interval-seconds.global`
  (default **30 s**, `MetricsConfigKeys.cs:16-21`) and **re-read after every tick** (`:49`); buffer
  capacity applied once at startup from `metrics.buffer-capacity.global` (default **1000**,
  `MetricsConfigKeys.cs:6-14`) at `:41`/`:175-191`. One pass = `FlushOnceAsync` (`:84-117`):
  `DrainAll` (`:88`), batch write, and self-metrics **only when the batch was non-empty** (`:103-105`).
- **Failure and shutdown:** a failed batch write is logged (EventId **970**) and *dropped, not
  retried* (`:119-140`); `StopAsync` (`:59-81`) runs one final flush bounded by
  `ShutdownFlushTimeout` = 5 s (`:31`) so a hanging store cannot hang shutdown. Log block:
  EventIds **970-974** (`:212-231`).
- **Test seams:** `TickSignal Flushes` (`:34`) and `TickSignal TimerArmed` (`:37`) — count-based
  broadcast signals (`src/AiRaccoon.Infrastructure/Maintenance/TickSignal.cs:41-59`), so the tests
  await a *count*, never a wall clock. Registration: `AppRegistrations.cs:203-206`.
- **Tests that pin it:** `tests/AiRaccoon.Tests/Unit/Metrics/MeasurementBufferTests.cs` (6 facts —
  burst within capacity, exactly at capacity, overflow drops-and-reports, drain restores occupancy,
  `EnqueuedCount` unaffected by draining, `ApplyCapacity`), `MetricsFlusherTests.cs` (16 facts),
  `MetricsRecorderTests.cs` (2), plus `MetricsFlusherTelemetryTests.cs` and
  `MetricsDependenciesSmokeTests.cs`. All `Unit`+`Fast`.
- **The coalescing precedent is in the watch pipeline, not metrics.** `WatchPipeline.TickOnceAsync`
  (`:176-185`) drains the channel with `TryRead` and folds each event into a
  `Dictionary<(ProjectId, Path), WatchEvent>` — last-writer-wins per key — then drains the
  *dictionary* to the scheduler. Duplicate signals for one key collapse to one job.

**Consequence for the extraction.** `Channel.CreateBounded<T>` with
`BoundedChannelFullMode.Wait` and **`TryWrite` only** (never `WriteAsync`) has exactly
`MeasurementBuffer`'s contract: full → `TryWrite` returns `false` immediately, nothing blocks,
nothing is silently discarded behind the caller's back. (`DropWrite` would return `true` and drop —
**not** today's semantics, and it would take `MeasurementBufferTests` red.) So the ruling is
buildable as a *behaviour-identical* reimplementation, with the existing metrics tests as the proof.

#### Design — one pump type, one instance per topic (G17 answered: CHANGE)

**The unit being extracted.** `src/AiRaccoon.Core/EventPump/` — `IEventPump<T>` / `EventPump<T>` /
`PumpTopic.cs`. Core, not Infrastructure: the pump is a `Channel<T>` plus counters, and
`System.Threading.Channels` is BCL, not framework/persistence/SDK, so `clean-architecture-layering`
is satisfied. **No SQLite, ONNX or Hosting type is visible from the pump** — the drain loops that
own those live in Infrastructure. The folder is a named mechanism, not a generic bucket: the
`screaming-architecture` invariant bans `Services/`/`Utils/`, not the thing itself under its own name.

**Surface** (deliberately five members; each one is demanded by a topic that exists today):

| Member | Contract | Which topic needs it |
|---|---|---|
| `bool TryEnqueue(T item)` | `TryWrite` on a bounded channel. `false` = at capacity (drop, counted) **or** coalesced away. Never blocks, never throws. | both |
| `IReadOnlyList<T> DrainUpTo(int budget)` | `TryRead` up to `budget` items, FIFO; returns fewer when the pump is empty. `budget` is a **count**, never a duration. | both |
| `Task WaitForItemAsync(CancellationToken)` | `WaitToReadAsync` — the wake-up the metrics buffer cannot give, and the only reason a channel beats today's `ConcurrentQueue`. | embed |
| `long EnqueuedCount` / `long DroppedCount` / `long CoalescedCount` | `Interlocked` counters; the assertion surface for every test below. | both |
| `void ApplyCapacity(int capacity)` | Mutable soft cap enforced by the same `Interlocked` reservation that exists today (`MeasurementBuffer.cs:25-31`), in front of the channel. | metrics |

`ApplyCapacity` is the one place the shape is not a pure channel, and it is deliberate: a
`Channel`'s bound is fixed at construction, while `metrics.buffer-capacity.global` is read from the
bank at flusher startup and pinned by
`MeasurementBufferTests.ApplyCapacity_ChangesTheCapAppliedToFutureEnqueues`. So the channel is
constructed at the topic's **ceiling** (a number that never changes) and the *effective* cap is the
mutable reservation counter. *Rejected:* fix capacity at construction — it is simpler by one
counter and would force an edit to `MeasurementBufferTests`, destroying the behaviour-preservation
proof that makes B1 safe at all.

**Topics.** A topic is one `EventPump<T>` instance plus one drain loop. Two exist:

| Topic | Item | Capacity | Coalesce | Trigger | Budget per pass |
|---|---|---|---|---|---|
| `metrics` | `Measurement` | `metrics.buffer-capacity.global`, default 1000 | no — every measurement is distinct data | `PeriodicTimer`, `metrics.flush-interval-seconds.global`, default 30 s | drain-all (bounded by capacity, unchanged) |
| `embed` | `EmbedDrainRequest(EmbedCorpus Corpus)` — `Memory` or `Code` | **8** (the item space is 2; 8 is slack, not a queue) | **yes**, on the record's own structural equality | `WaitForItemAsync` — signalled, not timed | **1 item**, then `maintenance.embed-rows-per-run.global` rows (default 128) |

##### Chosen: one pump type, one instance per topic. Rejected: a single pump with round-robin consumers.

The owner offered the round-robin shape ("we can even…"). For the two topics that exist it is the
worse of the two, on three grounds, and none of them is style:

1. **The topics have different trigger shapes.** Metrics is *time-triggered, drain-all*: its
   30-second cadence is a promise pinned by
   `MetricsFlusherTests.ExecuteAsync_FlushesAtTheConfiguredInterval`. Embed is *signal-triggered,
   drain-one*. A single round-robin consumer has to have one loop shape, so one of the two topics
   gets the wrong one.
2. **A shared consumer is a shared failure domain, and the embed side is the slow one.** The
   trigger for this whole work package was a **13,307 ms** `pending-embed` run. Round-robin puts a
   metrics flush behind that drain. Today's separation guarantees a measurement flush costs one
   batched INSERT and nothing else; round-robin trades that guarantee away for no gain.
3. **Round-robin exists to ration a resource two topics contend for. These two do not contend.**
   The scarce resource here is the inference pool, and after this change it has exactly **one**
   consumer — the embed topic. Metrics costs a batched SQLite write. There is nothing to arbitrate.

What the budget *means* under the chosen shape, stated so it cannot drift into a clock: **the embed
consumer takes exactly one work item per pass and drains exactly `maintenance.embed-rows-per-run.global`
rows for it — two counts, no duration anywhere.** The round-robin option stays additively
available: a third topic that genuinely shares the inference pool would be the moment to build it,
and it would be a change to the drain loops, not to the pump.

##### The embed topic in detail

**Producers** (they enqueue; none of them calls a generator for a drain any more):

| Producer | Today | After B2 |
|---|---|---|
| `PendingEmbedJob.RunAsync` (`:52-56`) | `embedder.EmbedPendingBatchAsync(connection, 128, ct)` | `pump.TryEnqueue(new EmbedDrainRequest(EmbedCorpus.Memory))`. `HasWorkAsync` (`:38-49`) is unchanged and is what makes the 15 s poll the recovery path. |
| `CodeReindexJob.RunAsync` (`CodeReindexJob.cs:46-50`) | `embedder.EmbedPendingBatchAsync(connection, 128, ct)` | `pump.TryEnqueue(… EmbedCorpus.Code)`; `HasWorkAsync` (`:38-42`, incl. the fingerprint reconcile) unchanged. |
| `WatchDigestExecutor.TryEmbedPendingAsync` (`:112-121`, called at `:88`) | `store.EmbedPendingAsync(projectId, **null**, ct)` — unbounded, per digest, up to 4 concurrent per project | **deleted**; the digest enqueues one `Memory` item and returns. The rows stay `embed_state='pending'`, which is what already survives a crash. |
| `FileIngestor.IngestDirectoryAsync` (`:132-168` → `:286-290`) | `embedder.EmbedIfConfiguredAsync` **per chunk**, serially, `embedInline` defaulting to `true` | pass `embedInline: false` and enqueue **once** at the end of the directory. |

**Not producers, deliberately** — two single-row inline embeds on the write path stay exactly as
they are: `SqliteMemoryStore.cs:146` and `:509` (`EmbedIfConfiguredAsync` for one just-written row).
That is one inference on the caller's own thread, in the latency the caller asked for; routing it
through a queue would make a synchronous write eventually-consistent for no benefit.

**One remaining direct drain, named rather than hidden.** `MemoryTools.cs:402` —
`memory_embed_pending` — calls `store.EmbedPendingAsync(projectId, limit, ct)` and returns counts to
the caller. It stays direct in B2: it is operator-initiated, bounded by its own `limit`, and turning
it into "queued, ask later" changes a tool contract (and its tests) for a path nobody complained
about. **Consequence to accept explicitly:** an operator running `memory_embed_pending` by hand can
overlap the consumer's drain. That is a deliberate act on an idle-ish bank, not the background
surprise the owner reported.

**Why the item carries no project id.** The brief for this rewrite described items as "drain project
X (memory|code), up to N rows". Two of the three drains cannot honour a project id today:
`IEntryEmbedder.EmbedPendingBatchAsync(connection, limit, ct)` (`IEntryEmbedder.cs:44`) and
`ICodeEmbedder.EmbedPendingBatchAsync` (`ICodeEmbedder.cs:39`) are **bank-wide** — the interface has
no project parameter, and `MemorySql.SelectAllPendingForEmbed` (`:354-355`) has no project filter.
Only the watch leg's `EmbedPendingAsync(projectId, …)` is scoped. Carrying a field the consumer
cannot honour without a new SQL path and a new store method would be a field that does nothing, and
it doubles the coalescing key space for that non-benefit. So the item is
`EmbedDrainRequest(EmbedCorpus Corpus)` — **two possible values in the entire system**.
*Consequence:* a fresh edit in project A is drained in row-id order alongside project B's older
pending rows rather than jumping the queue. Bounded by the budget, and strictly more work drained
per pass than the per-project shape. *If per-project priority is later wanted*, it is additive: add
`ProjectId` to the record and a filtered `LIMIT` query. Nothing else moves.

**Coalescing, and where the key is released.** `TryEnqueue` on a coalescing topic checks a
`HashSet<T>` under a `Lock` before `TryWrite`; an item already queued is not queued twice and
increments `CoalescedCount`. **The key is released at `DrainUpTo` — when the item is taken, before
the drain runs**, not at completion. A change that lands *during* a drain therefore queues a fresh
item and gets its own pass. *Rejected:* releasing at completion — it folds the mid-drain signal
into the run that already passed those rows, and would rely on the 15 s poll to notice. Releasing at
take costs at most one extra bounded drain and loses no wake-up.

**Backpressure: drop, never wait — the metrics path's choice, inherited.** `TryWrite` only. On a
full pump the signal is dropped and `DroppedCount` increments. This is safe here for a reason that
must not be lost: **the channel is a wake-up, not the record.** The durable record is
`embed_state = 'pending'` on the row — that is ADR-0076's outbox, already in the schema — and
`PendingEmbedJob.HasWorkAsync` / `CodeReindexJob.HasWorkAsync` are the relay that re-derives the
signal every **15 s** (`BankMaintenanceHostedService.OnDemandPollInterval`, `:79`). A lost signal
costs at most one poll interval of latency and zero rows. *Rejected, again:* a second queue table.
`derive-or-delete-the-list` — one durable record of one fact.

**Why this closes the duplicate-inference bug structurally.** `RunDueAsync` is reachable from both
`BankMaintenanceHostedService` loops (`:112-114`, `:163-164` and `:249-251`), on two connections, so
`PendingEmbedJob` can run twice at once today and — because `SelectAllPendingForEmbed` has no claim
or lease — both runs select the same 128 ids. After B2 both runs *enqueue*, the second enqueue
coalesces away, and the single-reader consumer is the only drainer in the process. **No lease, no
`'embedding'` state, no semaphore.** (Cross-process — two servers on one bank — is unchanged and out
of scope; `busy_timeout` still governs it.)

**State machine — asked, and answered "plain record".** The invariant
(`state-transitions-through-a-machine`) applies to a domain object with explicit states. The work
item has none worth recording: *queued* is "in the channel", *running* is "held by the single
reader", *done* is "gone". A `Status` field would be a second copy of a fact the structure already
carries, on an object with no identity, no persistence and no observer (`derive-or-delete-the-list`).
The state machine that matters here already exists and is durable: `embed_state` on the row —
`'pending'` → embedded — and B2 changes nothing about it except who performs the transition.

**Consumer.** `src/AiRaccoon.Infrastructure/Embedding/EmbedDrainService.cs`, a `BackgroundService`
following `BankMaintenanceHostedService`'s shape exactly: `ISqliteConnectionFactory.OpenBankAsync`
per pass (`BankMaintenanceHostedService.cs:163`), a `try`/`catch` that logs and keeps looping, an
`IOperationTelemetry` span that calls `NoteWork()` only on a non-empty drain (`MetricsFlusher.cs:90-98`
is the precedent), and a `TickSignal Drains` test seam. Loop: `WaitForItemAsync` → `DrainUpTo(1)` →
open connection → `EmbedPendingBatchAsync(connection, rowsPerRun, ct)` for that corpus → loop.

**It does not re-enqueue itself when rows remain.** This is the pacing decision, and it is the one
that answers the owner's actual complaint. Draining until empty would run the (now capped) inference
pool flat out until a large backlog cleared — which is the saturation that started WP11. Instead the
pace stays exactly today's: **128 rows per signal**, and signals arrive from the 15 s poll plus one
per digest or directory ingest. The gain over today is not throughput, it is that N digests in one
second now cause **at most one** extra drain instead of N unbounded ones.

**New log EventIds** come from the next free block in `docs/reference/logging-event-ids.md` — read
the register at implementation time, do not guess a number; the existing event-id uniqueness test is
the guard.

#### Owner-visible knobs (unchanged — G18 and G19 both APPROVED as written)

| Key | Default | Surface | Effect |
|---|---|---|---|
| `embedding.threads` | `max(1, physicalCores / 2)` = **5** on the owner's 10-core host; `0` means "ORT default" | `settings model threads <n>` / shown by the existing `settings model show` (`CliCommandTree.cs:198-217`) | ORT `IntraOpNumThreads` for every local session; takes effect on the next server restart (the session is cached per fingerprint, `EmbeddingService.cs:91`) |
| `maintenance.embed-rows-per-run.global` | **128** (today's `4 * BatchSize`, unchanged) | `settings maintenance embed-rows-per-run <n>`, listed by `settings maintenance list\|show` (`CliCommandTree.cs:548-562`) | The embed consumer's rows-per-drain, for **both** corpora — replacing `PendingEmbedJob.RowsPerRun` (`:36`) and `CodeReindexJob.RowsPerRun` (`:31`); takes effect on the next drain |

Two keys, not five. **The serialisation is not a knob** — under the chosen shape it is not even a
setting to omit: one channel with one reader is single-file by construction, so there is no value an
operator could write that puts three drains back on the inference pool. Both keys ride the generic
`/settings?key=` route (`SettingsProtocol.cs:9`, `:17`) — no new endpoint (G19).

#### Acceptance criteria

1. **No lock error under the load that produced one.** With ≥ 1,000 rows seeded `pending` in
   `entries`, ≥ 1,000 in `code_entries`, and a watch active over a real directory, a **10-minute**
   `ai-raccoon serve --idle-timeout 0` run logs **zero** `SQLITE_BUSY` / `database is locked`
   occurrences and zero EventId `400` warnings. Owner-run; the check is
   `grep -c "database is locked\|SQLITE_BUSY" <logfile>` = 0.
2. **The configured thread count is what the session got.** Asserted in a unit test (A1/A2), and
   `settings model show` reports it.
3. **Embeddings are unchanged, bit-for-bit.** `MiniLmGoldenVectorTests.BundledMiniLm_Embeddings_MatchCommittedGoldens`
   passes on arm64 with the cap applied. If it does not, WP11-A stops and G16 is re-asked.
4. **CPU is bounded.** Owner-run, both numbers on the same host and the same backlog:
   `top -l 3 -stats pid,cpu,command -pid $(pgrep -f 'ai-raccoon serve')` (or `ps -o %cpu= -p <pid>`
   sampled 10×). Baseline uncapped first, then capped. Target: peak `%CPU` ≤ **550** (5 of 10 cores)
   at the default, against a baseline expected near 1000–2000.
5. **The backlog still drains.** Rows drained per signal ≥ the pre-change figure at the same row
   budget — read from `maintenance_jobs.run_count` and the falling `pending` count, not a stopwatch.
   ADR-0076 measured ~72.6 rows/s uncapped; the drain needs 128 rows / 15 s ≈ **8.5 rows/s** to keep
   up, so a halved thread pool has ~4× headroom.
6. **Each pending row is embedded once**, and **the metrics topic behaves exactly as it did** — both
   by test, not inspection (E8 and the unmodified metrics suite).

#### Gates — RED first, counts and ordering only, no wall-clock assertions (owner ruling #464)

**B1 — the pump, and metrics as a topic.** New suite `AiRaccoon.Tests.Unit.EventPump.EventPumpTests`.

| # | Test | Must be seen failing on | Assertion shape |
|---|---|---|---|
| P1 | `TryEnqueue_BeyondCapacity_ReturnsFalseAndCountsTheDrop` | HEAD — the type does not exist | cap 2, three enqueues: `false` on the third; `EnqueuedCount == 2`, `DroppedCount == 1` |
| P2 | `TryEnqueue_FullPump_ReturnsFalseInsteadOfWaiting` | HEAD | the call returns on the calling thread with `false` — pins `FullMode.Wait` + `TryWrite`, and would go red under `DropWrite` (which returns `true`) |
| P3 | `DrainUpTo_BudgetSmallerThanBacklog_TakesExactlyTheBudgetInOrder` | HEAD | enqueue 5, `DrainUpTo(2)` → items 1,2; next call → 3,4. Counts **and** order |
| P4 | `DrainUpTo_EmptyPump_ReturnsEmpty` | HEAD | `Count == 0`, no exception |
| P5 | `TryEnqueue_CoalescingTopic_IdenticalItemIsNotQueuedTwice` | HEAD | 3 identical enqueues → `CoalescedCount == 2`, one item drained |
| P6 | `DrainUpTo_ReleasesTheCoalesceKey_SoAnItemArrivingAfterTheTakeQueuesAgain` | HEAD | take, re-enqueue same item → queued; the no-lost-wake-up assertion |
| P7 | `TryEnqueue_NonCoalescingTopic_KeepsDuplicates` | HEAD | metrics semantics: 3 identical measurements → 3 drained |
| P8 | `ApplyCapacity_ChangesTheCapForFutureEnqueues_NotWhatIsQueued` | HEAD | mirrors the existing metrics fact at pump level |
| P9 | `WaitForItemAsync_CompletesOnceAnItemArrives` | HEAD | `await` the task after enqueuing; assert it completed. **Never** assert elapsed time |
| P10 | `MeasurementBufferTests` (6 facts) — **unmodified** | see the defect injection below | the behaviour-preservation proof for the metrics topic |
| P11 | `MetricsFlusherTests` (16), `MetricsRecorderTests` (2), `MetricsFlusherTelemetryTests`, `MetricsDependenciesSmokeTests` — **unmodified** | — | must stay green with zero edits; an edit to any of them is the signal that the refactor changed behaviour |

**The prove-the-check-fails step for B1** (a refactor's tests all start green, so one must be *made*
to fail): after rewiring `MeasurementBuffer` onto `EventPump<Measurement>`, temporarily set the
metrics topic's coalescing flag to `true`, watch
`MeasurementBufferTests.TryEnqueue_BurstWithinCapacity_AllSucceedAndDrainWhole` go **red**, revert,
watch it go green. Quote both outputs in the PR description. Without that step P10/P11 are
"tests that have only ever passed".

**B2 — the embed topic.** New suite `AiRaccoon.Tests.Unit.EmbedDrain.*` (fakes for
`IEntryEmbedder`/`ICodeEmbedder`, no real ONNX).

| # | Test | Must be seen failing on | Assertion shape |
|---|---|---|---|
| E1 | `EmbedDrainServiceTests.OneSignal_RunsExactlyOneDrainOfTheRowBudget` | HEAD — no consumer exists | fake embedder: exactly 1 call, `limit == 128` |
| E2 | `EmbedDrainServiceTests.ManySignalsForOneCorpus_CoalesceToAtMostOneFurtherDrain` | HEAD — every digest drains today | 10 enqueues during a held drain → ≤ 2 total drains; `CoalescedCount == 9` |
| E3 | `EmbedDrainServiceTests.MemoryAndCodeSignals_NeverOverlapInTheEmbedder` | HEAD — the paths are unserialised | fake records **max observed concurrent entrants**; assert `== 1`. Counters, not clocks |
| E4 | `EmbedDrainServiceTests.DrainThrows_LoopSurvives_AndTheNextSignalStillDrains` | HEAD | call count 2 after a throwing first pass |
| E5 | `EmbedDrainServiceTests.RowsRemain_NoSelfReEnqueue_TheNextSignalDoesTheRest` | HEAD | after a full-budget drain the pump is **empty**; pins the pacing decision |
| E6 | `WatchDigestTests.Digest_LeavesRowsPending_AndSignalsTheDrain` | HEAD — `WatchDigestExecutor.cs:116` drains inline and unbounded | fake embedder call count **== 0** during the digest; pump `EnqueuedCount == 1`; rows still `pending` |
| E7 | `PendingEmbedJobTests.RunAsync_EnqueuesInsteadOfEmbedding` | HEAD (`:54`) | embedder calls 0, pump count 1 |
| E8 | `MaintenanceRunnerTests.TwoOverlappingPasses_EnqueueOneDrain_AndEachRowIsEmbeddedOnce` | HEAD — `SelectAllPendingForEmbed` (`MemorySql.cs:354`) has no lease, so both passes take the same ids | drive `RunDueAsync` from two connections; each row id appears **once** in the fake's call log |
| E9 | `CodeReindexJobTests.RunAsync_EnqueuesTheCodeItem` | HEAD (`CodeReindexJob.cs:48`) | as E7, on the code topic |
| E10 | `FileIngestorTests.IngestDirectory_EmbedsNoChunkInline_AndSignalsOnce` | HEAD — `FileIngestor.cs:286-290` embeds per chunk | per-chunk embed calls 0; pump `EnqueuedCount == 1` for N files |
| E11 | `EmbedDrainServiceTests.DroppedSignal_IsRecoveredByTheNextPoll` | HEAD | fill the pump to capacity so the signal drops; assert `HasWorkAsync` still true and the next poll's enqueue drains the rows. Proves "the channel is a wake-up, not the record" |

E3, E6 and E8 are the RED texts to quote in B2's PR description.

**A — the thread cap** (G16, unchanged from the proposal the owner approved): A1 `ThreadResolutionTests.Resolve_UnsetSetting_HalvesTheCoreCount`
(`(null, 10)`→5, `(null, 1)`→1, `("0", 10)`→0, garbage→default; resolution lives on the injectable
`EmbeddingService`, **not** a new static helper); A2 `ThreadResolutionTests.Generator_ReportsTheIntraOpThreadsItWasBuiltWith`
(assert the int, never timing); A3 `MiniLmGoldenVectorTests.BundledMiniLm_Embeddings_MatchCommittedGoldens`
— existing, `Slow`+`Integration`, run **first, before any production edit lands**, on a throwaway
build with the cap forced. It is the falsification test for the whole of A.

**C — the row budget** (G18, unchanged): C1 `RowBudgetTests.RowsPerRun_ComesFromTheBankSetting`
(set the key to 7, seed 20 pending, assert exactly 7 rows in one drain; unset → 128);
C2 `RowBudgetTests.CodeReindex_HonoursTheSameSetting` (same, on `code_entries`).

#### PR split, gates, collisions

**Lane.** dotnet-engineer / **Sonnet** for all four PRs; reviewer is a different agent
(code-reviewer / Opus), per §*How work lands*. Four PRs, run in the order below.

| PR | Owns | Gate | Blocked by |
|---|---|---|---|
| **WP11-A** — the ORT thread cap and its setting | `OnnxEmbeddingGenerator.cs`, `EmbeddingService.cs`, `EmbeddingSettingsKeys.cs`, `SettingsCommands.cs`, `CliCommandTree.cs` | `dotnet test --filter "FullyQualifiedName~ThreadResolution" --nologo -v m`, **plus** `dotnet test --filter "FullyQualifiedName~MiniLmGoldenVectorTests" --nologo -v m` run first | open PR **#475** (`fix/470-manifest-pooling-mode`) edits the same constructor |
| **WP11-B1** — extract the pump; metrics becomes a topic | new `src/AiRaccoon.Core/EventPump/*`; `src/AiRaccoon.Infrastructure/Metrics/MeasurementBuffer.cs` (body only — class name, namespace and `IMeasurementBuffer` unchanged); `AppRegistrations.cs`; new `tests/AiRaccoon.Tests/Unit/EventPump/*` | `dotnet test --filter "FullyQualifiedName~EventPump\|FullyQualifiedName~Metrics" --nologo -v m` — the new suite **and** the whole metrics suite, the latter with zero edits | nothing |
| **WP11-B2** — the embed topic, its producers, and the inline watch drain deleted | new `src/AiRaccoon.Infrastructure/Embedding/EmbedDrainService.cs`, `EmbedDrainRequest.cs`; `WatchDigestExecutor.cs`, `PendingEmbedJob.cs`, `CodeReindexJob.cs`, `FileIngestor.cs`, `AppRegistrations.cs`, `docs/reference/logging-event-ids.md`; the `ReplaceCoreAsync` held-span log line from Finding (b) as its **first** commit | `dotnet test --filter "FullyQualifiedName~EmbedDrain\|FullyQualifiedName~WatchDigest\|FullyQualifiedName~EventPump" --nologo -v m` | **B1**, and **WP3 (#436)** — shared `FileIngestor.cs` |
| **WP11-C** — `maintenance.embed-rows-per-run.global` | `BankMaintenanceConfigKeys.cs`, `EmbedDrainService.cs`, `SettingsCommands.cs`, `CliCommandTree.cs` | `dotnet test --filter "FullyQualifiedName~RowBudget\|FullyQualifiedName~EmbedDrain" --nologo -v m` | **B2** (it configures B2's consumer) |

**Collisions.**
- **#475 → A.** Hard sequence; verified with `gh pr view 475 --json files`.
- **B1 → B2 → C.** All three touch `AppRegistrations.cs`; B2 and C both touch `EmbedDrainService.cs`.
  Serial by construction, and each is small.
- **WP3 (#436) → B2.** Shared `FileIngestor.cs`, and WP3 also changes what a re-ingest leaves
  `pending` — which is exactly what B2's consumer drains. Right order on substance as well as files.
- **#476** (`fix/472-code-engine-guard-4xx`) touches `SettingsEndpoint.cs` / `SettingsProtocol` tests
  — **no collision**: both WP11 keys ride the generic `/settings?key=` route.
- **A is parallel to B1.** Disjoint files (`Embedding/Onnx*` vs `Core/EventPump` + `Metrics`), so
  once #475 lands A can run alongside B1.
- **WP2 (#459)** touches `ModelDownloadPlanner.cs` only — no overlap with any of the four.

**WP11-C follow-up (review round 1, #517, finding 9):** `PendingEmbedJob`/`CodeReindexJob` still
implement `IMaintenanceJob` and enqueue-only — `RunAsync` returns `ValueTask.FromResult(false)`
after a bare `TryEnqueue`, the same shape the old `Task.FromResult(false)` had, just renamed.
Dropping them (Option B: the on-demand poll enqueues both corpora directly from a small component,
no job-list membership) is the real fix; #517 kept Option A (the mechanical `ValueTask<bool>`
signature change) because Option B's blast radius (~20 files: DI wiring, the documented job-list
ordering guarantee in `AppRegistrations.cs`/`BankMaintenanceHostedService.cs`, several integration
tests, BDD steps) is disproportionate to a settings-key task. Not scheduled against any WP above —
pick it up as its own small PR when convenient.

**Open items this rewrite could not settle from the tree** (do not treat as facts):
- Whether `Channel.CreateBounded<T>(capacity)` pre-allocates for its capacity, which decides whether
  the metrics topic's channel ceiling can be `MetricsConfigKeys.MaxBufferCapacity` (1,000,000) or
  must be something smaller. **Settle it in B1's first commit** with an allocation assertion or by
  reading the BCL source — not by assuming.
- Which writer held the lock past 5 s in the owner's log (Finding (b) lists two candidates). B2's
  first commit is the `ReplaceCoreAsync` held-span log line that answers it; the fix is aimed only
  after that line has produced a number.

### WP12 — code-ingestion performance (from the 2026-08-22 profile, PR #508)

**Source of every number below:** `docs/work/2026-08-22-code-ingestion-profile.md` (PR #508) —
measured on `Mac16,12`, 10 physical / 10 logical cores, Release build of `b56d7fb3`, scratch bank on
port 7931. That document carries the commands and the measured/read/inferred tag for each figure;
this section carries only what the work packages are shaped by.

**Headline.** For this repository's own `src/` (469 `.cs`, 2,045,873 B → **1,762** `code_entries`):

| Phase | Wall | Share |
|---|---|---|
| `memory_ingest_directory` — walk + match + chunk + tokenize + SQLite writes | **3.57 s** | **0.34 %** |
| code embed drain to zero pending, default thread cap | **1,061.3 s** | **99.66 %** |

`dotnet-trace` (45 s speedscope, mid-drain): `OnnxEmbeddingGenerator.RunBatch` **99.6 %** of wall,
of which `InferenceSession.RunImpl` is 99.5 %; `SqliteCommand.ExecuteReader` 1.5 %; SentencePiece
encode **0.02 %**. **The chunker is not the problem and neither is SQLite — one native call is.**

Thread cap, identical protocol (restart, re-activate, fixed 150 s window, 1,762-row backlog):

| `settings model threads` | rows/s | CPU (`top`) |
|---|---|---|
| 1 | 0.213 | 19–23 % |
| **5** — the merged default, `max(1, cores/2)` | **2.347** | 124–140 % |
| 0 — ORT default (10) | 1.902 | 82–124 % |

So **WP11-A's cap is 23 % faster than ORT's own default**, not merely quieter, and a "be quiet"
setting must never be defaulted to 1. `ThreadResolutionTests.cs` already pins the resolution
(#492, trait fix #505) — no further work is owed there.

#### Work packages, in execution order

| PR | Owns | RED-first test (counts/ordering only — #464) | Gate | Lane | Blocked by / collides |
|---|---|---|---|---|---|
| **WP12-A** — length-sorted batching | `Embedding/CodeEmbedder.cs:85-90` (order the run's rows by `Value.Length` before the `Skip/Take(BatchSize)` slicing) and the same shape in `EntryEmbedder.EmbedAsync` | Fake generator records each batch: given 64 pending rows of alternating short/long text, **the first generator call receives the 32 shortest**. Red today: it receives rows 1–32 in id order. | `dotnet test --filter "FullyQualifiedName~CodeEmbedder\|FullyQualifiedName~EntryEmbedder" --nologo -v m` | dotnet-engineer / Sonnet — **dispatched** | `CodeEmbedder.cs`/`EntryEmbedder.cs`: no other WP touches them. Independent of WP11. |
| **WP12-B** — no 15 s gaps while the backlog is non-empty | WP11-B2's `EmbedDrainService.cs` **only** — the consumer re-signals its own topic when a drain returns a full row budget (`BankMaintenanceConfigKeys.DefaultEmbedRowsPerRun`, WP11-C's `maintenance.embed-rows-per-run.global`), so `WaitForItemAsync` returns at once. `BankMaintenanceHostedService.cs:79` (`OnDemandPollInterval`) is **not** edited. | Fake `TimeProvider`: a job reporting `HasWork` and consuming a full row budget is invoked **N times without the clock being advanced**. Red today: once per advanced tick. | `dotnet test --filter "FullyQualifiedName~EmbedDrain\|FullyQualifiedName~EventPump" --nologo -v m` | dotnet-engineer / Sonnet | **G20**, and hard-blocked on **WP11-B2** — outside a single consumer this speeds all three uncoordinated drains up at once and makes the saturation worse. Collides with **WP11-C** on `EmbedDrainService.cs`; run after C. |
| **WP12-C** — ingest counters on the existing job line | `Maintenance/MaintenanceJobRunner.cs` — the EventId **525** "ran in N ms" message gains `rows`, `batches`, `elapsed` | The log record for a run that embedded 128 rows in 4 batches carries `Rows=128`, `Batches=4`. Red today: the message has no such fields. | `dotnet test --filter "FullyQualifiedName~MaintenanceJobRunner" --nologo -v m`, plus `docs/reference/logging-event-ids.md` updated | dotnet-engineer / Sonnet | Ungated. Touches `MaintenanceJobRunner.cs`, which no other WP owns. |
| **WP12-D** — directory ingest honours nested ignore roots | `Ingestion/FileIngestor.cs:147` — reuse `ResolveIgnoreRootAsync` (`:113-139`) instead of loading rules only at the walk root | `FileIngestorIgnoreTests`: ingest `root/sub` where `root/ai-raccoon.ignore` excludes `sub/skip/**`; **row count for `skip/` is 0**. Red today: non-zero. | `dotnet test --filter "FullyQualifiedName~FileIngestorIgnore" --nologo -v m` | dotnet-engineer / Sonnet | **G21**. `FileIngestor.cs` — behind **WP3 (#436)** and **WP11-B2**, both of which own that file first. |
| **WP12-E** — research: quantized or accelerated code inference | No production file. Output is a dated research record + an ADR draft. | n/a — a research record, gated by its own measurement plan, not by a test. | The measurement plan in the record must reproduce PR #508's S3–S5 protocol so the numbers are comparable. | architect / Opus | **G22**. No file collision. |

**Why WP12-C is the whole of the instrumentation gap.** The profile's §7 proposes a five-phase
`IngestTimings` record mirroring `SearchTimings`. Applying `ask-if-simpler`: ingest is 0.34 % of the
clock, so a five-phase split of it buys nothing today. **Three integers on a log line that already
fires is the right first step**; the record only earns its place if a drain run ever shows time that
rows × mean-tokens does not explain. `IngestTimings` is therefore *parked*, not scheduled.

**What WP12 deliberately leaves out.** `CodeChunker.VerifyAndShed` re-tokenizes and re-concatenates
per shed step, and `BuildChunks:68` concatenates the winning text a second time (`CodeChunker.cs:115-129`,
`:68`). Measured at **below the noise floor** — the phase it lives in is 0.02 % of the end-to-end
clock. Recorded so nobody spends a day on it; file it as tidy-up only if other work lands in that file.

---

---

## Sequencing

**Parallel from the start** (no shared files): WP1, WP2, WP7, WP8, WP5.
**Serial chain:** WP4 → WP6 → *(owner)* S6b rewrite. WP4 is small and must land before WP6 rewrites
the corpus under it.
**Alone-ish:** WP3 — widest blast radius (`IFileIngestor`/`ICodeIngestor`/`SqliteMemoryStore`);
anything added later touching ingestion queues behind it.
**Shared-file map:** `ParityGateTests.cs` (WP4, then WP6 re-runs it); `RealWorldCorpus.cs` /
`reference-topk.json` (WP6 only); `SearchResults.cs` (WP1 only); `ModelDownloadPlanner.cs` (WP2
only); `docs/adr/README.md` (WP9 only). `OnnxEmbeddingGenerator.cs`
(WP11-A, **after open PR #475 merges**); `FileIngestor.cs` (WP3, then WP11-B2);
`AppRegistrations.cs` (WP11-B1, then B2, then C — serial); `MeasurementBuffer.cs` + the whole
`Metrics/` tree (WP11-B1 only); `EmbedDrainService.cs` (WP11-B2, then C). No two parallel WPs
touch the same file.

**WP12 (from PR #508) adds one more strand and one tail.** **WP12-A** (`CodeEmbedder.cs`,
`EntryEmbedder.cs`) and **WP12-C** (`MaintenanceJobRunner.cs`) share no file with anything above and
run in parallel from now. **WP12-B** is inside `EmbedDrainService.cs` and is therefore last in the
WP11 chain: B1 -> B2 -> C -> **12-B**. **WP12-D** is last in the `FileIngestor.cs` chain:
WP3 (#436) -> WP11-B2 -> **12-D**. **WP12-E** is research and touches no file.

**WP11 (the scope extension, gate answered in rev 1.2) sits outside the chain above** and is now
four PRs in two independent strands:

- **Strand 1 — A alone.** WP11-A (the ORT thread cap and `embedding.threads`) is blocked only by
  open PR **#475**, which edits the same constructor, and is otherwise parallel to everything.
- **Strand 2 — B1 → B2 → C, strictly serial.** **B1** (extract `EventPump<T>`, move metrics onto it)
  is blocked by nothing and can start immediately, in parallel with A and with every other WP: it
  touches only `Core/EventPump/`, `Metrics/MeasurementBuffer.cs` and `AppRegistrations.cs`, none of
  which any other WP opens. **B2** (the embed topic, its producers, the deleted inline watch drain)
  needs B1's pump **and** shares `FileIngestor.cs` with WP3 — so it runs **after WP3 #436 merges**,
  which is also the right order on substance, since WP3 changes what a re-ingest leaves `pending`
  and B2's consumer is what drains it. **C** (the rows-per-run key) configures B2's consumer and
  runs last.

The two strands are file-disjoint (`Embedding/Onnx*` and the settings CLI for A;
`Core/EventPump` + `Metrics/` for B1), so A and B1 can run at the same time once #475 is out of the
way. The whole of WP11 is behind neither WP4 nor WP6.

**The one sequencing insight to honour:** **#455 (WP6) lands before the S6b rewrite.** Verified: the
three private-prose paths are `jsaa-memory.db` (7 reachable blobs), `RealWorldCorpus.cs` (1 blob,
one commit `e303b4bb`) and `reference-topk.json` (2 commits). One `filter-repo` invocation with
three `--path` arguments scrubs all three; rewriting now scrubs one and guarantees a second rewrite
later. The runbook WP5 produces **must list all three paths** for this reason. The counter-argument
the owner may prefer: WP6 is the biggest item here, so waiting for it delays the rewrite by days
while the quiet window (zero open PRs, right now) is open — that trade is gate **G1**.

---

## Owner-only items: what the agent prepares, what only the owner can do

| Item | Agent prepares (no owner needed) | Strictly the owner's action |
|---|---|---|
| S6b (#414 rewrite) | WP5: runbook, blob-SHA record, mirror-backup step, fresh-clone verification script with a recorded failing run, precondition checklist | Lifting the push guard, running `filter-repo`, pushing the rewritten refs, re-planting them, telling every session to re-clone |
| ADR-0089 | The sizing in WP10; the implementation plan **after** ratification | Flipping `Status: Proposed` → `Accepted` (or amending it) |
| ai-badger #424 | Nothing — the PR is complete and `MERGEABLE` (4 files, no `VERSION` change) | The `VERSION` bump in ai-badger (owner-only standing rule; main is at 0.131.0, released 2026-08-21 15:40), then `gh pr ready 424` and merge |
| Publish policy | Nothing | Confirming 1.32.0's publish was intended; stating the standing policy (G4) |
| EventId 416→418 | WP9, both branches costed | Choosing keep or invert (G6) |
| p95 leg | WP4, split ready to accept or reject as one PR | Choosing split vs informational (G7) |
| Ref rewriting | WP7: the per-event table, the ruleset payload, the exact config edits | Applying `git config --global`, editing `~/.claude/settings.json`, creating the repo ruleset; confirming or denying the IDE/interactive-shell hypothesis |

---

## What session 3 deliberately leaves out (ask-if-simpler)

- **A second embedding-model tuning round** (H1/M5, code-chunk-size vs recall). Parked in the
  continuation ledger and gated on nightly `AIRACCOON_CODE_MODEL_DIR` wiring; it needs a measurement
  budget this session does not have, and nothing in the queue depends on it.
- **H21 `IMemoryStore` decomposition.** Its parking condition is "no open PR touching
  `IMemoryStore`/`SqliteMemoryStore`" — WP3 touches exactly that. Running both would collide on the
  same file every day.
- **H16 CI OS matrix, 1.29.0 lifecycle (#444), #435 closure.** Each is waiting on an external
  trigger (release boundary, second occurrence, owner close). Pulling them in adds work with no
  question answered.
- **A generic "no private prose anywhere" scanner.** The narrow guard in WP6 (three named artefacts,
  fragment-composed) is the version that can actually be seen failing today. A repo-wide scanner
  would trip on ~20 `.ai-badger/skills/learned/**` files that mention the private repo *by name*
  only — a different, lower-severity question, deliberately not opened here.
- **Automating the review loop further.** The 5-minute poll is enough; a bespoke watcher is more
  machinery than the problem needs.
- **Any change to the AWM denylist or `settings.json` by an agent.** Proposed in WP7, applied by
  the owner. That boundary is not a nicety.

---

## Owner gate — decisions only you can make

One card per decision, `G1`-numbered in order — count them, do not trust a total written here
(G16-G19 were answered in rev 1.2; G20-G22 are new with WP12). Each says what becomes true if you
approve, gives the numbers behind it, and carries a recommendation. Nothing in *Scheduling* starts before you rule.

### Owner actions

**G1 — The S6b history rewrite waits for #455 (WP6) and then scrubs all three paths in one pass.**
*Detail.* Three committed artefacts carry private prose: `jsaa-memory.db` (7 reachable blobs, 7
commits, already off HEAD), `benchmarks/…/RealWorldCorpus.cs` (1 blob, one commit `e303b4bb`, 294
lines, 107 `jsaa` mentions) and `tests/…/Unit/Retrieval/assets/reference-topk.json` (3 commits under 3 path
spellings — it was moved twice; 434/680 golden hits are `jsaa-*` with prose in the snippets). One
`git filter-repo --invert-paths` call takes all five `--path` arguments (three files + two historical
spellings; PR #473 carries the exact invocation). Rewriting now scrubs one and commits you to a second
rewrite later; waiting costs the days WP6 needs, during which the currently-perfect quiet window
(zero open PRs, no stale worktrees) may close. WP5 delivers the runbook and a verification script
with a recorded failing run either way.
*Why it matters.* A history rewrite invalidates every clone and can only be done in a quiet window;
doing it twice doubles that cost and doubles the chance of losing a branch.
*Recommendation.* **Wait for WP6, then one rewrite covering all three paths.**

**G2 — ADR-0089 is ratified: `Status: Proposed` becomes `Accepted`.**
*Detail.* `docs/adr/0089-the-project-id-is-a-guidv7-and-that-is-not-access-control.md` is the only
ADR in this repo that has ever carried `Proposed`, by design — it says you ratify it. It records
your G2 design input verbatim (sortable guidv7 as the token, `project_id_token_get`, CLI generate +
one-way convert, accident-prevention threat model with same-machine projects trusted, legacy raw ids
warn-but-work, `ai-raccoon.ignore` not `.gitignore`) and answers your open question under its own
heading: yes, any holder of the bank's `mcp-token` can still enumerate every project, and encrypting
the bank does not change that. One place extends your words — a non-guid id the bank does *not* know
is refused; that is the sentence to check before ratifying. Zero source files are affected either way.
*Why it matters.* Nothing can be built against it while it reads "Proposed", and the ledger row
closes on ratification, not on merge.
*Recommendation.* **Ratify**, after reading the "refused if unknown" sentence.

**G3 — ai-badger #424 is bumped, marked ready and merged.**
*Detail.* `Arasz/ai-badger#424` (`feat/ai-raccoon-code-model-warning`) is OPEN, draft, `MERGEABLE`,
last updated today 09:58Z. Four files, all skill documentation: it teaches the `ai-raccoon-memory`
skill to relay the `code engine not configured` warning as "code results are keyword-only, not
complete" and to name `ai-raccoon model set code default` once per session. It does **not** touch
`VERSION`; ai-badger main is at 0.131.0 (released 2026-08-21 15:40). The `VERSION` bump in ai-badger
is owner-only by standing rule, so this is blocked on you and on nothing else.
*Why it matters.* Until it ships, an agent whose code search has silently degraded to keyword-only
has no way to tell the user.
*Recommendation.* **Bump and merge** — it is documentation, and the gap it closes is silent.

**G4 — The standing publish policy is stated, now that 1.32.0 is already on nuget.org.**
*Detail.* This is not the "approve or decline" the brief expected. Run `32577511356` is
`completed/success`; its `production`-environment `publish` job ran 14:08:06→14:09:15Z; `v1.32.0` is
the Latest release; nuget.org lists `ai-raccoon` 1.27.0, 1.27.1, 1.28.0, 1.28.1, 1.29.0, **1.32.0**
(1.30.x and 1.31.x were never published). Your earlier note today said "I will not publish a new
version for now". So either the approval was deliberate and the note is superseded, or the
production environment approved without a fresh decision. The decision now: does a `VERSION` bump on
main continue to auto-cut and auto-publish, or does the publish job require an explicit dispatch?
*Why it matters.* Every `VERSION` bump merged to main currently reaches the public feed; a package
version cannot be unpublished, only delisted.
*Recommendation.* **Confirm 1.32.0 was intended, and state the rule going forward.** If it was not
intended, the fix is a workflow change (dispatch-only publish), not a delist.

**G5 — The ref-rewriting mitigations are applied: `pull.rebase=false` globally, a widened
push denylist, and a `refs/heads/task/**` ruleset blocking non-fast-forward pushes.**
*Detail.* All 100 force-push events the activity API returns are under login `Arasz` — one account
runs every lane, so the login identifies nobody. Six today: pd-s2 08:54:05Z, pd-s6a 08:54:30Z, pd-s9
09:16:41Z, fix-host-e2e 11:51:52Z, release-1-32-0 12:54:10Z, fix-code-engine 12:54:39Z. Five are
rebase-shaped rewrites (four preserve subject *and* author date; one replaced a merge commit with an
earlier non-merge commit); one is a reset whose new tip is `origin/main`'s ADR-0089 commit.
`pull.rebase=true` is set globally, which turns any `git pull` into exactly that. `soft_deny`
carries only `Bash(git push:* --force)` — by the documented permission syntax the `:*` form is only
recognised at the end of a pattern, so this rule matches *nothing*. What blocks agents today is the
dotnet-claude-kit `pre-bash-guard.sh` hook: the single `--force-with-lease` an agent typed today was
**blocked and never ran** (PR #474 quotes the hook output). **All six events are unattributed** —
none appears in any transcript; the 20 `git pull`s today all passed `--no-rebase`/`--ff-only`, so
`pull.rebase=true` did not fire either. An interactive shell or Rider's multi-branch push dialog would
fit the two ~25 s pairs, but **the agent is not asserting that** — please confirm or deny.
*Why it matters.* Two branches lost work mid-run today; the guardrail that was supposed to prevent
this does not match the flags actually used.
*Recommendation.* **Apply all three**, and tell us whether the five unattributed pushes were yours.

### Design

**G6 — `QueryTrimmedToWindow` keeps EventId 418; 416 is retired and never reused.**
*Detail.* `EmbeddingService.cs:365-368` moved 416→418 in #466 because `OnnxEmbeddingGenerator`'s
block runs 414-415 and needed 417, and 416 was wedged between them. Keeping 418 meant amending the documents
that name 416: ADR-0071 and ADR-0072 carry the 2026-08-22 amendment, the register
`docs/reference/logging-event-ids.md` records 416 as retired, and `docs/adr/README.md`'s ADR-0071
summary row (which still read "event **416**") is amended in PR #471. Inverting instead
— moving 414-415 so 416 can come back — means changing five documents, because 414 is named in three
ADRs plus a test. The reviewer sided with keeping 418. Either way the work is under an hour; the
question is which set of documents stays true without amendment.
*Why it matters.* Event ids are the stable handle in logs and ADRs; a second move later costs the
same again.
*Recommendation.* **Keep 418**, register 416 as retired.

**G7 — The parity gate's p95 assertion becomes its own `Performance=Benchmark` fact.**
*Detail.* `ParityGateTests.cs:29-73` asserts nDCG parity and a 1000 ms p95 budget in one `[Fact]`;
it has already gone red environmentally at 1672 ms. Your #464 ruling was that wall-clock budgets
live only in a benchmark lane. Splitting gives `FusedSearch_P95LatencyWithinBudget` the
`Performance=Benchmark` trait, which `build.yml`'s `Speed=Nightly&Performance!=Benchmark` lane
excludes while `nightly.yml`'s unfiltered run still executes it — so no coverage is lost, and a slow
host can no longer take the nDCG parity gate down with it. The alternative is making p95
report-only, which loses the budget as a gate entirely. Both read the same harness run, so neither
costs an extra corpus pass.
*Why it matters.* Today a busy laptop can turn a correctness gate red for a reason that has nothing
to do with correctness.
*Recommendation.* **Split** (option a).

**G8 — `search.adjustment` is added as a ninth exported phase; `search.affinity` keeps its current
binding.**
*Detail.* `SearchTimings` measures nine phases; `SearchResults.PhaseNames` lists eight, and
`PhaseNames[5]` — `"search.affinity"` — is bound to the `Merge` field. So `Adjustment` is measured
and never exported, and one exported name does not match its field. The clean fix adds
`"search.adjustment"`; the *tidy* fix would also rename `search.affinity` → `search.merge`, which
breaks an externally visible metric series that `memory_performance` already reports and that
existing stored rows carry.
*Why it matters.* A renamed series silently orphans historical metric data; an added one does not.
*Recommendation.* **Add `search.adjustment`, do not rename `search.affinity`** — record the
mismatch in the attribution plan instead.

**G9 — #455 is scoped as a full re-derivation of corpus, queries and the parity golden.**
*Detail.* #455 names `RealWorldCorpus.cs` (294 lines, 107 `jsaa` mentions) and two hardcoded private
paths. It does not name `tests/…/Unit/Retrieval/assets/reference-topk.json`, which nobody has
recorded before: 506 KB committed, **434 of 680 golden hits and 35 of 68 query ids are `jsaa-*`**,
with verbatim ADR prose in the snippets. It is derived from `RealWorldCorpus`, so it cannot be left
behind — but regenerating it means re-running the vendored `ReferenceRunner` (sqlite-memory 1.3.5 +
the pinned MiniLM gguf) and re-confirming the ground-truth judgments. The narrow alternative — strip
only the `jsaa-*` documents and keep the badger/home ones — is faster and shrinks the corpus by
roughly half, at the cost of a smaller retrieval baseline.
*Why it matters.* The parity gate's reference is itself the private prose; a half-fix leaves the
harm in place under a different filename.
*Recommendation.* **Full re-derivation from this repo's own public docs**, per ADR-0090's precedent.

### Scheduling — session 3 or park

**G10 — #459 (planner ignores a graph-baked pooled output) is scheduled in session 3.**
*Detail.* WP2. `ModelDownloadPlanner.PoolingDecision` (`:345-366`) returns from the
`1_Pooling/config.json` branch before consulting the ONNX probe, so bge-m3 gets `pooling.mode: cls`
while the fixture asserts `model-output`. It is right today only because BGE-M3's dense recipe is
genuinely CLS (cosine 1.0, measured). A model whose baked output is not CLS-pooled gets a silently
wrong manifest. Small: one method, one new test, a fixture that is already the oracle. Adjacent to
the merged #466 pooling fix, same file, no open work there.
*Why it matters.* It is the exact defect class the nightly parity gate exists to catch, sitting in
the code that writes the manifest that gate reads.
*Recommendation.* **Schedule** — small, and the fixture makes the RED test cheap.

**G11 — #465 (`search.adjustment` never exported) is scheduled in session 3.**
*Detail.* WP1, ruled in shape by G8. Nine phases measured, eight exported; the residual budget
silently absorbed the ninth and `memory_performance` cannot report it. One file plus two tests plus
a plan amendment — hours, not days. No dependency on anything else in the queue.
*Why it matters.* Phase attribution is the tool used to explain slow searches, and it is currently
missing a phase without saying so.
*Recommendation.* **Schedule** — the smallest real item in the queue.

**G12 — #436 (code-corpus prune gap) is scheduled in session 3.**
*Detail.* WP3. You already ruled this NEAR-TERM at the code-mem gate (P2 APPROVE); it was accepted
by ai-raccoon-cc and never started. A shrinking code file strands `code_entries` rows, so search can
return code the file no longer contains — the defect #420 fixed for the memory corpus, still open in
the corpus that shipped in 1.30.0. Medium: `ICodeIngestor` contract change, `FileIngestor`,
`SqliteMemoryStore.PruneChunksNotIn`, and a three-case RED set including the trap where a chunker
that produced zero rows must not be read as "delete everything". Widest blast radius in the plan.
*Why it matters.* It is a correctness bug in shipped behaviour, already approved, and it blocks the
parked H21 store decomposition while it sits open.
*Recommendation.* **Schedule**, and give it its own lane rather than sharing a session with WP6.

**G13 — #454 (AND-under-match anchor) is scheduled in session 3.**
*Detail.* WP8. The assertion is skipped, not deleted: its measurement was jsaa-specific and was not
re-pinned to an invented number. The sibling zero-match test still gates the OR-fallback *trigger*;
what is ungated is a non-zero AND primary that still excludes a relevant file. Closing it means
*finding* a query on the public docs corpus that under-matches, measuring the rank, and proving the
assertion can fail by disabling the fallback. It runs on the ADR-0090 fixture, not `RealWorldCorpus`,
so it does not collide with WP6. Research-shaped: it may legitimately find no such pair.
*Why it matters.* It is a hole in retrieval coverage that is currently invisible — the test file
looks green.
*Recommendation.* **Schedule, time-boxed**, with "no pair exists, here is the searched set" as an
acceptable outcome.

**G14 — #455 (benchmark corpus re-derivation) is scheduled in session 3.**
*Detail.* WP6, scoped by G9, and the gating dependency for G1's single-rewrite plan. It is the
largest item here: re-derive the corpus and queries from public sources, regenerate the parity
golden with the vendored engine, de-hardcode `scripts/src/benchmark_corpus.py:19-25`, and rewrite
`scripts/tests/test_benchmark_corpus.py`'s `test_hardcoded_paths_preserved_verbatim`, which
currently *asserts* the private absolute path. Roughly a full session on its own. Parking it means
S6b either waits indefinitely or runs twice.
*Why it matters.* It is the only thing standing between now and a single, final history rewrite.
*Recommendation.* **Schedule**, as the session's anchor item, with WP1/WP2/WP7/WP8 running beside it.

**G15 — ADR-0089's implementation is scheduled in session 3.**
*Detail.* WP10, and only meaningful if G2 ratifies. Five PRs: `projects` table + migration,
`project_id_token_get`, the `ToolGate` refusal path with legacy warn-but-work, the CLI
`project id generate|convert`, and the storage instructions + `ai-raccoon.ignore` entry. Verified
that none of it exists yet — no `projects` table, no `project_id_token_get` anywhere in `src` or
`tests`. The refusal path is the risk: every caller passes a raw id today. Alongside WP6 (a full
session on its own) and WP3 (the widest blast radius), this does not fit.
*Why it matters.* Half-landing a project-identity change would leave the bank refusing writes it
used to accept.
*Recommendation.* **Ratify now (G2), park the implementation** for session 4 with the sizing above
carried into the ledger.

### The embed/ingest load governor (WP11 — scope extension you ruled in today)

Four more cards. WP11's *scheduling* is not a card — you already ruled it into this session; these
four are the design decisions inside it, each decidable on its own.

**G16 — The ONNX intra-op thread pool is capped at half the physical cores by default, via a bank
setting `embedding.threads`.**
**ANSWERED: APPROVE** — built as WP11-A; `MiniLmGoldenVectorTests` (A3) remains its falsification gate.
*Detail.* The tree constructs exactly one ONNX session — `OnnxEmbeddingGenerator.cs:53`,
`new InferenceSession(modelPath)` with **no** `SessionOptions`; `grep -rn
"SessionOptions\|IntraOpNumThreads\|InterOpNumThreads\|ExecutionMode" src/ tests/ benchmarks/`
returns **0 lines**. ONNX Runtime's threading page (re-fetched today) states the default
`intra_op_num_threads = 0` means *"INTRA Threads Total = Number of physical CPU Cores"*; your host
reports `hw.physicalcpu` = **10**. Two engines are cached independently
(`EmbeddingService.cs:85-96` — memory model and `embedding.codeModel` have different fingerprints),
so a memory drain plus a code drain is **20 intra-op threads on 10 cores** before any query or
ingest joins in. Proposed default `max(1, cores/2)` = 5, with `0` meaning "ORT default" for anyone
who wants the old behaviour back. The alternatives are: leave it uncapped (today), pin to 1 (safest
for the host, slowest drain), or `cores - 2`. Throughput headroom is large either way — ADR-0076
measured ~72.6 rows/s and the drain needs ~8.5 rows/s to keep up with its own 15 s poll.
*The one thing that could veto this:* ADR-0049 says this bank's vectors depend on the host's
arithmetic path, and `MiniLmGoldenVectorTests` asserts byte identity against a committed golden. If
thread count perturbs the float reduction order, the cap changes stored vectors and the answer has
to be different. That test is run **first**, before any production edit.
*Why it matters.* This is the only change that addresses the saturation itself rather than rationing
the work that causes it, and it is roughly ten lines.
*Recommendation.* **Cap at `max(1, cores/2)`, expose `embedding.threads`, gate on the golden
vectors** — and if the goldens move, come back for a re-ruling rather than re-capturing them.

**G17 — Only one embed drain runs at a time, and the watch digest stops draining inline.**
**ANSWERED: CHANGE** (`docs/work/2026-08-22-post-delta-3-wp11-feedback.md`) — bounded channels, not a
semaphore; one extracted events pump with metrics and embed as separate topics. The design that
replaces the proposal below is **§WP11 rev 1.2** (*Finding (c)*, *Design — one pump type, one
instance per topic*, and the four-PR split). The card is kept verbatim beneath this line as the
record of what was asked; do not build from it.
*Detail.* Nothing serialises the three paths today: no semaphore or lock exists over any embedder
(`grep -rn "SemaphoreSlim\|Mutex\|lock (" ` over `Embedding/`, `Maintenance/`, `Watch/`), and
`BankMaintenanceHostedService.ExecuteAsync` runs the heavy-pass loop and the 15 s on-demand poll
loop concurrently (`:112-114`), each calling the same `_jobRunner.RunDueAsync` on its own connection
(`:249-251` and `:163-164`) — so `PendingEmbedJob` can be running **twice at once**, and because
`MemorySql.SelectAllPendingForEmbed` (`:354-355`) has no claim or lease, both runs select the same
128 ids and pay for every inference twice. Meanwhile `WatchDigestExecutor.cs:116` runs
`EmbedPendingAsync(projectId, **null**, …)` — an **unbounded** drain — after every replace, and the
watch pipeline ticks every second (`WatchPipeline.cs:61`) with up to 4 concurrent digests per
project (`WatchScheduler.cs:10`). Proposal: one process-wide `SemaphoreSlim(1,1)` in an injectable
component that every generator-calling path acquires, **and** delete the inline drain so the digest
leaves rows `pending` for `PendingEmbedJob`'s bounded relay — ADR-0076's own shape (`embed_state =
'pending'` already *is* the durable record; no second queue table). The alternative is to keep the
paths parallel and rely on G16's cap alone, which halves the threads but leaves the duplicated work
and the four unbounded drains in place.
*Why it matters.* Duplicated inference is CPU spent for nothing, and it is invisible — nothing logs
that two drains embedded the same row.
*Recommendation.* **Serialise, and make the relay the only drainer.** Not configurable: a bank that
can be set back to three concurrent drains is a bank that can be set back into today's log line.

**G18 — Both drains take their rows-per-run from one bank setting,
`maintenance.embed-rows-per-run.global`, defaulting to today's 128.**
**ANSWERED: APPROVE** — built as WP11-C, one key, default 128, no pacing delay.
*Detail.* `PendingEmbedJob.RowsPerRun` = `4 * EntryEmbedder.BatchSize` = 128 (`:36`) and
`CodeReindexJob.RowsPerRun` = `4 * CodeEmbedder.BatchSize` = 128 (`:31`) are both `const`. One key
replaces both; the default changes nothing on day one, so this card is about *reachability*, not a
new behaviour. The tempting alternative — a delay between batches, "how often" in the literal sense
— is **not** recommended: it turns a throughput problem into a latency problem, adds a knob nobody
can tune from evidence, and can only be tested with a wall-clock assertion, which your #464 ruling
forbids. The row budget already sets the pace: 128 rows per 15 s poll, expressed as a **count**.
A second alternative is two separate keys (memory and code tuned apart); it is one more key to
document and drift, for a distinction nobody has yet needed.
*Why it matters.* When the cap in G16 turns out to be one notch wrong on some host, this is the knob
that fixes it without a release.
*Recommendation.* **One key, default 128, no pacing delay.**

**G19 — Both knobs are bank settings written by the server, not CLI flags or environment variables.**
**ANSWERED: APPROVE** — both keys ride the existing generic `/settings?key=` route; no new endpoint.
*Detail.* ADR-0075: the MCP server is the only writer to the bank. Both keys ride the **existing**
generic `/settings?key=` route (`SettingsProtocol.cs:9`, `:17`), so no new endpoint and no new
protocol record — the CLI surface is two commands under nodes that already exist
(`settings model threads <n>` beside `settings model show`, `CliCommandTree.cs:198-217`;
`settings maintenance embed-rows-per-run <n>` beside `settings maintenance list|show`, `:548-562`).
Names follow the conventions already in the tree verbatim: `embedding.<camelCase>`
(`EmbeddingSettingsKeys.cs:9-28`) and `maintenance.<kebab-case>.global`
(`BankMaintenanceConfigKeys.cs:6-17`). The alternative — an env var like `AIRACCOON_EMBED_THREADS` —
is faster to build and invisible to `doctor`, unversioned, unsynced, and unreadable by the server
that has to honour it.
*Why it matters.* A tuning knob that lives outside the bank is a knob that disagrees with the bank,
and the process that has to obey it is not the process that was told.
*Recommendation.* **Bank settings, both of them**, on the existing route.

### Code-ingestion performance (WP12 — from the 2026-08-22 profile, PR #508)

Three cards. Each is shaped by a number that was measured on this hardware today, not recalled;
`docs/work/2026-08-22-code-ingestion-profile.md` carries the command behind every figure.

**G20 — The single-consumer embed drain runs continuously while rows are pending, bounded by
`embedding.threads`, instead of pacing on the 15 s poll.**
*Detail.* A full code drain of 1,762 rows took **1,061.3 s** end to end = 1.66 rows/s, but a clean
150-second window mid-drain measured **2.347 rows/s** — so roughly **29 % of the drain's wall clock
is not inference**. The mechanism is read straight out of the tree: `CodeReindexJob.RowsPerRun` =
`4 * CodeEmbedder.BatchSize` = 128 (`:31`), and the job is re-offered only by
`BankMaintenanceHostedService.OnDemandPollInterval` = 15 s (`:79`). 1,762 ÷ 128 = 13.8 runs × 15 s =
**207 s of pure idle inside a 1,061 s drain — 19.5 %**, with the rest of the 29 % being the 187 MB
session load and per-run setup. This card asks you to reverse a position **this plan itself argued**
in G18: there, the 15 s poll was described as the pace-setter — *"The row budget already sets the
pace: 128 rows per 15 s poll, expressed as a count"* — and a deliberate delay between batches was
rejected. That argument was right about *delays* and wrong about *this* delay: the 15 s here is not
pacing anybody, it is a timer that a known-non-empty backlog waits out fourteen times. The real
governor is the thread cap, which is now measured: 2.347 rows/s at 124–140 % CPU (cap 5) versus
1.902 at 82–124 % (cap 0, ten threads). **The bound stays; only the idle goes.** Two things make
this safe only in one order: outside WP11-B2's single consumer, removing the gap speeds up all three
uncoordinated drains at once — `PendingEmbedJob`, `CodeReindexJob`, and up to four unbounded
watch-digest drains — which is exactly the saturation you reported. So WP12-B edits
`EmbedDrainService.cs` and nothing else, and cannot start before B2 lands.
*Why it matters.* A fifth of every code re-index is a timer, not work, and the fix is inside a
component WP11 is already building.
*Recommendation.* **Approve, sequenced strictly after WP11-B2 and WP11-C.** Cheaper first move if
you want the gain sooner with no new mechanism: WP11-C's `maintenance.embed-rows-per-run.global`
already ships the key — set it to 512 and 13.8 idle gaps collapse to 3.4.

**G21 — Directory ingest honours `ai-raccoon.ignore` at every level, not only at the walk root.**
*Detail.* `FileIngestor.IngestDirectoryAsync` enumerates with
`Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)` (`:148`) and filters with rules
loaded from **one** file at the walk root — `_ignoreRulesProvider.LoadAsync(path, …)` (`:147`), no
nested discovery, as the code's own prose says at `:109-111`. This repository's `ai-raccoon.ignore`
sits at the repo root, so `memory_ingest_directory` pointed at `src/` would enumerate `src/**/bin`
and `src/**/obj`: **379 MB of build output against 2.0 MB of source** on this checkout (`du -sh src`).
The single-file path already solves this — `ResolveIgnoreRootAsync` (`:113-139`) finds the containing
watch, else the admitting scope entry, else the parent. WP12-D reuses it. This is a **behaviour
change, not a speed-up**: an ignore file that has no effect today would start having one, and a bank
could lose rows on its next re-ingest. The cheap alternative is one documentation line — *"point
`memory_ingest_directory` at a directory that has its own ignore file"* — zero risk, zero code.
*Why it matters.* Build output that enters the code corpus is not just wasted walk time; every chunk
of it becomes a row that has to be embedded, and embedding is 99.66 % of the clock.
*Recommendation.* **Approve the code fix**, behind WP3 (#436) and WP11-B2, which own `FileIngestor.cs`
first — but the documentation-only option is real and it is your call which cost you prefer.

**G22 — A time-boxed research item on quantized or accelerated inference for the code engine is
scheduled (WP12-E).**
*Detail.* Every fix ranked in PR #508 shaves 10–20 % off a 1,061-second drain. Changing the
*arithmetic* is the only lever with a different order of magnitude, and it has never been examined
for this repository. What is already known and constrains it: **ADR-0049** — "The bundled model's
embeddings depend on the host CPU" — established that three arithmetic paths on CI hosts produced
three different, individually-deterministic embeddings (`avx512_vnni` present vs absent), and that is
precisely why the replacement corpus is a committed bank with vectors baked on one path rather than
generated at test time. Int8/u8s8 quantization and a CoreML execution provider are both changes of
arithmetic path, so both land squarely on that ADR: they would change stored vectors, and the parity
golden and `MiniLmGoldenVectorTests` are downstream of that. Also measured today and relevant: at the
best thread cap the drain uses ~140 % of 1,000 % available CPU, so this box has ~7× headroom that no
amount of batching will reach. **The experiment** is one run of PR #508's S3–S5 protocol per variant
— restart, re-activate all 1,762 rows to pending, take a fixed 150-second window, record rows/s and
`top` CPU — against three arms: today's fp32 CPU session, an int8-quantized `code-daemon-embed-v1`,
and the CoreML EP on this arm64 host; plus a vector-drift check of the same 1,762 chunks against the
fp32 vectors. **The decision it would settle:** whether the code corpus gets its own engine variant
(and therefore its own fingerprint, its own re-index, and an ADR-0049 amendment saying which
arithmetic path its vectors were baked on), or whether the ~20 % batching-and-idle wins in WP12-A/B
are the whole of what is available and the ranked list is closed.
*Why it matters.* Without this, WP12 tops out at roughly a third off a seventeen-minute drain, and
nobody can say whether that is the ceiling or the floor.
*Recommendation.* **Approve as research only** — architect lane, one dated record plus an ADR draft,
**no production edit and no engine swap** until you rule on what it finds.
