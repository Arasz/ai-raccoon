# Research: the two open file#section hypotheses left by #666

**Date:** 2026-09-23
**Question:** Are the two "known open" items in PR #666 real? (H1) With `fusion.noRegression.enabled`
on, does `NoFusionRegression.Reorder` push the named section back down? (H2) Can live rows with
an empty `section` be matched by a `file#section` anchor, and does that cause the wrong section to
rank first?

Measured against PR #666 head `54183d23` (1.44.2). Its search code is byte-identical to merged main
`6015b1b5`: `git diff 54183d23 origin/main -- src/AiRaccoon.Infrastructure/Sqlite/Memory src/AiRaccoon.Core/Memory`
is empty. Scratch bank `<scratch>/dr2`, bundled local memory engine, Apple Silicon macOS, server
started with `serve --port 0 --idle-timeout 0`. The live bank `~/.ai-raccoon/memory.db` was read
only through `?mode=ro` and `PRAGMA query_only=1`.

```chart:bars
title: live rows with a source file, by section state (of 59,742)
section NULL: 32691
section set: 27051
```

## Findings

### F1 — H1 holds: with the reorder on, other rows are interleaved into the named section's rows [MEASURED]

The corpus is one watched file, `harbor-guide.md`, with Moorings, Tides and Lighthouse sections.
Tides is long enough to become five chunks (2–6) with `section='Tides'`. The query is
`harbor-guide.md#tides`. With the reorder off, all five Tides rows come first. With the reorder on,
the order becomes Tides, **chunk 1 (section NULL)**, Tides, **Moorings**, Tides, Tides, Tides. Under
full recall (`minRelativeScore 0`) the **Lighthouse** chunk also moves above the last Tides row,
which drops to 8th. The rows that jump are the ones the vector leg ranks 1st, 2nd and 3rd.

**Evidence:** `settings retrieval fusion disable|enable` and then `memory_search {query 'harbor-guide.md#tides', kind memory, limit 8}`, with and without `minRelativeScore 0`, on `<scratch>/dr2` (PR binary `1.44.2+54183d23`). With the reorder on, the served order was 2927b1ef(Tides, fts1) · 02988dcf(NULL, vec1) · 0b9d9b41(Tides, fts2) · 6b53fd5f(Moorings, vec2) · 512b4303 · 74b17608 · 296f614c. With full recall, d08640d3(Lighthouse, vec3) comes 7th, above 296f614c(Tides) in 8th.

### F2 — H1 cannot displace the first anchor row, only anchor rows 2..n [INFERRED]

Reasoned from `NoFusionRegression.Reorder`, which sorts by `min(fusedRank, bestLegRank)` and breaks ties
by fused rank (`src/AiRaccoon.Core/Memory/Fusion/NoFusionRegression.cs:21-29`). `AnchorMatchesFirst`
places an anchor row at fused rank 1, so its key is 1. A row the vector leg ranks first also has key 1,
and loses the tie on fused rank. Every other anchor row k has key ≥ 2, so any row with a leg rank below k
can pass it. F1's runs agree: the top row stayed first in every case. The single-anchor-row case was not
measured separately. Practical effect: a single-chunk section is safe, and a multi-chunk section is
diluted from its second row on.

### F3 — H2's first half holds: a row with a NULL section cannot be matched by an anchor [MEASURED]

The anchor expression is `{source_file section} : (file tokens AND "section")`. A row with no section
can match it only when every section token also appears in its path. The scratch bank shows this:
`/notes/ferry-notes.md` holds a `memory_write` with no section ("Fares: adult return tickets…"). The
query `ferry-notes.md#fares` never puts that row in the top 5. The control file
`/notes/ferry-control.md`, whose Fares row carries `section='fares'`, puts that row first (fts rank 1).
In the live bank, I replayed the anchor for each NULL-section row whose value starts with a heading,
using `file#heading-slug`. 26 of 32 missed. The 6 that matched have a heading that repeats the file name.

**Evidence:** Scratch: `memory_write` with and without a `section`, then `memory_search 'ferry-notes.md#fares'` and `'ferry-control.md#fares'`. Live: `<scratch>/h2.py`, a line-for-line replay of `SourcePathQuery.TryBuild` (`src/AiRaccoon.Infrastructure/Sqlite/Memory/SourcePathQuery.cs:32-60`) against `entries_fts`, read-only. Result: `26 ('null','target-missed','no-others')`, `6 ('null','target-matched','others-matched')`.

### F4 — H2's second half holds: NULL sections are a route to the wrong row ranking first [MEASURED]

For `ferry-notes.md#fares`, the served order was another file's Fares row (see F6), then the
Harbor-guide NULL-section chunk, Moorings and the ferry Schedule row. The target Fares row was absent
from the top 5, with or without the floor. When the named section's rows all have NULL sections,
the anchor matches nothing that belongs to them, and the order comes from whichever leg ranks
something else.

