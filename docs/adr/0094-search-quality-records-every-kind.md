# 0094. `search_quality` records every kind; code paths never enter the row

Date: 2026-09-03

Status: Accepted

Amends ADR-0088 decision 8 (reversed). The old record is immutable; this file is the amendment.

## Context

`search_quality` was built to answer one question: did a search help (plan
`docs/plans/2026-08-11-search-quality-metric-plan.md`)? Each `memory_search` writes a row keyed
by a per-call correlation id, and later `memory_record_grade` / `memory_record_followthrough`
calls join back to it. ADR-0088 decision 8 excluded `kind=code`/`both` from recording. The
reason was privacy: the recorder's rows ride the whole-file sync snapshot
(`SyncService.StripNonSyncableAsync` strips workspaces, settings, and the code corpus, but not
`search_quality` or `metrics`), so a code-adjacent query stored there would leave the machine.
The envelope withheld `Meta.CorrelationId` for those kinds for the same reason (integration
review S6): an id with no row behind it turns grades into silent no-ops, and no promise beats a
false one.

Then PR #580 flipped the default `kind` to `both`. Rows stop on Aug 24, the merge day.
hermes-default alone ran 307 searches after that with 0 rows. One follow-through exists in the
table's whole history. Grades skew hard (140 grade-2 against 18 grade-5). The signal did not
break. It was designed away from the path almost every caller takes, the day the default moved
under it. A tuning decision made today rests on a dead signal. That is the defect this record
fixes.

A side fact, not fixed here: `session_id` is NULL on all 1,534 rows. No caller ever passed one.
Even the surviving rows cannot be tied to a session. That repair belongs to the callers, not to
this decision.

## Decision

1. **The dispatcher records for every kind** (`SearchDispatcher.DispatchAsync`). `kind=memory`
   writes exactly what it writes today: the memory hit count and up to five memory source
   files. `kind=both` writes the memory leg's count and files. `kind=code` writes the code hit
   count with an empty file list.
2. **Code paths are never stored.** This is the hard rule, and it is what makes decision 1
   safe. `code_entries` never leaves the machine (ADR-0085), and a path stored in a syncing
   table would break that promise by the back door. A code row therefore carries counts, never
   paths. Follow-through still records the file an agent actually read, so the attribution the
   empty list gives up is partly recovered where it matters (on use, not on retrieval).
3. **The shared query text is stored as-is.** A memory query can already carry identifiers, so
   a code-adjacent query is the same leak class the table already accepts, not a new one. The
   alternative (content-free rows with the query blanked) would keep counts while giving up
   every textual join the table exists to serve. Rejected.
4. **The envelope always carries the correlation id** (`MemoryTools.Search`). Every id now has
   a row behind it, so the S6 withholding rule is reversed: grades and follow-through work for
   all three kinds.
5. **Metrics telemetry is untouched.** `kind=both` still records phase timings with the query
   hash nulled (S6). That stays. This decision changes what `search_quality` keeps, not what
   `metrics` keeps.

## Consequences

- **Positive**: the quality signal works on the default path again. Default searches (both)
   leave rows, grades and follow-through have something to key on for every kind, and the stale
   dashboards start moving the day this ships.
- **Positive**: no new sync leak class. Query text was already synced for memory rows; code
   rows add volume in that class, not a new class. Paths, the genuinely code-specific secret,
   are excluded by rule, and a test pins that (`Search_KindBoth_RecordsTheMemoryLeg_NeverCodePaths`,
   `Search_KindCode_RecordsTheCodeCount_WithNoSourceFiles`).
- **Negative**: a code row's file list is empty by design, so file-level retrieval attribution
   for pure code searches is lost at write time. Follow-through recovers the files agents
   actually opened. Accepted.
- **Negative**: `result_count` now means different things by kind (memory hits, except pure
   code where it counts code hits), and the row has no `kind` column to say which. Consumers
   must not compare counts across kinds blindly. A `kind` column is the honest repair and it
   needs a schema migration, so it is deferred, not denied. Until then an empty file list on a
   nonzero count hints at a code row, but a hint is not a schema.
- **Negative**: code query text syncs, as memory query text already does. The principled
   privacy fix is stripping `search_quality` and `metrics` from the sync snapshot (telemetry
   has no merge consumer; the merge only reads `entries` and tombstones). That change is
   bigger than this one and ships separately, if at all. This record accepts the sync in
   exchange for the signal.
- **Not addressed**: `session_id` attribution (caller-side, all NULL today); the `kind`
   column; the sync-snapshot strip; any change to grading or promotion scoring, which read
   this table but are not ruled here.
