# What moved promotion scoring since v3 shipped

Date: 2026-08-09. Companion to `2026-08-09-promotion-scoring-measurement.md`.

Baseline: scoring v3 as validated in round 2 (ADR-0018 §v3), whose parity gate asserts the C#
port stays within ±0.03 of `round2/agentC/scorer.py`. **That gate has not run since the port
merged** — it skips unless `AIRACCOON_SCORING_EVAL_FIXTURE` is set, and the fixture was lost.
Everything below therefore shipped unmeasured.

## The scorer's own code changed, and the ADR records none of it

| # | Change | Mechanism | Direction |
|---|---|---|---|
| 1 | `1d82251b` (#207) | The mid-sentence −0.18 is applied **after** the `[-1.60, +1.30]` clamp (`PromotionContentEvidence.cs:153-160`); the prototype folds it into `adj` **before** clamping (`scorer.py:363`, `:491`). A chunk saturating the ceiling now scores 0.18 below the validated model. | lowers |
| 2 | `1d82251b` (#207) | Mid-sentence detection strips leading markdown decoration (`*#>-–`) before reading the first character (`CandidateFeatures.cs:87-88`); the prototype strips whitespace only (`scorer.py:304`). Every chunk opening `**word`, `- word`, `> word` now trips the penalty. | lowers |
| 3 | `1d82251b` (#207) | A hardcoded `ProjectAliases` table (`CandidateFeatures.cs:48-59`) feeds foreign-project detection. Moves *toward* the prototype, which has the same table — but C# intersects it with the live project ids, so a project absent from the bank never counts as foreign, and one present but absent from the table falls back to a bare-substring match. | raises |
| 4 | `532e7c4d` (#232) | Bracketed spans are stripped before the 250-char foreign-subject head window (`CandidateFeatures.cs:70-75`); the prototype slices the raw value. A parenthetical project mention stops paying; text beyond 250 chars is pulled into the window. | both |

Change 1 carries a comment calling it a deliberate decision. It may well be the better rule — but
it was never measured, and it is the exact quantity the ±0.03 parity gate exists to police.

**Now measured.** With the gate restored and reporting, the C# scorer returns **0.713** on the
rebuilt 59-row reference fixture against the prototype's **0.7156** — a gap of 0.003, comfortably
inside the ±0.03 tolerance. So the four divergences are behaviourally minor *on this fixture*, and
none of them is the reason the model underperforms on the live corpus. They remain undocumented
drift that shipped without a gate; that is the finding, not a regression in the number.

## The channel router is bypassed for every `memory_write` row

`ProvenanceArchetype.cs:119`:

```csharp
if (string.IsNullOrEmpty(sourceFile) || HexName().IsMatch(Basename(rawPath.ToLowerInvariant())))
    return ProvenanceArchetype.OrganicNote;   // prior 2.30, second-highest in the table
```

`HexName` is `^[0-9a-f]{32,}\.md$`, and `SqliteMemoryStore.cs:1081` mints exactly that for **every**
write: `WritePathFor(value) => $"{ToHexStringLower(SHA256.HashData(...))}.md"`. The `||` means a
hex path alone is sufficient, so **a row's `source_file` is never consulted once it was written
through `memory_write`** — and that is the write path this repo's own `CLAUDE.md` instructs agents
to use.

Measured in the live bank: **159 hex-path rows, 129 of them carrying a `source_file` that is being
ignored.** What those source files actually are:

| `source_file` | rows | channel it should imply |
|---|---|---|
| `hermes/2026…` (11 distinct transcripts) | 83 | conversation mirror — hard noise |
| `hermes-memory` | 2 | same |
| `docs/work/reviews/…` | 2 | `review` (0.95) |
| `.github/workflows/publish.yml`, `tooling/validate.py`, `engine/badger_lib.py`, … | rest | code files, not documents |

The `hermes-default` queue shows the consequence directly — every row tagged `organic-note`:

```
3.00  ["organic-note","durable-fact-language"]           source_file=.github/workflows/publish.yml
2.85  ["organic-note","status-vocabulary","dated-fact"]  source_file=docs/work/reviews/2026-08-07-…md
2.30  ["organic-note"]                                   source_file=hermes-memory
```

Hermes conversation turns are pure status dumps — the same class ADR-0018 says filled the shared
tier with 53 turn-mirrors and prompted the wipe. `TurnMirror` (0.35) does not catch them:
`TurnMirrorPrefix.Markup()` matches tool-call XML, and a plain chat message has none. **They are
held back only by `settings.extract.exclude.prefixes = 'hermes/'`** — a hand-maintained operator
prefix applied in the store (`SqliteMemoryStore.cs:297-308`), not by the scorer. The sibling shape
`source_file = 'hermes-memory'` does not match that prefix and **is already in the queue at 2.30**.

**This is not simply a bug to invert.** A hex path means "written by an agent, not ingested from a
document", and for a curated finding that cites a `.cs` file, `organic-note` is arguably the right
channel — the `source_file` there is a *citation*, not a provenance. For a Hermes transcript the
same field is an opaque transcript id and the row is noise. The current rule cannot tell those
apart because it never looks. Which of the two a `source_file` implies is a question for labeled
data, not for a regex — it is carried into the tournament as a named problem.

## 29 queue rows still carry pre-v2 scores

`SharedExtractionRunner.ProposeAsync` refreshes a queued row's score only if that row still
survives into `ranked`. A row that has since become a shared-tier duplicate, or fallen below the
floor, is skipped by `RankAll` and **keeps its old score forever**.

Live: 27 rows at score 2.50 tagged `["cross-project","recent"]` and 2 at 3.50 tagged
`["cross-project","accessed","recent"]` — the retired four-bonus v1 vocabulary — all frozen at
`updated_at = 2026-08-08 20:08:23` while every other row refreshed at `2026-08-09 16:30`. Their
entries still exist; these are not orphans. Eviction is `ORDER BY score ASC`
(`PromotionQueueSql.cs:78`) and `ai-badger`'s live queue floor is 2.18, so **stale v1 scores
outrank correctly-scored v3 rows and survive eviction on a number the current model cannot
produce.**

## Ruled out

Checked and unable to move a score: `MarkdownChunker`/`IChunker` (untouched — chunk boundaries and
therefore `value` are unchanged); `HeadingPathParser` (feeds structure vectors only);
`MemorySql.SelectExtractionCandidates`; `FileIngestor` (only an `embedInline` flag);
`WorkspaceService.ConsolidateAsync` (still writes `source_file` null → organic, as before);
`PromotionCapacityPolicy` (reservation math unchanged); `CandidateFloor` (still 0.4, never moved);
`f3ce26c8` (#216, memory-as-a-communication-layer — added no new row shape); the OTLP/observability
series; the serve/proxy/token series; and #232's WP2/WP3/WP4 error-handling work.

`feat/c3-wire-workspace-provenance` is **refuted** despite its name: it wires `agentId`/`name` onto
the workspace record and does not touch `source_file`, `path`, or the consolidate write path.

## Merged mid-task, and it moves the score

`memory_set_ttl` and the sweep reaper landed on `main` as **#226** while this work was in flight —
recorded here as unmerged when first written, corrected on merge.
`SharedExtractionService.cs:68-71` drops any row with a non-null `TtlDays` from candidacy unless
`includeTtlRows` — so **setting a TTL silently vetoes promotion**: a forgetting knob doubles as a
promotion veto, which is not what an operator setting an expiry would expect. Separately, #232's
new `promotion_queue_entries_ad` trigger drops a queue row when its entry is deleted, so the
reaper will now drain the propose queue as it runs.