**Evidence:** Same scratch bank, `memory_search {query 'ferry-notes.md#fares', limit 5}` before and after the control rows existed. Before the control rows: vector-only order 02988dcf, 6b53fd5f, 09accf8c, 5e6c3f23 (the target, 4th). After: 4a07082a (ferry-control fares, fts1), then 02988dcf, 6b53fd5f, 09accf8c, c5efecf4. The target 5e6c3f23 is no longer in the top 5.

### F5 — NULL section is widespread and is not the same thing as chunk_index −1 [MEASURED]

The section is NULL on 32,691 of the 59,742 live rows that have a source file (55%). Of those, 24,429 have
`chunk_index = -1` and 8,262 do not. By creation month, 19,755 of 36,435 `.md` rows created in August have
no section, against 276 of 7,889 created in September. 1,814 files mix NULL and set sections;
1,669 files have no section on any row. PR #666's framing ("rows with chunk_index −1 and a nulled
section") covers about three quarters of the affected rows.

**Evidence:** Read-only `GROUP BY` queries over `entries` in `~/.ai-raccoon/memory.db`: `(1,'null',1,24429) (1,'set',1,17230) (1,'set',0,9821) (1,'null',0,8262)`; the by-month split `('2026-08', 19755, 36435), ('2026-09', 276, 7889)`.

### F6 — A third route: path tokens are matched as an unordered bag, so a different file can satisfy the anchor [MEASURED]

`ferry-notes.md#fares` matched `/notes/ferry-control.md` because that path contains `ferry`, `notes`
(the directory name), `md`, and its section contains `fares`. `TryBuild` ANDs the tokens of the file
name against the whole `source_file` column, so neither word order nor directory versus file name
matters. In the live bank, 1,024 of 3,754 `.md` basename anchors (27.3%) also match a file with a
different name. For example, `CLAUDE.md` matches `MEMORY.md` and `SESSION-…md` under a directory
path that contains `claude`. `AnchorMatchesFirst` then puts those foreign rows first.

**Evidence:** Scratch: `ferry-notes.md#fares` returns 4a07082a (source `/notes/ferry-control.md`) at fts rank 1. Live: a read-only replay of the file-only expression `{source_file} : (tokens)` per distinct `.md` source file: `md files tested=3754; … differently-named file=1024 (27.3%)`. Mechanism: `SourcePathQuery.cs:41-60` (token list joined with AND; column filter over the whole path).

### F7 — A section containing a space is never an anchor query; the 1.44.0 checklist's anchor probe was not one [READ]

The section group of `PathRegex` is `[\w-]+`. `observatory.md#Coastal duties` fails the regex, so it runs
as an ordinary query, and the `AnchorMatchesFirst` fix cannot reach it. The 1.44.0 checklist
(`docs/work/checklist/2026-09-23-1.44.0-release.json`, item `source-path-anchor-resolves-its-chunk`)
recorded its empty result as "same root cause as the absolute floor". The floor did drop the rows,
but the query never took the anchor path. The anchor item should use a slug spelling (`#coastal-duties`)
and needs re-running.

**Evidence:** `src/AiRaccoon.Infrastructure/Sqlite/Memory/SourcePathQuery.cs:63` — `^(?<file>[\w./-]+\.(?:md|markdown|txt))(?:#(?<section>[\w-]+))?$`.

### F8 — The heading-only NULL chunk is ratified behaviour, not a defect [READ]

Correction to this record's first version, which called it a chunker defect. When the unit after a
heading is too large to fit beside it, the chunk's only new unit is the bare heading, and its text is
the overlay (a copy of the previous chunk) plus that heading. `SectionsFor` and `HeadingPathFor` count
only contentful new units, so the chunk claims no section, and `SectionLabel` stores NULL. #549 pinned
this on purpose: "a chunk that only opens a section claims none". The section's body chunks still
carry the label, so the anchor still resolves the section (F1 matched all five Tides rows). The
remaining cost is one near-duplicate chunk per oversized section opener. The measured split between
`section` and `heading_path` (chunk 1 NULL / `Harbor guide > Tides`, chunks 2–6 `Tides` / empty) is
a separate oddity; its cause was not traced.

**Evidence:** `src/AiRaccoon.Core/Chunking/MarkdownChunker.cs:114-150` (HeadingPathFor, SectionsFor: contentful new units only); `src/AiRaccoon.Core/Chunking/TextChunkExtensions.cs:7` (empty Sections → NULL); pinned by `tests/AiRaccoon.Tests/Unit/Chunking/MarkdownChunkerHeadingPathTests.cs` `ChunkWithHeadings_AChunkThatOnlyOpensASection_CarriesNoHeadingPath`.

### F9 — The live NULL sections are mostly pre-#543 file chunks that were never re-chunked [MEASURED]

