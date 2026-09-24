# Research: a path or symbol header on each line chunk, on two whole repositories

**Date:** 2026-09-24
**Question:** The AST chunking study on whole repositories ([`2026-09-24-ast-chunking-on-whole-repos.md`](2026-09-24-ast-chunking-on-whole-repos.md)) found that cleaner chunk boundaries don't move code retrieval. Its closing suggestion was to leave the boundaries alone and give each line chunk a header instead: the file path, plus the enclosing symbol. On the granite engine, does that header improve retrieval on job-search-ai-assistant (C#/TS/TSX) and ai-badger (Python)?

**Answer:** No, not by the keep rule fixed before the runs. The header helps JSAA by about +0.04 span MRR in every variant, and one variant's interval clears zero. On ai-badger it costs about −0.04 in both nDCG@5 and file MRR. The effect depends on the repository, and it comes almost entirely from the path. The symbol adds nothing measurable.

```chart:range
title: span-hit MRR@10 change vs the re-embed control (95% CI low..mean..high)
JSAA path+symbol (144): -0.003..0.040..0.083
JSAA path+symbol, vector only: 0.002..0.045..0.087
JSAA path only: 0.004..0.042..0.081
ai-badger path+symbol (96): -0.061..-0.013..0.036
ai-badger path only: -0.051..-0.005..0.040
```

## Setup

The setup matches the AST study, so the two records can be compared directly:
- **Binary:** AiRaccoon Release sha256 86d3fee1….
- **Engine:** granite-embedding-small-english-r2 fp16 on WebGPU.
- **Queries:** the same 144 JSAA and 96 ai-badger queries.
- **Banks:** the same drained line-chunk banks.
- **Statistics:** `compare.py`'s paired bootstrap over targets (4,000 resamples, seed 1). Span and file MRR accept a byte-identical copy of the expected file; nDCG@5 accepts the exact path only.

The harness was a throwaway `header.py`, never merged, run in two steps:
1. It copies a drained bank and sets `value = header + value` on every chunk of the tested extensions (`.cs/.ts/.tsx`, `.py`). It then marks those rows pending, so the server re-embeds them. The code engine embeds `value` and nothing else (`CodeEmbedder.cs:91`).
2. After the drain, `restore` writes the original values back and keeps the new vectors. The same drain therefore yields two arms:
   - **header in value:** the header reaches both the vector leg and the keyword leg (`code_fts` indexes `value`).
   - **vector only:** the header reaches the vector leg alone, which is how a product version would store it.

The header is `<repo-relative path>\n<Outer.Inner.Member>\n`. The symbol is the chain of named declarations enclosing the chunk's first non-blank line, found through tree-sitter (`named_descendant_for_point_range` at the line's last character, then up the parents). If that line sits outside any declaration, as imports or a doc comment above a type do, the chain comes from the first line in the chunk that is inside one. Namespaces are left out because the path already carries them. TS arrow-function consts and `describe`/`it`/`test` titles count as names.

The keep rule was fixed before any header arm ran (`PLAN.md` in the session scratch). It makes one primary comparison, path+symbol with the header in value against the re-embed control. That arm is promising only if, in at least one repository:
- the span MRR 95% CI lower bound is above 0,
- nDCG@5 does not fall,
- the gain exceeds the re-embed noise.

The other repository's span MRR must also be ≥ −0.01. The other arms are attribution only.

## Findings

### F1 — Re-embedding unchanged line chunks reproduces the baseline exactly: the noise floor is 0 [MEASURED]

The control arm reset every tested chunk to pending and let the server re-embed it without any change: 6,137 rows on ai-badger and 14,903 on JSAA. Every metric matched the original arm to four decimals: nDCG@5 0.4805 and 0.6626, spanHit@5 0.5729 and 0.7847, hit@1 identical. The paired comparison shows 0 changed ranks in both repositories.

This answers an open question from the AST study (its "Still open" list), which asked whether its 69 changed JSAA ranks were WebGPU nondeterminism. They were not. The churn in that record is caused by chunking, so its per-query stories (its F5) are real.

**Evidence:** arms `q-none-ai-badger` (drain 185 s) and `q-none-job-search-ai-assistant` (drain 601 s), compared with the AST study's `q-main-*` arms. `compare2.py` reports control against control-re-embedded as `+0.000 [+0.000, +0.000]`, `0: 0↑ 0↓`.

### F2 — On JSAA every header arm lifts span MRR by about +0.04, but the primary arm's interval touches zero [MEASURED]

| arm (vs control, 144 queries) | Δ nDCG@5 | Δ span MRR | Δ file MRR |
|---|---|---|---|
| path+symbol, header in value (primary) | +0.024 [−0.012, +0.060] | +0.040 [−0.003, +0.083] | +0.034 [−0.005, +0.073] |
| path+symbol, vector only | +0.030 [−0.004, +0.065] | +0.045 [+0.002, +0.087] | +0.032 [−0.008, +0.073] |
| path only, header in value | +0.024 [−0.007, +0.056] | +0.042 [+0.004, +0.081] | +0.030 [−0.006, +0.068] |
| path only, vector only | +0.024 [−0.007, +0.058] | +0.038 [−0.001, +0.076] | +0.029 [−0.008, +0.067] |

All four arms agree to within 0.007, so they are not four independent chances. The primary arm misses its lower bound by 0.003. Every language slice gains span MRR: C# +0.040, TS +0.038, TSX +0.042, all with intervals that include zero. Identifier-fragment queries gain the most (+0.054 span MRR, +0.050 nDCG@5) and plain-English queries gain less (+0.026). By size, small files gain (+0.079 [+0.003, +0.161]). Large files lose nDCG@5 (−0.044 [−0.100, +0.007]) while their span MRR still rises slightly.

