# 0045. A context is a label inside a project, not a second isolation boundary

Date: 2026-08-14

Status: Accepted

## Context

`memory_write` takes an optional `context` argument. A write that uses it lands in `scope='custom'`
with a `context_label`, instead of the project scope.

`SearchContexts.For` built the list of contexts a search reads: the shared context for
`all`/`shared`, the project context for `all`/`project`, and the custom context **only when the
caller passed the same `contextLabel` back**. So a labelled write was reachable by search only if
the caller already knew its label.

There was no way to learn it. `MemorySql.CommittedContexts` — what `memory_stats` reports as "the
bank's committed contexts" — selected `WHERE scope = 'shared' OR (scope = 'project' AND …)`,
omitting custom rows entirely.

Measured against a live server on a scratch bank: a `memory_write(context: "adr")` returned
`stored: true` with a hash, the row was present in `entries` and matched in `entries_fts`, and
`memory_get` returned its content — while `memory_search` returned **0 results at every scope,
including `all`**, and `memory_stats` reported `entries: 0, contexts: []`. The same content written
without a `context` was found at every scope.

The project is the isolation boundary. Workspaces are a deliberate sandbox and the shared tier is a
deliberate cross-project surface; `context` was neither, but behaved like one — an isolation
boundary nobody designed, documented, or could see into.

## Decision

**A context partitions entries inside a project. It never hides them from the project.**

- `memory_search` with **no** `contextLabel` reads every context in the project: the unlabelled
  project context plus each custom label, plus the shared tier when `scope=all`.
- `memory_search` with a `contextLabel` reads **that context only** — narrowing, not augmenting.
  Naming a context means the caller wants that context, not that context plus everything else.
- Project isolation is unchanged and still gated: another project's rows stay invisible.
- `memory_stats` lists custom contexts alongside shared and project, so a label can be discovered
  rather than having to be remembered.

The labels are enumerated per query (`MemorySql.CustomContextLabels`) and one search runs per
context, as before.

## Consequences

- Content written with a `context` is now findable by the default search. On an existing bank this
  surfaces entries that were previously unreachable — that is the point, but it does mean a bank
  with heavy custom-context use will return more results than it did.
- Three tests asserted the old behaviour and were inverted, not deleted:
  `Search_WithoutContextLabel_ExcludesCustomScopedRows` →
  `…_IncludesEveryContextInTheProject`,
  `Search_WithContextLabel_IncludesCustomScopedRows_AlongsideProjectRows` →
  `…_ReturnsThatContextOnly`, and
  `Search_ProjectScope_ExcludesOtherProjectsAndCustomContexts` →
  `…_ExcludesOtherProjects_ButCoversItsOwnContexts`. Each had encoded the defect as the contract.
- The tool descriptions said the old thing and now say this one. `contextLabel`'s previous wording
  ("the project scope **also** searches custom-scoped rows under this context label") is what
  revealed the intent: custom rows were always meant to belong to the project.
- Not addressed here: `minScore` defaults to 0.7 while scores are normalized so the top result is
  always 1.0, so the default reads as a quality floor and behaves as a rank cutoff.
