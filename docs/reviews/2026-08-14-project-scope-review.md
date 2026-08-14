# Project-scope review — AiRaccoon

Date: 2026-08-14 · Base commit: `1d1889d517baf840df0b839f547091bd7f46808b` ·
Campaign branch: `campaign/project-scope-review-0814` · PR #290

Eight read-only expert lanes, an adversarial falsification pass, and live-system calibration against
the deployed bank. Ground truth is in
[`2026-08-14-project-scope-review-ground-truth.md`](2026-08-14-project-scope-review-ground-truth.md);
every lane's full report, verbatim, is in [`lanes/`](lanes/).

## Method

Lanes were derived from this repository's own layout rather than a fixed roster: **architecture**
(opus), **retrieval quality** (opus), **security/encryption/sync** (opus), **data access/SQLite**
(sonnet), **test-suite QA** (sonnet), **consumer surface — MCP tools and CLI** (sonnet), **runtime
operations** (sonnet), **CI/scripts/docs** (sonnet). Each ran in its own worktree, was given the same
measured ground-truth block, was told explicitly that **proving a briefed claim wrong is worth more
than confirming it**, and had to grade every finding `MEASURED` / `READ` / `INFERRED` / `UNVERIFIED`
with `path:line` evidence.

**94 findings** across the eight lanes plus the orchestrator, of which **43 are MEASURED**. Every
claim that drives expensive work was re-verified by the orchestrator at `path:line` and then attacked
by an independent falsification pass (see *Review integrity*).

## Verdict

**The compiler-enforced half of this system is in good shape and two things it cannot check are
broken: a delete path that ignores which project asked, and a vector index that has never seen a
tenth of the text in it.**

What is genuinely healthy should not be lost in what follows, because a lot of it is. `AiRaccoon.Core`
has no project references and — beyond `Microsoft.Extensions` — **zero** `using Microsoft.*` of any
kind; only 2 of its 106 files hold any I/O, HTTP, process or environment concern. There is not a
single `AddScoped` or `AddTransient` anywhere in `src/`, so the captive-dependency class of bug is
empty by construction. Every third-party GitHub Action is SHA-pinned. The suite is green at 2,861
passed / 0 failed, CI's three trait filters partition it **exactly** (two lanes measured this
independently and got the same three numbers), the default-interface-member dispatch trap is already
guarded with its own dedicated test, and the shared test fakes derive their output from their input.
The project deletes its own unfailable tests when it finds them, and ADR-0006 declines to re-tune
against a corpus it no longer trusts rather than silently re-picking a better number. That is unusual
discipline.

Against that, three things stand out.

**1. `memory_delete_context` decides whose data to delete from a string the caller supplies.** The
tool checks access mode against the `projectId` argument and then hands the raw `context` argument to
a filter builder whose `project:` branch **replaces** that `projectId` with whatever the context
string says, and whose `shared` branch has no project predicate at all. One call deletes another
project's entries; one more wipes the shared tier every project reads. This is the same defect commit
`7698dc63` fixed on the *write* path six commits ago — the fix was applied to the function where the
bug was found, not to the concept, and the delete path's copy of the same mapping sits 1,000 lines
away in a different file. **Its precondition is met in production**: `access.mode.global` on the live
bank is `full`, and the bank holds five real projects and 138 shared rows.

**2. 42.7% of the live bank's entries exceed the embedder's 256-token window, and the overflow is
dropped rather than split.** Measured with the real bundled WordPiece tokenizer: 6,897 of 16,145
entries, and 399,243 of 4,129,520 tokens — **9.67% of all indexed text** — are never embedded while
every row is marked `embed_state='embedded'`. The text stays in `entries.value` and in the FTS index,
so it remains keyword-reachable; what it can never do is surface on the semantic list. That makes it a
silent ranking defect rather than data loss, which is exactly why nothing has ever failed.

**3. The number `memory_search` returns as `ranking` carries no information about match quality.**
It is exactly `(rrfK+1)/(rrfK+rank)` — a closed-form function of rank position — because an
already-fused list is handed to a merger that fuses it a second time and rebuilds every score from
ordering alone. A strong match set and a set of near-orthogonal junk produce byte-identical score
curves; so does a live query for `"completely unrelated gibberish zzzqqq flibbertigibbet quantum
banana"`. Two independently-tuned knobs feed a value that is then discarded.

