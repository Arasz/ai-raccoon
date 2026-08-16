# Bank-open cost: implementation plan

Status: DRAFT — revision 2, after three adversarial reviews
Date: 2026-08-16
Baseline: commit 7cfaefca, worktree `improve-performance-after-trace`

Owner rulings incorporated: **no migration path** for the CLI surface (design the clean end state);
**always probe-and-auto-start** a server; **single-writer first** in the ordering; **no top-level
`ingest` operation** — a family splits only when it already has both kinds of verb; **all settings
operations go through the server, reads and writes alike** — the CLI may read the bank directly
where it must, but must never write it.

> **Revision 2 changed the load-bearing decision in WP1.** Revision 1 proposed bumping
> `CurrentVersion` 10 → 11. That is unsafe and is withdrawn — see §4.2. The replacement gates only
> the `Ddl` block, on a digest of `Ddl` stored in `PRAGMA application_id`, with no version bump.

## 1. What the trace said

dotnet-trace against a live 1.20.0 HTTP server, 786 writes + 786 searches over 28 s, inclusive
time from a speedscope call tree.

```
Search 9.9 ms/op:  SearchAsync 77.9% | OpenBankAsync 65.4% (n=4076) | InitializeAsync 53.3%
                   QueryGuardService 43.3% (n=1980) | GetSettingAsync 29.7% | EnsureAsync 17.5%
Write  6.9 ms/op:  WriteAsync 92.7% | OpenBankAsync 89.4% (n=2568) | InitializeAsync 74.2%
                   GetSettingAsync 56.3% | EnsureAsync 26.0%
```

**5652 bank opens served 1572 operations — 3.60 opens per operation.**

> **EventPipe caveat, applies to every number here.** EventPipe sampling inflates absolute
> wall-clock ms, and inclusive time in a call tree double-counts nesting. The *ratios* and the
> *call counts* (`n=`) are the signal. Never quote the ms as clean measurements, before or after.

### One root cause, two amplifiers

**Root cause — `MemorySchema.EnsureAsync` does full schema work on every logical open**
(`src/AiRaccoon.Infrastructure/Sqlite/MemorySchema.cs:365-488`). Every open, before the
`storedVersion >= CurrentVersion` early return at `:414`:

| Step | Line | Cost on a current bank |
|---|---|---|
| `PRAGMA user_version` (`ReadVersionAsync`, `:753`) | `:367` | header read, page 1 — cheap |
| `count(*)` over `sqlite_master` | `:381-385` | **none** — `&&`-guarded behind `storedVersion == 0` |
| the entire `Ddl` block, one `ExecuteNonQueryAsync` | `:387-389` | **~30 statements, unconditional** |
| `MigrateIngestScopeKeysAsync` | `:394` | one indexed `count(*)` on `settings`, unconditional |
| `EnsurePromotionQueueTriggerScopeGuardAsync` | `:398` | one `sqlite_master` read + normalise, unconditional |

The last two are each one indexed probe that returns without writing on a healthy bank. **The `Ddl`
block is the whole prize** — the other two together are two statements, and §4.2 keeps them.

**Amplifier 1 — every settings read is a full bank open.** `SqliteSettingsStore.GetSettingAsync`
(`SqliteSettingsStore.cs:9-17`) calls `factory.OpenBankAsync` per call, paying the root cause plus
`EnableExtensions` + `LoadVector` (`SqliteConnectionFactory.cs:229-232`) and three PRAGMAs
(`:287-302`).

**Amplifier 2 — callers read one key at a time.** Verified:

- `QueryGuardService.EvaluateAsync` (`QueryGuardService.cs:30-56`), which takes `ISettingsStore`
  (`:28`). The guard is **default-ON** (`QueryGuardConfigKeys.DefaultEnabled = true`,
  `QueryGuardConfigKeys.cs:13`), so it ran on every traced search. A typical Clean search costs
  **2 opens**: `queryGuard.enabled.global` (`:34`) then `queryGuard.structural.enabled.global`
  (`:66`). A non-Clean verdict costs 2 (`enabled` + `shadow`, `:52`). Structural enabled costs 4.
- `MemoryAccessGuard.ResolveAsync` (`MemoryAccessGuard.cs:9-24`), which takes **`IMemoryStore`**
  (`:7`) — up to **2 opens per write** (`:13`, `:21`). Reads are exempt (`:31-34`), so it adds
  **0** to the search path. *The `IMemoryStore` dependency is why WP3 is not a disjoint lane — §7.*

Arithmetic reproduces the trace: ~2.29 opens per write, ~2.8 per search.

**~29% of the 5652 opens is unattributed by this model.** `MetricsFlusher.cs:179,197` re-reads two
keys every tick (30 s default) and a 30 s trace catches one or two ticks, which is nowhere near the
gap. The residue is unexplained, so no package may claim it and §8 must report opens-per-operation
as a whole rather than as a sum of attributed parts.

### What is NOT the problem — do not "fix" these

- **Pooling is not broken.** `BuildConnectionString` (`:242-260`) never sets `Pooling = false`;
  only rekey (`:270`) and the legacy probe (`:187`) do, correctly. dotnet/efcore#28774 was fixed in
  6.0.11; we pin 10.0.11. Pooling reuses the native handle. What runs on every checkout is
  `EnableExtensions`, `LoadVector` and `EnsureAsync` — C# calls after `Open()`.
- **Query-guard compute is already optimal.** Both regexes are `[GeneratedRegex]`
  (`QueryGuardPolicy.cs:38-42`); ADR-0040/0041 measured the logic at ~0.057 ms p50.
- **Cold start is not an ONNX model load.** `BackendLauncher.cs:58-59`'s comment says it is; the
  comment is wrong. `NodeRunner.cs:113` awaits `EnsureEmbeddingAvailabilityAsync`, a SHA-256 over
  the bundled file; the session is built lazily in `EmbeddingService.CreateGenerator`. Revision 1
  built an open question and a §11 benefit on that comment; both are deleted, and the comment is a
  doc fix in WP7.

---

## 2. Hard constraints

