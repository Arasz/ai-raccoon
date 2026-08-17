# 0082. At forty queries, two table arms beat the baseline — and ADR-0081's verdict was a width artefact

Date: 2026-08-17

Status: Accepted

Supersedes the *conclusion* of
[ADR-0081](0081-the-table-chunking-correctness-properties-cost-retrieval.md) — "no arm wins" — while
keeping its measurements, which were correct at the width they were taken. ADR-0081 named the risk
itself: *"11 of 16 queries at zero is what lets two arms tie to six decimals. Do not act on a gap of
0.02 at this width."* Acting on that warning changed the answer.

## What changed

The graded set went from 16 queries over 13 documents to **40 queries over 22 documents**, all spans
still verified unique and inside a table row. The integrity gate rejected three non-unique spans and
one that had drifted into prose while the new queries were authored — three of the four would have
silently distorted the numbers.

Then the untried arm ADR-0081 named was built: **a table chunk that carries its section heading**.
The mechanism is confirmed at the source, not assumed — `FileIngestor.HeadingSection` derives
`section` and `heading_path` by parsing the *chunk's own text* through `HeadingPathParser`, so a
table chunk with no `#` line of its own gets `section = null`, can never satisfy a `file#section`
anchor, and forfeits the 4× bm25 section weight ADR-0077 flagged.

## What it measured

All arms on the same 40 queries, documents and search path:

| arm | chunks | mean nDCG@5 | mean MRR@10 |
|---|---|---|---|
| **baseline — prose and tables mixed (`main`)** | — | **0.227007** | **0.237599** |
| correctness properties: whole table + header carry-over | 684 | 0.146341 | 0.140645 |
| whole table + section heading | 703 | 0.196978 | 0.175060 |
| row linearised into sentences | 1306 | 0.245587 | 0.239653 |
| per-row + header carry-over | 1309 | 0.261360 | 0.244891 |
| **per-row + section heading** | 1310 | 0.292417 | **0.275129** |
| **linearised + section heading** | 1308 | **0.294594** | 0.273601 |

Four readings:

1. **Two arms now beat the baseline.** Per-row plus the section heading returns
   **+0.065 nDCG@5 (+29%)** and **+0.038 MRR@10 (+16%)** over leaving chunking alone; linearised
   plus heading is statistically indistinguishable from it and marginally ahead on nDCG@5. At 16
   queries every arm lost to the baseline.
2. **The section heading is worth having on every shape** — whole table +0.051, per-row +0.031,
   linearised +0.049 nDCG@5. It is the cheapest single change measured in this whole line of work,
   and it is the one ADR-0077 predicted from the bm25 weighting without ever testing it.
3. **The correctness properties alone still lose** (0.146 against 0.227). ADR-0081's central finding
   survives: separating prose from tables, on its own, costs retrieval. What it lacked was the
   context to put back.
4. **Per-row versus whole-table was invisible at the old width.** 0.261 against 0.146 here; 0.058
   against 0.039 there. The ordering was the same, the magnitude was not, and the gap was inside the
   noise the old width could resolve.

Relevance sets stayed at a mean of 1.00–1.02, maximum 2, across every arm, so no arm gained by
multiplying the units containing the answer.

## Decision

**Still nothing ships in this record.** Two arms now have a positive case, which is a different
state from ADR-0081's, but a chunking change rewrites every stored hash and the arms are measured on
one corpus of 22 documents against one embedding model.

What this record fixes is the *decision basis*: "no arm wins" is withdrawn, "the section heading is
worth restoring" is established, and the next attempt starts from `per-row + section heading` rather
than from the whole-table shape currently implemented on the branch.

## What is still missing before shipping

1. **A second corpus, or a held-out split of this one.** Forty queries authored by one author in one
   sitting is a tuning set. ADR-0056 exists because this project already shipped a gate measured off
   its tuning set.
2. **The prose side re-measured.** Every arm here changes only table regions, but per-row chunking
   nearly doubles the chunk count, and no number here says what that does to prose retrieval or to
   bank size.
3. **Replacement semantics exercised at scale.** Changed boundaries change `ContentHash`; the 910-file,
   15,099-chunk reingest question on the real bank is still open.

## Consequences

- **Positive:** ADR-0081's warning about width was correct and acting on it changed the verdict —
  the cheapest lesson in this line of work, and the one most worth keeping.
- **Positive:** the section heading is established as a real, cheap gain, independent of which table
  shape is eventually chosen.
- **Negative:** ADR-0081's headline reads as settled and is not; anyone citing "no arm wins" must be
  pointed here. Its measurements stand, its conclusion does not.
- **Negative:** the widened set makes the two leading arms indistinguishable from each other
  (0.2924 against 0.2946 nDCG@5, and the reverse ordering on MRR@10). Choosing between per-row and
  linearised needs more width again, or a criterion other than these two means.