The pattern behind all three is the same, and it is worth naming: **this codebase's failures are
concept-level, not line-level.** A confinement rule enforced in one of its two copies. A token budget
enforced at chunk time and not at embed time. A fusion computed and then recomputed. Each half is
individually correct, reviewed, and tested — which is why a green build, a green suite and 0 warnings
did not catch any of them.

---

## Live-system calibration — what is armed and what is merely loaded

Read-only queries against the deployed bank (`~/.ai-raccoon/memory.db`, 167 MB, 16,145 entries,
`user_version` 8, WAL) and the installed binary `1.12.0+c5f3fa264ea5ce310fc9c76ea093c4dabfc8665b`.

| Check | Result | What it changes |
|---|---|---|
| `access.mode.global` | **`full`** | The delete blocker is **armed**, not latent |
| Projects in the bank | jsaa 8,863 · ai-raccoon 3,997 · ai-badger 1,940 · arasz-home-page 855 · hermes-default 464 | Real cross-project data is in reach of it |
| `scope='shared'` rows | 138 | Wipeable in one call; also writable at the default `rw` |
| Entries over the 256-token window | **6,897 / 16,145 (42.7%)** | 9.67% of indexed text invisible to vector search |
| `structure_embedding IS NULL` | **10,311 / 16,145 (64%)** | Those rows are capped at half score by the fusion formula |
| vec0 chunks allocated | **36 for 16,150 vectors** (2.28×) | ~43 MB of a 159 MB bank is empty chunk padding |
| `ttl_days IS NOT NULL` | **0 of 16,145** | `memory_set_ttl` has never fired — loaded, not fired |
| `noise_entries` / `noise_clusters` | **0 / 0** | Seven tables, zero rows, ever |
| `promotion_discards` / `promotion_queue` | 965 / 19 | Discards outnumber everything else the feature produced; no reaper |

Two consequences follow that the code alone does not show.

**The delete blocker is the only finding here that is actively dangerous today.** Everything else is
either degrading quality silently or costing disk. That is what makes it Wave 1 on its own.

**The truncation is not explained by stale data, and establishing *what does* explain it took three
rounds.** The obvious benign story — the bank predates the chunk-budget fix — is refuted by the
timestamps: the oversized rate *rises* after the fix landed. The retrieval lane then attributed it to
stale server processes, which is true but incomplete, and the adversarial pass corrected the rest. The
honest account has three parts:

1. **Stale processes wrote pre-fix chunks for hours.** The binary was installed at **22:24:36** (not
   22:10:24 — that figure matches nothing on disk), and three long-lived `ai-raccoon --quiet` processes
   started at 17:21, 18:58 and 22:12 all predate it.
2. **The chunker at this base is clean** — 0 of 276 chunks over the window on the exact offending
   files — **but only once `embedding.provider` is set.** With it unset, `FileIngestor.ChunkSizeForAsync`
   silently keeps the default o200k counter and produces 104 of 258 over-window. Configuring the engine
   later re-embeds the bank but **does not re-chunk it**, so ingest-then-configure permanently poisons
   whatever was already there.
3. **`memory_write` does not chunk at all, in the code running right now.** A 9,360-character body
   stores as one row and is embedded from its first 254 tokens. That accounts for **555 live rows and
   114,883 never-embedded tokens**, and it is the path agents use for their own notes.

So it is a restart **and** a backfill **and** a code fix — not the two-step the first account implied.
The same staleness separately explains why this session's live MCP schema still advertised
`minScore: 0.7` after the code had renamed it to `minRelativeScore` defaulting to `0.0`; one lane
nearly filed that as a defect before tracing it.

---

## Review integrity — what the adversarial pass changed

An independent reviewer was given the ten load-bearing claims and the sources, **not** the
orchestrator's reasoning, and instructed to falsify them, defaulting to "refuted" when uncertain. It
re-derived every claim, re-ran every measured one, and **exploited two of them end to end**. Nothing
came back `UNVERIFIED`.

