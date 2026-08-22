# S6a design note — replacing `jsaa-memory.db` without hollowing out the retrieval gates

Date: 2026-08-22 · Issue: ai-raccoon#414 (owner gate G5 APPROVE) · Plan: `docs/work/2026-08-22-post-delta-next-steps-plan.md` §S6
Scope: design only. No repo edits were made. Every claim below was read at the source in
`/Users/arasz/RiderProjects/ai-raccoon` at `c89400a1`.

---

## 0. Corrections to the brief's premises (verified at source)

| Brief said | Source says |
|---|---|
| ~7 consumers | **15 files, ~60 test methods.** `grep -rn jsaa-memory --include=*.cs tests/` returns 14 test classes plus one shared helper. The seven named in the plan are a subset. |
| 19 MB bank, PII, private-repo content | Confirmed. `entries` = 2518 rows / 195 source files / 1 project, and `entries.value` holds the **full chunk text** — so this is 2.37 MB of private-repo prose, not just metadata. Scrubbing the email would not make it publishable. |
| `Arasz/job-search-ai-assistant` private, `Arasz/ai-raccoon` public | Confirmed (`gh repo view --json isPrivate` → true / false). |
| `gate-query-vectors.json` might be corpus-derived | **It is not.** Its schema is `Note / GeneratedOn / ArithmeticPath / Runtime / ModelSha256 / VocabSha256 / Dimension / Vectors[]`, each vector `(Id, Query, Sha256, Vector)`. Query text and its embedding only — zero document text. |
| Option A (generate at test time) is a live candidate | **It is disqualified by ADR-0049.** See §1. |

---

## 1. The constraint that decides this: ADR-0049

`docs/adr/0049-embeddings-depend-on-the-host-cpu.md` measured, with a six-job CI matrix, that the
bundled u8s8-quantized model produces **three different embeddings on three arithmetic paths**
(arm64 NEON / x64 without VNNI / x64 with VNNI), and that the resulting `AdrNdcg5` spread is
**0.070 — fourteen times** `GoldenFile.RankingTolerance` (5e-3). ADR-0050 fixed the *query* half by
committing `gate-query-vectors.json`; the *corpus* half is fixed only because the corpus vectors are
baked into the committed `.db`, generated once on arm64.

**Therefore: any shape that computes corpus vectors on the test host re-opens the exact defect
ADR-0049/0050 closed.** Not a tolerance question — a different-answers question.

Secondary but real: 14 classes each build a store in their constructor. Test-time ingestion of
~1.4k chunks through the real ONNX model, 14 times, does not fit nightly's measured 16m49s inside a
45-minute job without introducing a shared assembly fixture (new machinery, new ordering hazards).

---

## 2. Options and verdicts

**A — synthetic bank built at test time through `FileIngestor` from committed markdown. REJECTED.**
Re-introduces ADR-0049's host dependence for the corpus leg. Every pinned number becomes
unpinnable in CI; worse, the *mechanism* gates that read rank order (StructureFusion top-1, section
rank ≤ 3, exact-rank pins) become host-dependent coin flips rather than gates. Plus the runtime
cost above. A is strictly worse than E on the axis that matters.

**B — env-gated local jsaa bank, skip when absent. REJECTED.**
Moves 14 classes / ~60 methods into a lane only one machine ever runs. The ledger has already named
this failure mode for `GraphPooledOutputParityTests` (`AIRACCOON_POOLING_PARITY_MODEL_DIR`): a gate
that skips everywhere is a gate in name only. It also leaves the private corpus alive on disk as the
only thing that can turn the gates green — the privacy problem is relocated, not solved.

**C — A for mechanism + B for measurement. REJECTED.**
Inherits A's non-determinism on the CI half and B's skip risk on the local half, and doubles the
maintenance surface: two corpora, two catalogs, two sets of pins, two regeneration tools.

**F — delete the fixture and delete its tests. REJECTED.**
Satisfies #414's literal done-criterion ("suite passes without the fixture") and destroys the entire
retrieval-quality apparatus that ADRs 0004, 0005, 0006, 0047, 0049, 0050 and 0056 rest on. This is
the silent-hollowing outcome the task exists to prevent.

