# 0080. The table-chunking correctness properties cost retrieval, and no arm wins

Date: 2026-08-17

Status: Accepted

Builds [ADR-0077](0077-table-chunking-is-not-adjudicable-on-a-table-blind-corpus.md)'s two
correctness properties and scores its two surviving tuning arms on the corpus
[ADR-0079](0079-table-chunking-becomes-adjudicable-on-a-corpus-that-rechunks.md) built for exactly
this. The properties are implemented and tested; the measurement says shipping them **regresses
retrieval**, and none of the arms recovers it. Recorded so the next attempt starts from the numbers
rather than from the hypothesis.

## What was built

`MarkdownChunker` now treats a table region — a pipe row followed directly by a separator row,
outside any fence — as whole-chunk units that each repeat the header and separator. A table unit
takes no overlay and leaves none. That delivers both properties ADR-0077 classified as correctness
rather than tuning:

- **prose and tables never share a chunk**
- **no table body row is emitted without its header** (ADR-0048's "unbuilt, not broken" follow-up)

A pipe alone is not a table: a shell pipeline stays prose and a fenced table stays fence content,
which is the distinction ADR-0077's appendix corrects the 22.3% blast-radius figure for. Where a
header plus one row cannot fit the budget the region falls back to plain lines — the token budget
outranks the property, the same precedence `FlushAsSubFences` already applies to fences (ADR-0036).

Eight tests, four watched red before the change.

## The properties work, and they cost retrieval

They do exactly what they claim. The share of the answer-bearing chunk that is table content went
from a mean of **49% to 100% on every one of the sixteen graded queries** — no prose is averaged
into a table chunk's embedding any more.

Retrieval got worse:

| | chunks | mean nDCG@5 | mean MRR@10 |
|---|---|---|---|
| before this change (prose and tables mixed) | 343 | **0.070683** | **0.089658** |
| prose and tables separated, header carried over | 380 | 0.039433 | 0.064658 |

The prose that used to share the chunk was carrying the match. A bare pipe-syntax table is poor
embedding material, and purifying the chunk removed the context that made a paraphrased query hit
it at all.

**This is the part worth carrying forward.** ADR-0077 called these properties "not blocked on
measurement", which was true and easy to read as "free". They are not free — they are merely
*decidable* without a corpus. Deciding whether to *want* them still needs one.

## No arm wins

Both tuning arms ADR-0077 declined to reject, scored on the same documents, queries and search path:

| arm | chunks | mean nDCG@5 | mean MRR@10 | mean relevance set |
|---|---|---|---|---|
| before this change (prose and tables mixed) | 343 | **0.070683** | **0.089658** | 1.00 |
| shipped: whole table, header carry-over | 380 | 0.039433 | 0.064658 | 1.00 |
| per-row + header carry-over | 676 | 0.058167 | 0.051215 | 1.00 |
| row linearised into sentences | 675 | 0.058167 | 0.053199 | 1.00 |

Three readings:

1. **Every table-aware arm is worse than leaving it alone**, on both metrics. The finer arms recover
   about half the nDCG@5 loss and none of the MRR@10 loss.
2. **Linearisation did not rescue it.** ADR-0077 called a row rendered as a sentence "the most
   promising of the tuning arms", on the reasoning that an embedding model handles a sentence better
   than `| a | b |`. On this corpus it is indistinguishable from plain per-row on nDCG@5 (identical
   to six decimals) and ahead of it by 0.002 on MRR@10 — noise, not a result.
3. **The comparison is clean.** Every arm held a mean relevance set of exactly 1.00, maximum 1, so
   no arm gained by multiplying the units containing the answer — the mechanical inflation ADR-0077
   feared and ADR-0079 could only bound. Reporting set sizes beside the scores is what shows this,
   and it is why the comparison can be believed.

## Decision

**The implementation stays on its branch and does not merge.** The properties are correct, tested
and cheap to re-apply; what is missing is a reason to want them, and the only measurement available
says they cost more than they return.

Concretely, the gate ADR-0079 shipped **caught this on its first real use**: the pinned floor
(mean nDCG@5 ≥ 0.050) went red at 0.039433 against a change that would otherwise have read as a pure
correctness improvement. Those floors are deliberately **not** re-pinned downward here. A floor
lowered to admit the change it exists to catch is not a floor.

## What would change the answer

1. **A heading or section prefix on a table chunk.** Every arm here throws away the prose context and
   none puts anything back. A table chunk carrying its section heading is a different arm, is cheap,
   and is the obvious next thing to score — ADR-0077 already noted a whole-table chunk "forfeits the
   4x bm25 `section` weight when the table carries no `#` line of its own".
2. **A larger graded set.** Sixteen queries with eleven at zero means the means are moved by very few
   queries; the per-row and linearised arms tying to six decimals is a symptom of that, not a
   coincidence. Before acting on a gap of 0.02, widen the corpus.
3. **A retrieval-side change rather than a chunking one.** The pre-change baseline wins because the
   answer chunk carried prose; that argues for context at query time, not for cutting tables finer.

## Consequences

- **Positive:** ADR-0077's two correctness properties exist, with tests, and can be re-applied the
  day there is a reason to want them.
- **Positive:** both tuning arms are now measured rather than argued about, on a corpus that
  re-chunks, with relevance sets reported. ADR-0077's per-cell arm was rejected on inspection; these
  two are now rejected on evidence.
- **Positive:** ADR-0079's gate demonstrably discriminates — its first encounter with a real
  chunking change went red for the right reason.
- **Negative:** the defect ADR-0077 recorded stays open. 9.1% of chunks still mix table and prose,
  and that mixing is now known to be *helping* retrieval on this corpus, which makes "fix table
  chunking" a harder question than it looked.
- **Negative:** `ADifferentChunkBudget_MovesTheScores` drops to 2 of 16 moved under table-atomic
  chunking, below its threshold of 3. That threshold was calibrated against prose-dominated chunking
  and would need re-deriving, not lowering, if these properties ever ship.
