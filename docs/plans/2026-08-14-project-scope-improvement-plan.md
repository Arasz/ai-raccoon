# Improvement plan — project-scope review, 2026-08-14

Source review: [`docs/reviews/2026-08-14-project-scope-review.md`](../reviews/2026-08-14-project-scope-review.md) ·
Base commit `1d1889d517baf840df0b839f547091bd7f46808b` · Campaign branch
`campaign/project-scope-review-0814` · PR #290

Packages are grouped **by surface**, so everything touching one file lands in one change. Every
package carries acceptance criteria **and a gate that must be watched go red before the fix**. A gate
that has only ever passed is not a gate.

> **Status of the evidence.** Packages WP1–WP3 rest on claims that went through an independent
> adversarial falsification pass. Where that pass corrected or refuted a supporting number, the
> package text says so inline. No package is justified by an in-sample metric; where the only
> available number is in-sample it is labelled and the package is scoped to not depend on it.

---

## Sequencing rules for this campaign

**Serialisation points — files several packages edit, which cannot be parallelised however
independent the packages read:**

| File | Packages |
|---|---|
| `src/AiRaccoon.Infrastructure/Sqlite/SqliteMemoryStore.cs` (1,291 lines) | WP1 (`FilterFor`), WP4 (`SearchResultMerger` call), WP6 (`BumpAccessAsync`), WP9 (`ISettingsStore`) |
| `src/AiRaccoon.Infrastructure/Sqlite/MemorySchema.cs` (1,225 lines) | WP5 (v9 vec0 ladder step), WP7 (reapers) |
| `src/AiRaccoon/Tools/MemoryTools.cs` | WP2 (descriptions), WP8 (query-guard extraction), WP10 (`LogWarning`) |
| `src/AiRaccoon.Core/Memory/IMemoryStore.cs` | WP9 (settings members, DIM removal) |

**The measurement chain runs backwards from what must be proved.** Three orderings are load-bearing:

1. **The metrics gate must become non-vacuous before any ranking change is measured.** WP4 (the
   `ranking` field) and WP12 (structure fusion) both change ranking. Today the headline gate asserts
   only that nDCG/MRR/recall lie in `[0,1]`, so it would report success for either. **WP11 (held-out
   gate) precedes WP4 and WP12.** Getting this backwards produces numbers nobody can use.
2. **The servers must be restarted before the backfill, and the backfill before any corpus
   measurement.** WP3's backfill re-chunks the bank; if a pre-fix process is still writing, it
   re-introduces oversized chunks behind the backfill. And any ranking number taken before the
   backfill is measured on a different corpus than the one taken after. Order: **restart → backfill →
   measure**.
3. **WP1 and WP2 ship as one change.** Splitting them leaves the project-confinement concept enforced
   in one of its two copies again — which is exactly how the delete-path hole survived commit
   `7698dc63`'s fix to the write path. Landing only one is not half a fix; it is the same defect with
   a different address.

**What is *not* a serialisation point:** WP11's held-out gate is built from the committed test corpus,
not the live bank, so it can be built in parallel with WP3's backfill.

---

## Wave 1 — The delete path · one PR · release as 1.13.0

### WP1 · Confine `memory_delete_context` to the caller's project — **BLOCKER B1**
**Effort:** SMALL · **Surface:** `SqliteMemoryStore.FilterFor`, `EntryBucket.For`

`FilterFor`'s `project:` branch binds `["projectId"] = context["project:".Length..]`, discarding the
caller's `projectId`; its `shared` branch returns `scope = 'shared'` with no project predicate and an
empty parameter dictionary. The `workspace:` and `label:` branches **both** bind the caller's
`projectId` — that asymmetry is the evidence this is a bug rather than a design.

**Change:** extract the confinement check that `EntryBucket.For` already has into one function, and
call it from both. `FilterFor`'s `project:` branch binds the caller's `projectId` and throws
`ContextOutsideProjectException` when the context names a different one. Refuse `shared` on the delete
path.

**Acceptance criteria**
- `memory_delete_context(projectId: A, context: "project:B")` is refused, not executed, at every access mode including `full`.
- `memory_delete_context(projectId: A, context: "shared")` is refused.
- The existing legitimate uses — deleting one's own project context, a workspace context, a label context — still work.
- Exactly one function decides project confinement, and both call sites use it.