| Claim | Outcome |
|---|---|
| **B1** cross-project delete and shared-tier wipe | **Reproduced — and exploited.** Upgraded from READ to MEASURED |
| **B2** 42.7% over the window, 9.67% of tokens unembedded | **Reproduced** (42.74% / 9.70%, ±0.03pp) — the FTS sub-caveat **refuted** |
| **H5** the truncation persists only because the processes are stale | **Corrected** — chunker half reproduced, the causal story is wrong and incomplete |
| **H1** `ranking` is exactly `(rrfK+1)/(rrfK+rank)` | **Corrected** — mechanism real, closed form false in the dominant case |
| **H4** `alpha=0.5` halves structure-less rows | **Reproduced + corrected** — on the live path under an explicit setting; the cap is broader than claimed |
| **H11** vec0 chunk waste ≈ 43 MB | **Reproduced to the byte** — but the *cause* is corrected, and it changes the fix |
| **H13** a false `crit` on every graceful shutdown | **Symptom refuted** — the structural fact is real, the consequence is not, and the one real `crit` has a different root cause |
| **H6** `memory_write(context:"shared")` at default `rw` | **Reproduced — and exploited** |
| **H2** the baseline metrics gate can never fail | **Reproduced, softened** — the cited block is correctly diagnosed; the surrounding file is not as bare as the claim implies |
| CI's three filters partition the suite exactly | **Reproduced, no corrections** — verified five ways |

**The errors ran in both directions, and two of them change what gets built.** That is the point of
running this before anyone implements.

### B1 was exploited, not just read

From a project named `attacker`, against one named `victim`, on a scratch bank at mode `full`, driving
the real MCP server (`AiRaccoon 1.12.0.0`) over JSON-RPC stdio:

```
memory_delete_context {projectId: "attacker", context: "project:victim"}
  -> {"data":{"deleted":2}}
memory_stats victim   (before) -> {"entries":2,"contexts":["project:victim"]}
memory_stats victim   (after)  -> {"entries":0,"contexts":[]}
memory_stats attacker (after)  -> {"entries":1}   # attacker's own row untouched
```

and the shared tier, same session: two rows written by two different projects, both visible
cross-project, both destroyed by `memory_delete_context {projectId:"attacker", context:"shared"}` →
`{"deleted":2}`. **At the default `rw` the same call is correctly refused** (`access-denied:
memory_delete_context requires mode full (current rw)`) — so the gate works; the precondition is simply
satisfied in production.

The pass also sharpened the fix: `EntryBucket.cs:16-25` is the *write*-path twin of `FilterFor` and
**already throws** `ContextOutsideProjectException`, with a test that asserts it
(`MemoryStoreContextScopeTests.cs:39`). This is an invariant the project has ratified and gated on one
side, not an undecided design question.

### H5's causal story was wrong, and the correction adds work

Three separate errors, and the third is the expensive one.

1. **The timestamps were wrong.** The binary was installed at **22:24:36**, not 22:10:24 —
   `stat` on `~/.dotnet/tools/ai-raccoon` and the `1.12.0` store directory both say so, and nothing on
   disk matches 22:10:24. So all three `--quiet` processes predate the new binary, including the
   22:12:10 one the orchestrator reasoned about as post-update.
2. **A fourth process was missed** — `82875  Fri Aug 14 22:24:47  ai-raccoon serve --restart`, the only
   genuinely post-update one.
3. **`memory_write` does not chunk at all, in the current build.** A 9,360-character body written
   through it on HEAD stores as **one row**, `embed_state='embedded'`, embedded from its first 254
   tokens — roughly 85% of the content absent from its vector. Live cost: **555 rows, 320 of them
   over-window (57.7%), 114,883 tokens never embedded.** That is the path agents use for their own
   notes, and **no restart fixes it.**

The chunker half does reproduce — 0 of 276 chunks over the window on the offending files — but only
after `ai-raccoon model set local`. Which produced the pass's second new finding: **the ingest budget is
silently non-engine-aware when `embedding.provider` is unset** (104/258 over-window without it, 0/276
with it), and setting the engine later re-embeds but **does not re-chunk**. Ingest-then-configure is a
supported order that permanently poisons the bank, and is a more plausible origin for much of the live
42.7% than process age.

### H11 reproduced to the byte, and the byte-level cause changes the fix

`43,424,256 bytes ≈ 43.4 MB`, recomputed from `dbstat` page bytes. The `size` column is 1024 on every
chunk row and each blob is exactly 1,572,864 bytes (1024 × 384 × 4) whether the chunk is full or nearly
empty — so chunks are fixed-capacity and the arithmetic holds, measured rather than assumed.