**E — same architecture, new corpus: a committed bank built from *this repo's own public docs*
through the existing regeneration tool. RECOMMENDED.**

Nothing structural changes. The bank stays committed with corpus vectors baked on arm64; the query
vectors stay pinned; the numbers stay pinned; nightly keeps running every gate. Only the *content*
of the corpus changes — from private-repo prose to ai-raccoon's own public prose. All three tools
already exist and get re-pointed rather than rewritten (`JsaaCorpusRegenerationTool`,
`scripts/list-jsaa-corpus-files.py`, `GateQueryVectorRegenerationTool`).

It works because ai-raccoon's own tree has the *same shape* the gates need: ADRs with
`Context` / `Decision` / `Consequences` / `Alternatives considered` headings (the structure-vector
signal ADR-0004 tests), plus a second document family under `.ai-badger/` (the two-generator
structure `RetrievalTuningSetsTests.NoFamilyIsHeldOut_…` asserts).

### Corpus selection (measured, not estimated)

Running `scripts/src/jsaa_config.py`'s own `INCLUDE/EXCLUDE_GLOBS` against ai-raccoon's tree yields
352 files / 2.35 MB — same order as jsaa, so a ~19 MB bank again, and 4 of those files carry an
email address (all already public in those very files, but no reason to import them). Trimmed
variants, all with **zero** email-bearing files and both families present:

| Variant | Globs added to `docs/adr/*.md`, `docs/*.md`, `.ai-badger/invariants/*.md`, `.ai-badger/agents/*.md`, `README.md`, `CLAUDE.md`, `HERMES.md` | Files | Bytes | Est. chunks¹ | Est. bank² |
|---|---|---|---|---|---|
| V1 | — | 123 | 838 538 | ~893 | ~6.8 MB |
| **V2 (recommended)** | `.ai-badger/instructions/*.md`, `.ai-badger/skills/*/SKILL.md` | **178** | **1 382 424** | **~1472** | **~11 MB** |
| V3 | V2 + `docs/explanation|how-to|tutorials/*.md` | 193 | 1 509 949 | ~1608 | ~12 MB |

¹ from the measured jsaa mean chunk length of 939 chars (`sum(length(value))/count(*)` = 2 366 644 / 2518).
² from the measured jsaa ratio 19 173 376 B / 2518 chunks = 7.6 KB/chunk (text + 2×384 f32 + vec0 shadow copies + FTS index).
**Both estimates must be replaced by a measurement in W2 before anything is pinned.**

V2 is recommended because it clears `Vec0PartitionKeyProbe`'s `vectors.Count.ShouldBeGreaterThan(1000)`
without re-pinning it. V1 is the option if the owner prefers a ~7 MB fixture and accepts one recorded
re-pin of that threshold.

**The design choice that looks like a defect** (record this in the ADR): S6a replaces a committed
multi-megabyte binary in a public repo with… another committed multi-megabyte binary in a public
repo. That is deliberate. #414's harm is *private content*, not bytes; the byte win is S6b's history
rewrite. Generating the bank at test time to avoid the bytes is option A, and option A is wrong.

---

## 3. Per-consumer disposition

`M` = mechanism (asserts a code path is load-bearing; portable). `N` = measurement (a number that
only means something on the corpus it was measured on; must be re-measured or retired).

