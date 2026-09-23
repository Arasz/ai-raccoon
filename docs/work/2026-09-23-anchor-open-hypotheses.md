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

### F8 — The current chunker still writes NULL sections, and section and heading_path disagree [MEASURED]

A fresh watch ingest of `harbor-guide.md` produced chunk 1 with `section = NULL` and
`heading_path = 'Harbor guide > Tides'`. Its text is chunk 0's text again, plus the `## Tides` heading.
Chunks 2–6 are the reverse: `section = 'Tides'` and `heading_path = ''`. So new data also produces NULL
sections, and the heading row itself, the one most likely to be the target, is the row the anchor
cannot match. The 1.44.0 ADR-0106 rows in the live bank show the same split (for example chunk 2:
`section 'Decision'`, `heading_path ''`).

**Evidence:** Read-only `SELECT chunk_index, section, heading_path, value FROM entries` on `<scratch>/dr2/memory.db` after `memory_watch_add` of `<scratch>/probe2` (1,086-word file). Live: the same query filtered on `source_file LIKE '%0106-attach-or-start%'`.

### F9 — Live chunk_index −1 NULL-section rows are legacy memory_write output [INFERRED]

Two facts point this way. A `memory_write` with a `sourceFile` and no `section` on 1.44.2 stored
`chunk_index 1`, not −1 (scratch row 5e6c3f23). And F5's month split puts almost all NULL sections
in August. From these, the −1 rows come from before memory_write chunking (ADR-0064), and new
writes produce NULL sections only through F8's chunker path or a `memory_write` without a
section. The write path's code was not read to confirm this.

## Still open

- Which chunker code emits the NULL-section heading chunk, and why it duplicates the previous chunk's text (F8). Reading the markdown chunker's section and heading-path assignment would settle it.
- Whether `repair reingest` heals the live NULL sections. Test it on a bank copy with the watches table deleted, and drain embeddings afterwards.
- Whether an anchor should fall back to `heading_path`, which is not an FTS column, or whether ingest should fill `section` from it. That is a design ruling, not a measurement.
- F6's fix shape (match file tokens against the basename only, or require them to be adjacent) and whether the 27.3% collision rate hurts real queries. It needs the query log, which this investigation did not sample.
- F2's single-anchor-row case was not isolated in a run.