**But without the `ctx` partition key the same 21,985 vectors need 22 chunks rather than 49, so ~42.6 MB
— 98% — of the waste is attributable to the partition key, not to the 1024 default.** Pinning
`chunk_size` alone would recover almost nothing. The original work package would have shipped, passed
its gate, and reclaimed ~2% of what it promised.

Two inputs corrected: **13** partitions hold under 10 rows, not 14; and the bank is **172.1 MB** now,
not 159 MB — it grew ~22 MB during the review window.

### H13's symptom is refuted

The structural fact is real at all four sites, and `PeriodicTimer.WaitForNextTickAsync` was probed
rather than assumed — it **throws** on cancellation and never returns `false`. But
`Host.TryExecuteBackgroundServiceAsync` **returns early with no log at any level** when the task is
cancelled during `ApplicationStopping`, which is precisely this case. The evidence is direct:
`~/.ai-raccoon/quiet.log`, 104,002 lines across six days containing 10+ shutdown markers, has **zero**
`[Critical]` lines and zero `BackgroundService failed` lines.

The one real `crit` in `serve.log` has a different root cause entirely: **`NodeRunner.cs:117-129`
double-disposes the host**, calling `DisposeAsync()` in a `finally` after `WaitForShutdownAsync` has
already run `StopAsync`. The four catch blocks are still worth adding defensively, but the
justification and the root cause both needed rewriting before anyone implemented against them.

### H1's closed form is false in the dominant case

The double fusion is real and the arithmetic was re-derived. But `SearchQuery.cs:16` declares
`double SourceLambda = 0.1` — the default is **0.1, not 0** — so `SourceAffinityRanker.Rank` does not
short-circuit: it adds a sibling boost, **re-orders**, **drops** rows via `Consolidate`, and
**re-normalises**. Measured on the current build with ingested chunks: `1, 0.9487, 0.9369, 0.9254`
against the claimed formula's `1, 0.9839, 0.9683, 0.9531`. Solving backwards, output positions 2-4 are
*fused* positions 5, 6, 7.

**Corrected value:** `(rankBase + 0.1 × adjacentSiblings) / max`, where
`rankBase = (rrfK+1)/(rrfK+rank)`. It reduces to the claimed closed form only when no candidate earns a
sibling boost — true for bare `memory_write` rows, false for the ingested chunks that dominate the
bank. **The claim's substance survives**: the field carries no match-quality signal, only rank position
plus a structural adjacency term.

### Two claims got stronger under attack

**B2's FTS caveat was refuted in the finding's favour.** The lane hedged that `content='entries'`
external-content FTS5 might not index what it appeared to. It does: for the longest live row (34,010
chars), four terms drawn from the last 5% of its text each match through the index. The truncated tail
is genuinely keyword-reachable and genuinely vector-unreachable — which is what makes this a silent
ranking defect rather than data loss.

**H4 is on the live path under an explicit setting**, not an untouched default: the live bank carries
`retrieval.structureAlpha|0.5` as a real row, and the null fraction is **63.88%** — the claimed 63.9%,
exact. Two corrections tighten it: the penalty hits **any** row outside the structure top-k, not only
headless ones (`StructureVectorSearchByFilter` is a KNN bounded by `@limit`), and the fused score is an
intermediate value rather than the user-visible `ranking`. Both leave the finding standing.

### New findings the pass turned up while attacking the others