| # | File | Tests | What it pins | Class | Disposition |
|---|---|---|---|---|---|
| 1 | `tests/AiRaccoon.Tests/Unit/Retrieval/StructureFusionGateTests.cs` | 1 | generic query "What is the decision?" ranks the structure-matched chunk #1, score 1.0 | M | **Port with re-anchoring.** Highest-risk item: the query was chosen because content similarity was near-tied across jsaa ADRs. On the new corpus a new query + expected `(source_file, heading)` pair may be needed. Not done until the structure-term-disabled RED is *observed*. |
| 2 | `Integration/SectionTargetedRetrievalTests.cs` | 9 | S1–S6 section chunk rank ≤ 3; section hit@5 ≥ 4/6; file-rank no-regression vs committed baseline; schema migration adds `heading_path`/`structure_embedding` | M+N | Rank pins re-measured. **The schema-migration test should stop using this fixture at all** — it only needs a legacy-shaped bank; point it at a synthesized one and drop it off the corpus dependency permanently. |
| 3 | `Integration/QueryConstructionTests.cs` | 7 | AND→OR fallback rescues a zero-match; every catalog query returns results; FTS hit@5 ≥ 6 and MRR ≥ 0.70; exact ranks 8 and 3 | M+N | Fallback + zero-match tests port unchanged (mechanism). FTS guard and the two exact ranks re-measured. |
| 4 | `Integration/RelativeScoreFloorTests.cs` | 2 | shipped defaults truncate nothing; rank 1 scores exactly 1.0 for off-corpus queries | M | **Cheapest port — both assertions are corpus-independent.** Path + `ProjectId` swap only, no re-measurement. |
| 5 | `Integration/RrfParameterSweepTests.cs` | 1 (grid) | chosen RRF point holds C1/C5/A1/A4/A6/S2 rank gates and `AdrNdcg5 ≥ 0.5260827785380623 − 5e-3` | N + selection evidence | Re-pin on the new corpus. **The claim "these parameters were selected here" does not port** — see §7. |
| 6 | `Integration/SourceAffinitySweepTests.cs` | 1 (grid) | same for source-affinity λ, plus `gapVsBaseline ≤ 0.005` | N + selection evidence | Same. |
| 7 | `Integration/BaselineMetricsTests.cs` | 3 | evaluated-query count 19, category counts 6/10, finiteness, double-run determinism, report round-trip | M+N | Determinism and round-trip port free. Counts re-pin to the new catalog. |
| 8 | `Integration/SourceIdentityTests.cs` | 6 | results carry `source_file`/`chunk_index`/`total_chunks` (M); S2/Q2 rank ≤ 3, source-path anchor at rank 1, C1/C2/C5 rank pins (N) | M+N | Identity assertions port unchanged; rank pins re-measured. |
| 9 | `Integration/RetrievalBaselineTests.cs` | 10 | corpus integrity: all embedded, `ContentHash.Of(path,value)` holds for every row, `source_id` populated + no orphans, excluded content absent, FTS works after normalization | M | **Ports almost wholesale** — these assert properties of *a* bank, not of *that* bank. Negative tests H1–H3 re-anchor on the new EXCLUDE globs. |
| 10 | `Integration/PlatformNumericsProbe.cs` | 1 | nothing (prints CPU + embedding fingerprint + nDCG) | probe | Ports. Its ADR-0049 reference table becomes historical (§7). |
| 11 | `Integration/Retrieval/Vec0PartitionKeyProbe.cs` | 1 | `vectors.Count > 1000` ("the probe needs the real corpus to mean anything") | N | Ports under V2 with no change; under V1 needs one recorded re-pin. |
| 12 | `Integration/Embedding/VecDimensionReconcileTimingTests.cs` | 2 | reconcile p95 ≤ budget on a real-size bank; slow-stub variant must *fail* the budget | M+N | Re-measure p95 and re-pin the budget (its doc comment cites the 18 MB fixture by name and must be rewritten). The slow-stub test is already an in-suite discrimination proof — keep it, it is what makes the budget a gate. |
| 13 | `Integration/Retrieval/HeldOutRetrievalGateTests.cs` | 4 | per-query held-out nDCG@5 floors (A8 .131205 / A9 .553146 / A10 .169580), the mean floor, the reversal proof, in-sample > held-out | N + M | Floors re-measured over the **whole** gradeable set (§4). Keep the reversal proof — it is the discrimination. `InSampleScore_ExceedsHeldOutScore` **retires**: with nothing tuned on the new corpus there is no in-sample set. |
| 14 | `Integration/Retrieval/RetrievalTuningSets.cs` + `RetrievalTuningSetsTests.cs` | 3 | tier partition, held-out ≥ 3, family table `{docs, ai-badger}` | M | **Invert:** `TuningQueryIds` becomes empty → every gradeable query is held out. Family table re-pins to the same two families (V2 preserves both). |
| 15 | `Integration/JsaaCorpusRegenerationTool.cs` | 1 (env-gated) | regenerates the fixture from a second local checkout at a pinned foreign commit | tool | Becomes `DocsCorpusRegenerationTool`; the `git archive` + foreign-checkout + `JSAA_PINNED_COMMIT` machinery **deletes** — the corpus is this repo. Stays env-gated because it overwrites a committed binary. |
| — | `Integration/BaselineQueryCatalogTests.cs` | 6 | catalog is 44 queries, id list, difficulty strata, relevance grades, H1–H3 shape | N | No corpus dependency; counts and id list re-pin to the new catalog. |
| — | `Integration/PinnedQueryVectorFixtureTests.cs` | 5 | fixture covers exactly the catalog, records model shas + provenance, unit norm, throws on unknown text | M | **Ports unchanged.** Regenerating the fixture is enough. |

