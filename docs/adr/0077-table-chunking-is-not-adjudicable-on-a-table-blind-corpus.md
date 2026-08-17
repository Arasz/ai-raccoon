# 0077. Table chunking is not adjudicable on a table-blind corpus, and does not ship

Date: 2026-08-17

Status: Accepted

Records a change **specified, evidenced and not shipped**. No production code changes.
Relates to [ADR-0048](0048-a-chunk-is-a-well-formed-markdown-fragment.md), whose scope amendment
named table-header carry-over as unbuilt; and to [ADR-0058](0058-the-second-fusion-is-order-preserving-and-its-removal-is-not-yet-measurable.md)
and [ADR-0072](0072-a-term-budget-for-long-queries-is-not-adjudicable.md), which refused to ship for
the same class of reason. Raised by [#367](https://github.com/Arasz/ai-raccoon/issues/367).

## The framing this record exists to preserve

The proposal was to change how markdown tables are chunked, on the hypothesis that a table's unit of
information is finer than the chunk it currently lands in. **The hypothesis is probably right. It is
also, right now, unmeasurable in this repository** — and those are separate facts that a later reader
will be tempted to collapse.

Nothing here says table chunking is a bad idea. It says the evidence to choose a shape does not
exist, and that manufacturing it alongside the change is the trap this project has already been
caught by twice.

## Context

Issue #367 reported that a query paraphrasing a term defined in a markdown table never appeared in
the top 20 under default hybrid search, while FTS-only ranked the target chunk #1. The issue
attributed this partly to chunking: a chunk whose embedding averages several unrelated meanings
matches nothing well.

### The issue's own diagnosis is wrong, and the correction matters

#367 describes the target as *"a dense markdown table row packing three unrelated definitions into
one chunk."* Direct inspection of the chunk (`entries.id` 18336, `ai-badger/docs/retrieval.md`,
`chunk_index` 7/64, 1012 characters) shows something different:

- a prose tail from the previous section,
- a `### From a record to a fixture` heading,
- roughly 150 words of prose about fixture bias and telemetry harvesting,
- **then** a four-row table whose header is `| Bucket | What it means |`.

The matched row — `| clipped | The stored text hit the 200-char field cap, so it is a prefix, not the
query. |` — is a **clean term-and-definition pair**. The row is not what is packed; the *chunk* is,
and roughly seven eighths of its embedded text is unrelated prose.

The sibling chunk 9/64 opens with the same `clipped` row again, carried by the 48-token overlay, then
two further rows, then more prose. **The header appears only in chunk 7.** Chunk 9's rows are
header-orphaned — exactly the failure ADR-0048 measured at 33 of 34 chunks and recorded as "unbuilt,
not broken".

This correction changes what should be built. A per-cell split would separate `clipped` from its
definition and make *this* case worse — the case the issue exists for.

## Two questions wearing one hat

The proposal enumerated six chunk shapes as if they were comparable options. They are not. Two of
them are **correctness properties**, and the rest are **tuning parameters**:

| | |
|---|---|
| **Correctness properties** — deterministic, gateable on the text alone, no retrieval corpus needed | prose and tables never share a chunk; no table body row is emitted without its header |
| **Tuning questions** — require a graded retrieval measurement to choose between | whole table as one chunk; one chunk per row; one chunk per cell; a row linearized into natural language |

The first pair are structural invariants of the same kind ADR-0048 already shipped and gated (fence
balance: 70/4,126 chunks unbalanced before, 0/4,143 after, asserted over this repo's own
`docs/**/*.md`). The second group cannot be chosen without evidence.

Treating both as cells in one grid is what made the proposal look measurable.

## What was measured: the corpus cannot see tables

This is the finding that settles it. Measured at commit `e4384ab0`:

| surface | table content |
|---|---|
| `benchmarks/AiRaccoon.Benchmarks/.../RealWorldCorpus.cs` — the 68-query ParityGate corpus named as the non-regression side | **zero** pipe characters in the entire file |
| held-out tier expected sources (`A8`, `A9`, `A10`) | no tables |
| the 19 gradeable expected sources in `scripts/baseline-queries.json` | **3** contain any table chunk |

A change to table chunking cannot move a number derived from documents that contain no tables.

**And the held-out gate would not move even if they did.** `HeldOutRetrievalGateTests` reads a
**committed binary fixture**. It re-chunks only through `JsaaCorpusRegenerationTool`, which skips
unless an environment variable is set, reads a second checkout on one machine at a pinned commit, and
is skipped in CI. So under a chunking change the pinned floors stay exactly where they are: **the gate
goes green having measured nothing.** A grid over six arms would have needed eight fixture
regenerations, a cost and an external dependency the proposal never accounted for.

### The response variable was also wrong

The proposal keyed its falsifiers and its decision point on "the rank of `entries.id` 18336". Under
four of the six arms **that chunk does not exist** — the change under test destroys the identifier the
measurement is defined in terms of. Two arms additionally multiply the number of units containing the
answer, which improves any rank-of-any-match metric mechanically rather than by retrieving better.

## Decision

**Nothing ships.** `MarkdownChunker` is unchanged.

Each option is recorded with why it lost, so that a reader who cannot see what was tried does not
propose it again:

- **One chunk per cell** — rejected on inspection, not measurement. It separates a term from its
  definition, destroying the only coherent unit the observed document has, and would make the
  motivating case worse.
- **One chunk per row with header carry-over** — not rejected. It is the shape the evidence points
  at, and it expresses "for this column and this row, the value is X" without breaking the pairing.
  Blocked on measurement, not on merit.