| Constraint | Source | What it forbids |
|---|---|---|
| Threading | sqlite.org/threadsafe.html | No `SqliteConnection` touched by two threads at once. |
| Rekey drainability | `RekeyBankAsync` (`SqliteConnectionFactory.cs:99-132`) calls `ClearPool`, documents "callers must not hold an open bank connection" | No long-lived connection across a rekey. |
| Metrics liveness | `MetricsFlusher.cs:179,197` re-read two keys every tick; PR #352 proved a write takes effect without restart | Any cache must invalidate or exempt these. |
| Forward-version write guard | ADR-0019; `MemorySchema.cs:372-376`; `SyncService.cs:252` refuses `remoteVersion > CurrentVersion` | **A `CurrentVersion` bump locks every installed older binary out read-write and makes every older peer reject our sync snapshots.** |
| `Ddl` reaches every bank on every open | ADR-0023 `docs/adr/0023-promotion-queue-entries-delete-invalidation.md:73-81` — *"Do not 'fix' this by bumping `CurrentVersion`"* | Any change to when `Ddl` runs amends ADR-0023 and must preserve reachability by some other mechanism. |
| Single config channel | `docs/work/archive/2026-08-04-cli-config-findings.md:4,56-58` | No new MCP **tool** may mutate configuration. Constrains the *tool surface*, not the transport. |
| `--transport stdio` | ADR-0020:87-88 — *"the escape hatch and the E2E suite depends on it"* | No exclusivity mechanism may refuse a stdio server while `serve` holds the data root. |

**Test gaps to close, not assume:** no `QueryGuardServiceTests`, no `SqliteSettingsStoreTests`
(confirmed by search). The cross-process liveness contract for `queryGuard.*` is asserted by nothing.

**One documentation contradiction to resolve.** `CliCommandTree.cs:416` tells users buffer capacity
"takes effect on the next server restart", while `MetricsFlusher.cs:179` re-reads it every tick.
One of the two is wrong. Resolve it in WP2's lane (it is a settings-liveness question, not a CLI
one) before any cache is contemplated; it is currently the only written statement that a settings
key is *not* live, and the liveness constraint above rests on the opposite.

---

## 3. Corrections to earlier premises

**ADR-0074 does not say what the brief said.** Its subject is `MeasurementBuffer` and gate G4, not
settings liveness. The requirement is real; its source is `MetricsFlusher.cs:179,197` plus PR #352.

**ADR-0038 is weaker precedent than the brief implied.** It *records* ".NET-F2: every store method
opens a bank connection per operation" as an existing fact and optimises inside it. §4.1 reduces how
many operations there are, not what an operation does, so it need not argue against ADR-0038.

**Revision 1's claimed precedent reversal was itself wrong.**
`docs/plans/2026-08-15-performance-metrics-implementation.md` §0 Finding A rules *"No v10 ladder step
is needed, and one would be provably dead code"*. Revision 1 said WP1 reverses that. The redesigned
WP1 **agrees** with Finding A — it adds no ladder step and does not touch the ladder. What it amends
is ADR-0023's mechanism (§9).

**Stale comments to fix in passing.** `MemorySchema.cs:708` asserts the factory "opens unpooled,
per-operation connections" — the unpooled half is false. `MemorySchema.cs:446-448` says the trigger
"reruns unconditionally inside `Ddl`" — false: `PromotionQueueTriggerDdl` (`:53`) is referenced only
at `:728` and `:738`, inside `EnsurePromotionQueueTriggerScopeGuardAsync`, which is the trigger's
**sole creator**. `BackendLauncher.cs:58-59` names an ONNX model load that does not happen.
All three are doc fixes; each is attached to the package that touches the file.

---

## 4. Recommendations for the hot path

### 4.1 Batch the reads

`ISettingsStore` and `IMemoryStore` both expose `GetSettingsByPrefixAsync`
(`IMemoryStore.cs:103`, `SqliteSettingsStore.cs:30-41`): one open, returns a dictionary. Every
multi-read call site reads keys sharing a prefix:

- `queryGuard.enabled.global`, `.shadow.global`, `.structural.enabled.global`,
  `.structural.threshold.global` → `queryGuard.`
- `access.mode.global`, `access.mode.project:{projectId}` → `access.mode.`

One prefix read replaces 2 opens per search and up to 2 per write, capping the structural-enabled
worst case at 1 instead of 4. Liveness is byte-identical to today — still read fresh per operation.
No contract, semantic or liveness change, so no ADR. No interface change either: both ports already
have the method.

**Threading an ambient connection is rejected on a concrete ground:** `QueryGuardService` runs
*before* the search opens its connection and lives in `AiRaccoon.Core`, where `SqliteConnection`
would violate clean layering. There is no ambient connection to thread at the largest consumer.

### 4.2 Gate the `Ddl` block on a digest of itself

**Revision 1's version bump is withdrawn.** It fails on four independent counts, three of them
fatal:

1. **ADR-0023:73-81 rejects this exact repair by this exact mechanism**, because ADR-0019's forward
   guard then refuses a read-write open to every installed v10 binary against any bank a v11 binary
   has touched. On a machine running concurrent sessions that is irreversible lockout.
2. **`SyncService.cs:252`** refuses `remoteVersion > CurrentVersion`, so a v11 snapshot is rejected
   by every v10 peer.
3. **Moving the two repairs into a ladder step orphans fresh banks.**
   `EnsurePromotionQueueTriggerScopeGuardAsync` is the trigger's sole creator; a fresh bank that
   only runs `Ddl` would never get `promotion_queue_entries_ad`, leaving orphaned `promotion_queue`
   rows forever. Revision 1 relied on the false comment at `:446-448`.
4. **It breaks two existing tests on behaviour, not constants.**
   `MemorySchemaVersionTests.cs:705-723,734-763` stamp at `CurrentVersion`, damage the trigger, and
   require repair on reopen — each asserting *"the trigger fix must not require or trigger any
   version-ladder step"*. Revision 1's "stays green" was false, and re-greening by seeding
   `user_version=10` would convert a repair-reachability test into a ladder test, deleting the
   coverage.

**The replacement.** `PRAGMA application_id` is unused in this repo (zero hits across `src/` and
`tests/`). Store a digest of `Ddl` there and gate **only the `Ddl` block** on it:

```
EnsureAsync:
  storedVersion  = PRAGMA user_version            // unchanged
  if storedVersion > CurrentVersion: throw        // unchanged (ADR-0019)
  fresh = storedVersion == 0 && no user tables    // unchanged
  storedDigest = PRAGMA application_id            // NEW — page-1 header read, same page as above
  if (storedDigest != SchemaDigest):              // NEW gate
      execute Ddl                                 // unchanged body
      PRAGMA application_id = SchemaDigest        // NEW stamp
  MigrateIngestScopeKeysAsync(...)                // UNCHANGED — still unconditional
  EnsurePromotionQueueTriggerScopeGuardAsync(...) // UNCHANGED — still unconditional
  ... fresh branch, ladder, user_version stamping: all unchanged ...
```

`SchemaDigest` is the first 32 bits of `SHA-256(Ddl)` — computed from the same string the block
executes, so the two cannot drift ("derive the list, or delete it"). No version bump, no ladder
step, no change to `CurrentVersion`, `MigrateTo*`, or any refusal.