Total touched: ~60 test methods, ~20 pinned numbers to re-measure.

---

## 4. The one place the change makes things *better*

ADR-0056 exists because every published retrieval number was in-sample: the same 11 queries selected
the parameters and gated them. On a corpus **nobody has ever tuned on**, that circularity is gone by
construction — every gradeable query is out-of-sample. So:

- `TuningQueryIds` → `[]`; `HeldOut(catalog) == Gradeable(catalog)`.
- The held-out floors are measured over the full gradeable set instead of three queries.
- ADR-0056's finding (in-sample 0.673 vs held-out 0.285 — "out-of-sample retrieval scores 42% of the
  published figure") is preserved as a **historical measurement about a corpus that has left the
  repo**, and is no longer reproducible from it. That is a real loss of reproducibility and belongs
  in the ADR statement (§7).

Anti-drift guard to keep this honest: `RetrievalTuningSetsTests` must gain a test that goes red the
day `TuningQueryIds` becomes non-empty without an ADR amendment — otherwise someone re-tunes on this
corpus and the gate silently becomes in-sample again, exactly ADR-0056's original defect.

---

## 5. The five concrete settlements

### (1) The csproj change
`tests/AiRaccoon.Tests/AiRaccoon.Tests.csproj:40` — one line replaced, nothing else:
```diff
-        <Content Include="Resources/jsaa-memory.db" CopyToOutputDirectory="PreserveNewest"/>
+        <Content Include="Resources/docs-memory.db" CopyToOutputDirectory="PreserveNewest"/>
```
Line 41 (`Resources/gate-query-vectors.json`) is unchanged. Name per the plain-names invariant: the
bank of this project's docs → `docs-memory.db`; `ProjectId` const in the consumers → `"ai-raccoon"`
(currently `"job-search-ai-assistant"` in 8 classes).

### (2) `gate-query-vectors.json` / `PinnedQueryVectors` — both survive
The **mechanism** carries no corpus data and is unchanged. Verified schema (above): query text,
384-float vector, sha256, model provenance. Nothing derived from documents.

Its **content** is regenerated anyway, because the query catalog changes. Note what that removes for
free: `scripts/baseline-queries.json` currently commits 44 query texts naming private-repo ADR
titles and filenames (e.g. `docs:adr:0086-monochrome-console-design-system-for-the-frontend.md`,
"Why was shadcn/ui chosen over gluestack.io?"). That is private-repo *structure metadata* — no PII,
no document content.

**Recommendation:** treat it as leaving HEAD as a welcome side effect, and do **not** expand S6b's
history rewrite to cover `scripts/baseline-queries.json` or `gate-query-vectors.json`. ADR titles
carry no personal data, and widening a `filter-repo` pass raises S6b's blast radius for no privacy
gain. Owner decision — see §8 O1.

### (3) Tooling
- `Integration/JsaaCorpusRegenerationTool.cs` → `Integration/DocsCorpusRegenerationTool.cs`.
  Delete `JsaaRootEnvVar`, `DefaultJsaaRoot`, `PinnedCommit`, `ExtractPinnedCommit` and the
  `Directory.SetCurrentDirectory` dance (it existed only to keep a temp extraction root out of the
  stored paths — with the repo as its own corpus, repo-relative paths are already correct). Keep the
  env gate (`AIRACCOON_REGENERATE_DOCS_CORPUS=1`): it still overwrites a committed binary. Record
  the generating commit SHA into the bank's `settings` table as provenance.
