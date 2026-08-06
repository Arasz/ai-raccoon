# README compaction — worked example (ai-raccoon, 2026-08-06)

Root README compacted 561 → 158 lines with redirects to the docs tree. Use as a
pattern template, not as facts about any other repo — re-verify everything
against the code you're rewriting.

## Task shape

"clean up the readme, use correct MD constructs (CLI commands are a mess),
compact and redirect to detailed files, run it through the humanize skill".
Ran as an ai-badger task (task skill, worktree, draft PR early, reviewer gate).

## Ground truth extracted BEFORE writing (from code, not the old README)

- Version pins: `Directory.Packages.props` — `ModelContextProtocol` **2.1.0**
  (the old README said 2.0.0; only `ModelContextProtocol.Core` is 2.0.0).
  THE REWRITE CARRIED THE STALE 2.0.0 FORWARD and the reviewer caught it —
  this is the canonical mistake: the doc being replaced is itself drift.
- Tool count: grep `[McpServerTool]` → 19 (16 memory + 3 watch), 2 prompts.
- Suite count: run `dotnet test` → 1113 passed / 43 skipped (old README said
  "185+ cases" — badly stale).
- Bundled model size: `ls -l` → 23,026,053 bytes ≈ **~23 MB** (old README said
  ~21 MB; that figure belonged to the GGUF benchmark corpus, a different file).
- ADR count: `ls docs/adr/*.md` → 6 (old README said "none recorded yet").
- CLI verb tree: `src/AiRaccoon/Setup/Cli/CliCommandTree.cs` — the fenced block
  was reformatted against the code, not against the sibling READMEs.

## Redirect mapping (what stayed in README vs what moved)

| Section | Fate |
|---|---|
| Pitch, badge, quick start (install/run/connect), feature table | kept, compacted |
| CLI verb families (access/model/retrieval/sweep/sync/watch/encryption) | moved to `docs/reference/agent-memory-server.md` |
| Sync auth 4-method matrix | already in reference doc — cut from README |
| Embedding engine matrix + benchmark | README keeps 2-row table + MRR line; full numbers → `docs/reference/embedding-benchmark.md` |
| Observability instruments | README keeps meter name + 2 commands; design → `docs/adr/0002` |
| Launch flags, env var, `.mcp.json` | kept (compact tables) |
| Architecture tree | README keeps 5-line skeleton; deep dive → `docs/explanation/architecture.md` |
| Docs map | README lists 4 canonical pages |

## Content-regression checks (load-bearing details that almost got dropped)

- `--no-launch-profile` stdio-corruption warning (VS Code repo-path client
  wiring) — NOT in the reference doc, restored there (the redirect target).
- Encrypted-bank `.mcp.json` placement ("env in user-scoped config, never in a
  tracked file") — dropped, then restored to the reference doc per reviewer
  SHOULD-FIX.
- Packaging note (`DOTNET_ENV=local` vs `dotnet_env`, macOS case-sensitivity;
  `dotnet pack -c Release` + `.mcp/server.json`) — survived only in a plan doc;
  restored into README Development section (NIT 6).
- Lesson: after cutting, `git show main:README.md | grep` the old doc for
  operational warnings (flags, quoting, env placement, packaging) and relocate
  each one explicitly.

## Markdown-construct fixes

- The "CLI commands are a mess" block: two commands per line, space-aligned
  columns, untagged fence. Fixed to one command per line, `# family:` comment
  separators, ```bash fence. THE SAME BROKEN BLOCK existed in all three files
  (root README, reference doc, packaged README) — fix the class, and note the
  packaged README was also missing the `encryption` verb family entirely.
- Bold-inline-header bullet wall ("**One bank per install scope.** …") → feature
  table (humanizer pattern 16).
- Untagged architecture fence → ```text.
- Em-dash sweep: parenthetical/sentence-tail `—` → colons/periods; kept the
  list-definition dashes in the docs map (standard markdown).

## Humanize pass (per humanizer skill)

- Removed: em-dash tics, "by design", "serves as", bold-lead bullets, rule-of-three.
- Added: direct opening ("That is the whole setup."), active voice, plain verbs.
- Reviewer re-checked with the humanizer skill loaded and found only the doc-map
  dashes + CLI-comment dashes remaining — both cleaned.

## Reviewer gate caught (not the rewrite)

- SHOULD-FIX: MCP SDK 2.1.0 (stale claim carried forward).
- SHOULD-FIX: encrypted `.mcp.json` example lost in compaction.
- NIT: doc-list em-dashes, untagged fence, CLI-comment dashes, packaging note.
- Verdict: APPROVE-WITH-CHANGES, zero MUST-FIX; gates build 0 warnings,
  tests 1113/0/43.

## Merge-race recovery (PR merged mid-review)

The user squash-merged PR #37 (2b1eb78) while the review-findings commit
(7d2cf44) was still on the branch — a known pattern for this user. The merge
captured everything up to the SDK-version fix but orphaned the final findings
commit. Recovery:

```bash
gh pr view <n> --json state,isDraft,headRefOid   # detect MERGED first
git fetch origin
git checkout -b followup/review-findings origin/main
git cherry-pick <orphaned-commit-sha>            # applies cleanly onto main
git push -u origin followup/review-findings
gh pr create --title "docs: apply code-review findings from #<n> (...)" \
  --body "Follow-up to #<n> (merged before the review round finished). ..."
```

Do NOT try to push more commits to the merged branch — the PR is closed; the
follow-up PR is the correct carrier. Check `gh pr view --json state` BEFORE
pushing follow-ups to a branch whose PR the user may have already merged.