**What this dissolves.** No lockout (ADR-0019 untouched), no sync break (`CurrentVersion` untouched),
no orphaned trigger (its creator still runs unconditionally), and both existing trigger tests stay
green **unmodified** — they damage the trigger on a current bank and the unconditional repair still
finds it. **Revision 1's WP1-T3 hash-pin tripwire disappears**: editing `Ddl` changes the digest,
which invalidates every bank mechanically. The trap is closed by a mechanism, not by discipline.

**Why the two repairs stay unconditional, and what that costs.** A digest over `Ddl` certifies that
*the bank's schema matches the DDL this binary intends*. It certifies nothing about a trigger body
edited out of band, and `MigrateIngestScopeKeysAsync` is a **data** migration whose trigger is an
older binary writing `watch.scope.*` rows after we stamped — which no DDL digest can see. Gating
either behind the digest would silently strand exactly the cases their comments (`:391-393`,
`:396-397`) exist to catch. So the fast path on a current bank is **four statements** — two page-1
header reads and two indexed probes — against roughly thirty-two today. That is the honest number;
do not quote "one statement".

**Costs to weigh, stated rather than buried.**
- **Collision.** A `Ddl` edit whose 32-bit digest equals the previous value would reach no existing
  bank. P ≈ 2.3 × 10⁻¹⁰ per edit, against today's absolute guarantee. Accepted; the alternative
  (a wider digest) has nowhere to live in the header.
- **Zero is not a digest.** An unstamped bank reads `application_id = 0`. If `SchemaDigest` ever
  computed to 0, a *fresh* bank would skip `Ddl` entirely. WP1-T4 asserts it is non-zero.
- **Alternating binaries re-run `Ddl` on each switch.** Two builds with different `Ddl` sharing one
  bank flip the digest back and forth, one `Ddl` run per switch. Today they would run it on every
  open, so this is strictly better, but it is not free.
- **`application_id` is conventionally a file-type magic number** (`file(1)`, `sqlite3_analyzer`).
  Using it as a digest means the bank never gets one. Rejected alternative: a `schema_digest` row in
  `settings` — semantically cleaner and survives `.dump`/restore, but costs a B-tree read instead of
  a header field already on the page `user_version` just touched, and can be clobbered by settings
  merges. Record both in ADR-0075.

**What it leaves on the table.** The open itself remains: `EnableExtensions`, `LoadVector`, three
PRAGMAs, pool checkout. `InitializeAsync` is 53.3%/74.2% against `EnsureAsync`'s 17.5%/26.0%, so a
large residue sits **outside** `EnsureAsync` and nothing here touches it. Do not promise the whole
`OpenBankAsync` share.

### 4.3 The "free" removal — a correction

The brief said the structural-disabled read "is free to remove". It is not *removable* — that would
make `queryGuard.structural.enabled.global` unsettable, defeating the ADR-0041 kill switch. It
becomes **free** under §4.1: batching folds it into the prefix read the `enabled` check already
pays for.

---

## 5. The architecture change: single-writer settings + CLI consolidation

Two orthogonal decisions, which revision 1 conflated and which get one ADR each:

- **§5.2 — the command-tree shape.** Where a verb lives: `settings <family> …` or top level.
- **§5.3 — the bank transport.** How a verb reaches the bank: through the server, or directly.

A verb's answer to one does not determine its answer to the other. `extract prune` is a top-level
*operation* (§5.2) that nonetheless goes *through the server* (§5.3). `encryption` is a top-level
operation that stays *on the CLI*. Keeping the two questions apart is why ADR-0076 and ADR-0077 are
separate documents.

### 5.1 Auto-start: `BackendLauncher` can be used as-is

**Probe-and-start already exists and fits.** `BackendLauncher.AcquireAsync`
(`src/AiRaccoon/Hosting/Proxy/BackendLauncher.cs:42-99`) probes `/mcp`; if nothing answers it starts
`ai-raccoon serve` and polls at `PollInterval = 250ms` until the probe answers or
`DefaultBudget = 30s` expires. It takes `(port, fileName, arguments)` and is already DI-registered
(`ProxyRegistrations.cs`). The settings path needs no new launcher.

**Concurrent launches: handled by tolerance rather than prevention.** Two CLI invocations can both
probe, both miss, both `Start()`. The loser's child fails to bind and exits; `AcquireAsync` detects
`backend.HasExited` (`:74-78`) and runs a last-chance re-probe under `LastChanceBudget = 5s`
(`:86-96`), whose comment says exactly why: *"another starter may have won the port between the
two."* No change needed.

**Lifetime: settled.** `BackendLauncher` (`:13-14`) *"never kills, signals or terminates the backend
— lifetime belongs to IdleWatchdog alone."* A server auto-started by a settings command persists and
is later reaped by `IdleWatchdog` (event ids 610-612). Servers do not accumulate, and only the first
settings command after an idle period pays a cold start.

**Latency budget.** Warm: one probe plus one loopback round trip — comparable to or better than
today's direct bank open. Cold: process start, JIT, the encryption-key resolve, the bank decrypt
probe, the embedding-file hash, and the Kestrel bind. **This is not an ONNX model load** (§1), so
revision 1's "genuine ergonomic regression" and its lazy-model-load mitigation are both withdrawn.
The remaining cold cost is real but **unmeasured**; WP7 must measure it rather than assert it, and
its acceptance criteria say so.

**Every settings operation now pays this, reads included** (§5.3), so the cold number is no longer
a corner case — it is the floor on `settings … list` on an idle machine. That is the price of one
implementation per subsystem, and it is the main thing to check against the measurement.

**Bootstrap from truly cold works because `encryption` stays on the CLI.** `encryption bitwarden`
runs in-process (`EncryptionCommands.cs:128` opens the bank, `:141`/`:254` rekey, `:164-166` write
settings), creating and keying the bank; only then can `serve` resolve a key and decrypt. Any
`settings …` command issued first on an unencrypted fresh install also works — `serve` resolves no
key, creates the bank, answers. The ordering constraint holds **only because `encryption` is
exempt**, which is why §5.3 names it the one exception to write-exclusivity.

### 5.2 The command-tree shape: the both-halves rule

**Rule (owner):** a family moves **wholesale** into `settings` when every one of its verbs only
reads or writes the settings table. A family **splits** only when it *already has* both kinds of
verb today. Nothing is invented to satisfy the pattern.

