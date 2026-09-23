# Research: AST chunking on the granite engine, and Magika for file selection

**Date:** 2026-09-23
**Question:** Now that granite-embedding-small-english-r2 is the code engine, does tree-sitter AST chunking clear the keep rule, and would Magika content detection improve which watched files get ingested?

```chart:bars
title: held-out nDCG@5 change vs main, AST arm (95% CI low / mean / high)
CI low: -0.039
mean: -0.009
CI high: 0.018
```

## Findings

### F1 — On granite, AST chunking is a DROP: held-out nDCG@5 −0.009, 95% CI [−0.039, 0.018] [MEASURED]

The baseline is main at 8dbec253 (bundled granite fp16 plus the identifier column of ADR-0109). It scored nDCG@5 0.8238, the same as the granite figure in `docs/work/2026-09-23-code-retrieval-eval-results.md` F12. The AST arm re-chunked every corpus file with tree-sitter at the product's 510-token budget and let the server re-embed. It lost on the tuning split too (−0.012), and on both target categories (behaviour-nl −0.013, identifier-fragment −0.015). On code-daemon the same arm was the closest miss of the task (+0.018 [−0.006, 0.044]). Granite removes that margin.

**Evidence:** `run_code_eval.py --binary <main Release> --arm granite-main --save-hits --allow-busy` (fresh ingest and drain, 36 s); `spike_p5_chunk.py data-root-granite-main bank-ast --mode ast` from the #686 scratch, with token counting switched to granite's `tokenizer.json` (`add_special_tokens=False`), output "VERIFY OK", 1,373 chunks, 4 SCSS regex fallbacks; `run_code_eval.py --arm granite-ast --reuse-bank bank-ast --drain`; `compare_code_eval.py results-granite-main.json results-granite-ast.json --target-category behaviour-nl` → `DROP`. Apple M4, a 1.46.2 live server at about 100% CPU alongside (metrics are deterministic; drain times are not).

### F2 — The per-language C# gain (+0.086) is one query out of ten; C# span-hit MRR goes down [MEASURED]

C# has 33 queries, all from this repository, 10 of them held out. AST changed 8 ranks: 2 better, 6 worse. Span-hit MRR moves −0.051 [−0.13, 0.035] over all 33 and −0.037 [−0.28, 0.19] over the 10 held out. The harness's +0.086 nDCG@5 comes almost entirely from self-001 going from rank 5 to 1.

AST chunks are cleaner. 71% of C# chunks start at a member, doc comment or attribute and end on a closing line, against 61% for the line chunker, and the line chunker already ends 97% of its chunks on `}` or `;`. The losses come from AST separating a doc comment or a class header from the code it describes (self-003), or ranking a tidy but wrong member first (self-009, self-031).

**Evidence:** `csharp_compare.py` over `hits-granite-{main,ast}.json` and both banks' `code_entries`, bootstrap 2,000 resamples, seed 1; boundary counts over `code_entries` rows whose `source_file` ends in `.cs` (198 and 203 rows).

### F3 — Magika would skip 6 of the 13,169 files the extension allow-list accepts [MEASURED]

Across the 14 watched directories of the author's bank, 14,528 files survive the hidden-segment and `WatchDenySet` rules. The allow-list already skips every binary Magika found (138 PE executables, 63 PNG, plus pdb, fonts, sqlite, onnx, nupkg). Of the accepted files, Magika calls five small JSON test snapshots "unknown" and one Markdown file "empty". A NUL-byte check catches the binary case without a model.

**Evidence:** `census.py` (magika 1.0.3, Python, standard_v3_3 model) over the `watches` paths of `~/.ai-raccoon/memory.db`; `ai-raccoon.ignore` rules were not applied. 57.9 s, about 4 ms per file including I/O.

### F4 — Every skipped text file has a recognisable extension; there is no extension-less code [MEASURED]

The 1,111 skipped text files are YAML (223 or more), `.mjs`/`.cjs` (110), `.jsonl` data (259), csproj/props/targets (33), Terraform/HCL (28), SVG (16), XML (13), Vue (12), shell (10) and TOML (6). The 38 extension-less files are LICENSE, VERSION and similar plain text. Adding extensions covers the code among them, and deterministically.

**Evidence:** the same census, grouped by route and by Magika label and extension.

### F5 — Magika's label is less reliable than the extension on this machine [MEASURED]

It labelled 228 `.yaml` files as plain text and 84 as Dockerfiles, 74 `.ts` files as JavaScript, and 27 `.html` files as plain text. Choosing a chunker from its label would misroute more files than choosing by extension.

**Evidence:** the same census, `(extension, label)` counts on the accepted and skipped routes.

### F6 — The tree-sitter language pack works offline on osx-arm64 but weighs 449 MB native [MEASURED]

`XbergIo.TreeSitterLanguagePack` 1.20.0 restored in an `osx-arm64` build and parsed a C# file in 54 ms. It parsed C#, Python, Kotlin, YAML, TOML and XML with an empty cache and downloaded nothing. Its single native library is 449 MB on disk (39 MB compressed), and it depends on TreeSitter.DotNet 1.3.0, which adds 65 MB of per-RID grammars. It needs a RID-specific build and reflection-based JSON enabled.

**Evidence:** file-based apps in the session scratch (`smoke.cs`, `offline.cs`); nupkg listings from `api.nuget.org/v3-flatcontainer`.

### F7 — Across all 14 repositories in RiderProjects, the extended list leaves only config, data and specs unindexed [MEASURED]

Tracked files outside the memory and code lists are YAML (1,298), `.jsonl` data (276), XML (50), csproj (36), `.lock` (28), props (12), Gherkin `.feature` (12), TOML (9) and slnx (5), plus images, patches and checksums. `.jsx` and `.tsx` were already indexed. This change adds `.feature`; the config formats wait for the structured config chunkers.

**Evidence:** `git ls-files` in every repository under `~/RiderProjects` (node_modules excluded), grouped by extension against `CodeExtensions.All` and the memory extensions.

### F8 — Magika fits the product only where extensions are unreliable [INFERRED]

From F3 to F5: on repositories the allow-list is right about binaries, the missing coverage is an extension list, and Magika mislabels config and markup. It would earn its 3.1 MB model and the per-file inference in watched folders that are not repositories (downloads, shared drives), where extensions lie. None of the census directories is like that.

## Still open

- Whether AST helps C# with a larger C# query set: 33 queries from one repository cannot separate a real per-language effect from noise. A second C# repository in the corpus would settle it.
- Whether a larger code chunk budget (granite's window is 8,190 tokens) changes either arm. Both ran at 510.
- Structured config files (YAML, TOML, XML, csproj) stay unindexed until the config chunkers task; tree-sitter's size (F6) is the cost to weigh there against Tomlyn and SharpYaml.
- The census did not apply `ai-raccoon.ignore`, so a few skipped files may already be excluded on purpose.