- `scripts/list-jsaa-corpus-files.py` → `scripts/list-corpus-files.py`;
  `scripts/src/jsaa_config.py` → `scripts/src/corpus_config.py` carrying the V2 glob set, with
  `JSAA_ROOT` and `JSAA_PINNED_COMMIT` deleted.
- `scripts/ingest-jsaa-docs.py`: **keep** — it is the owner's local CLI for ingesting the private
  project, not a fixture producer. But it hardcodes `JSAA_ROOT = /Users/arasz/RiderProjects/job-search-ai-assistant`
  and a jsaa commit SHA in a public repo. Move both to env vars / a gitignored local config in the
  same PR (~20 lines). Leaving the pin behind after the fixture goes is the "derive or delete the
  list" smell. Split to a follow-up issue only if review says the PR is too large.
- `docs/work/2026-08-15-fts-term-budget/sweep.py` opens the fixture by path. It is a dated work-doc
  artifact describing what was done at the time — **leave it untouched**; note in the ADR that the
  corpus it read has left the repo.

### (4) `.gitignore`
Existing entries are `*.db-shm` / `*.db-wal`; `*.db` itself is **not** ignored and must stay that way
(the new fixture is committed). Add exactly:
```
# Removed at #414: a bank built from the private job-search-ai-assistant tree must never come back.
tests/AiRaccoon.Tests/Resources/jsaa-memory.db
# Owner-local regenerated banks (never committed).
tests/AiRaccoon.Tests/Resources/*-local.db
```
A `.gitignore` line is not a gate — pair it with the W0 test below.

### (5) RED-first proof, per changed gate
`prove-the-check-fails` binds *per corpus*: a RED captured on the jsaa corpus proves nothing about
the same assertion on the new one. Each ported gate needs its own observed red.