| Family | Disposition | Evidence |
|---|---|---|
| `access`, `retrieval`, `sweep`, `noise`, `queryguard` | wholesale → `settings` | `ConfigCommands.cs` — settings-table writes only |
| `maintenance` | wholesale | `CliCommandTree.cs:400` — two interval keys |
| `performance` | wholesale | `:416` — three metrics keys |
| `ingest` | **wholesale** | `:297-303` — its only child is `scope`, a settings allowlist |
| `sync` | wholesale, pending the both-halves pass | not read in this plan; see §10.2 |
| `watch` | **split** | `settings watch` = enable/scope/concurrency (`:308`, *"CONFIGURES watching — it does not register watches"*); `watch registered` (`:317-319`) reads the **watches table** — stays an operation |
| `extract` | **split** | `settings extract` = mode, kill switch, interval, capacity (`:331-345`); `extract prune --apply` (`:352-354`) **deletes promotion_queue rows** (ADR-0023) — stays an operation |
| `model` | **split** | `settings model` = show + provider keys; **`model set` stays a top-level operation** — `EntryEmbedder.ConfigureAsync` → `SelectAllEmbedded` **re-embeds the entire bank**. It is not a settings command and cannot sit behind a fire-and-forget response (§5.4) |
| `serve`, `encryption` | operations | starts a process / rekeys a file, and `encryption` is the bootstrap path |

**No top-level `ingest` operation is created.** Ingestion is MCP-only (`memory_ingest_file`,
`memory_ingest_directory`) and stays there. `ingest` is an example of the plain wholesale move, not
of the split.

**Resulting top level:** `settings`, `serve`, `encryption`, `watch` (registered only), `extract`
(prune only), `model` (set only). No new noun is needed — the split families keep their names and
shed their config children. Revision 1's `bank` verb and its open question are withdrawn.

**Per the no-migration ruling: delete, do not alias.** `watch scope` is a deprecated alias for
`ingest scope`, kept because *"breaking every existing setup script at the same time is gratuitous"*
(`CliCommandTree.cs:313-315`). That reasoning is void — delete the alias, and add no `settings`-era
aliases for the old top-level verbs.

**Why this is worth a break.** The 1.20.0 checklist found the metrics subsystem shipped three live
settings keys and no CLI family, because adding a subsystem meant adding a whole top-level family
and nobody did. Under `settings <subsystem>` a new subsystem is a node, not a family. That is a
structural fix to a defect this project hit, and it is not a performance argument at all.

### 5.3 The bank transport: write-exclusivity

