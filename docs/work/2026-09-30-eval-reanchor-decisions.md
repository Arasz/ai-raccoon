# eval-reanchor — composite labels, hermes chunks, P3 disposition

Task `air-254-vs-1022-retrieval-measurement-809` step `eval-reanchor`, issue #809.
Regenerates the frozen `eval-set-100.json` against COPY1022, moves its refresh pin, and rules
on the P3 legacy-parity test. PR-1.R2 owns the doc/state updates here.

Copy (immutable input): `/Users/arasz/ai-raccoon-eval/copies/memory-1022-2026-09-30.db`,
sha256 `d23ee28e52d181a65786e41f7e2d39f3aa7fc67dd17a8202e2675026c076b4eb`, 21,765 entries,
`embedding.chunkBudget=1022`; pin `docs/work/2026-09-30-copy-1022.pin.json`
(FALLBACK-converged, ceiling-stamped — stamp-gate note). Opened `mode=ro` throughout.

## Decision 1 — composite-label family matching (**applied**)

A 1022-budget chunk joins every leaf section it holds content for into one `section` label,
pipe-joined (`MarkdownChunker.SectionsFor`, `src/AiRaccoon.Core/Chunking/MarkdownChunker.cs:139`).
Observed labels (COPY1022, MEASURED):

- `0008 | Context | Decision`
- `Decision | Consequences | Alternatives considered`
- `0025 — The sweep reaper | Context | Decision 1 — on by default, not off`

The 254-era generator (frozen at `P3_BASE 12a72dfb`) predicated a family on the whole section
string and resolved **0/75** ADR family queries on COPY1022; the first refusal is
`no chunk of 0004-dual-vector-structure-signal.md matches family 'context'` (MEASURED).
`_section_matches` now matches the family against **any pipe-separated component** (a plain
label is the single-component case); the `expectedSource` slug rule is untouched
(`slugify(row.section)`, which `corpus.resolve_anchors` verifies against the heading's last
segment). All 75/75 ADR family queries resolve; 58/100 committed anchors land on composites.

RED-first: `scripts/tests/test_retrieval_tuning_eval_corpus.py::test_composite_label_families_resolve_in_copy`
failed on the unmodified generator (`AssertionError: a composite label must match its Context
component`), then passed; the copy-gated companion
`test_every_adr_family_resolves_in_the_pinned_copy` exercises all 25 files x 3 families.
Mutation: reverting the matcher to whole-string matching reddens the contract test again
(`1 failed`), restoring it greens it (`1 passed`).

### Known drift (recorded, not hidden) — E026

74/75 ADR queries keep their old target-section slug inside the newly resolved chunk; one does
not (MEASURED):

| id | old anchor slug | new chunk (first family match) | the old section still exists at |
|---|---|---|---|
| E026 | `decision-2-the-kill-switch-and-the-threshold-are-global-not-per-project` | ci=0 `0025 — The sweep reaper \| Context \| Decision 1 — on by default, not off`, hash `c631f874ee72faa4…` | ci=1 `Decision 2 — the kill switch and the threshold are global, not per-project`, hash `b5965cf6e385508e…` |

E026's family is `decision`; under the generator's frozen first-chunk-wins rule the first 1022
chunk holding content for that family is the merged Context+Decision-1 chunk. The old 254 copy
also held Decision 1 inside its `Context`-labelled chunk, but it had no composite label naming
it, so the first exact `Decision…` section was Decision 2. The anchor is **not hand-edited**:
the A/B regenerates `expectedHash` per arm with this same generator, so the cross-arm basis
stays generator-consistent. The query text is unchanged.

## Decision 2 — hermes transcripts are re-chunked (**applied**)

The 1022 migration re-chunked the `hermes-default` transcripts: their rows left the single-row
`chunk_index=-1` marker for positive chunk indices (observed `0..13`, `total_chunks` `4..23`;
MEASURED). The spec's `-1` filter therefore matched 0 rows for all 14 hermes queries (the
generator raised before any edit). Decision: the hermes specs pass `chunk_index=None` — the
marker plus the project/scope bucket is the stable anchor, still asserted unique. **25/25**
markers now resolve to exactly one row and their hashes match the committed corpus in spec
order. All 14 hermes hashes moved; 7 of the 25 non-file hashes (the shared-tier rows) did not.

## Decision 3 — P3 legacy-parity test: **skip with a named reason (not rescope)**