1. **`memory_write` does not chunk, and the current build truncates it silently** — 555 live rows, 114,883 tokens. See H5 above. **This is the most important thing the adversarial pass found.**
2. **The ingest chunk budget is non-engine-aware when `embedding.provider` is unset**, and configuring the engine later re-embeds without re-chunking.
3. **The `project:` scoping invariant is enforced and tested on writes, absent and untested on deletes** — which is how B1's fix should be scoped.
4. ~~Nothing gates the `Speed` trait~~ — **refuted by the orchestrator, and this one is worth reading as a lesson.** The pass and the QA lane disagreed; the QA lane was right. `tests/AiRaccoon.Tests/Unit/SpeedGateCoverageTests.cs` exists, asserts every `[Fact]`/`[Theory]` class carries a `Speed` trait, and has the anti-vacuity check. Settled by opening the file, then by **proving the gate can fail** — a trait-less probe class turned it red and removing it turned it green. **The pass that corrected five lane claims got one of its own new findings wrong.** That is the argument for arbitrating by reading rather than by counting who said what, and it is why an adversarial pass is a stage in a campaign rather than the last word in one.
5. **The deployed server is older than HEAD** — its schema exposes `minScore`/0.7/ADR-0006 where HEAD has `minRelativeScore`/0/ADR-0047. Related: `quiet.log` contains **zero** "Chunk truncated at embed time" lines, because the code carrying that EventId is not what is running.
6. **`NodeRunner.StartHttpMcpServer` double-disposes the host** — the actual cause of the one real `crit`.
7. **A flaky test sits in the PR gate** — `ToolRefusalsTests.KnownRefusal_ReturnsRefusal_WithoutAnSdkErrorLog` failed on a single `Speed=Fast` run and on an unfiltered run, then passed clean on rerun. This **corrects the QA lane**, which saw it only under artificial concurrency and reasonably concluded it was not a CI risk.

---

## Findings

### Blockers

| # | Finding | Grade | Lane |
|---|---|---|---|
| B1 | `memory_delete_context` deletes any project's entries and can wipe the shared tier, because `FilterFor`'s `project:` branch binds `project_id` from the caller's context string and its `shared` branch has no project predicate | **MEASURED — exploited end to end** | security F1 |
| B2 | 42.74% of live entries exceed the 256-token embedding window; 9.70% of all indexed text is never embedded and is silently dropped rather than split | MEASURED, reproduced independently | retrieval F5 |
| B3 | **`memory_write` does not chunk at all in the code running now** — 555 live rows, 320 over-window, 114,883 tokens never embedded, on the path agents use for their own notes | MEASURED | adversarial NF1 |

### High

| # | Finding | Grade | Lane |
|---|---|---|---|
| H1 | The `ranking` field is exactly `(rrfK+1)/(rrfK+rank)` and carries no match-quality information — a fused list is fused a second time | MEASURED | retrieval F1 |
| H2 | The headline nDCG/MRR/recall gate asserts only that its metrics lie in `[0,1]`, so it cannot fail | READ | retrieval F8 |
| H3 | Every published RRF number is in-sample — the same 11 queries select the parameters and then gate them | READ | retrieval F7 |
| H4 | The dual-vector fusion scores the 64% of rows with no structure embedding at half of what a headed row can reach, biasing ranking on document formatting | MEASURED, **two lanes independently** | retrieval F3 + data F4 |
| H5 | The truncation persists because the running servers predate the fix, not because the chunker is wrong | MEASURED | retrieval F6 |
| H6 | `memory_write(context: "shared")` writes the cross-project tier at the default `rw` mode, bypassing the promotion-review pipeline | READ | security F3 |
| H7 | Access mode resolves the mode of the project the caller *names*, so it is not an authorization boundary | READ | security F2 |
| H8 | `memory_promotion_list` skips the access gate entirely when `projectId` is omitted, returning every project's queued content in full | READ | security F4 |
| H9 | `memory_sync` uploads the entire bank — every project — while `projectId` only names the object key; unencrypted by default | READ, loaded-not-fired | security F5 |
| H10 | A remote sync blob that parses as SQLite is trusted, so whoever can write it authors the agent's memory | READ, loaded-not-fired | security F6 |
| H11 | vec0's default `chunk_size` of 1024 against a partition key wastes ~43 MB of a 159 MB bank | MEASURED | data F2 |
| H12 | `promotion_discards` has no reaper — 965 rows and growing | MEASURED | data F5 |
| H13 | Four hosted services turn their own graceful shutdown into a false `crit` "unhandled exception stopped the host" | MEASURED | operations F1 |
| H14 | An unrecognised CLI token silently falls through to launching the server, exiting **0** | MEASURED | surface F1 |
| H15 | Any MCP-backend autostart failure is undiagnosable — the child's stdout/stderr is unconditionally discarded | MEASURED | surface F2 |
| H16 | CI builds and tests on `ubuntu-latest` only while `publish.yml` ships six RIDs, and ADR-0049 already measured a 0.070 nDCG spread across host CPUs | READ | ci-docs F3 |
| H17 | v1.11.0's GitHub release notes list 1 of the 21 commits it shipped, because ~20 went straight to `main` | MEASURED | ci-docs F10 |
| H18 | Core launders a dependency on an Infrastructure exception type through string matching on its type name | READ | architecture F1 |
| H19 | The DI helper registers every implementation under both its concrete type and its interface, dissolving the port boundary project-wide — 8 of 8 tool classes inject the concrete `ToolGate` | MEASURED | architecture F2 |
| H20 | `WorkspaceService` and its port are pure domain logic living in Infrastructure, forcing a type alias to escape their own namespace | READ | architecture F4 |
| H21 | `IMemoryStore` is a 26-method god port mixing persistence, file ingestion, embedding orchestration and settings | MEASURED | architecture F6 |
| H22 | The MCP layer holds real business logic — a consent gate, a mode decision, two pipelines and a query-guard policy engine | MEASURED | architecture F9 |
| H23 | The architecture-enforcement library is pinned but never referenced, and no architecture test exists | READ | architecture F12 |
| H24 | The ingest chunk budget is silently non-engine-aware when `embedding.provider` is unset (104/258 over-window vs 0/276), and configuring the engine later re-embeds without re-chunking | MEASURED | adversarial NF2 |
| H26 | `NodeRunner.StartHttpMcpServer` double-disposes the host, which is the actual cause of the one real `crit` in `serve.log` | READ | adversarial NF6 |

