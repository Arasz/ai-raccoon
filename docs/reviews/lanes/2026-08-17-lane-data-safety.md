# Lane review: data safety and migration — #357/#367/#371 plan (2026-08-17)

Scope: can this plan destroy or corrupt a user's memory bank. Base commit `a2a48b3e`. Everything
below is traced against the actual code at that commit, not the plan's prose description of it.

## Blocker findings

### BL1 — WP3's reconciliation key (`ctx`, `source_file`) omits `path`, so it will delete rows that merely *cite* the file being reconciled, not just rows that came from ingesting it

**Plan section:** §WP3 "Ingest is a three-way set reconciliation", the `old ∩ new` / `old ∖ new` /
`new ∖ old` table and the Files table ("read the stored hash set for bucket+source"). Also
underlies WP2's own statement, §WP2 `source_state`: *"`(ctx, source_file)` is exactly the key
`RecomputeChunkColumnsForContext` partitions by and the key WP3's reconciliation operates on."*

**Traced:**
- `FileIngestor.InsertChunksAsync` (`src/AiRaccoon.Infrastructure/Ingestion/FileIngestor.cs:154-166`)
  writes `path = <the file path>` and `source_file = <the same file path>` for a genuine file-ingest
  row — `path == source_file` always, for rows FileIngestor produced.
- `SqliteMemoryStore.AddContentAsync` (`SqliteMemoryStore.cs:608-679`), the `memory_write` /
  `memory_share` write path, writes an **arbitrary caller-supplied `path`** (for `memory_write`,
  `path` is caller content; for promotion via `ShareAsync`, `path = "shared/{ContentHash.OfValue(value)}.md"`,
  `SqliteMemoryStore.cs:312`) while `sourceFile` is set to whatever the caller (or the original
  source row) cites — `AddContentAsync` line 643: `sourceFile = source.SourceLocator is { Length: > 0 }
  ? source.SourceLocator : null`. For these rows **`path != source_file`.**
- `memory_write`'s `sourceFile` parameter is free-form and unvalidated
  (`src/AiRaccoon/Tools/MemoryTools.cs:61`, `MemoryWriteRequest`) — any agent can cite any path,
  including the exact path of a file that is separately, legitimately ingested.
- The existing code already had to solve this exact confusion once, for `memory_delete_source_path`.
  `MemorySql.DeleteBySourcePath` (`MemorySql.cs:191-195`) matches on `path`, **not** `source_file`,
  and says why in the comment immediately above it: *"Matching is on `path`, not `source_file`:
  mirror/ingest rows carry the real file path in both columns, while manual memory_write rows carry
  path = &lt;sha256(content)&gt;.md and merely cite the file in source_file — the digest owns the mirror
  rows, never manual rows that cite the file."* WP3's reconciliation, as specified, does not carry
  this same guard forward — its key is `(ctx, source_file)`, exactly the union RecomputeChunkColumns
  used for a low-stakes renumbering op. WP3 promotes that same key to a **DELETE**, and does not
  re-derive whether the key is still precise enough now that the stakes changed.

**Concrete destruction scenario:**
1. Project `P` ingests `/repo/docs/retrieval.md` under context `project:P` (or any label). Rows get
   `path = source_file = /repo/docs/retrieval.md`.
2. An agent calls `memory_write(projectId: "P", content: "...", context: "project:P",
   sourceFile: "/repo/docs/retrieval.md")` — citing the same file as provenance for a note, which
   `memory_write`'s own docstring explicitly supports and nothing forbids. This row gets
   `path = <sha256(content)>.md`, `source_file = /repo/docs/retrieval.md`, same `ctx` bucket
   (`project:P`) as the real ingested rows.