**Evidence:** `cmp-{pathsym,pathsymemb,path,pathemb}-job-search-ai-assistant.md` in the session scratch. The prep logs show all 14,903 chunks headered and 1,684 without a symbol. `check-*.log` shows 0 pending and all 16,760 rows with vectors after each run.

### F3 — On ai-badger every header arm loses about 0.04 nDCG@5 and file MRR, with intervals below or at zero [MEASURED]

| arm (vs control, 96 queries) | Δ nDCG@5 | Δ span MRR | Δ file MRR |
|---|---|---|---|
| path+symbol, header in value (primary) | −0.043 [−0.088, −0.002] | −0.013 [−0.061, +0.036] | −0.041 [−0.085, −0.000] |
| path+symbol, vector only | −0.040 [−0.084, −0.000] | −0.009 [−0.058, +0.041] | −0.039 [−0.082, +0.001] |
| path only, header in value | −0.047 [−0.091, −0.007] | −0.005 [−0.051, +0.040] | −0.040 [−0.082, +0.001] |
| path only, vector only | −0.041 [−0.082, −0.002] | −0.002 [−0.048, +0.042] | −0.040 [−0.081, +0.001] |

File MRR is copy-aware, so the loss is not an artefact of the 241 duplicated `.py` files (AST study F4, `2026-09-24-ast-chunking-on-whole-repos.md`). By file rank, 12 queries got worse and 6 better. Small files lose the most span MRR (−0.072 [−0.150, −0.005]), which is the opposite of JSAA, where small files gained the most.

**Evidence:** `cmp-*-ai-badger.md`; prep logs show all 6,137 chunks headered and 513 without a symbol; restore sha `bedeb6c1…` equals the main bank's.

### F4 — The losses come from the header giving a sibling or test file the target's name [MEASURED]

The header repeats the file's name on every chunk of that file. When the query names the topic, every file whose path contains that topic gets the same boost. That includes the target's own test file, an adjacent hook, or a mapper named after the exception it maps.

| query | expected file | top-1 with the header |
|---|---|---|
| bpy-014 "score idf matched terms" | `features/common/retrieval/bm25.py` (rank 1 → 3) | `tests/test_retrieval_bm25.py` |
| bpy-030 "branch merge unset upstream" | `skills/git-work/scripts/git_config_health_hook.py` | `tests/test_git_config_health_hook.py` |
| jcs-059/060 (score offer fit activity) | `…/Activities/ScoreOfferFitActivity.cs` (1 → 2) | `…/ScoreOfferFitActivityTests.cs` |
| jcs-044 "practice question no section" | `…/DomainExceptionProblemMapper.KnowledgeBase.cs` (8 → out of top 10) | `…/PracticeQuestionHasNoSectionException.cs` |

The overall share of test files in the top 5 did not move (0.22 on ai-badger and 0.39 on JSAA in both arms), so this is not a general drift toward tests. The header makes files with near-identical names compete. JSAA wins more often than it loses, 30 queries better and 27 worse by file rank. ai-badger's many small hook scripts, with names that spell their topic, lose 12 to 6.

**Evidence:** a per-query file-rank diff of `hits-q-none-*.json` against `hits-q-pathsym-*.json` (ad hoc script in the session scratch); `sbs-pathsym-*.md` holds each changed query's top-1 chunk from both arms.

### F5 — The symbol adds nothing measurable beyond the path, and the keyword leg adds nothing beyond the vector leg [MEASURED]

Path+symbol and path alone differ by at most 0.008 on any headline metric in either repository. That is well inside every interval. The vector-only arms match the header-in-value arms to within 0.006. The keyword leg gains nothing, because `code_fts` already indexes `source_file` and, since ADR-0109, the split identifiers. Whatever the header does, it does through the embedding of the path.

**Evidence:** F2 and F3 tables. 92% of ai-badger chunks and 89% of JSAA chunks carried a symbol line (prep logs).

### F6 — With the 2026-09-23 P4 result, a chunk header has now missed the keep rule on two engines and three corpora [INFERRED]

P4 (`2026-09-23-code-retrieval-eval-results.md` F8) tried a path header and a path + declaration header on the 116-file eval corpus with the old code-daemon engine. There the header was added only where it fit under a 510-token cap. That record's F6 measured the room at 80–90% of chunks for the path alone and 64–80% with the declaration line, by size band. Both forms were dropped. This run removes that coverage limit: granite truncates at 8,190 tokens (`OnnxEmbeddingGenerator.cs:393-399`), so every chunk got its full header. It also uses whole repositories, the target engine, and a zero noise floor.

The result is the same shape as P4. There is a small positive lean on C#/TS that does not clear the rule, and on this Python repository the lean is negative. Neither boundary quality (the AST study) nor chunk context (this record) is the lever. On repositories like ai-badger the header does measurable harm, so it should not ship as a default.

## Still open

- Whether to break the name competition in F4 by where the name goes rather than by leaving it out. One option is to embed the path as a separate vector with a small fusion weight, the way ADR-0004 handles headings for memory. Another is to add the header only to non-test files. Neither was tested, and both add machinery for a gain that is at most +0.04 on the repository where it helps.
- JSAA's +0.04 would need about 2–3 times the queries to resolve. The queries are still one model family's (Sonnet), written from a size-stratified sample.
- Only `.cs`, `.ts`, `.tsx` and `.py` were headered. The other extensions kept their original rows and vectors in every arm.
