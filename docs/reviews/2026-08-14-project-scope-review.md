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

**The truncation is not explained by stale data, and that took measuring to establish.** The obvious
benign story — the bank predates the chunk-budget fix — is refuted by the timestamps: the oversized
rate *rises* after the fix landed (42.3% before → 45.9% after → 48.1% after the installed binary's
commit). The actual mechanism is that **the running server processes predate the binary**. Three
long-lived `ai-raccoon --quiet` processes started at 17:21, 18:58 and 22:12 while the binary was
updated at 22:10:24, so writes kept being produced by pre-fix code for hours. The chunker at this base
is clean: run over the exact files whose bank rows overflow, it produces **0 over-window chunks**.

That distinction decides the work. It is a **restart plus a backfill**, not a chunker change — and the
same staleness explains why this session's live MCP schema still advertised `minScore: 0.7` after the
code had renamed it to `minRelativeScore` defaulting to `0.0`. One lane nearly filed that as a defect
before tracing it.

---

## Review integrity — what the adversarial pass changed

<!-- ADVERSARIAL-TABLE -->

---

## Findings

### Blockers

| # | Finding | Grade | Lane |
|---|---|---|---|
| B1 | `memory_delete_context` deletes any project's entries and can wipe the shared tier, because `FilterFor`'s `project:` branch binds `project_id` from the caller's context string and its `shared` branch has no project predicate | READ, orchestrator-reverified | security F1 |
| B2 | 42.7% of live entries exceed the 256-token embedding window; 9.67% of all indexed text is never embedded and is silently dropped rather than split | MEASURED | retrieval F5 |

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
measured independently by two lanes with zero escapees and zero pairwise overlap. The
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
- **`FilterFor` has four other callers** that were not enumerated for untrusted context strings.
- **S3 conditional-write support is unverified** — all conflict tests use a fake, and an endpoint that ignores `If-Match` silently degrades CAS to last-writer-wins.
- **Windows behaviour is untested** anywhere, and `UnixFileMode` is POSIX-only.
- ~~Whether the ADR `Status:` staleness extends beyond 0013/0029/0030~~ — **closed by the orchestrator.** Swept every ADR the index records as superseded or reversed: exactly one (ADR-0002) self-updates correctly; **four** still read `Accepted` — 0013, 0029, 0030 and **0033**, the last of which the lane did not find. Three different Status header formats are in play, which is why no existing check catches it. See the plan's WP17 for the derived gate.
- **Whether the 2026-08-12 silent nightly failure was ever noticed** — no issue or commit references it.
