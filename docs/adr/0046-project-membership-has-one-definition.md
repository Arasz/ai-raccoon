# 0046. "Rows belonging to this project" has one definition

Date: 2026-08-14

Status: Accepted

## Context

ADR-0045 established that a context is a label inside a project rather than a second isolation
boundary, and fixed `memory_search` and `memory_stats` accordingly. It did not fix the rest of the
surface, and measuring the tool surface live showed the result: for a single entry written with
`context: "adr"`, `memory_get` returned it and `memory_delete` removed it, while `memory_set_ttl`
and `memory_share` both answered `unknown-hash`.

The reason was not one query. "Which rows belong to this project" was written out by hand as
`scope = 'project'` in nine queries, a trigger and a search filter:

| Site | Purpose |
|---|---|
| `MemorySql.SelectSourceByHashAndProject` | what `ShareAsync` resolves |
| `MemorySql.SelectExtractionCandidates` | the sweep and promotion scorer |
| `MemorySql.SelectProjectIds` | which projects exist |
| `MemorySql.CountProjectEntries` | `memory_stats`' entry count |
| `MemorySql.CommittedContexts` | `memory_stats`' context list |
| `MemorySql.UpdateEntryTtl` / `SelectEntryMetadata` | `memory_set_ttl` |
| `MemorySql.CaptureQueueRowsForSourcePath` / `RestoreQueueRowsStillBacked` | queue survival across re-ingest |
| `PromotionQueueSql.OrphanCountsPerProject` / `DeleteOrphans` | `extract prune` |
| `MemorySchema.PromotionQueueTriggerDdl` | ADR-0023's orphan trigger |

Six of them carried a comment naming *another* one as the authority — "`e.scope = 'project'`
matches what ShareAsync can actually resolve (MemorySql.SelectSourceByHashAndProject)". That is a
hand-maintained mirror with no comparison between the copies, and when ADR-0045 moved the rule for
search and stats, the other nine silently disagreed
(`.ai-badger/invariants/derive-or-delete-the-list.md`).

## Decision

**The rule lives in `ProjectRows` and nowhere else.** A project's rows are its committed rows and
any context-labelled rows inside it; workspace rows are scratch and shared rows are cross-project.
The schema already partitions this way — `uq_entries_committed_bucket` is declared over
`scope IN ('project','custom')`.

`ProjectRows` exposes the predicate (`Of`, `Scope`) and the tie-break (`CommittedFirst`). Every
site above now composes from it.

**Cross-reference (ADR-0089):** the `projects` registry answers a different question —
*which projects exist* — from `ProjectRows`' *which rows belong to a project*. ADR-0089 adds no
`projects` join to any site above; its own §Membership argues this in full.

**`ProjectRowsSingleDefinitionTests` compares the copies**: it scans `src/` for a hand-rolled
`scope = 'project'` and fails on any that is not one of two declared exceptions — the context-key
`CASE` (which maps a row to its display context) and the per-context search filter (which builds
*one* context's predicate, since each custom context gets its own query). It was watched red by
restoring one literal, then green.

### Where the tie-break was doing a different job

`UpdateEntryTtl` and `SelectEntryMetadata` restricted to `scope = 'project'` for a real reason,
recorded in their comments: a hash is not a unique row, because identical content written twice
under different labels shares one, so "the entry for this hash" needs a deterministic winner. That
reasoning is right and is preserved — the queries now order by `ProjectRows.CommittedFirst()` and
take the first row. What was wrong was using an existence filter to express a preference: with no
project row at all, the tie-break became a claim that the entry did not exist.

## Consequences

- `memory_set_ttl` and `memory_share` work on context-labelled entries. A TTL set on one is now
  honoured, because `SelectExtractionCandidates` reads the same rows — that pairing is why this was
  not fixed one query at a time in ADR-0045: widening TTL alone would have accepted a setting the
  sweep could never act on, reporting success for a no-op (the failure ADR-0032 exists to prevent).
- Two tests asserted the H4/H5 rule and are inverted, not deleted:
  `DeletingTheProjectEntry_WithOnlyACustomScopeSiblingSurviving_DropsTheQueuedCandidate` →
  `…_WithACustomScopeSiblingSurviving_KeepsTheQueuedCandidate`, and
  `Prune_TreatsACustomScopeSiblingAsAnOrphan_ButNotALiveProjectScopeEntry` →
  `Prune_RemovesOnlyQueueRowsNothingInTheProjectBacks`. Both stated their premise in the doc
  comment — "ShareAsync resolves candidates with `scope = 'project'`" — and that premise is what
  changed. The prune test gained a genuinely unbacked queue row so it still has something to
  remove and cannot pass by removing everything.
- The trigger's replacement probe asked whether the stored SQL contained the substring `"scope"`.
  It does, both before and after this change, so every existing bank would have kept the old body
  while reading as up to date. The probe now compares the stored SQL to the body it intends,
  whitespace- and semicolon-insensitively — sqlite_master stores the statement without its
  `IF NOT EXISTS` and without the trailing `;`, which is what made the first attempt replace the
  trigger on every open until the comparison was corrected.
- Existing banks holding context-labelled rows will see them appear in shares, TTLs, sweeps and
  project listings for the first time. That is the intent.