The remaining **62 findings** at MEDIUM and LOW are in the lane reports, grouped by surface in the
plan.

---

## What is healthy — verified, not assumed

Recorded explicitly so a later simplification pass does not sweep it up.

**Layering.** `AiRaccoon.Core` has no `ProjectReference` and no `using Microsoft.*` beyond
`Microsoft.Extensions`; a sweep for `System.IO`/`File.`/`Directory.`/`Process.`/`Environment.`/
`HttpClient`/`Socket` across all 106 Core files returns hits in exactly **two**. FluentValidation in
Core is idiom (declarative validation on domain records, ADR-0001), not leakage. 21 of 26 MCP tool
methods are genuinely thin, median body 9 lines.

**Concurrency and lifetime.** Zero `AddScoped`/`AddTransient` anywhere in `src/`, so captive
dependencies are impossible by construction. Zero `async void` outside handlers, zero
`.Result`/`.Wait()`/`GetAwaiter().GetResult()`. No overlapping-tick risk: every hosted-service loop
fully awaits its pass. `Workspace.TransitionTo` is a textbook state machine, enforced twice — once in
the domain and once as a conditional `UPDATE` so a lost race throws rather than double-consuming.

**Data access.** All **86** parameterised SQL statements were swept for silently dropped
parameters — **zero mismatches in either direction**, so the prior review's `ttl_days` class of defect
is genuinely closed, not patched. `last_insert_rowid()` is never used anywhere; every insert path
re-`SELECT`s by natural key. Bare `ON CONFLICT DO NOTHING` correctly does *not* swallow `CHECK`
violations (verified against a scratch DB). `EXPLAIN QUERY PLAN` on a populated, `ANALYZE`d bank shows
every hot lookup using an index, with vector KNN correctly partition-pruned before the `MATCH`.
`noise_entries` has a working reaper.

**Security controls.** FTS5 injection is structurally impossible (`[\p{L}\p{N}_]+` terms only). Path
containment resolves symlinks **per segment** on both sides then compares with a separator — the
correct construction, and rarer than it should be. Ingest scope fails closed. Key material is escaped
by the database via parameterised `SELECT quote($key)`, never string formatting. The loopback token
flow is well built end to end: 256 CSPRNG bits, `FixedTimeEquals`, 0600 set atomically at `open(2)`
rather than chmod-after-create. Binding is `IPAddress.Loopback` with `Configuration.Sources.Clear()`
so `ASPNETCORE_URLS` has no provider to arrive through. Across **111** `[LoggerMessage]` declarations,
**zero** carry entry text. Cloud credentials never leave the machine (`DELETE FROM settings` on every
push path, ADR-0014). HKDF uses the BCL primitive correctly — the no-hand-rolled-crypto invariant
holds.

