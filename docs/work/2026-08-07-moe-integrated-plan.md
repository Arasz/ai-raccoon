# Full-project MoE review — integrated plan

Date: 2026-08-07
Panel: 7 experts (A layering · B persistence · C host/MCP/CLI · D tests · E cross-cutting · F docs · G scripts/build)
Per-expert reports: `docs/work/2026-08-07-moe-{a..g}-*.md`

## Verdict

The architecture is in better shape than the things around it. Core's purity is
compiler-enforced (`AiRaccoon.Core.csproj` carries no `ProjectReference`), all 23 `IMemoryStore`
signatures pass the port-altitude check, all 7 ADRs hold against current code, the
`[LoggerMessage]` invariant holds absolutely (0 violations in 342 files), and 111 of 117 catch
clauses are legitimate.

The damage is concentrated in four places: **sync** (a credential leak and two data-loss shapes),
**observability that reports failures as successes**, **one duplicated pipeline that broke the
MCP-thin invariant**, and **a test gate that does not run a third of the suite over a BDD layer
that is partly hollow**.

Nothing here argues for a rewrite. Every item below is a bounded change to a working system.

## Arbitrations

Three findings were changed on evidence rather than passed through. Recorded so they are not
re-litigated.

| Finding | As reported | Arbitrated to | Evidence |
|---|---|---|---|
| B8 — hand-rolled SHA-256 KDF | High, code fix, "invalidates every encrypted bank" | **Low, write an ADR** | `docs/work/2026-08-05-db-passphrase-ssh-and-cloud-vaults.md:22,117` measured SHA-256(label‖seed) and HKDF as equivalent sanctioned options; seed is 32 uniform bytes so stretching buys nothing; line 126 records the ADR/carve-out as the real open item. B's fix would force a `PRAGMA rekey` of every user's bank for zero security gain. |
| D1 — CI runs only `Speed=Fast` | Critical, accidental | **High, deliberate but leaky** | `build.yml:26-28` documents the split with `nightly.yml` as backstop. Substance retained: 105 Reqnroll scenarios are excluded *by omission* (no `@Speed` tag), and `nightly.yml:4` self-warns that scheduled runs are best-effort. |
| A-14 — SSH parser crypto | UNVERIFIED, out of lane | **Refuted** | Expert E audited it directly: `OpenSshPrivateKeyParser` is format decoding, and the Bitwarden subprocess is correct. Distinct file from B8's `SshKeyDerivation.cs`. |

### Settled — do not re-raise without new evidence

MCP layer holds business logic *as a general claim* (confined to `ShareTools` alone) · three
projects over-engineered for 13.5k LOC · `Path.GetFullPath` in Core as I/O ·
`Options`/`Setup`/`Observability`/`Tools` as bucket drift (sanctioned chassis) ·
`IPromotionQueueMetrics` as an abstraction with no buyer · HTTP-gated `ExtractionHostedService`
and serve-only `IdleWatchdog` as mode drift (documented + test-pinned) · `IdleWatchdog` dropping
in-flight work · embedding awaited under a write lock · `memory_promotion_list` access check ·
FTS5 injection (closed twice over; all SQL parameterised) · `WatchScheduler` concurrency untested
(`WatchSchedulerTests.cs:22-41` asserts `MaxConcurrent`, stronger than the idiom the grep sought) ·
the ReferenceAssets copy flake (fixed; fails loudly now).

## The file-contention map

This is what makes the schedule, and it is the part to get right. Four files are touched by
findings from three or more experts; each becomes a **single serial lane**. Everything else is free.

| Contended file | Claimed by | Lane |
|---|---|---|
| `Sqlite/SqliteMemoryStore.cs` | B(R3,R4,R6-R10) · A(R7,R8) · E(F-07) · D25 | WI-8 (last) |
| `Sync/SyncService.cs` | B1,B2,B10 · E(F-04) · D23 | WI-1 |
| `Tools/ShareTools.cs` | C1,C2,C3,C4 | WI-3 |
| `Sqlite/MemorySchema.cs` + `SqliteConnectionFactory.cs` | B3,B4,B5,B6 · D24 | WI-5 |