**Gate — watch it go red first.** A new test creating two projects, writing to `victim`, setting
`access.mode.global = full`, then calling `memory_delete_context(projectId: "attacker", context:
"project:victim")` and asserting **zero rows deleted and a refusal**. On today's code this test must
report rows deleted — run it before the fix and record the count in the commit message. A second case
does the same for `context: "shared"`.

**Also add the derived guard** that would have caught it: a test enumerating every `[McpServerTool]`
method by reflection and asserting each one's guard requirement, mirroring the existing
`EveryTool_NamesTheProjectIdParameter`. Break it with a stub tool that omits the gate and watch it go
red.

### WP2 · Refuse a direct write to the shared tier — **H6**
**Effort:** QUICK · **Surface:** `EntryBucket.For` · **Ships with WP1, not after it**

`EntryBucket.cs:11-14` maps `context == "shared"` to `scope='shared'` and is not covered by the
project check added at `:19-22`/`:41-44`. At the default `rw` mode, one `memory_write` puts arbitrary
content in the tier every project reads, exempt from the sweep, bypassing both the propose/review flow
and `memory_share_extract`'s `confirm=true` gate.

**Acceptance criteria**
- `memory_write(context: "shared")` is refused with a message naming `memory_share` as the route.
- `memory_share`/`memory_share_extract` still reach the shared tier.

**Gate — watch it go red first.** A test writing with `context: "shared"` at default mode and
asserting the refusal plus `SELECT COUNT(*) FROM entries WHERE scope='shared'` unchanged. Today it
lands the row; record that.

*Blocked on owner question 2 only if the answer is "direct shared writes are intended" — in which case
this package is withdrawn and the review records the decision.*

### WP3 · Restart the deployed servers, then backfill the over-window rows — **BLOCKER B2 + H5**
**Effort:** MEDIUM · **Surface:** operational + a re-chunk pass

The chunker at this base is **clean** — run over the exact files whose bank rows overflow it produces
0 over-window chunks. The defect is that pre-fix server processes are still running and still writing.

**Change, in this order:**
1. Restart every running `ai-raccoon` process; confirm `ai-raccoon --version` and the process start times against the binary's mtime.
2. A backfill that re-chunks and re-embeds rows whose WordPiece length exceeds the window. `EntryEmbedder` re-embeds stored text unchanged and never re-chunks, so this must go through the chunker, not the embedder.
3. A startup log line recording the running assembly version against the bank's engine fingerprint, so this class of drift is visible rather than requiring `ps`.

**Acceptance criteria**
- EventId 414 count is **zero** over a day of normal ingest after the restart. (WP7 of the prior campaign said it "should be provably zero"; this is the package that makes that true.)
- Post-backfill, `SELECT COUNT(*) FROM entries` where WordPiece length > 254 is zero.
- No entry's text is lost — `SUM(length(value))` is unchanged or larger across the backfill.

**Gate — watch it go red first.** A test that ingests a document known to produce an over-window chunk
under the *old* budget, asserts zero over-window chunks under the current one, and — the part that
must go red — asserts the EventId 414 counter stays at zero across the ingest. Break it by forcing the
legacy budget and watch the counter rise.

**Scope decision needed** (owner question 3): whole-bank re-chunk, or only the 6,897 rows currently
over the window. The narrower option is cheaper and sufficient for the measured defect; the wider one
also normalises chunk boundaries changed by ADR-0048. **Recommendation: the narrow one**, because the
wider one changes the corpus under WP11's gate for a benefit nobody has measured.

---

## Wave 2 — Make the measurements honest before changing what they measure

### WP11 · A retrieval gate that can fail, on a held-out family — **H2 + H3**
**Effort:** MEDIUM · **Surface:** `tests/…/Integration/BaselineMetricsTests.cs`, `RrfParameterSweepTests.cs`, `scripts/baseline-queries.json`
**Precedes WP4 and WP12.**

`BaselineMetricsTests.cs:107-112` asserts only `ShouldBeInRange(0.0, 1.0)` on nDCG@5, MRR and
recall@5 — values those functions return by construction for any input. The file concedes it: "logged
as a data point, not asserted." Adjacent gates require `>= 1` of 19 graded queries to hit at rank ≤ 3,
which passes with 18/19 misses.

