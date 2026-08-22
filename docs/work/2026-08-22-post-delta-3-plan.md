# Plan — post-delta session 3 (rev 1 — owner gate pending)

**Date:** 2026-08-22 · **Base:** main `021d6c17` (v1.32.0 released 14:00:31Z **and published to
nuget.org**; PRs #463/#464/#466/#467/#468 merged) · **Status:** rev 1 — **owner gate pending**; no
work package starts until the gate below is answered, except the two marked *ungated* ·
**Task:** `post-delta-3` · **Lane:** architect (plan + gate), Opus.

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
8. WP6 — #455 re-derive corpus, queries and the parity golden (G14, scoped by G9).
9. WP3 — #436 the re-ingest prune reaches `code_entries` (G12).
10. WP8 — #454 AND-under-match anchor (G13); WP9 — EventId disposition (G6).

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

## Sequencing

**Parallel from the start** (no shared files): WP1, WP2, WP7, WP8, WP5.
**Serial chain:** WP4 → WP6 → *(owner)* S6b rewrite. WP4 is small and must land before WP6 rewrites
the corpus under it.
**Alone-ish:** WP3 — widest blast radius (`IFileIngestor`/`ICodeIngestor`/`SqliteMemoryStore`);
anything added later touching ingestion queues behind it.
**Shared-file map:** `ParityGateTests.cs` (WP4, then WP6 re-runs it); `RealWorldCorpus.cs` /
`reference-topk.json` (WP6 only); `SearchResults.cs` (WP1 only); `ModelDownloadPlanner.cs` (WP2
only); `docs/adr/README.md` (WP9 only). No two parallel WPs touch the same file.

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

Fifteen cards. Each says what becomes true if you approve, gives the numbers behind it, and carries
a recommendation. Nothing in *Scheduling* starts before you rule.

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
