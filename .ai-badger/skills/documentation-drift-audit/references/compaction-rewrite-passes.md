## Compaction / rewrite passes (READMEs and user-facing docs)

A "clean up the readme, use proper markdown, compact it and redirect to the
detailed files" task is a rewrite, not a drift fix — but it inherits the audit's
core rule: **the doc being replaced is NOT ground truth.** It is itself the
accumulated drift. Verified 2026-08-06 on the project's root README (561 → 158
lines): the rewrite carried forward the old README's "MCP SDK 2.0.0" claim,
which contradicted `Directory.Packages.props` (pin is 2.1.0) — caught by the
reviewer, not by the rewrite. Full worked example:
`references/readme-compaction-recipe.md`.

Method:

1. **Pull ground truth from code FIRST, before writing a line.** Version pins
   (`Directory.Packages.props`, csproj TFMs), tool counts (grep the `[McpServerTool]`
   surface), test counts (run the suite), model/file sizes (`ls -l`, not the old
   doc's rounded figure), ADR/record counts (`ls docs/adr`). Every number you keep
   must survive a grep against the declaration, not against the old prose.
2. **Redirect, don't duplicate.** The root README's job is: what it is, quick
   start, feature table, and pointers. Detail (CLI verb families, sync auth
   matrix, error shapes, watch semantics) lives in `docs/reference/` /
   `docs/explanation/`. Before cutting a section, confirm the redirect target
   actually contains the detail — a pointer to a page that lost the content is
   worse than the duplication.
3. **Content-regression check against the OLD doc.** `git show main:README.md`
   and grep for load-bearing operational warnings that compaction tends to drop:
   `--no-launch-profile` stdio-corruption warning, encrypted-bank `.mcp.json`
   placement ("never in a tracked file"), shell-quoting gotchas, packaging
   env-var case-sensitivity (e.g. `DOTNET_ENV=local` vs `dotnet_env` on macOS).
   Relocate these to the redirect target; dropping them loses real knowledge.
4. **Fix the class, not the site.** The same broken construct usually lives in
   several files written together — a two-commands-per-line CLI fenced block
   appeared in the root README, `docs/reference/agent-memory-server.md`, AND the
   packaged `src/<Proj>/README.md`. Grep the whole doc surface for the
   pattern after fixing the first copy. The packaged README may also be missing
   verb families that the reference doc gained (e.g. `encryption`) — diff the
   CLI block against the command tree, not against a sibling README.
5. **Markdown-construct checklist** (the "CLI commands are a mess" complaint):
   one command per line in fenced blocks — never two-per-line space-aligned;
   group families with `# family:` comment lines; tag every fence
   (```bash / ```json / ```text); tables need a header separator row; avoid
   bold-inline-header bullet walls (feature table instead); sentence-case
   headings. Em-dash tics are the humanizer's tell — run the final prose
   through the humanize patterns (strip `—` parentheticals, "by design",
   "serves as") before committing.
6. **Packaged/embedded READMEs are self-contained.** `src/*/README.md` ships
   inside the tool package: it cannot use repo-relative links, so it must keep
   its own CLI block and its own copy of critical warnings. It is hand-maintained
   and drifts independently — treat it as a separate doc to verify, not a mirror
   to skip.