**Tests and CI.** The three CI filters partition the suite exactly — 2142 + 143 + 585 = 2870,
measured independently by two lanes **and re-verified five ways by the adversarial pass** (empty
complement, three empty pairwise intersections, discovery sum, execution sum, independent unfiltered
total). **And it is held by a mechanism, not by discipline** — the adversarial pass claimed otherwise and was
wrong, which the orchestrator settled by opening the file. `SpeedGateCoverageTests` reflects over every
class carrying a `[Fact]`/`[Theory]` and asserts each has a `Speed` trait, with the anti-vacuity
assertion alongside it. Proved it can fail: a trait-less probe class turned it red
(`these classes carry no Speed trait, so no CI job runs them: …ProbeNoSpeedTraitTests`), and removing
the probe turned it green. The
default-interface-member dispatch trap is guarded explicitly, with a comment naming the mechanism and
its own dedicated test. Shared fakes derive their vectors from `SHA256` of the actual input, so
swapping inputs would change outcomes. Env-gated probes use `Assert.Skip`, not a bare `return`, so
they report Skipped rather than Passed. The trait-coverage gates **assert their own reflection query
still finds classes**. Ratchets carry genuine raise histories — seven clean increments, one per
commit, never silently re-pinned. Every third-party Action is SHA-pinned; `publish.yml` uses OIDC
trusted publishing with no stored key.

---

## Disconfirmed — hypotheses tested and found wrong

These are findings too, and several of them saved work.

- **"Core leaks framework concerns throughout its shape."** False. The leak is two files, sharp and localised — which makes it cheap to fix rather than a rewrite.
- **"The `entries.embedding` BLOBs are ~31 MB of write-only vestige."** The orchestrator's own hypothesis, **refuted by the data lane**, which traced the one reader it missed: `RebuildVecTableAsync`. They are rebuild insurance whose price is unquantified, not dead weight.
- **"Orphaned vectors explain the vec0 bloat."** Refuted — 4 stray rowids in `vec_entries` and 1 in `vec_structure`, against a 43 MB gap. It is chunk padding.
- **"The `NoWarn` suppression hides a live vulnerability."** Refuted. `dotnet list package --vulnerable --include-transitive` is clean, and only NU1901/NU1903 (low/moderate) are suppressed — NU1902/NU1904 still warn and `TreatWarningsAsErrors` still fails the build. A defensible trade.
- **"Hardcoded secrets are in the test fixtures."** Refuted. The Azure key is Microsoft's published Azurite emulator key; the SSH key frames are built at runtime; the hex literals are git SHAs and model digests.
- **"49 worktrees and 108 branches mean lost work."** Largely refuted — one truly local-only branch, whose content had already landed on `main` by another route.
- **"The suite is flaky."** Refuted. Two failures appeared only under three concurrent `dotnet test` invocations on one machine — a condition CI never creates — and both pass 100% in isolation.
- **"The System.CommandLine `--` prefix trap is present."** Checked exhaustively at every call site and **not found**.
- **"`memory_search`'s `contextLabel` is a cross-project read primitive."** Refuted — every context string is built from the caller's own `projectId`. Read isolation holds **by construction rather than by check**, which is precisely why the delete path fails.
- **"The chunker is the source of the production truncation."** Refuted, and this one changed the plan (H5).
- **"The 2.43:1 test-to-production ratio is bloat."** Not supported by anything sampled.

---

## Owner questions

Routed as a decision list; each needs one ruling. Marked ● where work is blocked until answered.