## Work items

Every item names its acceptance criteria and the gate that proves it. TDD applies to all code
items: the failing test comes first.

### WI-1 — Sync data safety · **Critical** · lane: `SyncService.cs`

| Chunk | What | Gate |
|---|---|---|
| **1a** | Strip `settings` (or allowlist non-secret keys) from the pre-push snapshot | Test: push snapshot contains no `sync.secretKey`/`connectionString`/`embedding.apiKey` row |
| 1b | Settings merge: stop the unconditional remote-clobber; add a real LWW column or drop the claim | Test: local-newer setting survives a pull |
| 1c | Delete/tombstone atomicity + watermark so a re-created fact is not silently re-deleted | Test: crash-between-delete-and-tombstone leaves no resurrection |
| 1d | Conflict-retry tests driving `SyncConflictException` through the 412 path via `FakeCloudStore` | `SyncService.cs:126-183` covered |

**1a ships alone, ahead of everything.** On an unencrypted bank the S3/Azure credentials are
uploaded in plaintext to the object store they unlock. Verified: `SyncService.cs:68` strips only
workspace entries; `SyncCloudStoreFactory.cs:20,23` and `SettingsCommands.cs:116` confirm the
secrets live in settings rows.

### WI-2 — Observability truthfulness · **High** · free lane
2a `RecordInvocation` fires before the query so `RecordError` is a no-op — failed calls counted
success (`ToolExecutionActivity.cs:44-47`, all 23 sites) · 2b drop unbounded `project_id` tags
(`PromotionQueueMetrics.cs:53,56,60,66`; copy `ToolCallMetrics`) · 2c EventId collisions at
200/601/602/603.
**Gate:** a failing tool call records an error and an Error span.

### WI-3 — MCP-thin restoration · **Critical** · lane: `ShareTools.cs`
3a extract one `SharedExtractionRunner` — the propose pipeline exists twice, byte-identical
helper included (`ShareTools.cs:129-146` / `ExtractionHostedService.cs:96-124`) · 3b push
validation + the `autoPromote && !confirm` cross-project gate down to `PromotionQueueService`
(which validates nothing today) · 3c inject `TimeProvider` (`ShareTools.cs:138`) · 3d dedupe
~175 LOC of boilerplate across 7 tool classes.
**Gate:** one code path, asserted by test; no business branch left in the tool layer.

### WI-4 — Test gate integrity · **Critical** · mostly free
4a bootstrap the ONNX model in CI (48 fresh-worktree failures trace to one gitignored file) ·
4b close the gate leak — tag the 105 scenarios or run the full suite (it is 4m39s) · 4c the 42
empty step bindings: implement or delete, no silent passes [`NativeMemorySteps.cs`] · 4d three
dishonest xUnit tests (`SweepRunnerTests.cs:25,40`, `WatchServicePortTests.cs:34`,
`McpServerSetupHostTests.cs:159`) · 4e wire both pytest suites into CI (193 tests currently
unenforced).
**Gate:** each previously-vacuous scenario fails when its behaviour is broken.

### WI-5 — Schema evolution · **High** · lane: `MemorySchema.cs`
5a introduce `user_version` + a migration path (no marker exists; legacy banks keep their
pre-CHECK/pre-FK shape forever, and `architecture.md:79-81` claims otherwise) — **needs an ADR** ·
5b connection-open cost: full DDL + 6 probes + two `COUNT(*)` on every open · 5c FTS rebuild
rethrow can fail bank-open under `SQLITE_BUSY`; empty catch on the destructive dedupe migration.
**Gate:** legacy-bank test; open cost measured before/after (currently unmeasured — do not quote a
number until it is).

