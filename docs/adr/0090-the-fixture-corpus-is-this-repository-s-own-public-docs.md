# 0090. The fixture corpus is this repository's own public docs

Date: 2026-08-22

Status: Accepted

Issue: ai-raccoon#414 (immediate half, S6a). Design note:
`docs/work/2026-08-22-s6a-fixture-replacement-design.md`.

## Context

`tests/AiRaccoon.Tests/Resources/jsaa-memory.db` was a 19 MB SQLite bank built from the
private `job-search-ai-assistant` repository. It held 2518 chunks — 2.37 MB of that
project's prose, not merely metadata — and the owner's email address in 94 rows. Fourteen
test classes and roughly sixty test methods read it, and the whole retrieval-quality
apparatus of ADRs 0004, 0005, 0006, 0047, 0049, 0050 and 0056 rests on numbers measured
against it.

The obvious fix — generate a synthetic bank at test time — is disqualified by ADR-0049.
The bundled u8s8-quantized model produces three materially different embeddings on three
arithmetic paths, and the resulting `AdrNdcg5` spread is 0.070, fourteen times
`GoldenFile.RankingTolerance`. Corpus vectors computed on the test host would turn every
retrieval gate into a measurement of the CI runner's CPU. That is precisely the defect
ADR-0050 was written to close.

Deleting the fixture and its tests satisfies the issue's literal wording and destroys the
apparatus. That is the outcome this decision exists to prevent.

## Decision

Keep the architecture; replace the content. The bank stays committed with its corpus
vectors baked once on arm64, the query vectors stay pinned, the numbers stay pinned, and
every gate keeps running in nightly CI. Only the prose changes — from a private project's
documents to this repository's own public documents.

`tests/AiRaccoon.Tests/Resources/docs-memory.db`, project id `ai-raccoon`, built by
`DocsCorpusRegenerationTool` from the selection in `scripts/src/corpus_config.py`:
`docs/adr`, top-level `docs/*.md`, `docs/explanation|how-to|reference|tutorials`,
`.ai-badger/invariants|agents|instructions`, `.ai-badger/skills/*/SKILL.md`,
`.ai-badger/delegation.md`, and `README.md` / `CLAUDE.md` / `HERMES.md`.

Measured on arm64, 2026-08-22: **199 tracked files → 2049 chunks, 1013 carrying a
structure embedding, 605 distinct heading paths, 0 pending embeds, 17 231 872 bytes.**

Three properties of the selection are load-bearing rather than incidental:

1. **Only git-tracked files are selected.** Observed during this work: the first
   regeneration picked up a sibling branch's unmerged, untracked ADR and baked 29 chunks
   of it into the fixture. The bank must be reproducible from a clean clone, because the
   pinned retrieval numbers rest on that.
2. **Two document families survive** (`docs/` and `.ai-badger/`). `RetrievalTuningSetsTests`
   asserts the corpus carries more than one generator; a selection that collapsed to one
   would hollow that gate out silently.
3. **Skill *reference* files are excluded by measurement**, not taste. They are 148 files —
   43% of the candidate selection's files, 30% of its bytes — and including them produced
   an ~19 MB bank, no smaller than the one being removed. Excluding them costs nothing the
   gates depend on and leaves ~1700 chunks, comfortably clear of the 1000-vector floor
   `Vec0PartitionKeyProbe` asserts.

There is no exclusion glob list. Measured: with these include globs the selection is
byte-identical whether an eighteen-entry exclude list is applied or none is, because no
include glob is recursive enough to reach `docs/work/` or `.ai-badger/skills/learned/` in
the first place. A list where no entry does anything is the stale list
`derive-or-delete-the-list` warns about. Narrowness of the includes is the mechanism, and
`test_excluded_trees_stay_out` is the gate on it — observed red when an include glob was
widened to `docs/**/*.md`.

## What this costs

No mechanism gate is lost. Every assertion that proves a code path is load-bearing — the
structure-vector fusion, the AND→OR FTS fallback, the relative score floor, source identity
on results, corpus hash-contract integrity, the vec0 dimension reconcile budget — ports to
the new corpus and is re-proved red there. Every consumer class keeps running in nightly CI
against a committed bank; nothing moves behind an environment variable.

Three things are genuinely lost, and none is recoverable from this repository again:

1. **ADR-0049's numeric table is no longer reproducible in-repo.** `PlatformNumericsProbe`
   still reproduces the *phenomenon* — three arithmetic paths, three different embeddings —
   but the figures 0.5260827785380623 (arm64), 0.5587755695473325 (x64 no-VNNI) and
   0.48859561353453607 (x64 VNNI) were measured on the jsaa corpus and stay as recorded
   history.