3. The file is edited and re-ingested (or `StaleSourceSweepJob` fires because `ChunkerVersion`
   bumped, e.g. WP7). WP3's reconciliation reads "old" = every entries row with
   `ctx = 'project:P'` and `source_file = /repo/docs/retrieval.md'` — **this includes the
   `memory_write` row from step 2.** "New" is the fresh hash list computed as
   `ContentHash.Of(/repo/docs/retrieval.md, chunk)` for each chunk of the live file. The
   `memory_write` row's hash was computed as `ContentHash.Of(<sha256(content)>.md, content)` —
   structurally different input, so it can **never** appear in "new," regardless of content.
4. The row lands in `old ∖ new` and is **DELETED** — a manually written note, containing whatever
   the agent judged worth remembering, destroyed by an unrelated file re-ingest, with no message to
   the agent that wrote it.

The identical mechanism deletes a **promoted/shared-tier row**: `ShareAsync` (`SqliteMemoryStore.cs:299-315`)
creates the shared row via the same `AddContentAsync` path, with `path = "shared/<hash>.md"` and
`source_file` copied from the original row's `source_file`. If that same `(ctx, source_file)`
pair is ever reconciled (see BL2 for how `ctx="shared"` gets reconciled), the promoted row is
in "old," can never be in "new," and is deleted — this **directly answers the prompt's question
"can reconciliation delete a promoted/shared row?" with a traced yes.**

**Why B5/B6 don't catch this:** B5 ("gone-file rows are never touched") doesn't apply — the file is
present and live. B6 ("sourceless rows are never touched") doesn't apply either — the row **has** a
`source_file`, just not a matching `path`. The stated gate list has no test for "a row that cites
this source_file but was not produced by ingesting it."