- **A row linearized into natural language** — not rejected, and the most promising of the tuning
  arms: pipe syntax is not prose, and an embedding model handles a sentence better than `| a | b |`.
  Blocked identically. Note ADR-0048:72-77 already accepted emitted text that is not a byte-exact
  substring of the source, so the precedent for generated chunk text exists.
- **Whole table as one chunk** — blocked on measurement. Also forfeits the 4× bm25 `section` weight
  when the table carries no `#` line of its own, which is a cost none of the finer arms avoids either
  unless the heading is prepended.
- **Prose and tables never share a chunk** / **no header-orphaned body row** — *not* blocked on
  measurement, because they are not tuning questions. Blocked instead on the ingest work below.

## Why the correctness properties do not ship either

They change chunk boundaries. Changed boundaries change `ContentHash.Of(path, value)`, and
`FileIngestor.InsertChunksAsync` only INSERTs — so a re-ingest **adds** rows beside the stale ones
rather than replacing them. ADR-0069 measured this exact accident at 6,240 rows.

Replacement semantics are therefore a prerequisite, and the mechanism largely exists already:
`SqliteMemoryStore.ReplaceFileAsync` does replace-by-path in a single transaction with a fingerprint
re-check, and `WatchDigestExecutor` already uses it. Routing file ingest through it is the enabling
work, and it is smaller than the reconciliation that was originally proposed for this.

That work was deliberately kept out of this release: it is destructive, the maintenance runner can
start jobs within seconds of the first bank open after upgrade, and there was no backup, dry-run or
confirmation step designed. Shipping an unattended destructive sweep to a live 25,995-entry bank, to
enable a chunking change that is itself blocked on evidence, is not a trade worth making.

## What would make this adjudicable

Stated concretely so the next attempt does not have to re-derive it:

1. A graded query set whose expected documents **contain tables**, authored independently of any
   chunking change and ideally by someone other than its author. None of the current 19 gradeable
   queries qualifies — this was checked, not assumed.
2. A retrieval gate that actually **re-chunks** rather than reading a committed fixture, or a fixture
   regeneration made cheap and reproducible enough to run per arm in CI.
3. A response variable that survives the change under test — defined over the *document* or the
   *answer span*, not over a chunk id that some arms delete.

Until at least (1) and (3) exist, a number produced here is evidence for refusing, not licence for
shipping.

## Consequences

- **Positive:** #367's chunking hypothesis is preserved with its evidence, its correction, and the
  precise reason it is unresolved — rather than being closed as speculation or shipped on a
  measurement that could not have failed.
- **Positive:** the measurement blindness is now recorded. Any future retrieval work touching tabular
  content knows the gate corpus cannot see it, which was true before this record and undocumented.
- **Negative:** roughly 9.1% of chunks in the real bank contain a markdown table header separator, and
  they keep embedding as an average of unrelated content. The defect is real and stays open.
- **Negative:** ADR-0048's "unbuilt, not broken" follow-up remains unbuilt, now for a second recorded
  reason.
- **Not done:** routing file ingest through `ReplaceFileAsync`, which unblocks the correctness
  properties.

## Appendix — what is wrong, and what does not work

Recorded per the owner's instruction, so that a later reader can see the failures without repeating
them.

**In the issue.** #367's stated cause ("a table row packing three unrelated definitions") does not
match the chunk. The row is clean; the chunk mixes prose with several rows. Anyone acting on the
issue text alone would build the wrong fix.

**In the proposed measurement.**
- The non-regression corpus contains no pipe characters at all, so it could not have detected a
  regression in the thing being changed.
- The held-out gate reads a committed binary fixture and does not re-chunk, so its floors are
  invariant under the change; a green result would have meant nothing.
- The response variable (`entries.id` 18336) is destroyed by four of the six arms it was meant to
  compare.
- The two proposed axes were not independent: one axis had a single cell in its "off" position, so no
  interaction was estimable, and arms were assigned different settings on a third axis, confounding
  the very comparison the design existed to isolate.
- "No arm is adjudicable" was offered as a permitted outcome but was, given the rules as written, the
  *only* reachable one — which creates pressure to quietly relax a rule instead.

**In the blast-radius figure.** An early measurement reported "22.3% of chunks contain table
content". That counts any line bearing two or more pipes and therefore also catches shell pipelines
inside code fences. The defensible figure is **9.1%** — chunks containing a table header separator
row. The looser number was propagated into planning as a 22% blast radius, roughly a 2.4×
overstatement presented as measurement.

**In the proposed migration.** A reconciliation keyed on `(ctx, source_file)` would have deleted
`memory_write` notes and promoted rows that merely *cite* a file. `MemorySql.cs:188-190` already
carries a comment warning against exactly this, because manual rows carry `path = <sha256>.md` and
cite the file only in `source_file`. Separately, `ctx = "shared"` strips `project_id`, so two projects
ingesting the same path into the shared tier could delete each other's rows. The existing
`ReplaceFileAsync` keys on `path` and does not have either defect.

**In a stale number this record nearly inherited.** ADR-0075's prose states `Ddl` is 39 statements
and 42 for a full ensure. `MemorySchemaDdlStatementCountTests` pins **0 DDL / 4 total** on the
digest-matched path and **40** on the stale-digest path. ADRs are immutable and record what was true
when written; the executable check tracks today. Pin counts from the test, not from the ADR that
first recorded them.