### WI-6 — Silent failure paths · **High** · partially serial with WI-8
6a `SqliteMemoryStore.cs:725-728` swallows all `SqliteException` → `[]`; when the bundled model is
absent FTS is the only modality, so this returns genuinely empty (needs an `ILogger` — the primary
ctor has none) · 6b corrupt watch-scope row parses as `[]` then the CLI overwrites it — permanent
allowlist loss · 6c ingest tools have zero path containment while `memory_watch_add` enforces it,
using a primitive that already exists · 6d decide and pin the `GenerateAsync` contract (fail vs
leave pending).

### WI-7 — Dead code & layer edges · **Medium** · free, highly parallel
7a delete `CloudSyncConnection` trio + `RuntimePlatform` (zero refs, not DI-registered) · 7b move
`JetBrains.Annotations` off Core (used only by the host, which doesn't declare it) · 7c move
`ApiEnvelope` out of Core (MCP schema in the domain layer, leaks into a Core port) · 7d delete 6
`ICommands` interfaces with no second implementation — **A and C found this independently** · 7e
sweep default to Core.

### WI-8 — `SqliteMemoryStore` decomposition · **Medium/L** · lane, runs last
8a extract settings store (ten callers use only its 4 settings methods) · 8b extract file
ingestion — the real seam behind 1227 lines · 8c extract embedder orchestration · 8d transactions
(only 3 exist in all of `src/`; `BeginTransaction` is never called).
**Explicitly rejected:** a read/write CQRS split and per-table repositories — structure for its own
sake. **Blocked by WI-9**, which removes 23 hand-written forwarders from the blast radius.

### WI-9 — Extension-host ruling · **owner decision** · blocks WI-8
~230 LOC of extension machinery whose only plugin is a documented no-op; the CLI builds a third
object graph that skips its decorator entirely. Deleting it **reverses a ratified spec decision
(spec-issue-1 §6.2)** and therefore needs an ADR either way.

### WI-10 — Docs accuracy · **High** · free
10a tool count → **22** (README, architecture.md, reference/README, SECURITY.md, ADR-0002 claim
17/19/20 and disagree with each other) · 10b `docs/work/README.md` claims the directory is empty
(~90 files); `reference/agent-memory-server.md` omits two live tools · 10c remove the misfiled
`job-search-ai-assistant-ingestion-pipeline.md` — it is another project's doc polluting this repo's
retrieval · 10d `MCP_TRANSPORT` env var does not exist; it is a `--transport` flag · 10e
`docs/work/` sweep: keep 7 / promote 1 / archive 45 / delete 7 — **`docs/work/features-*/**.feature`
are build-embedded Reqnroll paths; moving them breaks the test csproj** · 10f write the three owed
ADRs (bank maintenance, schema versioning, SshKeyDerivation carve-out).

### WI-11 — Repo hygiene · **Low** · free
17 stale task worktrees + ~45 merged branches; `scripts/baseline-results.json` tracked against its
own refactor plan's decision; duplicated sha256 helper; hardcoded personal-machine paths in 3
scripts.

## Parallel schedule

```
Wave 0  WI-1a ─────────────────────────────── ship alone, first (credential leak)

Wave 1  WI-2  observability      ┐
        WI-4  test gate          │  all free lanes, no shared files
        WI-7  dead code          ├─ run concurrently
        WI-10 docs               │
        WI-11 hygiene            │
        WI-1b/c/d sync (after 1a)┘

Wave 2  WI-3  MCP-thin (ShareTools lane)   ┐ independent of each other
        WI-5  schema (MemorySchema lane)   ├─ concurrent
        WI-6  silent failures              ┘   6a serialises into Wave 3

Wave 3  WI-8  SqliteMemoryStore decomposition — single serial lane
        (gated on WI-9 ruling + WI-5 landing)
```

**Why WI-8 is last:** it is the only item whose blast radius is the whole store, and three other
items (WI-5, WI-6a, WI-9) change files it will rewrite. Doing it first means doing it twice.

## Owner decisions — settled 2026-08-07

1. **WI-9 — delete the extension host.** Reverses spec-issue-1 §6.2; ADR required. Unblocks WI-8.
2. **WI-1a — strip the whole `settings` table.** Owner asked which settings would be lost; the
   inventory is below. Selective propagation is deferred to WI-1b, because the merge clobbers
   local unconditionally today.
3. **WI-10e — sweep approved** (keep 7 / promote 1 / archive 45 / delete 7).
4. **WI-6d — leave the entry `pending`** on embedding-provider outage; do not fail the write.
5. **WI-12 — remove the hand-made crypto.** Owner reaffirmed the invariant after being shown the
   migration cost. See below.

### Settings inventory (answers the WI-1a question)

| Class | Keys | Sync? |
|---|---|---|
| **Secret** | `sync.secretKey`, `sync.accessKey`, `sync.connectionString`, `embedding.apiKey` | never |
| **Machine-specific** | remaining `sync.*` (this machine's target — propagating is circular); `encryption.source`, `encryption.bitwarden.*` (how *this* machine unlocks its bank — syncing can lock the other one out); `watch.scope\|enabled\|concurrency.*` (absolute local paths) | never |
| **Machine-agnostic** | `access.mode.global`, `sweep.threshold`, 5× `extract.*`, 2× `maintenance.*`, `embedding.provider\|model\|baseUrl` | **deferred to WI-1b** |

`embedding.model` is the one that arguably *should* propagate: a mismatch across machines yields
incompatible vectors, and `vec0` is hardcoded `float[384]` with no dimension validation (B9). That
makes it a guarded feature, not a side effect of a security fix.

### WI-12 — replace the hand-made KDF with platform HKDF · **High** · lane: `SshKeyDerivation.cs`

**This reverses the B8 arbitration above, on owner instruction.** The arbitration was correct that
plain SHA-256 was a sanctioned option and carries no practical weakness for a 32-byte uniform seed.
The owner's ruling is that the invariant ("never implement key derivation yourself — delegate to an
audited platform library") is not subject to a cost/benefit trade, and that is theirs to make.

- 12a Replace `SHA256.HashData(label ‖ seed)` with `HKDF.DeriveKey(SHA256, ikm: seed, info: label)`.
- 12b **Mandatory migration.** The derived key changes, so every existing encrypted bank becomes
  unopenable without one. Version the label (`…/v1` → `…/v2`); on open, try v2, fall back to v1, and
  on a successful v1 open `PRAGMA rekey` to v2 and record the version. Rekey-including-WAL is
  already the best-covered subsystem in the suite, so the primitive exists.
- 12c ADR recording the replacement (folded into WI-10f).

**Gate:** a bank created with the v1 derivation opens, migrates, and reopens under v2 — and a v2
bank never silently falls back. Ship behind the same review as WI-1a; both touch user data at rest.

## Execution status — 2026-08-07

### Shipped

| PR | Item | Evidence |
|---|---|---|
| #88 | **WI-1a** — sync strips the `settings` table | CI green; 2 tests added |
| #89 | **WI-2a/2c** — tool outcome recorded after the call; `PromotionQueueService` → EventIds 700-704, `WatchPipeline` → 302 | red→green proven; CI green |
| #90 | **WI-7a/7b** — 167 lines of dead code; `JetBrains.Annotations` off Core | CI green |
| #91 | **WI-10** — tool count → 22 in 5 docs; 45 archived / 7 deleted; ADR-0010/0011/0012; foreign design doc removed | CI green; Reqnroll paths verified intact |
| #92 | 10 scaffolded `SKILL.md` files carrying duplicate `description:` keys | YAML parse verified |

### Cancelled or changed on ruling

- **WI-2b cancelled.** `project_id` stays on `PromotionQueueMetrics`; owner accepted the cardinality cost so a
  concurrent OTLP exporter keeps the per-project dimension. Note the cost basis changed after the ruling:
  local EventPipe (free) → OTLP export (one billable series per project, opt-in and off by default).
- **WI-7d changed.** Both expert reviews recommended deleting the six `I*Commands` interfaces. Both were wrong:
  `ConfigCommands` is a static dispatcher taking them as optional parameters — the invariant's one sanctioned
  exception. Owner ruled to refactor `ConfigCommands` into an injectable component instead, so the exception is
  not needed. **Not started.**
- **WI-12 added.** Owner reaffirmed the no-hand-rolled-crypto invariant after being shown the migration cost:
  `SshKeyDerivation` must move to platform `HKDF`. ADR-0012 records the decision; implementation pending and
  must carry a `PRAGMA rekey` migration or existing encrypted banks stop opening.

### Shipped since the first status pass — 2026-08-07 evening

| PR | Item | Evidence |
|---|---|---|
| #93/#94/#95/#101/#104 | Watch catch-up runaway: stale-sweep unregister, fingerprint cascade, cancellable single-flighted scan, SQLite scan lease | Watch suite 339 green |
| #97 | **WI-7d** — `ConfigCommands` injectable, six single-use interfaces deleted | CI green |
| #99 | **WI-12** — platform HKDF + rekey migration for legacy-keyed banks | derivation re-verified against an independent RFC 5869 implementation; both pinned vectors match |
| #105/#107 | `serve observability` verbs, OTLP export, explicit `service.name` | live E2E against a running server |
| #109 | `_evictedScore` histogram now recorded; every `Log` class its own EventId block | 66 EventIds measured unique |
| #110 | **WI-3** — one propose pipeline; promotion-queue argument guards | coverage preserved, verified method-by-method |
| #111 | One `ToolGate` replacing seven duplicated helper copies | all 23 call sites keep envelope-before-record ordering |
| #112/#113/#122 | **WI-4 (partial)** — tests that could not fail deleted, 30 empty BDD bindings implemented, BDD gated in its own CI job | — |
| #114 | Sync strip applied to the merge and retry push paths, not just the first | RED verified by reverting the fix |

**Resolved without work:** EventIds 1/2/3 "reused across six files" — measured, and **no `EventId` 1, 2 or 3 exists
anywhere in the solution**. The claim was an artifact of the old reference doc, now regenerated from measurement.

### Shipped overnight — 2026-08-07 22:20 → 2026-08-08 02:30

| PR | Item | Evidence |
|---|---|---|
| #123/#124 | Keyword-modality degradation logged (EventId 900); FTS5 section anchors quoted | the hyphenated-anchor regression was RED before the fix |
| #126 | **WI-6c** — ingest paths contained in the project's declared scope; the scope renamed out of `watch.*` into `Core/Ingestion` | deny-by-default; legacy `watch.scope.*` keys migrated on bank open |
| #128 | **#117** — queue counter drift, transactions, utilization race | — |
| #129 | **#115** — sync settings boundary + integrity check on every pushed snapshot | — |
| #130 | **WI-9 (partial)** — the two `IMemoryExtension` hooks nothing could fire are gone | the host and its four reachable hooks remain |
| #132 | **WI-7e** + `Core/Common/` retired | — |
| #133 | `memory_share_extract` scores against all known projects | the runner reads the project list itself, so no caller can narrow it |
| #134 | **WI-7c (cheap half)** — `PromotionMeta` in Core, `ResponseMeta` gone from the port | — |
| #136 | **#120** — key material redacted and zeroed, migrate probe side-effect-free | — |
| #137 | **#119** — all five test-quality gaps, each mutation-proved | Fast 1158 green; two corrections to the issue text recorded in the PR |

**Issues closed against code, not against PR titles:** #115, #117, #119, #120.

### Still open — re-verified against code 2026-08-08 02:30

| WI | State | Issue |
|---|---|---|
| WI-4 (Python tests) | **Closed won't-do** — owner ruled 2026-08-07 that these are script tests and stay outside the CI gate | [#116](https://github.com/Arasz/ai-raccoon/issues/116) |
| WI-5 | still open: `grep user_version src/` → 0 hits, no migration marker | — (ADR-0011 owns it) |
| WI-6a | **Shipped in #123** | — |
| WI-6c | **Shipped in #126** | — |
| WI-7c | **Complete** — `McpToolContractTests.cs` now pins the wire contract, and `ApiEnvelope` moved out of Core to `src/AiRaccoon/Tools/ApiEnvelope.cs` | [#118](https://github.com/Arasz/ai-raccoon/issues/118) |
| WI-7e | **Shipped in #132** | — |
| WI-8 | **8b shipped in #161** (file ingestion extracted to `Infrastructure/Ingestion/FileIngestor.cs`; 8c already out via `Infrastructure/Embedding/EntryEmbedder.cs`, #149) — `SqliteMemoryStore` is 956 lines, down from 1274. 8a (settings store) and 8d (transactions) still open | — |
| WI-9 | **Shipped in #162, per ADR-0016** — the extension host, `IMemoryExtension`, and `RetrievalRatingExtension` are deleted, not kept. Supersedes ADR-0013 | [#118](https://github.com/Arasz/ai-raccoon/issues/118) |
| WI-11 | still open, and worse: 14 worktrees, 51 remote branches | — |

**WI-7c and WI-7e had fallen off this list entirely** — present in neither Shipped, Cancelled, nor Still-open —
and the previous Still-open list named five items that had already shipped. Both are now reconciled. The lesson
is the one this document already records about consensus: a tracking list is a claim about the world and goes
stale like any other, so it gets re-derived from code, not carried forward.

## Backlog — re-derived from code and the issue tracker, 2026-08-08

The 1.2.0 integration review's 10 blockers and most of its residuals have shipped (see the two Shipped tables).
What is left, ordered by what a user actually loses:

1. **[#118](https://github.com/Arasz/ai-raccoon/issues/118) Architecture drift — the remainder.** Items 2, 3 and 4
   shipped (#130, #132); item 1 is now complete (`ApiEnvelope` left Core's root once `McpToolContractTests.cs`
   landed, #134); WI-9 is resolved too — the owner ruled to delete the extension host outright rather than keep
   it, shipped as ADR-0016 + #162.
2. **[#135](https://github.com/Arasz/ai-raccoon/issues/135) Re-propose overwrites score unconditionally.** Opened
   out of #117 item 3: the scoring inputs are consistent now, but `ON CONFLICT DO UPDATE SET score = excluded.score`
   still means the newest propose wins outright. Needs a ruling on what a re-score means for eviction.
3. **WI-5 — schema versioning.** `grep user_version src/` still returns nothing; ADR-0011 states the problem and
   owns it. Every bank migration so far has been a probe-and-patch on open, which does not scale.
4. **WI-8 — `SqliteMemoryStore` decomposition, remainder.** 8b (file ingestion) shipped in #161, bringing the file
   to 956 lines (down from 1274). 8a (settings store extraction) and 8d (transactions) are still open; the WI-9
   ruling that used to gate this item has now landed, so nothing blocks starting them.
5. **WI-11 — branch and worktree hygiene.** 14 worktrees and 51 remote branches on this machine, several belonging
   to agent lanes that have already merged. Mechanical, but it is now actively confusing: two lanes opened
   competing PRs for #119 on 2026-08-08 (#137 and #138) because neither could see the other.
6. **[#82](https://github.com/Arasz/ai-raccoon/issues/82) SEP-2640 skill discovery** — pre-existing feature work,
   unrelated to this review; last because everything above is a defect.

### Not tracked as issues

WI-5 stays with ADR-0011. WI-8 and WI-11 are recorded above; file them when someone picks them up rather than
opening issues nobody has scoped.