**Fix:** the reconciliation's "old" query and its DELETE predicate must add `AND path = @sourceFile`
(mirroring `DeleteBySourcePath`'s own rule), so only genuine file-ingest-owned rows participate in
the three-way comparison at all. Add a gate: a `memory_write` row and a promoted row that each cite
the reconciled `source_file`, in the same `ctx`, must survive reconciliation byte-for-byte.

---

### BL2 — `ctx="shared"` strips `project_id`; two projects (or two ingest passes) that both direct-ingest the same path into the shared tier can delete each other's rows

**Plan section:** §WP3, same key as BL1; the prompt's explicit question "can reconciliation delete a
promoted/shared row… interaction with the shared promotion tier."

**Traced:**
- `EntryBucket.For` (`src/AiRaccoon.Infrastructure/Sqlite/EntryBucket.cs`) has an explicit,
  intentional branch for `context == "shared"` — a direct file ingest into the shared tier — noted
  in its own comment as *"an open owner decision (2026-08-14 project-scope review, WP2)."* This is
  reachable from `memory_ingest_file`/`memory_ingest_directory` because their `context` parameter is
  a free string (`MemoryTools.cs:238,254`; `ContextNaming.SharedContext = "shared"` is not blocked).
- `MemorySql.ContextKeyExpression` (`MemorySql.cs:577-586`), the same `ctx` expression WP2 names as
  WP3's reconciliation key: `WHEN {prefix}scope = 'shared' THEN 'shared'` — **no `project_id`
  segment**, unlike every other branch (`project:{project_id}`, `custom:{len}:{project_id}:{label}`,
  `workspace:{len}:{project_id}:{workspace_id}`). `ctx="shared"` is identical across every project.
- `uq_entries_shared_bucket` (`MemorySchema.cs:25-27`) is `(path, hash) WHERE scope='shared'` —
  globally unique, not project-scoped, confirming shared identity was never meant to carry
  project_id in the schema either.

**Concrete destruction scenario:**
1. Project `A` runs `memory_ingest_file(projectId: "A", path: "/shared-docs/policy.md", context:
   "shared")`. Rows: `scope=shared, project_id=A, ctx="shared", source_file=path`.
2. Project `B`, on the same machine or a shared mount, has the identical absolute path in its own
   ingest scope and also runs `memory_ingest_file(projectId: "B", path: "/shared-docs/policy.md",
   context: "shared")`. Because the shared-bucket existence check
   (`EntryExistsByPathAndHashInBucket`) filters by `project_id`, project B's identical-content
   chunks look "new" to B's check even though A already inserted them; the INSERT then hits
   `uq_entries_shared_bucket`'s global `(path,hash)` uniqueness and no-ops for identical chunks, but
   any chunk where B's local copy differs even slightly (line endings, a stray edit) inserts a
   **second row under project_id=B**, same `ctx="shared"`, same `source_file`.
3. B edits its local copy and re-ingests, or `StaleSourceSweepJob` fires for `(ctx=shared,
   source_file=/shared-docs/policy.md)`. Reconciliation reads "old" = every row with `ctx='shared'
   AND source_file=…` — **this includes A's rows**, because `ctx` carries no project distinction.
   "New" is computed from B's current file content. Any of A's chunk hashes not reproduced by B's
   current content (the common case — B's edits are B's own) land in `old ∖ new` and are **deleted**,
   removing project A's promoted/shared content because project B re-ingested its own copy.

**Fix:** the reconciliation predicate must key on the same identity `EntryBucket.For` actually
enforces at write time — i.e. include `project_id` even for `scope='shared'` — or the plan must
explicitly forbid direct file-ingest into `context="shared"` from ever entering the
reconciliation/sweep path and say why. Right now nothing in WP1-WP7 says either.

---

### BL3 — No backup, no dry-run-by-default, and no operator confirmation before the first automatic sweep; the plan never mentions "backup" once

**Plan section:** whole document — `grep -i backup docs/plans/2026-08-17-issue-close-357-367.md`
returns zero hits.

**Traced:**
- `MaintenanceJobRunner.RunDueAsync` (`src/AiRaccoon.Infrastructure/Maintenance/MaintenanceJobRunner.cs:23-46`)
  runs any job whose `Interval => null` on **first sight** (`IsDue` returns true when `lastRun is
  null`) — this is exactly WP2's repair job and WP3's `StaleSourceSweepJob`, both specified with
  `Interval => null`.
- `BankMaintenanceHostedService` (`src/AiRaccoon.Infrastructure/Maintenance/BankMaintenanceHostedService.cs`)
  runs a startup pass immediately (`ExecuteAsync` → `RunOnceAsync` before anything else), **and**
  polls `HasWorkAsync` every 15 seconds via `RunOnDemandPollLoopAsync`
  (`OnDemandPollInterval = TimeSpan.FromSeconds(15)`, line ~70). So the moment an operator upgrades
  the binary and the process opens the bank, the repair job and (once WP3 ships) the stale-source
  sweep begin mutating rows **unattended, within seconds**, with no confirmation prompt and no
  `--dry-run` exposed anywhere in the tool surface for these two jobs.
- `~/.ai-raccoon/backups/` (verified on disk, read-only) already holds
  `memory-1.15.0-pre-wp3-backfill-20260815-185910.db` and `memory-1.8.0-pre-qa-20260813-072243.db` —
  **the project's own operator has already judged, twice, that an irreversible bulk-mutation pass
  over this bank needs a manual snapshot first.** The plan does not encode that judgment anywhere:
  no automated pre-sweep snapshot, no CLI flag to require one, no doctor-adjacent check that refuses
  to let the sweep proceed without one.

**Why this matters given BL1/BL2:** BL1 and BL2 are exactly the kind of defect that a check-in-CI
or held-out corpus cannot catch (they require a specific write-path history: a `memory_write` or
promotion that happens to cite a live file's path, or two projects sharing an absolute path) — the
realistic way this plan's design gets proven wrong is a production bank losing rows in the field,
discovered after the fact. Without a backup step, that loss is permanent the moment
`MaintenanceJobRunner` commits the delete.

**Fix:** before WP2's repair job or WP3's sweep is allowed to run for the first time against a given
bank (detectable via `maintenance_jobs` having no row for that job name), require or automatically
take a `VACUUM INTO` snapshot into `~/.ai-raccoon/backups/`, following the exact precedent already
on disk. At minimum, ship a release note instructing operators to snapshot manually before
upgrading, and consider gating the first run behind an explicit CLI acknowledgement rather than the
on-demand poll loop's 15-second unattended pickup.

---

## Major findings

### MJ1 — no stated guard against "new" coming back empty from a read/chunk failure, which would read as "everything is stale"

**Traced:** today's `FileIngestor.InsertChunksAsync` has an explicit early-out:
`if (chunks.Count == 0) { return 0; }` (`FileIngestor.cs:117-120`) — a chunker returning nothing
leaves existing rows alone. WP3's design description ("build the fresh hash list… apply the three
partitions") does not restate this guard, and it is not in the B1-B10 gate list. If the fresh chunk
list is empty for any reason — a transient I/O error surfaced as an empty string rather than an
exception, an unmounted volume that returns a zero-byte read instead of failing, or a genuine
chunker bug — "new" is `{}`, so **every stored row of that source is `old ∖ new` and gets deleted**,
the worst-case version of exactly the destruction pattern R4 in the plan's own risk list is trying to
prevent for the 1,304 non-re-derivable rows, except here it would fire for *any* source, re-derivable
or not.

**Fix:** explicitly retain the zero-chunk early-out (or equivalent: refuse to reconcile when "new" is
empty but the source previously had rows, treating that as an ingest failure to report, not a
signal to delete). Add a gate: `Reconcile_WhenFreshChunkListIsEmpty_LeavesExistingRowsUntouched`,
watched red by forcing an empty chunk list against a populated source.

### MJ2 — `doctor` has no defined behavior for a bank whose `user_version` is *ahead* of the binary, and its structural diff will plausibly misreport a healthy-but-newer bank as wrong-shaped

**Plan section:** §WP1, "Reports `user_version` and the digest as context lines, not findings."

**Traced:** `MemorySchema.EnsureAsync` (`MemorySchema.cs:405-415`) throws
`UnsupportedSchemaVersionException` when `storedVersion > CurrentVersion` — but only `EnsureAsync`
does that check, and WP1 explicitly forbids `doctor` from ever calling `EnsureAsync`. So on an
ahead bank, `doctor`'s `SchemaShapeReader` reads the *actual* (newer) shape via pragmas, and
`ExpectedShapeAsync` derives the *older* binary's own idea of the shape by running its own (older)
`Ddl` into a scratch DB. Any table/column/index the newer schema added that the older `Ddl` doesn't
know about is present in "actual" and absent from "expected" — under the stated design ("Missing
column reported… Differing index set reported…") this reads exactly like corruption and would
report findings, exiting `BankSchemaMismatch = 19`. The plan already recognized this exact class of
problem for encrypted/unopenable banks ("A bank that cannot be opened has an **unknown** shape, not
a wrong one") but does not extend the same treatment to an ahead bank, even though `user_version` is
read and available before any comparison runs.

**Why this belongs in the data-safety lane:** `doctor`'s entire value proposition is "a verb whose
whole product is trust." A false "your bank is broken" report on a perfectly healthy ahead-bank is
the exact kind of signal that pushes an operator toward a destructive manual intervention (deleting
and re-ingesting, restoring an old backup over a newer bank, etc.) that the plan otherwise correctly
tries to prevent everywhere else.

**Fix:** when `storedVersion > CurrentVersion`, `doctor` should report shape as **unknown** (this
binary is older than your bank) and skip the structural diff, the same way it already handles a key
resolution failure. Needs its own gate, symmetrical to A6: `Doctor_AheadUserVersion_ReportsUnknownShape_NotAMismatch`.

### MJ3 — the WP2-alone window may not actually deliver correctly-ordered `chunk_index` for survivor rows, which the plan's own R1 mitigation depends on

**Plan section:** §WP2 consequence table: *"`FileIngestor.cs:203` | replaced by stamping position
from the chunk list (**WP3 stamps all rows of the source, survivors included**)"*; §WP2 "decision
point," measured on "a bank with WP2 merged and the repair job run"; §Risks R1.

**Traced:** WP2 ships and merges *before* WP3 (WP3 is "Gated on: WP2"). The plan's own text
attributes full-source restamping (survivors included) to **WP3's** reconciliation, not WP2's. In
the WP2-only window, an ordinary re-ingest under the still-insert-only path stamps a correct
position only on **newly inserted** rows (computable at insert time without WP3); previously-existing
rows that are skipped via the exists-check (`FileIngestor.cs:149-152`) receive no `UPDATE` under an
insert-only path, so their `chunk_index`/`total_chunks` are whatever they were last stamped — stale
after `RecomputeChunkColumnsForContext` is deleted, unless the standalone repair job (WP2's own,
independent of WP3) is guaranteed to re-fire for that source. The repair job's own trigger is
`HasWorkAsync` = "any source has `position_known = 0`." The plan never states whether an ordinary
WP2-only re-ingest sets `position_known = 0` on the touched source after inserting only *some* new
rows — if it doesn't, survivor rows stay wrongly ordered indefinitely in the WP2-only window, which
directly weakens R1's stated mitigation ("WP2 ships and merges first, the decision-point experiment
runs on the repaired bank before WP4 begins") for any file edited even once between WP2 landing and
WP3 landing.

**Not itself a deletion risk** — nothing is destroyed — but it is a correctness/trust gap in the
exact experiment the rest of the plan (WP4-WP7 sequencing, R1) is built to protect, so it belongs in
this lane as a "the safety net has a hole" finding, not a "data is destroyed" one.

**Fix:** state explicitly, and gate, that any WP2-only re-ingest which inserts new rows (i.e. it
detects the source changed) marks that source's `position_known = 0`, so the repair job — which
*does* correctly fix survivors independent of WP3 — picks it up before the decision-point experiment
runs.

### MJ4 — "one transaction" for reconciliation is asserted, not yet mechanically true anywhere in the codebase

**Plan section:** WP3 Files table ("apply the three partitions in one transaction"); B8 ("Delete +
insert atomic; mid-insert failure leaves the old rows").

**Traced:** the code this replaces, `FileIngestor.InsertChunksAsync` (`FileIngestor.cs:129-204`),
issues a bare sequence of `connection.ExecuteAsync` calls with **no `BeginTransactionAsync`** —
effectively autocommit per statement today. B8 is exactly the right gate to catch this if WP3 fails
to introduce a real transaction, but the plan states the requirement as already-decided design
rather than as an explicit implementation obligation, and doesn't say whether the `source_state`
upsert is inside the same transaction as the delete/insert. If it's a separate commit, a crash
between "entries reconciled" and "`source_state` upserted" leaves that source looking stale forever
(safe — retried on rescope) or, if the ledger is written first and the entries commit second, could
leave it looking done when it isn't (a correctness gap, not directly destructive, but worth pinning
down since it directly answers the prompt's transaction-boundary question).

**Fix:** name the mechanism (`SqliteTransaction`/`connection.BeginTransactionAsync()`) explicitly in
the plan text, and extend B8 to assert the `source_state` row and the entries mutation commit or
roll back together.

---

## Verified safe (traced, not just asserted)

- **Vector/FTS consistency on delete (prompt Q5):** `vec_entries_ad`, `vec_structure_ad`, and
  `entries_fts_ad` are all `AFTER DELETE ON entries` row-level triggers
  (`MemorySchema.cs:143-145,171-174,199-201`) that fire automatically and unconditionally for
  **every** row deleted from `entries`, however the DELETE is issued (single-row or `WHERE hash IN
  (...)`). As long as WP3's delete is a real `DELETE FROM entries WHERE …` (not raw file surgery or
  a rebuild), vec0 and FTS orphaning cannot happen — no extra gate is needed for this specific
  concern. `promotion_queue_entries_ad` (`MemorySchema.cs:57-64`) likewise cleans up any dangling
  promotion-queue candidate for a deleted row.
- **The #371 repair job (prompt Q3) does not delete or misplace anything:** it is a pure
  hash-matched `UPDATE …chunk_index…WHERE hash = …` (WP2 text) with no INSERT/DELETE; a row whose
  hash isn't found in the freshly-chunked live file (because the file changed since ingest) is
  simply left with `position_known = 0` — never assigned a wrong position, never removed. Traced
  against `ChunkBackfill.cs` (a different, already-existing, explicitly out-of-scope
  delete/reinsert operation) confirms the codebase already knows how to do this destructively; the
  repair job's design deliberately avoids that shape. One narrow, non-destructive wrinkle: if the
  live file contains two chunks with byte-identical text (duplicate boilerplate at two document
  positions), a naive `hash → position` map can only retain one position for that one stored row —
  a possible wrong-but-plausible position, never data loss.
- **Gone-file rows cannot be reached by an ingest-triggered reconciliation at all:**
  `File.ReadAllTextAsync` (`FileIngestor.cs:59`) throws before any chunk list is built if the file
  is missing, and `StaleSourceSweepJob`'s own `HasWorkAsync` is specified as requiring "a live
  file." A **permanently** gone file is therefore structurally excluded, not just
  gate-protected — consistent with the plan's "R4" framing. (MJ1 above is about the narrower,
  real risk: a *transient* read failure that returns something other than a clean exception.)
- **`doctor`'s own worst-case gate is well-aimed:** A5 (`Doctor_OnAMigratableBank_ChangesNothing`,
  perturbed by swapping the read-only open for `factory.OpenBankAsync`) is explicitly called out by
  the plan as "the most important red here… the failure it catches is silent data mutation," and
  that framing is correct — `SqliteConnectionFactory.OpenBankAsync` is the only thing in this
  codebase that calls `EnsureAsync`, and WP1 never calls it. Confirmed by reading
  `AppRegistrations.cs:93-125`'s existing read-only-open pattern, which WP1 says it copies.

## Still open — needs execution to settle

1. **Whether BL1/BL2 are reachable in practice for this specific project's actual usage pattern** —
   I traced that the code paths exist and are unguarded, but I did not run a test against a real or
   scratch bank confirming the exact SQL WP3 will emit (it isn't written yet — this is a plan
   review). Whoever implements WP3 should write BL1's and BL2's scenarios as failing tests *first*,
   per this project's TDD invariant, before writing the reconciliation query.
2. **Whether `memory_write`/promotion rows citing a live source_file are common on the real bank**
   today — I did not query `/Users/arasz/.ai-raccoon/memory.db` for
   `source_file IS NOT NULL AND path != source_file AND source_file IN (SELECT DISTINCT source_file
   FROM entries WHERE path = source_file)` (rows that cite a path also owned by a real ingest). That
   number would show how much of the field bank is exposed to BL1 on day one; I did not run it
   because I was asked to stay read-only and this lane was scoped to code tracing, not a new DB
   query pass — the orchestrator's ground-truth numbers didn't include this cut.
3. **Whether any project currently uses `context: "shared"` directly on `memory_ingest_file`/
   `memory_ingest_directory`** (as opposed to only via `memory_share`/promotion) — this determines
   how live BL2's precondition is. Grepping call sites shows the parameter is unrestricted, but I
   did not find or rule out actual production usage.
4. **The exact SQL WP3 and the repair job will use** — none of it is written yet; my findings are
   against the plan's stated design and the codebase it will graft onto, not against an
   implementation. Re-run this lane's checks once WP3/WP2 have real diffs.