**The rule (owner ruling, and it replaces revision 1's per-verb split):**

> 1. **All settings operations go through the server — reads and writes alike.**
> 2. **The split is per *thing*, not per verb:** CLI-only things stay on the CLI; everything else
>    goes through the server.
> 3. **The CLI may read the bank directly where it genuinely must. The CLI must never write it.**

The load-bearing invariant is **write-exclusivity, not read-exclusivity**. Revision 1 proposed
routing writes through the server while reads stayed direct; that gives one subsystem two
implementations, two code paths and two sets of failure modes — `settings sweep set` over HTTP and
`settings sweep list` over SQLite. Coherence of the implementation beats saving a round trip on a
read. The cost is that every settings operation now pays the acquisition (§5.1); §8 must report it.

**What stays on the CLI, and why each one is a hole to be justified.** Every family left on the CLI
must survive the question *"why can this not go through the server?"*:

| Thing | Stays on CLI? | Justification |
|---|---|---|
| `serve` | **yes** | It *is* the server. CLI-only by definition. |
| `encryption` | **yes — the named exception to write-exclusivity** | It is the bootstrap path: it creates and keys the bank *before* a server can resolve a key and decrypt (§5.1). It writes settings by design. Nothing else has this property. |
| everything else | **no** | Including `settings …`, `watch registered`, `extract prune`, `model set`. |

Two only. Any addition to this table is an addition to the exception list in ADR-0077 and must come
with its own justification and its own opt-out entry in the WP7-T1 fixture.

**Secret writes become network payloads.** `sync add` stores S3 secret keys and Azure connection
strings; `model set openai` stores an API key. Under this model they move, so those secrets travel
over loopback HTTP. Two things follow:

1. **`McpTokenGate.GuardedPaths` is a default-open allowlist.** `McpTokenGate.cs:26` is
   `[McpPath, ShutdownEndpoint.Path]`, and `:73` guards only paths matching it. Forget to add a
   route and you ship an unauthenticated secret-write endpoint on loopback. The array is also a
   hand-maintained mirror of the route table, which violates "derive the list, or delete it".
   **Acceptance criterion (WP7-T2):** every mapped endpoint is guarded unless explicitly opted out,
   proven by enumerating the application's route table and asserting each entry is either guarded or
   on a declared opt-out list — not by reading `GuardedPaths`.
2. **Sequencing, not exemption.** "It holds a secret" does not distinguish `sync` from
   `model set openai`, so carving `sync` out would open a second hole in write-exclusivity for a
   reason that immediately generalises. Recommendation: `sync` moves like everything else, and the
   endpoint guarantee is made airtight **first** — WP7-T2 green before any secret-bearing family is
   routed. §10.1 puts the choice to the owner in exactly those terms.

### 5.4 `model set` needs a progress-bearing shape, not a 202

`EntryEmbedder.ConfigureAsync` → `SelectAllEmbedded` **re-embeds the entire bank**. It goes through
the server like everything else — the embedder lives there — but it cannot be a fire-and-forget
accepted-response: a user changing engines on a large bank needs to know it is running and when it
finished. Design it with a streamed or polled progress response, and treat "what shape" as a WP7
design task rather than an assumed one. This is why §5.2 keeps it a top-level operation rather than
a `settings` verb: the command-tree shape and the transport agree here for once.

### 5.5 Exclusivity: scope the lease to bank *writers*, and reuse the one this repo has

Revision 1 proposed "a pidfile or lock beside `memory.db`" gating *bank access* per data root. Two
corrections:

- **As specified it breaks the E2E suite.** ADR-0020:87-88 makes `--transport stdio` the escape
  hatch *the suite depends on*. A lease refusing stdio while `serve` holds the root breaks the tests
  and the legitimate MCP-client-plus-CLI case. **Scope the lease to processes that write the bank**,
  which under §5.3 means servers and `encryption` — never a reader.
- **Do not invent a pidfile.** `SqliteWatchScanLease` (`src/AiRaccoon.Infrastructure/Watch/
  WatchScanLease.cs`) is already a cross-process TTL lease on a bank row: `LeaseTtl = 60s`,
  `HeartbeatInterval = 20s` (*"a third of the TTL, so two missed renewals still keep the lease"*),
  owner `machine:pid:guid` with the Guid there because *"PIDs are recycled, and without it a fresh
  process could inherit a dead one's lease"*. Reuse that shape and cite it in ADR-0077.

**A cache is still not authorised.** With a writer lease, one writer is enforced rather than
conventional — but `encryption` remains exempt by design (§5.3), so a cache would still need that
hole bounded, and after WP1 + WP2 + WP3 the remaining prize is roughly half of an open that no
longer runs a thirty-statement DDL block. §8 gives the number that would justify it. WP8 stays gated.

---

## 6. Work packages

xUnit v3 with Shouldly; traits `[Trait(TestCategories.Category, …)]` and
`[Trait(TestCategories.Speed, …)]` (`tests/AiRaccoon.Tests/TestCategories.cs`). CI runs
`Speed=Fast`, `Category=bdd`, `Speed=Slow` as separate jobs
(`.github/workflows/build.yml:109,141,185`). Single test project: `tests/AiRaccoon.Tests`.

### WP1 — gate the `Ddl` block on a digest (`MemorySchema.cs`)

**Change.** `SchemaDigest` = first 32 bits of `SHA-256(Ddl)`; read `PRAGMA application_id` after the
version read; run `Ddl` and stamp the digest only when it differs. No version bump, no ladder step,
no change to either unconditional repair. Fix the stale comments at `:446-448` and `:708`.

**Acceptance criteria:**
1. A bank whose stored digest matches executes **four** statements inside `EnsureAsync`: the two
   header reads and the two repair probes. No `Ddl`.
2. A bank whose stored digest differs runs `Ddl` and re-stamps.
3. A trigger damaged on a current bank is still repaired on the next open — unchanged behaviour.
4. Legacy `watch.scope.*` keys are still migrated on a current bank — unchanged behaviour.
5. A fresh empty bank is fully created, stamped at `CurrentVersion`, and stamped with the digest.
6. A bank stamped at `CurrentVersion + 1` still throws `UnsupportedSchemaVersionException`.
7. `CurrentVersion` is unchanged at 10.

**TDD failing tests first:**
- **WP1-T1** `EnsureAsync_OnADigestMatchedBank_SkipsTheDdlBlock` — open twice, count statements on
  the second. **Fails before**: today the block always runs.
- **WP1-T2** `EnsureAsync_WhenTheStoredDigestIsStale_RerunsTheDdlBlock` — seed a wrong
  `application_id` on a current bank, assert `Ddl` ran and the digest was re-stamped. **Fails
  before**: `application_id` is unread, so nothing reacts to it. *This is the reachability
  guarantee — the replacement for revision 1's hash pin, and a behaviour test rather than a
  tripwire.*
- **WP1-T3** `EnsureAsync_RepairsADamagedTriggerOnADigestMatchedBank` — the property revision 1
  would have broken. Green before and after; it is the regression net, so write it first.
- **WP1-T4** `SchemaDigest_IsNotZero` — an unstamped bank reads 0, so a zero digest would make fresh
  banks skip `Ddl`. Cheap, and the failure it prevents is total.
- `Integration/MemorySchemaVersionTests.cs:705-723,734-763` stay green **unmodified**. If either
  needs editing, the design has regressed to revision 1 — stop.

**Gate:** `dotnet test tests/AiRaccoon.Tests --filter "Speed=Fast|Speed=Slow" --nologo -v m`
(`MemorySchemaVersionTests` is under `Integration/`, so `Speed=Fast` alone is insufficient.)

**Files:** `src/AiRaccoon.Infrastructure/Sqlite/MemorySchema.cs`, new tests alongside
`tests/AiRaccoon.Tests/Integration/MemorySchemaVersionTests.cs`.

### WP2 — batch the query-guard settings reads

**Change.** One `GetSettingsByPrefixAsync("queryGuard.")` in `EvaluateAsync`;
`EvaluateStructuralAsync` takes the dictionary. Defaults and parse functions unchanged — a missing
key yields `null`, as today. `QueryGuardService` takes `ISettingsStore`, so no fake outside this
package's own test file is involved.

**Acceptance criteria:** (1) one `EvaluateAsync` makes exactly one `ISettingsStore` call in every
branch — disabled, Clean, Warn, Refuse, structural-enabled; (2) every verdict identical to today,
including shadow mode returning `Clean` with `Shadowed` set; (3) settings stay fresh per call.

**TDD failing tests first** — new `Unit/Memory/QueryGuard/QueryGuardServiceTests.cs`:
- **WP2-T1** `EvaluateAsync_ReadsSettingsStoreExactlyOnce`, counting fake, five branches.
  **Fails before** with 1, 2, 2, 2, 4.
- **WP2-T2** `EvaluateAsync_VerdictsUnchanged` — table written against current behaviour, green
  before and after. **Ships as a pure-test package before WP7** (§7).
- **WP2-T3** `EvaluateAsync_ObservesASettingsChangeBetweenCalls` — the cross-process liveness
  contract, currently proven by nothing, and the rail that fails if WP8 lands a cache without
  invalidation.

Also in this lane: resolve the `CliCommandTree.cs:416` / `MetricsFlusher.cs:179` contradiction (§2).

**Gate:** `dotnet test tests/AiRaccoon.Tests --filter "Speed=Fast" --nologo -v m`
**Files:** `src/AiRaccoon.Core/Memory/QueryGuard/QueryGuardService.cs` + new test file.

### WP3 — batch the access-mode settings reads

**Change.** One `GetSettingsByPrefixAsync("access.mode.")` on `IMemoryStore`; resolve via
`AccessModePolicy.Resolve(global, perProject)` — the current code passes `null` for `perProject`
(`:23`) after an early return (`:16-19`); the batched form uses the parameter as designed.

**`FakeMemoryStore` must change** (`tests/AiRaccoon.Tests/Unit/TestHelpers/FakeMemoryStore.cs:112`
throws `NotOverridden(nameof(GetSettingsByPrefixAsync))`). No interface change is needed —
`IMemoryStore.cs:103` already declares it — but ten test files reference `FakeMemoryStore`, so this
is a shared-file lane, not a disjoint one (§7). Give it a real dictionary-backed implementation
consistent with the existing `GetSettingAsync` fake rather than a per-test override.

**Acceptance criteria:** (1) a write-requiring `EnsureAsync` makes exactly one settings call;
(2) `AccessRequirement.Read` still makes zero; (3) per-project still overrides global, absent both
resolves to `Rw`; (4) prefix scoping is exact.

**TDD failing tests first** (extend `Unit/Access/AccessModeGuardTests.cs`):
- **WP3-T1** `EnsureAsync_ForWrite_ReadsSettingsStoreOnce` — **fails before** with 2.
- **WP3-T2** `ResolveAsync_DoesNotConfuseAnotherProjectsKey` — seed
  `access.mode.project:other = "ro"`, none for this project, assert `Rw`. **Fails against a naive
  prefix implementation** — guards the one new failure mode batching introduces.
- **WP3-T3** the existing `FakeMemoryStoreTests` still pass, and the new fake method is exercised.

**Gate:** `dotnet test tests/AiRaccoon.Tests --filter "Speed=Fast" --nologo -v m`
**Files:** `src/AiRaccoon/Access/MemoryAccessGuard.cs`, `Unit/Access/AccessModeGuardTests.cs`,
`Unit/TestHelpers/FakeMemoryStore.cs`.

### WP4 — `SqliteSettingsStoreTests` (pure test package)

No production change. **Acceptance criteria:** `GetSettingsByPrefixAsync` matches exact prefix only;
returns an empty dictionary rather than throwing on no match; round-trips values written through a
**second factory instance against the same bank file**. **WP4-T3** (second-connection visibility) is
load-bearing — it is the executable statement of the constraint §5.5 rests on and must exist before
WP8 is considered. **Ships before WP7** (§7).

**Gate:** `dotnet test tests/AiRaccoon.Tests --filter "Speed=Slow" --nologo -v m`
**Files:** new `tests/AiRaccoon.Tests/Integration/Storage/SqliteSettingsStoreTests.cs`.

### WP5 — measurement (§8)

Harness buildable now. The **"before" trace at 7cfaefca is being captured in parallel by the owner**;
this package consumes it rather than re-running it. Acceptance criteria and gate are in §8 — the
package is not done when a trace exists, it is done when §8's report is written with a stated
verdict, including "no improvement" if that is what the numbers say.

### WP6 — `settings <subsystem>` consolidation (clean break, no aliases)

**Change.** Restructure per §5.2: `settings`, `serve`, `encryption`, `watch registered`,
`extract prune`, `model set`. Delete the deprecated `watch scope` alias. No compatibility shims.
No new top-level noun, no created `ingest` operation. **Command-tree shape only — the transport is
WP7**, so no verb is sorted here by whether it reads or writes.

**Acceptance criteria:** (1) every wholesale family is reachable **only** as `settings <family> …`;
(2) the old top-level config verbs no longer parse; (3) `watch registered`, `extract prune` and
`model set` remain top-level and behave identically; (4) `serve` and `encryption` are unchanged;
(5) `watch scope` no longer exists.

**TDD failing tests first:** parameterised parse tests over the new paths (**fail before** — they do
not parse) plus assertions that the removed paths now error (**fail before** — they still succeed).
Test argv, not just handlers; System.CommandLine option names keep their `--` prefix at `GetValue`.

**Gate:** `dotnet test tests/AiRaccoon.Tests --filter "Speed=Fast" --nologo -v m`
**Files:** `src/AiRaccoon/Setup/Cli/CliCommandTree.cs`,
`src/AiRaccoon/Setup/Cli/Commands/ConfigCommands.cs`, `tests/AiRaccoon.Tests/Unit/Setup/*`.

### WP7 — settings go through the server; the CLI never writes the bank

**Change.** A control-plane settings endpoint on the server (**not** an MCP tool — §2's
single-channel constraint), modelled on `/shutdown`, serving both reads and writes. Every settings
command, `watch registered`, `extract prune` and `model set` acquire a backend via
`BackendLauncher.AcquireAsync` and go through it; only `serve` and `encryption` stay on the CLI. A
bank-writer lease per data root, shaped on `SqliteWatchScanLease` (§5.5). A progress-bearing shape
for `model set` (§5.4). Fix the `BackendLauncher.cs:58-59` comment.

**Acceptance criteria:**
1. **A CLI process performs zero bank *writes*** — no `INSERT`/`UPDATE`/`DELETE`/DDL against the
   bank — for every command except `encryption`. Direct *reads* are permitted.
2. `encryption` is the only entry on the write opt-out list, and the list is asserted, not implied.
3. Every settings command — read and write — goes through the server; there is one implementation
   per subsystem, not two.
4. With no server, one is started and the command succeeds; with a live server, it is reused.
5. Two concurrent settings commands from cold both succeed and exactly one server ends up running.
6. A second bank-*writing* process against the same data root is refused with a clear message,
   **while `--transport stdio` and a concurrent MCP client are unaffected** (ADR-0020:87-88).
7. `encryption` and `serve` still work from a cold state with no server.
8. `model set` reports progress rather than returning immediately (§5.4).
9. The MCP tool list contains no config-mutating tool.
10. **Every mapped endpoint is guarded, or is on a declared opt-out list** — proven by enumerating
    the route table, not by reading `McpTokenGate.GuardedPaths` (§5.3).
11. Cold- and warm-path latency for a settings **read** is **measured and recorded** — this is the
    path that regressed by design, and it must not be asserted to be fine.

**TDD failing tests first:**
- **WP7-T1** (criteria 1-2) is the sharpest, and it replaces both revision 1's ArchUnitNET rule and
  the reviewer's connection-counter. The ArchUnitNET rule — "no type in the `settings` namespace
  depends on `AiRaccoon.Infrastructure.Sqlite`" — **passes today and cannot fail**:
  `ConfigCommands.cs` imports only `AiRaccoon.Core.Memory`, and settings writes already reach the
  bank through a Core port resolved from DI. A **connection** counter is the opposite error: it is
  too strong under §5.3, and would fail on a legitimate fallback read. **The correct seam is a
  write-counting wrapper** — count statements that mutate the bank, per CLI process, with an
  explicit opt-out list holding `encryption` alone. **Red today by construction**: the settings and
  sync command handlers write settings directly right now.
- **WP7-T2** (criterion 10) enumerates routes and fails the moment an unguarded one is mapped.
  Red before, because a route can be added today with nothing noticing. **This must be green before
  any secret-bearing family is routed through the server** (§5.3, §10.1).
- **WP7-T3** (criterion 6) red before — no lease exists.
- **WP7-T4** (criterion 9) fails the moment someone implements the endpoint as a tool.
- **WP7-T5** (criterion 3) asserts a settings read and a settings write in the same family take the
  same path — the regression revision 1's per-verb rule would have shipped.

**Gate:** `dotnet test tests/AiRaccoon.Tests --filter "Speed=Fast|Speed=Slow" --nologo -v m` plus
the `Category=bdd` job, since criterion 6 touches the stdio path the E2E suite depends on.

### WP8 — settings cache. **Gated**: requires WP7 including the lease, WP4-T3, a decision on the
`encryption` hole, and a §8 measurement that justifies it. Not authorised by this plan.

---

## 7. Parallelism and ordering

**Ordering (owner ruling): single-writer first.** WP7 precedes the batching packages. Two
mitigations for what the reversed order loses, both of which are pure-test packages with no
production change and no ordering constraint of their own:

- **WP4** and **WP2-T2** land **before WP7**, giving it a regression net.
- The **§8 "before" trace is captured at 7cfaefca now**, in parallel, before WP7 removes the
  baseline's meaning.

**Lanes:**

| Package | Production file(s) | Test file(s) | Lane |
|---|---|---|---|
| WP1 | `Sqlite/MemorySchema.cs` | `Integration/MemorySchemaVersionTests.cs` + new | A (concurrent) |
| WP2 | `QueryGuard/QueryGuardService.cs` | new `Unit/Memory/QueryGuard/*` | B (concurrent) |
| WP3 | `Access/MemoryAccessGuard.cs` | `Unit/Access/*`, **`Unit/TestHelpers/FakeMemoryStore.cs`** | C |
| WP4 | *(none)* | new `Integration/Storage/*` | pre-WP7 |
| WP6 | `Setup/Cli/CliCommandTree.cs`, `Setup/Cli/Commands/ConfigCommands.cs` | `Unit/Setup/*` | D |
| WP7 | `Hosting/*`, `Setup/Cli/Commands/*` | `Unit/Setup/*`, `Integration/*` | D |

**Correction to revision 1's claim of four disjoint lanes.** WP3 touches `FakeMemoryStore.cs`, which
ten test files reference — it is not disjoint from anything that also edits that fake. WP1, WP2 and
WP7 are genuinely disjoint from each other and from WP3's file set, so **WP1, WP2, WP3 and WP7 run
as four concurrent lanes**, with WP3 owning `FakeMemoryStore.cs` exclusively for its duration.

**WP6 and WP7 need not serialise.** Revision 1 said they must. They need not: WP7 is a
composition-root change — `ConfigCommands` takes `IMemoryStore` by injection, so substituting a
server-backed store touches neither `CliCommandTree.cs` nor any handler body. They share
`Setup/Cli/Commands/`, so run them in one lane for merge hygiene, but the dependency is a
convenience, not a constraint, and either order works.

**Do not add a convenience method to `ISettingsStore` or `IMemoryStore`** — both already declare
`GetSettingsByPrefixAsync`. WP2 defines its own counting `ISettingsStore` double; WP3 extends the
shared `FakeMemoryStore`.

### If only ONE package shipped: **WP1.**

`EnsureAsync` is 17.5% (search) / 26.0% (write) inclusive and is paid on **all 5652 opens**. WP1
removes the ~thirty-statement `Ddl` block from every open after the first, including the settings
opens WP2/WP3 would remove. **WP1 partially subsumes WP2/WP3; the reverse is not true.** It is also
lowest-risk: one file, no caller-visible behaviour change, no version bump, and both existing
trigger tests stay green unmodified.

**Ceiling caveat:** `InitializeAsync` is 53.3%/74.2% against `EnsureAsync`'s 17.5%/26.0%, and ~29%
of the opens are unattributed (§1); the residue outside `EnsureAsync` is untouched by anything here.

---

## 8. Measurement protocol

**This package must be able to report "no improvement".** Every criterion below can be missed.

**Acceptance criteria (WP5):**
1. An **A-A run** is completed first — two "before" traces under identical conditions — and its
   spread is published as the noise floor. Any before/after difference smaller than that floor is
   reported as **no measured change**, not as a win.
2. Both runs are **Release** configuration. A Debug number is not admissible.
3. **Warm-up is exactly:** start the server, then drive 50 writes and 50 searches against the
   scratch data root, then wait for the first `MetricsFlusher` tick to pass, and only then start
   collection. The bank must already be stamped and digest-stamped before collection begins.
4. **Primary metric — bank opens per operation.** A count, not a time, so EventPipe cannot distort
   it. Baseline **5652 / 1572 = 3.60**. **Target: ≤ 2.5.** If it does not fall below 2.5, the report
   states that batching did not land.
5. **Secondary — `EnsureAsync` inclusive share of `InitializeAsync`.** A ratio *within one trace*,
   so comparable where absolute ms are not. **Target: at least halved.** If it does not fall, the
   report states that WP1 did not land.
6. **Regression check — settings-command latency, warm and cold.** §5.3 routes reads through the
   server that used to be direct. This number can only get worse; report it, with a stated ceiling
   above which the ruling goes back to the owner.
7. The report accounts for the **~29% unattributed opens** explicitly — either by attributing them
   or by naming them as still unattributed. It may not silently fold them into a win.
8. **Explicit non-target:** absolute ms per memory operation. No ms in the headline. (Criterion 6 is
   a wall-clock number by nature and is reported separately, outside the headline.)

**Gate:** the written report at `docs/work/`, reviewed against criteria 1-8. The package is not done
because a trace was collected.

**Harness (identical before and after):**
1. Release build; server over HTTP MCP against a **scratch data root** on `--port 0`.
2. Warm up per criterion 3.
3. `dotnet-trace collect -p <pid> --duration 30 --format speedscope`.
4. Drive **786 writes + 786 searches**, same distribution, same project id.
5. Inclusive-time analysis of the speedscope JSON: `n=` for `OpenBankAsync`, `EnsureAsync`,
   `InitializeAsync`, `GetSettingAsync`/`GetSettingsByPrefixAsync`, plus inclusive percentages.

**Fairness:** the before run uses the trace captured at 7cfaefca; the after run must use a bank
already stamped with the current digest, or it includes a one-off `Ddl` run. Same machine, same
power state, back to back.

**The number that would authorise WP8:** if, after WP1-WP3, opens attributable to
`GetSettingsByPrefixAsync` still exceed ~35% of total opens **and** `OpenBankAsync` inclusive stays
above ~40%, the cache is worth its risk. Otherwise it is not.

---

## 9. ADRs

**Three.** Revision 1 proposed two and folded the CLI break and the bank transport into one.

**ADR-0075: the `Ddl` block is gated on a digest of itself, stored in `PRAGMA application_id`.**
**Amends ADR-0023**, whose §"No `CurrentVersion` bump" (`:73-81`) rests on *"`MemorySchema.Ddl` runs
unconditionally on every bank open"*. After this it runs when the digest differs, which preserves
ADR-0023's reachability guarantee by a different mechanism. Cites **ADR-0019** (the forward-version
write guard, unchanged, and the reason a version bump was rejected) and **ADR-0011** (schema
versioning, which owns the ladder — untouched here; this ADR does *not* amend it, and it does not
reverse Finding A of `2026-08-15-performance-metrics-implementation.md`, which stands).
Alternatives to record: the `CurrentVersion` bump (rejected — §4.2's four counts); a `schema_digest`
row in `settings` (rejected — §4.2's trade); a process-level "known current" memo (rejected — loses
cross-process detection, which ADR-0020 shows is not theoretical); leaving `EnsureAsync` alone
(rejected — 17.5-26.0% on all 5652 opens). Consequences must name the 32-bit collision risk, the
zero-digest hazard, the alternating-binary re-run, and the loss of `application_id` as a magic
number.

**ADR-0076: `settings <subsystem>`, and a family splits only when it already has both kinds of
verb.** The CLI break, the deleted `watch scope` alias, the `watch`/`extract`/`model` splits, the
decision *not* to create a top-level `ingest`, and the 1.20.0 metrics-family defect as motivation.
Scope: the command tree only.

**ADR-0077: the CLI never writes the bank; all settings operations go through the server.** Records
write-exclusivity as the invariant (and why read-exclusivity is explicitly *not* claimed), the
per-*thing* split with `serve` and `encryption` as the only CLI-only entries, the bootstrap ordering
that forces the `encryption` exception, the rejected per-verb read/write split and the two-
implementations-per-subsystem failure it would cause, the auto-start ruling and `BackendLauncher`
reuse, the bank-writer lease shaped on `SqliteWatchScanLease`, the secret-write consequence with the
derived endpoint-guard criterion, the `model set` progress shape, and the measured latency cost to
settings reads. Cites ADR-0020 as parent. Splitting it from ADR-0076 matters because the CLI shape is
defensible on its own and should not be re-litigated whenever the transport is.

**No ADR for WP2/WP3/WP4** — batching two reads into one prefix read changes no contract, semantics
or liveness property.

---

## 10. Open questions for the owner

1. **Does `sync` move now or after WP7-T2 is green?** Under §5.3 it moves, which puts S3 secret keys
   and Azure connection strings into loopback HTTP bodies while `McpTokenGate` is still a
   default-open allowlist. Recommendation: **sequence, do not exempt** — land WP7-T2's derived route
   guard first, then route `sync`. Exempting it would need a property that distinguishes it from
   `model set openai`, and there is none.
2. **`sync`'s both-halves pass.** Placed wholesale in §5.2 provisionally. If it has a verb that
   moves data rather than configuring the mover, it splits like `watch` and `extract`. Not read in
   this plan.
3. **The `model set` progress shape** (§5.4) — streamed, polled, or something else. The constraint
   (no fire-and-forget over a whole-bank re-embed) is settled; the mechanism is a WP7 design task
   that would benefit from a ruling.
4. **The rekey race**, independent of all of this: `ClearPool` (`SqliteConnectionFactory.cs:114`) is
   process-local, so a live `serve` racing a CLI `encryption migrate` is unguarded today. The
   bank-writer lease does **not** close this — `encryption` is exempt from it. File separately.
5. **Move `InfrastructureOptions` to Core?** (§11.4) — a small layering improvement worth deciding
   on its own merits, not as a side effect of a split nobody has committed to.

**Closed since revision 1:**
- *Lazy embedding-model load* — withdrawn. No eager model load exists (§1).
- *The operations-verb noun* (`bank …`) — withdrawn. The split families keep their names (§5.2).
- *The hash-pin tripwire* — dissolved. The digest is the mechanism (§4.2).
- *Whether reads stay direct* — closed by the owner: they do not (§5.3).
- *How the version bump is recorded* — moot (no bump), and release recording is not the owner's call
  anyway: `.github/workflows/release.yml:22,46-60` triggers on a `VERSION` push, validates semver,
  tags and cuts a release automatically. **The CLI break must be described in the release body.**

---

## 11. Should the CLI become a separate csproj?

**Recommendation: no — not in this task.** The verdict survives review; two of the arguments for it
did not.

### 11.1 The coupling table overstated the barrier

Ten of the 22 files under `src/AiRaccoon/Setup/Cli/` reference Infrastructure across eight
namespaces, and single-writer removes few of them. But two rows were miscounted:
`Infrastructure.Embedding` and `Infrastructure.Sqlite` appear in CLI files as **key-constants
classes**, which are a one-line move to Core, not a coupling to persistence. The real barrier is
narrower than revision 1 claimed and consists of the six encryption-related references plus
`Infrastructure.Options`.

Those six are **not removable even in principle**: §5.1 establishes that `encryption` must stay a
direct, in-process operation because it is the bootstrap path. A CLI project that cannot reference
Infrastructure cannot host `encryption`, and `encryption` is a CLI command.

### 11.2 The packaging objection assumed a second executable

`AiRaccoon.csproj` is `PackAsTool` with a single `ToolCommandName ai-raccoon` and six RIDs. Revision
1 argued that two executables means the second gets no apphost shim, and that a split would break
`BackendSessions.cs:142`, which resolves the backend as `Environment.ProcessPath` and re-execs the
CLI with `serve` arguments.

That objection holds only for a second *executable*. **A CLI class library keeps one
`ToolCommandName` and leaves `ProcessPath` untouched** — the same assembly still hosts both, so the
self-exec mechanism the auto-start ruling leans on is unaffected. The packaging risk is therefore
much smaller than stated. The verdict stands on §11.1 and §11.3 instead.

### 11.3 The two claimed benefits that survive scrutiny

1. **"The single-writer invariant becomes structural."** Correct in principle; the split cannot
   deliver it, because `encryption` keeps bank *writes* in the same CLI — and under §5.3 that is
   precisely the invariant at stake. What a split would enforce is "most of the CLI cannot open the
   bank", which is both weaker than write-exclusivity and stronger than it needs to be, since
   §5.3 permits fallback reads. **WP7-T1 delivers the invariant exactly** — zero bank writes per CLI
   process, `encryption` on a declared opt-out list — with no project-graph change.
2. **"Testability — CLI tests that need no bank."** Weak. The DI seams already allow it; nothing in
   the current suite is blocked on this.

*(Revision 1's "startup cost" benefit is deleted: it rested on the ONNX claim, which is false (§1).)*

### 11.4 Keep a future split cheap

1. **Do not add new `Infrastructure.Sqlite` references from CLI code.**
2. **Consider moving `InfrastructureOptions` into `AiRaccoon.Core`** — a plain options type consumed
   by 9 files in the host and 2 in Infrastructure; living in `Infrastructure` is a mild layering
   smell. Independently defensible; §10.5.

### 11.5 Verdict

**Follow-up at best; not this task.** The decisive benefit is obtainable now for the cost of one
behavioural test, and the split does not reduce to a mechanical move after WP7 because `encryption`
must keep direct bank access. Revisit only if a second consumer of the CLI surface appears.
