# Follow-up task seed: structured config chunkers (XML / YAML / TOML) + file-type registry — 2026-09-23
Owner asks: plain-text chunker is THE fallback chunker; add .toml/.xml/.yaml chunkers; build a best-effort
file-type → extensions (and known file names) registry from local repos.
Facts:
- Today memory owns .md/.markdown/.txt(.PlainText)/.json; code owns CodeExtensions.All (25 language exts,
  src/AiRaccoon.Core/Ingestion/CodeExtensions.cs). No corpus ingests XML/YAML/TOML → net-new ingestion (feature, 1.45.0 + README line).
- Census ~/RiderProjects (content-sniffed, bin/obj/node_modules/.git/worktrees skipped):
  YAML .yaml 828, .yml 242 | XML .csproj 75, .xml 66, .props 51 (Directory.Build.props 24, Directory.Packages.props 27),
  .slnx 24, .config 5 (nuget.config 5), .targets 2, .pubxml 2, .runsettings 1 | noise-XML .trx 60, .svg 16, .user 4 |
  TOML .toml 30 | .editorconfig 6 is INI, not TOML.
- JsonFileTypeChunker falls back to IMarkdownChunker on malformed JSON; switching to plain text changes chunk text → reingest.
Open design: parser packages (YamlDotNet/Tomlyn) vs line-based top-level-key / [table] splitting; XML via System.Xml.Linq;
registry of extensions + exact file names; exclusions; unified fallback = PlainTextChunker.

## Parser evaluation (2026-09-23) — owner asked: performance / support / last commit
Bench: scratchpad/parserbench (Release, Apple M4, net10.0, other sessions running → noisy), all 1,070 local YAML
(2,455 KiB) and 30 TOML (17 KiB) files, texts preloaded, 1 warm-up + 7 passes; two runs [MEASURED]:
- YamlDotNet event parser: median 100 / 153 ms (range 65-251); 0 failures
- VYaml event parser:      median  61 /  39 ms (range 16-158); 0 failures
- SharpYaml event parser:  median  42 / 100 ms (range 36-212); 0 failures
- Tomlyn syntax tree 2.0/4.7 ms; CsToml 0.7/1.2 ms; Tomlet 1.3/2.3 ms; 0 failures each
→ ~0.04-0.14 ms per YAML file: negligible next to embedding; performance does not decide.
Source positions (reflection) [MEASURED]: YamlDotNet ParsingEvent.Start/End ✓, SharpYaml Start/End ✓, VYaml parser.CurrentMark ✓,
Tomlyn SyntaxNode.Span ✓ (lossless syntax tree), CsToml TomlDocument.LineNumber only (document-level) ✗, Tomlet none ✗.
Support [READ: GitHub API + NuGet, 2026-09-23]:
- YamlDotNet 18.1.0 (2026-06-26), 567M downloads, MIT, TFMs net10/net8/ns2.x/net47, last commit 2026-09-15, 132 open issues
- SharpYaml 3.14.0 (2026-09-20), 75M downloads, MIT (nuspec; GitHub says "Other" because LICENSE.txt carries the YamlDotNet notice), net10/net8/ns2.0, last commit 2026-09-20, 0 open issues, releases ~every 2 weeks (xoofx, also Tomlyn)
- VYaml 1.4.0 (2026-06-21), 0.46M downloads, MIT, up to net9 (no net10 TFM), last commit 2026-07-06, 19 open issues
- Tomlyn 2.10.1 (2026-07-02), 5.9M downloads, BSD-2, net10/net8/ns2.0, last commit 2026-08-18, 5 open issues
- CsToml 1.8.6 (2026-08-15) 21k downloads; Tomlet 6.2.0 (2025-12-24) 0.5M; Tommy last commit 2022 (stale)
Recommendation: TOML → Tomlyn (only candidate with spans; active). YAML → SharpYaml (spans, net10 TFM, most recent
release, 0 open issues, same maintainer as Tomlyn); YamlDotNet is the conservative alternative (larger community, slower cadence).