Before 877a8ea4 (#543, 2026-08-23), `FileIngestor.HeadingSection` parsed each chunk's own text, so
any continuation chunk without a heading of its own got NULL. Among live `.md` rows, 18,116 of the
29,651 pre-2026-08-23 rows with unknown position have NULL sections. Since the fix, 287 of 9,542
positioned rows do (3%, which includes F8's heading-only chunks). `memory_write` stores the caller's
`section` verbatim, so a write that omits it is NULL by design. `.txt` rows are almost all NULL
(4,764 of 4,766): the text chunker produces no sections, yet `PathRegex` accepts `file.txt#section`,
so a `.txt` anchor can never match.

**Evidence:** `git show 877a8ea4^:src/AiRaccoon.Infrastructure/Ingestion/FileIngestor.cs` lines 445-457 (HeadingSection over chunk text); `src/AiRaccoon.Infrastructure/Sqlite/Memory/SqliteMemoryStore.cs:364-399` (memory_write inserts the caller's section, chunk_index −1); read-only live query grouped by extension and created_at < 1787443200: `('md', old, unk, 29651, 18116)`, `('md', new, pos, 9542, 287)`, `('txt', new, pos, 4766, 4764)`.

### F10 — The existing repairs can re-label the legacy rows; 98% are in reach [INFERRED]

`repair chunk-index` writes the current chunker's section onto every row it can reproduce by hash
(`ChunkIndexRepair.cs:86`, pinned by `ChunkIndexRepairTests.RunAsync_RepositionedRows_TakeTheSectionTheCurrentChunkerReports`).
`repair reingest` replaces every mirror row of an existing file that the current chunker can't
reproduce (`ReingestRepair.cs:57-100`). 19,721 of the 20,023 NULL `.md` rows (98%) are mirror rows
whose file still exists, so running the two repairs in that order should re-label them. This is
reasoned from the code and the pinning test; it was not run against a bank copy.

**Evidence:** Eligibility, measured read-only: `mirror=True file_exists=True: 19721 (98%)`.

### F11 — heading_path would not fix either problem [MEASURED]

The wrong-file match (F6) comes from the file tokens, which heading_path does not touch. For the NULL
sections, 32,607 of the 32,691 live NULL-section rows also have an empty heading_path, so a
heading_path fallback would rescue 84 rows.

**Evidence:** Read-only live query: `heading_path empty among section IS NULL rows: (1, 32607), (0, 84)`.

### F12 — Both anchor defects are replicated by minimal unit tests [MEASURED]

`SourcePathQueryFtsBehaviourTests.Anchor_NamingOneFile_DoesNotMatchAnotherFileHoldingTheSameWords`
runs TryBuild's expression against a real FTS5 table. `ferry-notes.md#fares` matched
`/notes/ferry-control.md` (directory supplies `notes`) and `/docs/notes-ferry.md` (words reordered).
`SourcePathQueryTests.TryBuild_SectionWithSpaces_IsAnAnchor` returned false for
`observatory.md#Coastal duties`. All three cases were red, and the 9 existing tests stayed green. They are
committed with `Skip` naming the defect, so the fix drops `Skip` and must turn them green.

**Evidence:** `dotnet test tests/AiRaccoon.Tests --no-build -- --filter-class "AiRaccoon.Tests.Unit.Search.SourcePathQuery*"` → `failed: 3, succeeded: 9`, with `but was actually ["/docs/ferry-notes.md", "/notes/ferry-control.md"]` and `["/docs/ferry-notes.md", "/docs/notes-ferry.md"]`; then with Skip: `succeeded: 9, skipped: 2`.

### F13 — On 1.44.0 a slug anchor serves only the wrong section [MEASURED]

Re-running the checklist item with `observatory.md#coastal-duties` on the installed 1.44.0: the one
result is `Brass instruments` (vector rank 1), and the anchored `Coastal duties` row is dropped by the
absolute floor. #666 (1.44.2) fixes this route.

**Evidence:** Scratch `<scratch>/dr3` on 1.44.0, `memory_search {'observatory.md#coastal-duties'}` → `bca97d00 section=Brass instruments legs=[('vector', 1)]`, truncation `absoluteRelevance dropped 1`.

## Still open

- The fix shape for F6: match the file tokens as an ordered phrase against the basename, not a bag against the whole path. A phrase alone still matches `ferry/notes.md` for `ferry-notes.md`. Whether the 27.3% collision rate hurts real queries needs the query log.
- Whether the section group should accept spaces (F12), and whether `.txt` should stay in PathRegex when it can never carry a section (F9).
- F10 is reasoned, not run: repair a bank copy (watches table deleted, embeddings drained afterwards) and recount the NULL sections.
- Why section and heading_path disagree across a section's chunks (F8's last point).
- F2's single-anchor-row case was not isolated in a run.