1. ● Is `access.mode.global = full` intended on this install, given `memory_sweep` requires it? (Decides whether B1 ships as a hotfix or with Wave 1.)
2. ● Should the shared tier be writable directly via `memory_write(context: "shared")`, or only through `memory_share`? (H6 is a one-line fix if the answer is "only through share".)
3. ● Should the B2 backfill re-chunk the whole bank, or only rows currently over the window?
4. Is `memory_sync`'s whole-bank behaviour intended, or was per-project sync the design? (Decides whether H9 is a code fix or a doc fix.)
5. Should `memory_promotion_list` with no `projectId` stay on the MCP surface, or become CLI-only? (H8.)
6. Do you want `ro` to mean genuinely read-only, or is access-count bookkeeping an accepted exception?
7. Do you want `ranking` to carry a real fused score, or is rank-order the intended contract? (H1 changes a published output field.)
8. Should a chunk with no parseable heading score on content alone, or is the structure penalty deliberate? (H4.)
9. Is `tests/AiRaccoon.Tests/Resources/jsaa-memory.db` — 19 MB of another private project's documentation, 2,518 rows, the owner's email in 94 of them — intended to stay in this repo, and is this repo intended to become public?
10. Should `AddRequiredSingleton` stop registering the concrete type, accepting the compile errors that exposes? (H19.)
11. Should the ranking algorithms move from `Infrastructure/Sqlite/` into Core and become public?
12. Pick the port convention: (a) every port a Core or host type consumes lives in Core, (b) ports live beside their implementation, or (c) status quo, documented in an ADR.
13. Should ArchUnitNET be wired up now with three starter rules, or deferred? (H23.)
14. Is the ~20-commit direct-to-main window around v1.11.0 an accepted fast-iteration exception, or enforced going forward? (H17.)
15. Is a macOS/Windows CI leg worth the cost, given ADR-0049 already measured platform-dependent output? (H16.)
16. Should the 182 Python test functions become a CI gate, or is "dev-only, owner-run" the permanent scope?
17. Is `BackgroundServiceExceptionBehavior.StopHost` (the implicit .NET default) the intended fail-fast posture, or should it be `Ignore`?
18. Retention windows for `promotion_discards` and `search_quality` — or is "keep forever" intended?
19. Is the 23 MB ONNX model staying in git, or moving to a release-asset download?
20. Is `dotnet AiRaccoon.dll` a supported way to run the server, or a dev-only artefact that should fail fast? (H15.)

---

## Still open

- **Leave-one-family-out on the RRF parameters has not been run.** Until it is, every ADR-0006 nDCG figure is in-sample and cannot justify work (H3).
- **Whether H4's score halving actually reorders real results.** The arithmetic and the 64% population are proved; a concrete rank flip on the live bank was not demonstrated.
- **Whether `alpha = 0.5` in `StructureFusion` was ever swept.** It is a bare constant with an ADR reference and no sweep artefact.
- **No security finding was demonstrated by execution** — every one is source-traced. B1 in particular needs a red-first test before its fix.
- ~~`FilterFor` has four other callers that were not enumerated for untrusted context strings~~ — **closed by the orchestrator, and it narrows B1 rather than widening it.** There are exactly **three** call sites (`SqliteMemoryStore.cs:211`, `:393`, `:687`):
  - `:211` — inside `SearchAsync`, but the context comes from `SearchContexts.ResolveAsync`, which builds every string from `query.ProjectId`. **Not caller-controlled.**
  - `:393` — `DeleteContextAsync`. **Caller-controlled. This is B1, and it is the only one.**
  - `:687` — `ListContextAsync`, which *does* take a caller-supplied `context` parameter, but its only callers are `WorkspaceService.cs:35,49` and `SweepService.cs:19,23`, all of which construct the context internally. **Not reachable with an untrusted string from any tool or CLI verb.**

  So B1's fix is one call site, and the security lane's "read isolation holds" stands. **One second-order
  observation worth recording:** `SweepService.cs:23` calls `ListContextAsync(projectId,
  ContextNaming.SharedContext)`, and that branch of `FilterFor` has no project predicate — so the sweep
  run for *any* project evaluates the *whole* shared tier for deletion. Combined with security F7 (a
  `Read`-gated `memory_search` writes `rating` on shared rows, and `rating` is the sweep's deletion
  input), one project's search traffic influences what the sweep deletes from a tier every project
  reads. That is design-adjacent rather than clearly a defect — the shared tier *is* cross-project — but
  it should be a stated property rather than an emergent one.
- **S3 conditional-write support is unverified** — all conflict tests use a fake, and an endpoint that ignores `If-Match` silently degrades CAS to last-writer-wins.
- **Windows behaviour is untested** anywhere, and `UnixFileMode` is POSIX-only.
- ~~Whether the ADR `Status:` staleness extends beyond 0013/0029/0030~~ — **closed by the orchestrator.** Swept every ADR the index records as superseded or reversed: exactly one (ADR-0002) self-updates correctly; **four** still read `Accepted` — 0013, 0029, 0030 and **0033**, the last of which the lane did not find. Three different Status header formats are in play, which is why no existing check catches it. See the plan's WP17 for the derived gate.
- **Whether the 2026-08-12 silent nightly failure was ever noticed** — no issue or commit references it.