`scripts/tests/test_p3_cli_parity.py::TestRefreshParity::test_generators_reproduce_the_legacy_payload`
is opt-in (`P3_CLI_PARITY=1`). Evidence (2026-09-30, MEASURED):

- COPY1022: the frozen `12a72dfb` generator refuses `0004…` family `context` (composite) —
  non-zero exit, no payload written.
- `cb99fe6ecbe0.db` (254-era): the same generator refuses `0060…` family `context` — that copy
  already carries composites (`0060. An unrecognised verb… | Context`, `Decision | Consequences`).
- Therefore **no available copy is one both generators process**: the research record's
  "rescope it to a copy both generators can process" option is unavailable.
- Independently pre-existing (not caused by this step): the project-corpus half is red on the
  254 copy — the legacy project generator's output carries 14 debris ids (`C002 C022 C026 C040
  C041 C042 C043 C044 C045 C049 C095 C096 C098 C099`) while the test pins 23, measured against
  a copy (`e0434a72`) that no longer exists. Re-measuring that pin is a separate decision, so
  a rescope is not this step's to make.

Applied: the test probes the copy for composite section labels and skips before extracting the
legacy tree, with the reason naming the copy:
`frozen P3_BASE 12a72dfb generator rejects composite section labels, which <copy> carries — no
copy both generators process is available; disposition: skip, not rescope`. On a plain-label
copy the test still runs — the skip is copy-conditioned, not a deletion.

## Decision 4 — the plan-graph AC1 check is vacuous (**reported**)

The plan check diffs `[.queries[].text]`; no corpus entry has a `text` key, so both sides are
100 nulls and the diff passes for any query-text change. Mutation (MEASURED): appending
`" MUTATED"` to E001's `query` left the given `.text` check at exit 0 while the honest `.query`
variant failed (exit 1). The step's AC1 evidence is the `.query` variant: 0 changes, rerun at
the join.

## Re-anchor result (MEASURED)

Command: `python3 scripts/retrieval_tuning/build_eval_corpus.py --copy <COPY1022> --output scripts/retrieval_tuning/corpora/eval-set-100.json`

| field | changes vs pre-rebase HEAD | detail |
|---|---|---|
| `query` | **0** | the frozen instrument is unchanged |
| `expectedHash` | 93 | 75/75 ADR, 18/25 non-file |
| `expectedSource` | 58 | all composite section slugs |
| `answerSpan` | 60 | follows the resolved chunk's text |
| any other field | 0 | ids, buckets, scopes, category, difficulty, limits, grades |

- `header.snapshotSha256 = d23ee28e52d181a65786e41f7e2d39f3aa7fc67dd17a8202e2675026c076b4eb`
  equals the copy-1022 pin json sha; a fresh generator run is byte-identical to the committed
  artifact (determinism test, twice per run).
- 100/100 `expectedHash` values resolve to exactly one copy row (`_check_hash_unique` inside
  `generate`), and 25/25 non-file markers resolve to exactly one row.
- `project-corpus-100.json` is deliberately untouched (stays on `cb99fe6e`, the granite golden
  pairing), so the refresh wrapper's honest end state is exit 2: eval-set `OK,
  committed-match=True`, project-corpus `SNAPSHOT-MISMATCH`.

## What was rejected

- **Substring matching** (`"Context" in section`): accepts `Alternatives considered` for the
  consequences family and similar false positives; per-component equality/startswith keeps the
  old plain-label semantics exactly.
- **A silent `chunk_index` fallback in the resolver**: it would mask the 1022 re-chunk; the
  spec data moved instead and the uniqueness assertion stayed.
- **Hand-editing the corpus JSON**: forbidden by the protocol; every field above is generator
  output.
- **Rescoping the P3 test**: no copy both generators process exists, and the project-half
  legacy-debris pin needs its own deliberate re-measure.
- **Re-pinning `project-corpus-100.json`**: the A/B's granite golden pairing requires it to
  stay on `cb99fe6e`.

## Host overlap and grades

A detached `arm-254` re-embed finisher (bash pid 53374; server pid 48183 started 12:39 local)
ran against `$HOME/ai-raccoon-eval/arms/arm-254` throughout this step. All copy access was
`mode=ro`; COPY1022's sha was re-verified at the end of the step and matched the pin.

Grades: the copy sha/entry count are READ from the pin json; hash and marker resolutions,
field-change counts, chunk-index ranges and the P3 refusals are MEASURED against the copies;
the rowid stability behind `ORDER BY chunk_index` ties is a HYPOTHESIS backed by two
byte-identical generate runs on this immutable copy.
