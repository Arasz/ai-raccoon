# MoE review of the #357 / #367 / #371 plan

Date: 2026-08-17
Base commit: `e4384ab0` (VERSION 1.22.0). Build green at base: 0 warnings, 0 errors.
Target: `docs/plans/2026-08-17-issue-close-357-367.md`
Lanes: `docs/reviews/lanes/2026-08-17-lane-{retrieval-measurement,data-safety,architecture-surface,gates,simplicity}.md`

Five lanes reviewed the plan before any code was written. The review changed the release
substantially, so this record exists to say what was cut, why, and on what evidence.

## Verdict

**The plan does not ship as written.** Two of its seven work packages are withdrawn on measured
evidence, one is withdrawn as a reinvention of an existing mechanism, and one new table is deleted
on three independent grounds.

What ships in 1.23.0: the `doctor` verb (#357), `chunk_index` as document order (#371), and the
fusion confidence flag default-off with its evaluation telemetry (#367, partial).

## The four decisions, and what forced each

### 1. The table-chunking measurement cannot be made. WP4 and WP7 are withdrawn.

Not a judgement — a measurement. **No offline surface in this repository can see a table-chunking
change:**

| surface | table content |
|---|---|
| `RealWorldCorpus.cs`, the 68-query ParityGate corpus | **zero** pipe characters (`grep -c '\|'` → 0, verified twice, independently) |
| held-out tier A8 / A9 / A10 expected sources | no tables |
| the 19 gradeable expected sources | 3 have any table chunk |

Worse, the held-out gate reads a **committed binary fixture**. It re-chunks only through a tool
that is skipped unless an env var is set, needs a second checkout on one machine at a pinned
commit, and is skipped in CI. So under a chunking change the floors do not move: **the gate goes
green having measured nothing.**

Building a table-bearing graded corpus alongside the change is the trap ADR-0072 named in its own
words. So the honest output is the one ADR-0058 and ADR-0072 already produced twice: specify it,
evidence it, and do not ship it. That is ADR-0078.

The plan also had a measurement flaw independent of the corpus: its response variable was "the rank
of `entries.id` 18336", and under four of its six arms **that chunk does not exist**. Two arms also
multiply the number of units containing the answer, which improves any rank-of-any-match metric
mechanically rather than by retrieving better.

### 2. What survives the cut is not a tuning question at all

Two of the proposed arms are **correctness properties**, not parameters:

- prose and tables never share a chunk;
- no table body row is emitted without its header.

These need no retrieval corpus. They are structural invariants over the text, gated deterministically
on this repo's own `docs/**/*.md` — the identical shape as ADR-0048's fence-balance gate, which
measured 70/4,126 chunks unbalanced before and 0/4,143 after. ADR-0048 already measured the second
property failing at **33 of 34 chunks** and recorded it as "unbuilt, not broken".

Only whole-table vs per-cell vs linearized-row is genuinely a tuning question, and that is exactly
the part nothing here can adjudicate.

**These properties are not shipped in 1.23.0 either**, for the reason in §3: a chunker change moves
boundaries, which moves hashes, and without replacement semantics a re-ingest *adds* rows beside the
stale ones (ADR-0069 measured 6,240). They are specified in ADR-0078 and blocked on the ingest work,
not on evidence.

### 3. Replacement semantics already exist, and the plan's version was less safe

`ReplaceFileAsync` (`SqliteMemoryStore.cs:460`, `IMemoryStore.cs:99`, used by
`WatchDigestExecutor.cs:52`) already does replace-by-path **in one transaction**, with a fingerprint
re-check, so no reader sees the file chunkless and a crash rolls the whole replace back.

WP3 proposed to build a reconciliation keyed on `(ctx, source_file)`. `MemorySql.cs:188-190` carries
a comment warning against precisely that:

> Matching is on `path`, not `source_file`: mirror/ingest rows carry the real file path in both
> columns, while manual `memory_write` rows carry `path = <sha256(content)>.md` and merely cite the
> file in `source_file` — the digest owns the mirror rows, never manual rows that cite the file.

So WP3 would have reintroduced a bug the codebase documents and guards against, and would have
deleted `memory_write` notes and promoted rows that merely *cite* a file. A second scenario: `ctx =
"shared"` strips `project_id`, so two projects ingesting the same path into the shared tier could
delete each other's rows.

WP3 is withdrawn. The real follow-up is one line of intent — *route file ingest through the existing
`ReplaceFileAsync`* — and it is much smaller than what was planned.

### 4. `source_state` is deleted. Three lanes, three independent reasons.

| reason | consequence |
|---|---|
| No consumer in this task; staleness is derivable from a hash comparison the repair already performs | the "derive the list, or delete it" invariant cuts against storing it |
| The table starts empty and nothing backfills it, so its trigger cannot fire on a legacy bank | the repair would report success having done nothing — the same silent-no-op shape as **#357 itself** |
| `SourceAffinityRanker` is pure over `MemorySearchResult`; an empty marker means the ADR-0005 boost is off on every existing bank, and permanently on fixture-backed suites | the plan would have silently disabled a shipped ranking feature |

Replacement: `chunk_index = -1` for unknown position, plus an explicit `ChunkIndex < 0` guard in
`SourceAffinityRanker.SiblingCount` and `Consolidate`. Two lines, no schema, no join. The sentinel
alone is insufficient — `-1` and `0` differ by exactly 1 and would read as *adjacent* — which is why
the guard is explicit. The 161 sourceless rows need nothing: the ranker already short-circuits on
`SourceFile is null` at lines 24, 70 and 99.

This departs from the owner's "option B with A" ruling, recorded as a judgment call rather than a
reinterpretation. The capability the ruling asked for is preserved; the stored marker that cannot
work is not.

## Why the destructive work does not ship while nobody is watching

The data-safety lane established that `MaintenanceJobRunner` / `BankMaintenanceHostedService` would
run the repair and sweep **fully unattended, within 15 seconds of the first bank open after
upgrade**, with no backup, no dry-run and no confirmation — against a live 26k-entry bank, while the
owner is away.

Cutting the chunker change removes the *reason* replacement semantics were urgent: the only argument
for dropping old chunks was to make a chunking change visible. With no chunking change, there is
nothing to drop. Shipping an unattended destructive sweep to fix a real-but-not-urgent duplicate
class, with the owner unavailable, is not a trade worth making.

## Corrections to the ground truth I briefed the lanes with

- I stated `Ddl` is **39 statements / 42 total**, taken from ADR-0075's prose.
  `MemorySchemaDdlStatementCountTests` pins **0 DDL / 4 total** on the digest-matched path and **40**
  on the stale-digest path. ADRs are immutable records of what was true when written; the test tracks
  today. Pin counts from the executable check, not the ADR that first recorded them.
- The plan propagated my **22.3%** "pipe-bearing chunks" figure as a 22% blast radius. That measure
  counts any line with two or more pipes and catches shell pipelines in code. The defensible figure
  is **9.1%** (chunks containing a table header separator). A ~2.4× overstatement presented as
  measurement.
- One correction in the plan's favour: "invisible to everything, metrics included" is wrong —
  `SqliteMemoryStore.cs:843-847` does call `Log.KeywordModalityFailed`.

## Findings carried into implementation

- The fusion heuristic's `max(rrf, best_single_leg)` is not the safe rewrite the plan claimed.
  `SearchResultMerger.cs:26` **re-fuses the fused list**, replacing every score with `(k+1)/(k+rank)`
  from position — so an injected magnitude is discarded downstream. Ties are broken by
  `ThenBy(result.Path, StringComparer.Ordinal)`, which would make the top hybrid result a function of
  the file name. The heuristic must act where its effect survives, and that is not where the plan put it.
- `LegAvailability` needs an explicit home in Core, or an implementer places it beside
  `SqliteMemoryStore` and breaks layering.
- WP6 contradicted itself on whether the leg-availability signal ships regardless or only on one
  research outcome. It ships regardless: it is a correctness signal, not a tuning result.
- Every gate needs a stated RED perturbation. The plan's C4 — the one gate that would prove the
  heuristic rescues #367's shape — had none, which is the exact `ShouldBeInRange(0.0, 1.0)` failure
  ADR-0056 exists to remember.

## Still open

- Whether routing ingest through `ReplaceFileAsync` preserves per-row metadata (`rating`,
  `access_count`, `last_accessed_at`) across the round trip, or whether the promotion-candidate
  machinery at `MemorySql.cs:197-199` is the only thing carried. Needs reading before that work is
  scoped.
- Whether the two chunking correctness properties can be gated without a fixture regeneration. The
  ADR-0048 precedent suggests yes — it asserted over live `docs/**/*.md`, not the binary fixture.
- The 1,143 orphaned and 161 sourceless rows still have no defined position-repair story. They get
  the `-1` sentinel; whether anything better is possible is unanswered.