Separately, the same **11 queries** both select the RRF parameters (ADR-0006's 96-point grid) and gate
them (`RrfGateQueryIds`, `SourceAffinityGateQueryIds`). Every published nDCG figure is in-sample.

**Change:** partition the corpus by **what generated each document** (jsaa / ai-badger /
arasz-home-page), tune on some families, gate on the held-out ones. Replace the range assertions with
a pinned per-query floor measured on the held-out set.

**Acceptance criteria**
- No gate asserts only a range.
- The pinned floor is a held-out number, labelled as such, with the family partition recorded.
- Every surviving in-sample number in `docs/adr/0006-*.md` is labelled in-sample.

**Gate — watch it go red first.** Perturb the ranker deliberately (e.g. reverse the fused order) and
confirm the new gate fails. Today's gate passes under that perturbation — demonstrate that first, in
the commit message. This is the single most important red-first demonstration in the plan, because
every ranking package downstream is measured by it.

### WP4 · Stop fusing an already-fused list — **H1**
**Effort:** QUICK · **Surface:** `SearchResultMerger.cs:26`, `SqliteMemoryStore.cs:266-273`
**Follows WP11.**

`SearchResultMerger.Merge` re-runs `ReciprocalRankFusion.Fuse` on a single, already-fused list,
rebuilding every score from rank position and discarding the fused modality scores. A strong match set
and near-orthogonal junk produce identical score curves.

**Change:** have `Merge` take the already-fused list and apply `SourceAffinityRanker`, the floor and
the limit directly, without re-entering `ReciprocalRankFusion`.

**Acceptance criteria**
- `ranking` varies with match quality, not only with position.
- Result **ordering** is unchanged by this package alone (it is a score-reporting fix, not a ranking change) — or, if ordering does change, the held-out gate says whether it improved.
- `minRelativeScore` still behaves as documented.

**Gate — watch it go red first.** A test asserting that a strong candidate set and a junk candidate set
produce **different** `ranking` values at the same rank. Today they are byte-identical; record the two
identical curves before the fix.

*Owner question 7 decides whether `ranking` should carry a real score at all. If rank-order is the
intended contract, this package becomes "delete the redundant second fusion and document that `ranking`
is positional", which is cheaper and still worth doing.*

### WP12 · Stop penalising content that has no heading — **H4**
**Effort:** QUICK · **Surface:** `StructureFusion.cs:23-28,52-56`
**Follows WP11. Found independently by two lanes.**

`Fused = alpha * contentSim + (1-alpha) * (structureSim ?? 0.0)` with `alpha = 0.5`. A row with no
`structure_embedding` never appears in the structure KNN list, so `structureSim` is **absent**, not
low — and defaulting it to `0.0` caps that row at half the score a headed row can reach. 64% of the
live bank has no structure embedding, by design (`EmbedIfConfiguredAsync` only computes one when a
heading parses).

**Change:** when `structureSim` is absent, score on content alone — degrade to content-only **per row**,
which is what the class comment already claims happens per bank.

**Acceptance criteria**
- A headless row and a headed row with equal content similarity score equally.
- The held-out gate from WP11 does not regress.

**Gate — watch it go red first.** A test with two entries of equal content similarity, one with a
heading and one without, asserting equal fused scores. Today the headless one scores half; record both
numbers.

---

## Wave 3 — Storage, retention and the boundary the container dissolved

### WP5 · Pin vec0's `chunk_size` — **H11**
**Effort:** SMALL · **Surface:** `MemorySchema.Ddl`, `RebuildVecTableAsync`, a new **v9** ladder step

36 chunks allocated for 16,150 vectors (2.28×); 20 partitions of which 14 hold under 10 rows, each
still getting a full 1024-slot chunk. ~43 MB of a 159 MB bank is empty padding.

**The ladder is append-only** — add a v9 step that rebuilds both vec0 tables with an explicit
`chunk_size`, sourcing from `entries.embedding`/`structure_embedding` (the pattern `MigrateToV2Async`
already uses). Never renumber or delete an existing step.

**Acceptance criteria**
- `chunk_size` is explicit on both vec0 tables and derived from the install's real partition-size distribution, not guessed.
- Bank size after the migration is measured and recorded, before and after.
- KNN results are unchanged.

**Gate:** a test asserting the migration is idempotent on an already-migrated bank, and a
before/after size assertion on a fixture with many small partitions. Break the chunk-size value and
watch the size assertion fail.

### WP6 · Compute `rating` in SQL instead of losing updates — **data F7**
**Effort:** QUICK · **Surface:** `BumpAccessAsync`, `MemorySql.BumpAccess`

The read-then-write is two round trips with no transaction; `access_count` is immune (relative SQL
expression) but `rating`, computed client-side from a stale read, loses updates under concurrent hits
on one hash — and `rating` feeds sweep eligibility.

**Gate — watch it go red first.** The lane's interleaving reproduction, as a test: two connections,
both read, both write; assert `rating` reflects the final `access_count`. Today it does not; record the
two values.

### WP7 · Reap `promotion_discards` and `search_quality` — **H12 + data F6**
**Effort:** SMALL · **Surface:** `BankMaintenanceHostedService.RunPassAsync`, `PromotionQueueSql`

965 discard rows against 19 queued and 138 shared entries, with no delete statement anywhere.
`search_quality` gets a row per search, forever, and already has an
`idx_sq_project_time (project_id, created_at)` index built for a purge query that is never issued.
`noise_entries` already has a working reaper in this exact file — generalise it.

*Retention windows are owner question 18.*

### WP8 · Move the business logic out of the MCP layer — **H22**
**Effort:** MEDIUM · **Surface:** `ShareTools.cs:43-118`, `MemoryTools.cs:170-222`

`memory_share_extract` is 62 body lines against a median of 9 — a consent gate, a mode decision and
two orchestration pipelines that exist nowhere else, so the CLI and the background extraction loop
cannot reach them and they cannot be unit-tested. `EvaluateQueryGuardAsync` is a tiered policy engine
reading its own settings inside the tools file.

Extract `ShareExtractService` and `QueryGuardService` into Core. Both tool methods become the thin
delegations the other 21 already are.

### WP9 · Finish the `ISettingsStore` extraction and make three lying defaults abstract — **architecture F7 + F8**
**Effort:** SMALL · **Surface:** `IMemoryStore.cs`, `SqliteMemoryStore.cs:33,720-724`, `IPromotionQueueStore.cs`

The settings members remain on `IMemoryStore` alongside the new port, `SqliteMemoryStore` hand-builds a
second `SqliteSettingsStore` beside the registered one, and three default interface members return
semantically wrong answers: `GetAsync` → "not found", `DeleteInScopeAsync` → **widens a scoped delete
into an unscoped one**, `ClaimAsync` → **deletes the row instead of claiming it**. Each protects one
test fake at the cost of a silent wrong answer.

Make all three abstract; the compiler lists the work.

### WP10 · The one-line hygiene set
**Effort:** QUICK each · can share a PR

- `AllowAutoRedirect = false` on the proxy's token-carrying handler (`ProxyRegistrations.cs:19`) — its hardened sibling already does this for a documented reason (security F13).
- `Mode = SqliteOpenMode.ReadOnly` on the rekey probe, or correct the comment that claims it (security F14).
- `loggingBuilder.AddFilter("Microsoft.Extensions.Http", LogLevel.Warning)` — 10 MB of `quiet.log` is a 10-second framework heartbeat in a file deliberately never rotated (operations F3).
- Wrap the four hosted-service timer awaits in `catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)`, matching `WatchHostedService`, so an ordinary shutdown stops logging a `crit` claiming a crash (H13).
- `Log.SearchQualityRecordFailed` for the one direct `logger.LogWarning` among 111 source-generated call sites (operations F7).
- `RuleFor(x => x.Limit).InclusiveBetween(1, 200)` plus `MaximumLength` on `Query` and `Content` (security F9).
- `git rm identifier.sqlite` and a `.gitignore` rule (ci-docs F9).

---

## Wave 4 — Boundaries, gates and the things that let this happen

### WP13 · Wire up the architecture test that is already paid for — **H23**
`tests/Directory.Packages.props:18` pins `TngTech.ArchUnitNET.xUnitV3` and no project references it.
The only mechanical layering guard in the repo is a missing `ProjectReference`, which catches
assembly-level leaks and nothing else — not the string-matched Infrastructure dependency in Core, not
the domain service in Infrastructure, not the ranking domain under `Sqlite/`, not the concrete-type
injections. **Every architecture finding in this review is invisible to CI**, which is why they
accumulated at 0 warnings.

Three starter rules, each watched fail against today's code first: Core depends on no other project
assembly; no type in `AiRaccoon.Core.*` references `System.Net.*`; every `[McpServerTool]` class's
constructor parameters are interfaces. **Rules two and three fail today** — that is the demonstration.

### WP14 · Close the port boundary the DI helper dissolved — **H19**
`AddRequiredSingleton` registers each implementation under both its concrete type and its interface, so
injecting the concrete Infrastructure class is exactly as easy as injecting the port and nothing
reports the difference. 8 of 8 tool classes inject the concrete `ToolGate`. Register only via the
interface and fix the compile errors — each one is a consumer that was bypassing a port. *Owner
question 10.*

### WP15 · Derive the tool list everywhere it is pinned — **QA F1 + surface F5/F8/F9 + security F16**
Nine stale or pinned copies of the 26-tool surface across four lanes' findings. The numeric assertions
are all correct today; the names and prose are not (`ToolsNamespace_ExposesAll24SpecTools` asserts 26;
one E2E file carries three different numbers; `SECURITY.md` says 23; `docs/reference/README.md` and
`docs/explanation/architecture.md` say 22). `ToolInventoryTests.cs:124-149` already does this correctly
for the packaged README — apply that pattern to the rest and delete the pins.

### WP16 · Platform coverage in CI — **H16**
Add `macos-latest` and `windows-latest` legs to `build-fast` only, for cost control. ADR-0049 already
measured a 0.070 nDCG spread across host CPUs against a 5e-3 tolerance, and ADR-0050 documents the
fixture-pinning workaround it forced. Six RIDs ship with no PR gate on four of them. *Owner question 15.*

### WP17 · Documentation and decision-record truth
- ADR-0013, ADR-0029, ADR-0030 still say `Status: Accepted` in their own files while the index correctly records them superseded or reversed. A reader who opens them directly sees a live-sounding decision describing code that no longer exists. ADR-0002 already has the right pattern.
- `SECURITY.md`: correct the tool count, correct "`ro` mode allows only reads" (search writes `access_count`, `last_accessed_at` and `rating`, including on shared rows), and add exception messages and stack traces to the "what leaves the process" table — OTLP export ships absolute filesystem paths today.
- ADR-0043's "Known gap" describes a defect that `ServerRestart.cs:160` has since closed.
- ADR-0048 claims "a chunk is a well-formed markdown fragment"; what it delivers is fence balance. A 200-row table splits with 33 of 34 chunks carrying orphaned body rows. Narrow the claim or widen the guarantee.
- `docs/reference/agent-memory-server.md` omits `memory_promotion_list`'s `includeFullValue` — the one existing route to a full entry body.

### WP18 · Python packaging honesty — **ci-docs F6 + F7**
`pyproject.toml` declares zero dependencies while scripts import `numpy`, `scikit-learn` and `httpx`;
`uv.lock` is 8 lines and locks none of them, so `uv sync` on a clean checkout cannot run the code it
covers. Delete the three unreferenced version-pinned checklist scripts (~1,671 lines, forked per
release and abandoned after 1.10.1) or generalise one.

---

## Explicitly not doing

- **Re-tuning the RRF parameters.** ADR-0006's own amendment already found `k=120, 2:1` scores higher on the regenerated corpus and deliberately declined to re-pick. That judgment stands until WP11 gives a held-out number; re-tuning against an in-sample grid would be the circular-benchmark failure a second time.
- **Deleting `entries.embedding`/`structure_embedding`.** The orchestrator proposed it as ~31 MB of vestige; the data lane refuted it by finding the reader. They are rebuild insurance, and WP5 makes a rebuild *more* likely, not less.
- **Restructuring the folder tree** (`Core/Isolation` → `Core/Workspaces`, a `Core/Promotion`, moving the ranking domain out of `Sqlite/`). Real findings, but a wide rename during a campaign this size is a merge-conflict generator with no behavioural payoff. Owner questions 11 and 12 decide it; if approved, it is a separate campaign after this one merges.
- **The `jsaa-memory.db` fixture.** Owner question 9 — nobody but the owner can settle whether another project's documentation belongs in this repo.

---

## Risks

- **`SqliteMemoryStore.cs` is edited by four packages.** Sequence them; do not run WP1, WP4, WP6 and WP9 concurrently in separate worktrees.
- **WP3's backfill runs against live user data.** Snapshot the bank first (`VACUUM INTO`), and verify `SUM(length(value))` is non-decreasing across the pass.
- **WP4 may change result ordering** even though it is framed as a score-reporting fix. That is precisely why WP11 precedes it.
- **WP14's fix produces a large mechanical compile-error sweep.** Land it alone, not inside a wave.
- **Restarting the servers (WP3 step 1) interrupts every agent session using them.** Coordinate; it is not a background action.