| Gate | RED before the change | RED after the port (must be observed on the new corpus) |
|---|---|---|
| W0 fixture-absence guard | file still present → guard red | delete the file → green; restore it → red again |
| StructureFusionGate | — | force `StructureFusion.Fused` to ignore the structure term → expected chunk loses rank 1. **If it stays green, the gate is vacuous on this corpus and the query/expected chunk must be re-anchored until it isn't.** |
| QueryConstruction AND→OR | — | disable the OR fallback → the zero-match query returns empty |
| RelativeScoreFloor | — | re-introduce an absolute floor → truncation appears in the ≤ limit check |
| HeldOut floors | — | the in-suite `ReversedRanking_FailsTheHeldOutMeanFloor` must fail the mean floor **and be watched failing**, not assumed |
| Rrf / SourceAffinity sweeps | — | move the chosen point one grid step off the optimum → a rank gate or `AdrNdcg5` goes red (ADR-0050's mutation recipe) |
| Vec reconcile timing | — | the in-suite slow-stub test already is the proof; re-run it against the re-pinned budget |
| Corpus integrity (RetrievalBaseline) | — | corrupt one row's `value` in a scratch copy → the `ContentHash.Of(path, value)` check goes red |

---

## 6. Ordered work list — one PR

| # | Work | Acceptance | Command that proves it |
|---|---|---|---|
| W0 | **RED first.** Add a guard test: `Resources/jsaa-memory.db` does not exist, and no `.cs` under `tests/` mentions `jsaa-memory`. | Red while the fixture is present. | `dotnet test --filter "FullyQualifiedName~FixtureRemoval"` → 1 failed |
| W1 | `scripts/src/corpus_config.py` (V2 globs) + `scripts/list-corpus-files.py`. | Selects 178 files, 0 carrying an email. | `python3 scripts/list-corpus-files.py . \| python3 -c "…"` — assert count and an email scan over the selection |
| W2 | `DocsCorpusRegenerationTool`; run once **on arm64**. | `Resources/docs-memory.db` written; rows > 0; `EmbedPendingAsync` leaves 0 pending; `structure_embedding` non-null on > 0 rows; **measured** file size recorded, and a test pins a size ceiling. | `AIRACCOON_REGENERATE_DOCS_CORPUS=1 dotnet test --filter "…DocsCorpusRegenerationTool"`; `du -h tests/AiRaccoon.Tests/Resources/docs-memory.db` |
| W3 | New `scripts/baseline-queries.json` over the new corpus (same category structure: ADR / Structural / Invariants / Negative), with `expectedSource`, difficulty and relevance grade. | `BaselineQueryCatalogTests` green at the new pinned count. | `dotnet test --filter "FullyQualifiedName~BaselineQueryCatalogTests"` |
| W4 | Regenerate `gate-query-vectors.json`. | `PinnedQueryVectorFixtureTests` green **unmodified** (5 tests). | `AIRACCOON_REGENERATE_GATE_QUERY_VECTORS=1 dotnet test …` then `dotnet test --filter "…PinnedQueryVectorFixtureTests"` |
| W5 | Re-point all 15 consumers: fixture path, `ProjectId`, expected sources. Run; capture every measured number. | Suite runs to completion; failures are only the pinned numbers. | `dotnet test --filter "Category=Retrieval"` |
| W6 | Re-pin each number. One line per number in the commit message, **old jsaa value recorded beside the new one**. | No bound widened without a stated reason. | review of the diff |
| W7 | Produce each RED from §5(5). | Every ported gate observed failing once. | per-row commands above; each red pasted into the PR body |
| W8 | Delete `Resources/jsaa-memory.db`; csproj line swap; `.gitignore`. | W0's guard flips green. | `git rm`, `dotnet test --filter "FullyQualifiedName~FixtureRemoval"` |
| W9 | ADR-0089 (§7) + status lines on ADR-0047 / 0049 / 0050 / 0056 marking their measurements historical. | ADR index updated; the one-line-frontmatter trap avoided. | ADR index check |
| W10 | Work note + ledger row; comment on #414: S6a done, S6b still owed. | #414 shows the immediate half closed and the rewrite still open. | `gh issue comment 414` |

Honest sizing: this is roughly a day, including one arm64 regeneration run and one full
`Category=Retrieval` pass — not a one-hour PR. If it must be split, W0–W2 + W5's mechanism-only
classes can ship first, but **the second PR must be filed as a blocking issue before the first
merges**, and the ledger must record the window during which the measurement gates are skipping —
that window is option B, time-boxed, and it should be short and visible.

---

## 7. The ADR statement of lost coverage (draft, for ADR-0089)

> ## What this costs
>
> No mechanism gate is lost. Every assertion that proves a code path is load-bearing — the
> structure-vector fusion, the AND→OR FTS fallback, the relative score floor, source identity on
> results, corpus hash-contract integrity, the vec0 dimension reconcile budget — ports to the new
> corpus and is re-proved red there. Every one of the 14 classes keeps running in nightly CI on a
> committed bank; nothing moves behind an environment variable.
>
> Three things are genuinely lost, and none of them is recoverable from this repository again:
>
> 1. **ADR-0049's numeric table is no longer reproducible in-repo.** `PlatformNumericsProbe` still
>    reproduces the *phenomenon* — three arithmetic paths, three different embeddings — but the
>    specific figures 0.5260827785380623 (arm64), 0.5587755695473325 (x64 no-VNNI) and
>    0.48859561353453607 (x64 VNNI) were measured on the jsaa corpus and stay as recorded history.
> 2. **ADR-0056's circularity measurement is now historical.** The in-sample/held-out gap — 0.673
>    against 0.285, "out-of-sample retrieval scores 42% of the published figure" — was measured over
>    a tuning/held-out partition of the jsaa corpus. That partition cannot be re-derived here. On the
>    new corpus the gap is not merely unmeasured, it is undefined: nothing was ever tuned on this
>    corpus, so every gradeable query is out-of-sample and the held-out gate covers the whole catalog.
>    That is a stronger gate and a weaker record, and both halves are deliberate.
> 3. **The evidence that the shipped fusion parameters are optimal does not port.** ADR-0005's
>    source-affinity grid and ADR-0006's 96-point RRF grid selected k = 60, weights 1:1 and λ = 0.1
>    over the jsaa corpus. `RrfParameterSweepTests` and `SourceAffinitySweepTests` continue to gate
>    that the chosen point holds its rank and nDCG floors here, re-pinned; they no longer testify
>    that it was *selected* here. If the chosen point turns out not to dominate its neighbours on this
>    corpus, that is a finding about parameters overfitted to one corpus — file it, do not widen the
>    gate.
>
> ## The choice that looks like a mistake
>
> This ADR replaces a committed multi-megabyte binary fixture in a public repository with another
> committed multi-megabyte binary fixture in a public repository. That is deliberate. Issue #414's
> harm is private third-party content, which S6a removes; the byte cost is removed by S6b's history
> rewrite. Building the bank at test time instead would avoid the bytes and re-open ADR-0049: corpus
> vectors computed on the test host take one of three arithmetic paths whose nDCG spread is fourteen
> times the gate tolerance, which would turn every retrieval gate into a host measurement — the exact
> defect ADR-0050 was written to close.

---

## 8. Open points for the owner — each with a recommendation so work can proceed

| # | Question | Recommendation (proceed under this unless overridden) |
|---|---|---|
| O1 | Does S6b's history rewrite also strip `scripts/baseline-queries.json` and `gate-query-vectors.json`, whose query texts name private-repo ADR titles? | **No.** ADR titles carry no personal data and no document content; widening `filter-repo` raises S6b's blast radius for no privacy gain. The jsaa query texts leave HEAD anyway in W3/W4. |
| O2 | Corpus size: V2 (~178 files, est. ~11 MB) or V1 (~123 files, est. ~7 MB)? | **V2.** It clears `Vec0PartitionKeyProbe`'s `> 1000` vector threshold with no re-pin and keeps the reconcile-timing budget on a realistically sized bank. Take V1 only if the committed size is the binding constraint, and accept two recorded re-pins. |
| O3 | One PR (~60 tests, ~20 re-pins, ~1 day) or split mechanism-now / measurements-later? | **One PR.** A split creates a window where the measurement gates skip in CI — option B, which this note rejects. If split is forced, file the second half as a blocking issue first and record the window in the ledger. |
| O4 | `scripts/ingest-jsaa-docs.py` de-hardcoding (private path + pinned commit) — same PR or follow-up? | **Same PR.** ~20 lines, same class of leak, and leaving the pin behind after the fixture goes is precisely the stale-list smell. |
| O5 | If `StructureFusionGateTests` cannot be made to go red on the new corpus with any query? | Then the structure signal is not load-bearing on ADR-shaped prose from this repo, which is a **finding about ADR-0004**, not a reason to keep a passing assertion. File it, mark the gate `Skip` with the issue number, and say so in ADR-0089. Do not ship a green assertion that has never been seen red. |

---

## 9. Amendments — what execution proved wrong (2026-08-22, PR #450)

This note was written before the work. Executing it corrected the following. The original text
above is left unchanged so the diff between plan and outcome stays readable.

| § | The note said | Measured at execution |
|---|---|---|
| §2 | V2 selection = 178 files / 1 382 424 B / ~1472 chunks / ~11 MB | **199 files / 1 608 970 B / 2049 chunks / 17 231 872 B (16.43 MiB)**. The note's V2 also included `.ai-badger/skills/*/references/**`, which is 148 more files and produced an ~19 MB bank — no smaller than the one being removed. Those are excluded by measurement. |
| §2 | Trimmed variants have **zero** email-bearing files | One remains: `docs/reference/agent-memory-server.md` carries the placeholder `you@domain.com`. The owner's real address is absent, which is #414's actual harm, and the test asserts that rather than "no @ sign". |
| §2 | ~19 MB → ~11 MB, i.e. a large byte win | 19 173 376 → 17 231 872 B, **about a tenth**. The note's 7.6 KB/chunk ratio does not hold here (8.4 KB/chunk). The byte win is S6b's, not this PR's. |
| §3 #15 / §5(3) | `Directory.SetCurrentDirectory` can be deleted | **Wrong — it is load-bearing.** It is what makes `IngestFileAsync` store repo-relative `source_file` values; without it this worktree's absolute path is baked into the committed binary. Kept, with the reason recorded. Verified: 0 rows match `/%` or `%worktrees%`. |
| §3 (table) | `PinnedQueryVectorFixtureTests` is 5 tests | 6. Ports green unmodified either way. |
| §4 | `TuningQueryIds` → `[]` is a contained change | It is not. Both parameter sweeps derived their **evaluation** set from `TuningQueryIds`, so emptying it left them measuring zero queries and reporting nDCG@5 = 0 — two gates silently stopped gating. Fixed by separating `SweepGateQueryIds` (what a sweep evaluates) from `TuningQueryIds` (what a sweep once selected over). |
| §8 O2 | V2 (~178 files, ~11 MB) or V1 (~123, ~7 MB) | Neither as specified. Shipped: V2 **minus skill reference files** — 199 files, 16.43 MiB, ~2049 chunks, clear of `Vec0PartitionKeyProbe`'s 1000-vector floor with margin and with no re-pin of that threshold. |
| §8 O5 | If `StructureFusionGateTests` cannot be made red, file an issue and `Skip` | **Does not apply.** It is red decisively: with the structure term on, all five vector-only top hits carry the "Decision" heading; with it off, none do. No issue filed, nothing skipped. |
| §5(5) | (not anticipated) | `RrfParameterSweepTests` carried a private `BuildHashMapFromCorpus` duplicating `CorpusHashMap.Build` and **drifted** — it compared `heading_path` verbatim where the shared helper slugifies. They agreed only while every gate section was one word. Deleted in favour of the shared helper. |
| §5(4) | `.gitignore` needs the two entries | Correct, but the accompanying exclude-glob list in `corpus_config` did not: measured, the selection is byte-identical with an 18-entry exclude list and with none, because no include glob reaches those trees. The list is deleted rather than carried. |
| §6 W3 | (not anticipated) | The catalog must be intersected with `git ls-files`. The first regeneration baked 29 chunks of a sibling branch's untracked, unmerged ADR into the fixture. |

### Not done here, deliberately

- **S6b** (history rewrite) — owner-executed, out of scope, #414 stays open for it.
- **ai-raccoon#454** — an AND-under-match retrieval anchor on this corpus. The jsaa-specific
  assertion is `Skip`ped rather than re-pinned to an invented number.
- **ai-raccoon#455** — `benchmarks/.../RealWorldCorpus.cs` still carries prose extracted from the
  private jsaa tree (294 lines, 107 mentions, no PII). Same harm class as #414 at much smaller
  scale; fixing it means re-deriving benchmark ground truth, which needs its own gates.

## Amendment — 2026-08-22: row 12 superseded

Row 12 planned to "re-measure p95 and re-pin the budget" for
`Integration/Embedding/VecDimensionReconcileTimingTests.cs`, and to keep the slow-stub test because
"it is what makes the budget a gate". Both are superseded: owner ruling, same day, is that no test
asserts wall clock ("they should not fail on any env"), so there is no budget left to re-pin.

The file is replaced by `Integration/Embedding/VecDimensionReconcileWorkTests.cs`, which asserts the
work the no-change path does rather than the time it takes — the exact `sqlite3_trace` statement
sequence, `total_changes()` unchanged, `schema_version` unchanged, both vec table definitions
byte-identical. The slow-stub discrimination proof is replaced by
`ChangePath_RecreatesTheVecTables_AndEveryWorkObservationReportsIt`, which drives the REAL
reconciler down its change path (engine at 512 against a 384 bank) instead of a stub that sleeps.

Row 12's fixture caveat is also resolved rather than accepted: the plan wanted a ≥200 MB bank and
settled for the 16.43 MiB `docs-memory.db`. Under a statement-count gate the size stops mattering,
and `NoChangePath_CostsTheSameOnARealBankAsOnAnEmptyOne` now proves the O(1)-in-bank-size claim the
old doc comment could only argue for in prose.
