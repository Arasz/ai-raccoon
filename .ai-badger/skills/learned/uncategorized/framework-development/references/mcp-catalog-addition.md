# Adding a common MCP server to the ai-badger catalog

Verified recipe (hermes = PR #300, commit 3ff8674f; ai-raccoon = task/ai-raccoon-integration, 2026-08-05 — second instance of the same pattern). Read the hermes PR diff first: it is the canonical example end-to-end.

## The four artifacts

1. **`features/common/mcp/<server>/meta.json`** — server identity + prerequisite. Schema `schemas/mcp-server.schema.json`; only `name` required, `additionalProperties: false`. Keys: `name` (must match dir name AND the stack-mcp.json
   declaration name), `package`,
   `description` (human catalog prose — never injected into agent files), `homepage`,
   `prerequisite: {summary, check, install}`. Example shape:
   ```json
   {
     "$schema": "../../../../schemas/mcp-server.schema.json",
     "name": "ai-raccoon",
     "package": "arasz.ai-raccoon",
     "description": "…",
     "homepage": "https://github.com/…",
     "prerequisite": {
       "summary": "the ai-raccoon global .NET tool",
       "check": "ai-raccoon --version",
       "install": "dotnet tool install -g arasz.ai-raccoon"
     }
   }
   ```
2. **`features/common/mcp/<server>/server.md`** — prose injected into every agent doc as a
   `## MCP Tools: <server>` section (via `mcp_tools.fill_mcp_described`). **Hard budget:
   ≤15 lines INCLUDING the `<!-- … -->` comment line** — auto-enforced by
   `test_every_shipped_server_md_stays_within_the_line_budget` in
   `tests/test_mcp_catalog_instructions.py` (auto-globs every `*/mcp/*/server.md`). Keep ≤14 to be safe. End with a line noting the declaration is conditional on PATH presence.
3. **`features/common/mcp/<server>/tools.json`** — curated intents per tool:
   `{"name", "intent", "tags"}`. `intent` ≤200 chars (schema + generated index limit); `tags`
   from the CLOSED vocabulary in `features/common/mcp-tags.json` (categories: language, action, domain, meta). Auto-glob test `test_the_curated_tool_intents_use_the_shipped_tag_vocabulary`
   enforces both. Count the tools against the server's REAL surface (probe a live server with tools/list) — the file is a curation library, not a completeness claim, but a wrong count reads as drift.
4. **Declaration** in `features/common/stack-mcp.json`:
   ```json
   {
     "name": "ai-raccoon",
     "command": "ai-raccoon",
     "declare": true,
     "availability": { "command": "ai-raccoon" }
   }
   ```
   `availability.command` gates emission on `shutil.which(command)` resolving. The schema (`schemas/stack-mcp.schema.json`) already supports `availability` since hermes — no schema change needed for a second server. Keep existing entries
   byte-identical.

## Render rule that bites (copy-paste trap #1)

`mcp_tools._render_entry` splits `command` on whitespace: a zero-arg command renders as
`{"command": "ai-raccoon", "tools": ["*"]}` with **NO `args` key**. The hermes test asserts
`args: ["mcp", "serve"]` because hermes has args. Copying that assertion verbatim to a zero-arg server fails. Assert the exact dict, args-less.

## Index + release ordering

1. Bump `VERSION` FIRST (minor for a new declared server + skill). `index_build.py` bakes frameworkVersion into `index.json`.
2. `python3 tooling/index_build.py` — regenerates index.json; mcp items = subdirs carrying meta.json (no manual index edit). Tests read index.json, so this must run before GREEN.
3. Changelog entry `docs/changelog/<ver>-<slug>.md` (house style) + `tooling/changelog_index.py`
   (never hand-edit the README table).
4. `tooling/version_sync.py` — propagates VERSION into plugin.json/marketplace.json; re-run
   `index_build.py --check` after.
5. Skills: catalog skills are index-discovered — **do NOT add them to
   `features/common/skills.json`** (that file is external-sources only: superpowers, pr-review-toolkit). Skill lives at `features/common/skills/<name>/SKILL.md` with full frontmatter (name, description, version, author, license, platforms,
   metadata.hermes.*).

## Pre-commit hook chain forces commit packaging (verified 0.78.0)

The local pre-commit hooks (`version-sync`, `index-build`, `docs-guard`,
`scaffold-freshness-guard`) run on EVERY commit and abort it when the committed tree disagrees with the staged state — so the "small separate commits" shape above collapses into exactly two commits:

1. **Feature commit**: catalog files + stack-mcp.json + skill + `index.json` + the self-scaffold refresh, all together. Why: the hooks stash unstaged files and compare against the STAGED tree — `index_build.py --check` compares against the
   committed index.json, so index.json must ride in the same commit as the catalog files; and
   `scaffold-freshness-guard` fails while the feature tree has no matching re-scaffold, so the refresh must land in that same commit too.
2. **Release commit**: VERSION bump + `version_sync.py` output (plugin.json, marketplace.json, index.json) + changelog entry + `changelog_index.py` output, together. Why:
   `version-sync --check` demands plugin/marketplace match VERSION at commit time, and
   `docs-guard` demands the changelog entry exist for the committed VERSION.

Running `version_sync.py` at bump time (instead of after the changelog) is fine — it only touches version literals. After the release commit, re-verify `version_sync.py --check` +
`index_build.py --check`. A later `SKILL_SCOPES` edit also changes index.json (the index records each skill's scope) — expect another index rebuild commit.

## The skill half: SKILL_SCOPES routing (verified 0.78.0 — the plan missed this)

A new common skill is index-discovered, but it MUST also be declared in
`engine/badger_lib.py` → `SKILL_SCOPES` (scope `default` or `optIn`). Leaving it undeclared breaks four things at once:

- `tests/test_sync_plugin_skills.py::TestCatalogRouting::test_every_catalog_skill_is_reachable_by_a_declared_route`
  — "common-stack skill (s) routed nowhere".
- The scaffolder's `bl.stack_local_skills()` treats an undeclared skill in
  `features/common/skills/` as STACK-LOCAL and silently scaffolds it into every target — including `--skills ''` runs, breaking the empty-skills contract (`tests/test_scaffold_empty_skills.py`, 3 tests).
- `tooling/sync_plugin_skills.py --check` reports "missing: <name>" — run it (no args) to create the tracked plugin copy `skills/<name>/{SKILL.md, SKILL.full.md}` (generic-copy pattern) and commit it.
- `index.json` must be rebuilt (skill scope is recorded per index entry).

After declaring the scope: rebuild the index, re-run the self-scaffold refresh (scope affects the scaffolded manifest), re-run the freshness guard.

**Agent-doc budget**: a new MCP section (~17 lines) grows the repo's own scaffolded agent docs. `tests/test_agent_doc_budget.py` reads `.ai-badger/config.json` →
`agentDocs.maxLines/maxChars`. When over, RAISE the budget there — `.ai-badger/config.json`
is the config INPUT the freshness guard re-scaffolds FROM, so editing it is safe, not
"hand-editing a generated file".

**Full-suite blast radius**: catalog additions break auto-glob tests beyond the new test file (`test_mcp_catalog_instructions`, `test_agent_doc_budget`, `test_scaffold_empty_skills`,
`test_sync_plugin_skills`, `test_common_hermes_mcp_server` GOLDEN region). Run the FULL suite before committing the feature — a targeted run of the new test file is not enough. When a full-suite failure appears, prove pre-existing vs
caused-by-change by running the failing tests in the untouched main checkout (the suite's conftest redirects HOME to scratch, so running pytest there is safe).

## Self-scaffold refresh — the =all trap (copy-paste trap #2)

`gates/scaffold_freshness_guard.py` re-scaffolds the repo under `AI_BADGER_MCP_AVAILABILITY=all`
(deterministic). A manual re-scaffold WITHOUT it rewrites the command to
`${HOME}/.dotnet/tools/ai-raccoon` for tools under USER_TOOL_DIRS (e.g. `~/.dotnet/tools`), producing a FALSE stale finding. Always refresh with:

```bash
AI_BADGER_MCP_AVAILABILITY=all python3 features/common/skills/den-refresh/scripts/refresh.py --target . --root "$PWD"
```

Expected diff: agent docs gain the `## MCP Tools: <server>` section; the MCP config destination gains the entry — in THIS repo that is `.github/mcp.json`, not a root
`.mcp.json` (none exists here): `{"command": "<server>", "tools": ["*"]}` with no `args`
key for a zero-arg command. `.claude/settings.json` gains `mcp__<server>__*` in permissions.allow plus the server in enabledMcpjsonServers, and `.ai-badger/delegation.md`
lists the server. Never hand-edit generated files.

## Test template

Mirror `tests/test_common_hermes_mcp_server.py` (15 tests): catalog declares with conditional availability; schema accepts; catalog indexed with metadata files; installed → declared + split into `.mcp.json` (exact entry); unavailable →
omitted; unavailable → removed from an existing generated config; user-authored entry preserved; home-relative generated entry removed; catalog validation tracks the schema; env override `=all`/`=none`/unset trio; catalog validation green;
tools.json carries the exact tool-name set with valid intents/tags; skill indexed. The `_patch_<server>_lookup` fixture must patch `shutil.which` to answer for the new server AND keep hermes/code-review-graph answers — an "unavailable" case
must still leave the others declared.

## validate.py

No changes needed for a new server: `SCHEMA_INSTANCES` globs (`features/*/stack-mcp.json`, `features/*/mcp/*/meta.json`, `features/*/mcp/*/tools.json`)
auto-cover new files. Don't rename files outside those patterns.

## Do-not-touch list

Hermes/code-review-graph catalog files are byte-pinned by `GOLDEN_MCP_REGION`-style tests — edits there fail unrelated tests. The `<!-- … -->` comment in server.md is part of the injected region; keep it.