2. **ADR-0056's circularity measurement is now historical.** The in-sample/held-out gap —
   0.673 against 0.285, "out-of-sample retrieval scores 42% of the published figure" — was
   measured over a tuning/held-out partition of a corpus that has left the repository. On
   the new corpus the gap is not merely unmeasured, it is undefined: nothing was ever tuned
   here, so every gradeable query is out-of-sample and the held-out gate covers the whole
   catalog. That is a stronger gate and a weaker record, and both halves are deliberate.
3. **The evidence that the shipped fusion parameters are optimal does not port.** ADR-0005's
   source-affinity grid and ADR-0006's 96-point RRF grid selected k = 60, weights 1:1 and
   λ = 0.1 over the jsaa corpus. The sweep tests continue to gate that the chosen point holds
   its rank and nDCG floors here, re-pinned; they no longer testify that it was *selected*
   here. If the chosen point turns out not to dominate its neighbours on this corpus, that is
   a finding about parameters overfitted to one corpus — file it, do not widen the gate.

## One honest qualification on "out-of-sample"

This ADR claims the held-out gate is out-of-sample because no parameter sweep ever selected
against this corpus. That is true of the *parameters*. It is not entirely true of the *queries*.

Three catalog entries were revised against measured retrieval while authoring them:

- **S6** was re-worded when its first phrasing did not retrieve ADR-0004's Decision chunk at all.
- **S3** was moved twice — ADR-0006 → ADR-0024 → ADR-0031 — because the first two targets sat in
  documents whose sibling chunks outranked them.
- **S4** was moved from ADR-0006 to ADR-0024 for the same reason.

The reason in every case was structural rather than score-chasing: ADR-0006 is a 26-chunk
document, and a section target inside it measures sibling competition (this repo's own
`docs/notes/adr-sibling-competition.md` names the effect), not section targeting. The final
targets sit in 4-5 chunk ADRs where the section is the discriminating unit. No bound was
loosened to accommodate a query, and the A and C sets were authored once and never revised
against a score.

Still, "chosen after looking at what the retriever returns" is a weaker claim than "never seen
by the retriever", and the S set has the weaker one. Anyone reading a published S-set number
should know that. The A set (10 queries) and C set (3 graded) carry the stronger claim.

## The choice that looks like a mistake

This replaces a committed multi-megabyte binary fixture in a public repository with another
committed multi-megabyte binary fixture in a public repository — 17.2 MB against 19.2 MB, a
reduction of about a tenth, not an order. That is deliberate. Issue #414's harm is private
third-party content and an email address, which this removes entirely; the byte cost is
removed by S6b's history rewrite, which is the only thing that actually reclaims the space.
Building the bank at test time would avoid the bytes and re-open ADR-0049.

A second choice that reads as a defect: the guard test that forbids the old fixture's name
composes that name from string fragments instead of writing it as a literal. The acceptance
criterion for #414 is that a grep for the name across `tests/` returns nothing, and a guard
spelling the string it forbids would be its own last offender.

## Consequences

- `scripts/list-corpus-files.py` + `scripts/src/corpus_config.py` replace
  `scripts/list-jsaa-corpus-files.py`. `scripts/src/sources.py` takes its glob lists as
  parameters: one matcher, two configurations.
- `scripts/ingest-jsaa-docs.py` survives as the owner's local CLI for the private project,
  but its tree location and commit pin move to `AIRACCOON_JSAA_ROOT` /
  `AIRACCOON_JSAA_PINNED_COMMIT`. An absolute path into a private checkout and a foreign
  repository's SHA no longer sit in a public repo, and the pipeline refuses to run unpinned.
- `docs/work/2026-08-15-fts-term-budget/sweep.py` opens the old fixture by path. It is a
  dated work-doc artifact describing what was done at the time and is left untouched; the
  corpus it read has left the repository.
- ADRs 0047, 0049, 0050 and 0056 carry a status line marking their measurements historical.
- S6b — rewriting the blob out of git history — is **not** done here. It needs the owner
  present, all sessions stopped, and the force-push hook deliberately lifted. Until it runs,
  the private bank is still reachable in history and #414 stays open.

## Alternatives considered

- **Synthetic bank generated at test time from committed markdown.** Rejected: re-opens
  ADR-0049 for the corpus leg, making every pinned number a host measurement, and costs a
  full ONNX ingest per test class.
- **Env-gated local jsaa bank, skip when absent.** Rejected: moves fourteen classes into a
  lane one machine ever runs, and leaves the private corpus alive on disk as the only thing
  that can turn the gates green. The privacy problem is relocated, not solved.
- **Delete the fixture and its tests.** Rejected: satisfies the issue's literal wording and
  destroys the retrieval-quality apparatus seven ADRs rest on.
