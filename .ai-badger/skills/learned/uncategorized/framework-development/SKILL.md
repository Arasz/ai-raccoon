---
name: framework-development
description: >-
  Use when modifying ai-badger framework source files.
platforms: [linux, macos]
---

# ai-badger framework development

Development workflow for modifying the ai-badger framework itself (this repo), not for scaffolding a target project.

## Source-of-truth hierarchy

```
features/<stack>/skills/<name>/     ← SOURCE of truth (edit here)
  ↓ sync_plugin_skills.py
.ai-badger/skills/<name>/           ← scaffolded copy (do NOT edit directly)
  ↓ (for hermes projects)
skills/<name>/                      ← hermes plugin copy (do NOT edit directly)
```

**Never edit `.ai-badger/skills/` or `skills/` directly** — they are overwritten by sync. Edit `features/<stack>/skills/<name>/` and then sync.

## Edit → sync → test → build cycle

1. **Edit** the source file under `features/<stack>/...`
2. **Sync** plugin copies: `python3 tooling/sync_plugin_skills.py`
3. **Test**: `python3 -m pytest -q`
4. **Build check**: `python3 tooling/index_build.py --check`
5. **Freshness** (self-scaffold changes): `python3 gates/scaffold_freshness_guard.py` — the committed `.ai-badger/` must exactly match what a re-scaffold produces
6. **Commit** all changed files (source + synced copies + re-scaffolded `.ai-badger/`)

Skipping step 2 causes `test_repo_plugin_copy_is_in_sync` to fail.

## Adding a common MCP server (catalog entry)

Two verified instances (hermes PR #300, ai-raccoon 2026-08-05): the recipe — meta.json / server.md (≤15 lines INCLUDING the comment line, auto-enforced) / tools.json (intents ≤200 chars, tags from the CLOSED vocabulary in
features/common/mcp-tags.json) / stack-mcp.json declaration with `availability.command` gate, the zero-arg `.mcp.json` render trap (no
`args` key), VERSION-first → index_build → changelog → version_sync ordering, the
`AI_BADGER_MCP_AVAILABILITY=all` self-scaffold requirement (else `${HOME}` rewrite → false freshness finding), and the hermes test-file mirror — is in
`references/mcp-catalog-addition.md`. Two execution lessons the written plan missed (verified 0.78.0): the pre-commit hook chain forces the work into exactly TWO commits (feature commit = catalog + skill + index.json + self-scaffold refresh
together; release commit = VERSION + version_sync + changelog + changelog_index together), and a new common skill MUST be declared in `badger_lib.SKILL_SCOPES` or it leaks as a stack-local skill (silently scaffolded even under
`--skills ''`) and fails the routing/empty-skills/sync tests. Also: run the FULL suite before committing — catalog additions break auto-glob tests (agent-doc budget, empty-skills, sync-plugin-skills, mcp-catalog-instructions).

## Invariant subsystem, schema constraints & release mechanics

Before touching the invariants catalog, `collect_invariants()`/`items()`, manifest
`record()`/drift coherence, or cutting any release: read
`references/invariants-subsystem.md` — verified file:line map of the invariant rendering flow (collect_invariants → INVARIANTS slot), the "source must live under the framework root" manifest rule, the configHash self-execution pattern
(#128 — a new config key needs no manifest machinery), the top-level `additionalProperties: false` schema trap, and the full RELEASING.md cut order (VERSION → changelog entry → changelog_index.py → version_sync.py → release_guard.py vs the
last TAG).

### Project-local content conventions (issue #313, design verified 0.80.0 → shipped 0.81.0)

The shipped shape for "let a project add its own X rendered into agent files" — implemented as `.ai-badger/invariants/local/*.md`: a convention dir READ (never copied/recorded/pruned/ overwritten) by `collect_invariants()` (scaffold.py:
443-451) after the framework loop —
`sorted()` glob, strip, skip empties, `demote_headings()` (scaffold.py:193), append after framework invariants into the same `{{INVARIANTS}}` slot (template_rendering.py:119). Why manifest entries are impossible: `record()` does
`source.relative_to(self.root)`
(scaffold.py:348) — a project-local source raises; drift re-hashes root/source (drift.py:441-444,466) so a missing source would read "removed". `copy_file` writes only
`src.name` into the dest dir (scaffold.py:384), so `local/` can never collide with framework copies; prune touches only manifest targets (superseded_prune.py:24-54). Config keys are schema-gated (`additionalProperties: false`,
config.schema.json:13) — a convention-dir design avoids a schema change a config key would force. CLAUDE.md/HERMES.md are `managed`+`template`, rewritten every run (`.ai-badger/` aibCopy written BEFORE the seed-once check, agent_files.py:
79-84), so new local files reach both docs on ANY re-scaffold — "delete the file to remove" works. `detect_additions.py`'s MANAGED walk lists unmanifested `.ai-badger/invariants/local/*` as NEW feed candidates (expected noise — user
classifies project-specific). Release: additive scaffold-output change = 0.MINOR per RELEASING.md:14 even with no schema; BREAKING_VERSIONS only when a re-scaffold is REQUIRED (additive → untouched); version_sync output
(plugin.json/marketplace.json/ index.json) + changelog_index output (README table) + re-scaffolded `.ai-badger/` mirror +
`skills/` sync all committed in the SAME PR or CI gates fail. Freshness-gate precision:
the guard compares STAMP-NORMALIZED, not byte-identical (STAMP_KEYS/STAMP_LINE_RE, gates/scaffold_freshness_guard.py:46-49,152-172), and re-scaffolds a copy of tracked+untracked-unignored files, so project-local inputs present in the tree
are deterministic by construction. Full worked review with finding shapes:
`references/invariants-subsystem.md` + plan-review's
`references/scaffold-feature-plan-review.md`.

**Implementation facts (verified 0.81.0, TDD in tests/test_project_local_invariants.py):**

- **The planned "tiny private helper" cannot fit scaffold.py** — it sat at 840 lines and
  `tests/test_scaffold_skill_delivery.py::test_scaffold_py_keeps_headroom_under_the_too_many_lines_ceiling`
  enforces `MODULE_LINE_BUDGET = 850` (pylint C0302 refuses at 1000; the gate message says
  "Extract a collaborator instead of compressing a comment"). The logic shipped as a new collaborator module `features/common/skills/welcome-ai-badger/scripts/local_invariants.py`
  exposing `append_rendered(rendered, local_dir, delivered, demote, notes)`; scaffold.py gained only ~5 lines (`Set` import, `delivered` set, one import at the bottom block with
  `# noqa: E402`, one call). A collaborator must take scaffold.py-owned pure functions (`demote_headings`) as a Callable parameter — importing scaffold from the collaborator is a circular import; `local_invariants.py` copies into `skills/`
  and `.ai-badger/skills/`
  automatically via `sync_plugin_skills.py`'s whole-dir copytree.
- **Note texts (pinned by tests, keep stable):** per-collision
  `"project-local invariant '<stem>' shares a name with the delivered invariant '<stem>' — both render; use config.exclude.invariants to drop the delivered one"`
  (fires per colliding file, both still render) and one aggregate
  `"rendered N project-local invariant(s) from .ai-badger/invariants/local/ — edit or delete the files to change them"`
  only when N > 0.
- **Test-suite facts that bite:** common invariants are delivered for EVERY stack — no stack config yields zero framework invariants, so the "_None yet._" fallback test must exclude the whole catalog via
  `config["exclude"] = {"invariants": sorted(p.stem for p in root.glob("features/*/invariants/*.md"))}`. Whitespace-only local files must be skipped BEFORE appending: an empty-string member makes the list truthy, so `"\n\n".join([""])`
  blanks the section and SUPPRESSES the "_None yet._"
  fallback. Assertion pitfall: `"## X" not in section` matches `"### X"` (substring) — use
  `"\n## X" not in section`. A "no prune/overwrite note" assertion must not naively grep
  `"invariants/local" in note` — the surfacing note itself contains that path; filter for prune/overwrite wording (`"removed"`/`"left in place"`/`"overwrit"`).

## Adding a framework hook (Hermes/Claude/Copilot)

⚠️ **Hermes hook surface is the DIRECTORY PLUGIN since 0.80.0 (PR #311) — the 0.79.x loose-copy gap is historical.** The memory-first gate (0.84.0) shipped a BLOCKING
`pre_tool_call` hook on it (verified live 2026-08-06: plugin enabled, deployed module drive-tested). `adjust_hooks.py` drops flat `.py` files into
`~/.hermes/plugins/`, but Hermes loads only directory plugins (`plugin.yaml` +
`__init__.py` `register(ctx)`, opt-in via `plugins.enabled`) — none of the four hermes entries in `hooks-manifest.json` has ever fired. The `register(ctx)` ABI in
`ai_badger_hooks.py` is correct; only the packaging is wrong. Verified diagnosis + fix direction: `references/hermes-plugin-deployment-gap.md`.

Verified end-to-end with the memory-grade hook (0.79.0); full subsystem map in
`references/hooks-subsystem.md` — read it before touching `features/common/hooks/` or a hook skill. Recipe: new hook feature = skill-owned scripts under
`features/common/skills/<skill>/scripts/` (all logic in one shared `memory_grade.py`, a thin per-agent transport for Claude/Copilot), a `# Feature` section in
`ai_badger_hooks.py` (lazy sibling import `_load_*` + exception-guarded call from
`post_tool_observer`, stash/pop in `pre_llm_inject_context`), the `(skill, filename)`
tuple added to `adjust_hooks.SHARED_SKILL_MODULES`, a hooks-manifest entry naming ALL three agents, and a `hooks.json` entry with a matcher.

Execution lessons (all verified 0.79.0):

- **The pre-commit hook chain forces self-scaffold refresh + plugin-skills sync at EVERY commit**, not just at the end: the moment a new file lands under
  `features/common/skills/<skill>/scripts/`, `scaffold-freshness-guard` fails ("the re-scaffold writes it; the tree has not got it, stale") and `plugin-skills-sync` fails ("diverged: <skill>"). Run BOTH and commit them with each
  work-package commit:
  `AI_BADGER_MCP_AVAILABILITY=all python3 features/common/skills/welcome-ai-badger/scripts/scaffold.py --config .ai-badger/config.json --target . --root . --no-install --skills ''`
  and `python3 tooling/sync_plugin_skills.py` (it copies the WHOLE skill dir incl.
  `scripts/`, so new scripts become tracked under `skills/<skill>/scripts/`).
- **New hooks need F1/F3/F8 test shapes** — see the reference: copilot wiring tests must assert presence not exact count (Copilot adjust has no skills filter; Claude hook_wiring skips un-scaffolded scripts), a per-hook three-agents presence
  test is required, and sibling-module tests inject the real module into `sys.modules` under the module-name constant (register the real module object, not a copy — `__globals__`
  identity is what makes constant patches work). Round-trip tests must
  `monkeypatch.chdir(tmp_path)` or the stash key (explicit cwd) misses the pop key (os.getcwd ()).
- **TDD per work package works with this hook chain**: RED commit (test-only files pass the gates) → implementation → green commit with refresh+sync folded in; the freshness guard re-verifies itself on every commit.
- **`select_hooks`' endswith filter cannot match a command with trailing flags.** Both the Claude wiring (`hook_wiring.py:select_hooks`) and the Copilot adjuster filter source commands by `command.rstrip('"').endswith(script)` —
  `python3 .../x.py --record` never matches, so the entry silently never wires into `.claude/settings.json`. FOLD flag behavior into an existing script (the memory-first gate's recorder lives inside
  `memory_grade_hook.py`, one process per memory_search) instead of adding a flagged command.
- **Copilot matchers are case-sensitive against runtime tool names** (`grep`/`bash`
  lowercase vs Claude's `Grep`/`Bash`). The hooks-manifest agent arm supports a per-agent
  `matcher` override (schema field; the Copilot adjuster prefers
  `copilot_entry.get("matcher") or entry.get("matcher")`) — required whenever a matcher must differ per host.
- **Hermes `pre_tool_call` plugin callbacks carry no session_id** — state is per-process keyed by project cwd, reset in `on_session_start` (`reset_gate_state()`); a missing sibling module must FAIL OPEN (return None). Shared hook scripts
  must exit 0 on every path (malformed JSON included): Copilot command preToolUse is fail-closed, so a crash would deny the very tool call the gate only meant to gate.
- **Pre-commit pylint only sees staged files; the pre-push `verify.sh` pylint lane lints ALL deployment copies** (`features/`, `.ai-badger/skills/`, `skills/`). Run
  `.lefthook/pre-push/verify.sh pre-push` before pushing (stdin: `<sha> <sha> refs/heads/<b>
  refs/remotes/origin/<b>`) — a style fix can pass pre-commit and still fail the push gate on the copies.
- **A changelog entry without its `ai-badger--vX` tag fails the pre-push RELEASE lane**
  ("UNTAGGED RELEASES … tag them at the commit that carried each VERSION") and blocks every push from that checkout. Fix: `git tag -a ai-badger--vX <release-commit>` + push the tag. After any merged release, verify the tag exists — 0.83.0
  was never tagged until the lane forced it (2026-08-06).
- Release tail is the same as any feature: VERSION bump → `version_sync` →
  `changelog_index` → re-scaffold (stamps the new version into the repo's own scaffold).

## Dogfooding the ai-raccoon memory store (user preference, 2026-08-05)

When a session actually USES the ai-raccoon memory store (the `ai-raccoon-memory` skill's search/watch workflow, or direct MCP calls during framework work), the user wants every
`memory_search` quality-logged: append one JSONL line per search to
`docs/work/<date>-ai-raccoon-memory-quality.jsonl` in the repo — `query`, `scope`,
`projectId`, the full `result`, and a `usefulness` score 1–5 — and keep appending until the task finishes. If the instruction arrives mid-task, backfill the searches already run.

Operational traps hit while dogfooding (ai-raccoon 1.0.4, 2026-08-05):

- **Stale embedding settings break search with a cwd-relative model path.** A bank whose settings rows carry leftover `embedding.model`/`embedding.baseUrl` (from an earlier
  `model set openai`) while `embedding.provider=local` fails `memory_search` with
  `Load model from <cwd>/<model-name> failed: File doesn't exist` — the non-empty model name is resolved cwd-relative instead of using the bundled model. Fix: `ai-raccoon model
  set local` (null model/baseUrl DELETE the rows; `model show` then reads `(unset)`), and make sure `model_qint8_arm64.onnx` is next to the tool — copy it into the tool-store
  `Models/` dir beside `vocab.txt` (`~/.dotnet/tools/.store/<id>/<ver>/.../tools/net10.0/<rid>/Models/`; the nupkg ships only vocab.txt).
- **Watch-ingested entries stay `pending` until the embedding engine is fixed** — the watch digests fine (state `healthy`) but every entry lands `pending`. After the fix:
  `memory_embed_pending` (omit `limit`) reports `processed: N, pending: 0` and
  `memory_stats` `pending: 0` proves it.
- **`memory_watch_status` is the dogfood gate:** watch state `scanning → healthy` plus a
  `memory_search` returning docs-derived hits with `sourceFile` is the evidence the skill's watch-on-docs ritual actually worked.

The ai-raccoon-side skills (`ai-raccoon-pitfalls`, `ai-raccoon-retrieval-analysis`) are external-dir — new operational facts for them go through feed-badger, not direct patches.

## Task-flow lessons (running /task in this repo, verified 0.78.0/0.79.0)

- **Plan-review step between architect plan and implementation (mandatory).** After the architect produces the plan, dispatch a test-engineer to verify every load-bearing claim against the CURRENT merged tree — plans drift when a merge
  lands between plan-writing and implementation (the ai-raccoon plan was written pre-#302; the merged tree differed). The reviewer returns severity-tagged findings with file:line evidence and a verdict (PLAN-READY / PLAN-READY-WITH-NITS /
  PLAN-NEEDS-REVISION). Fold ALL findings into the plan document itself (F1-style: severity + file:line + concrete plan-fix), commit the amended plan, THEN dispatch implementation. Finding classes that recur: exact return-dict key of the
  hook being extended (verify the key the code returns, not the docstring), presence-vs- gap semantics of manifest-coverage tests, path-hardcoding in test helpers that a new filename breaks, and env-var pinning in config-gate tests when the
  feature will be enabled machine-wide after merge (the suite must not inherit the ambient enable).
- **Copilot review does NOT arrive on Arasz repos — the local reviewer is the gate.** The github-extension review-round loop never completes: the Copilot reviewer never posts (user-confirmed "no copilot review" / "it will not run out of
  credits" — not a quota issue, it just does not come). Do not poll for it beyond one short window. The Phase 3 code-reviewer delegation IS the real quality gate; after its findings are fixed and all gates are green, mark the PR ready and
  squash-merge without waiting for a Copilot round.
- **state.json ride-along trap (finish protocol → next task's Phase 0).** The finish-protocol state.json update can ride into the doc-audit PR: the audit agent commits state.json alongside its docs fixes in the same dirty tree, leaving
  main's state.json stale (old
  `next`/`completedTasks`) after the main PR merged. The next task's Phase 0 MUST detect this (compare `next` and `completedTasks[0]` against the last finished task) and repair by pulling the correct state from the unmerged branch
  (`git show origin/<doc-audit-branch>:.ai-badger/state.json > .ai-badger/state.json`), not by re-authoring the entry. A kept worktree from the previous task (holding the next task's plan + session records) is expected — carry the plan
  forward into the new worktree.

## Hermes token data

When framework code must read real Hermes usage (e.g. the `/task` tracker's token gathering), see `references/hermes-state-db-schema.md` — verified `state.db` schema (sessions, session_model_usage, messages, async_delegations), the
`result_json` tokens shape, the read-only `mode=ro` access pattern, and the checkpoint-key mapping. Key traps: per-message
`token_count` is NULL on every row (contextTokens must stay 0 — never substitute cumulative input), and `HERMES_SESSION_ID`/`HERMES_HOME` are the env signals.

## common stays generic — agent-specific via adjustments

`features/common/` must stay agent-agnostic; agent-specific behavior ships as
`features/<agent>/adjustments/adjust_*.py` (declared in `adjustment.json`, run at scaffold time by `scaffold.py::run_adjustments()`). PR #301 review enforced this: hermes sqlite/state.db parsing baked into
`features/common/skills/task/scripts/` was sent back with
"Extract all hermes specific entries to adjustments". The generic seam that satisfies it: a small source registry in the common module + a guarded optional import of an adjustment-delivered sibling module (`try: import session_sources;
session_sources.register(sys.modules[__name__]) except ImportError: pass` at the bottom of the common module). Implementation-phase traps (all verified, 0.77.1): the sibling module must be FULLY self-contained — even a lazy
`import tracker_lib` inside a function trips pylint R0401 cyclic-import, so duplicate the one-line helper instead; the framework-side module must carry its CONTRACT name (`session_sources.py`) or `gates/deps_guard.py` reads the import as
undeclared third-party (first-party = exists in tree); and any shipped-surface change on a tagged branch needs a VERSION bump + `version_sync` + `changelog_index` + re-scaffold or `gates/release_guard.py` fails. Verified mechanics — context
dict keys,
`files` path convention (relative to `target_dir.parent`), adjustment ordering AFTER skill delivery, freshness-guard semantics (`--skills ""` = reuse manifest skills, not "none"), plugin sync scope, test fixture module-identity pitfalls,
worktree venv shadowing in the pre-push gate: `references/adjustments-and-freshness-guard.md`.

## Skill scope registration

Every skill must be declared in `engine/badger_lib.py` → `SKILL_SCOPES`:

```python
SKILL_SCOPES: Dict[str, str] = {
    "skill-name": SKILL_SCOPE_DEFAULT,   # auto-scaffolded
    # or
    "skill-name": SKILL_SCOPE_OPT_IN,    # only when explicitly requested
}
```

- `default_skills_in(skills_dir)` filters by this registry — an undeclared skill directory is silently skipped.
- `default_skill_names()` returns all declared default skills (global, stack-agnostic).
- `default_skills_for_stacks(root, stacks)` returns default skills from only the given stacks.
- **Undeclared common skill = silent stack-local leak**: a skill dir in
  `features/common/skills/` NOT in SKILL_SCOPES is picked up by `stack_local_skills()` and scaffolded into every target — including `--skills ''` runs, breaking the empty-skills contract (`test_scaffold_empty_skills`).
  `test_every_catalog_skill_is_reachable_by_a_declared_route`
  enforces the declaration; `tooling/sync_plugin_skills.py --check` enforces the plugin copy. Add the scope FIRST (default/optIn), then run sync_plugin_skills.py, then rebuild index.json (the index records per-skill scope).

## Stack-aware skill discovery

Skills can live in any stack directory (`features/common/skills/`, `features/claude/skills/`, etc.). The discovery functions scan ALL stacks:

- `iter_feature_dirs(root)` yields `(stack, feature, dir)` for every `features/<stack>/<feature>/`
- `default_skills_in(skills_dir)` checks SKILL_SCOPES + SKILL.md existence per directory
- `stack_local_skills(skills_dir)` discovers skills NOT in SKILL_SCOPES (stack-specific)
- `skills_for_stack(root, stack)` combines both: universal defaults for common, stack-local for others
- `find_skill_in_stacks(index, stacks, name)` locates a skill item across multiple stacks

Stack-local skills (e.g. auto-wm from claude) are NOT in SKILL_SCOPES. They're discovered from their stack directory by `Scaffolder.run()` and `sync_plugin_skills.py`.

### SKILL_SCOPES: universal skills only

`SKILL_SCOPES` in `badger_lib.py` is ONLY for universal skills — those that ship to every project regardless of stack. Stack-specific skills must NOT be in this dictionary.

- **In SKILL_SCOPES as `default`** — auto-scaffolded into every project (task, prompt-markers, den-refresh, welcome-ai-badger). Lives in `features/common/skills/`.
- **In SKILL_SCOPES as `optIn`** — available but only scaffolded when explicitly requested. Lives in `features/common/skills/`.
- **NOT in SKILL_SCOPES** — stack-local skill. Discovered automatically when its stack is configured. Lives in `features/<non-common-stack>/skills/`. The Scaffolder discovers it via `bl.stack_local_skills()` during `run()`.
  sync_plugin_skills.py discovers it via
  `bl.skills_for_stack()`.

A skill in `features/claude/skills/` that is in SKILL_SCOPES as `default` creates a contradiction: the index says "default" but the scaffold never offers it (because
`DEFAULT_SKILLS` scans common only). Remove it from SKILL_SCOPES — the stack directory IS the declaration.

### Shared logic in badger_lib.py

When adding skill discovery logic, put it in `engine/badger_lib.py` — not in scaffold.py or sync_plugin_skills.py. Both consumers import from badger_lib. Functions like
`feature_items()`, `find_skill_in_stacks()`, `stack_local_skills()`, and
`skills_for_stack()` live there so changes happen in one place.

## Pitfalls

### Editing scaffolded copies instead of source

The `.ai-badger/skills/` and `skills/` directories are generated. Edits there are overwritten by `sync_plugin_skills.py` and will cause sync-test failures.

### Forgetting to sync before committing

`test_repo_plugin_copy_is_in_sync` compares source files to `.ai-badger/skills/` copies. Always run `python3 scripts/sync_plugin_skills.py` after editing framework source files. Also manually copy to `.ai-badger/skills/` if the scaffold
copy was changed.

### scaffold.py's 850-line budget — extract a collaborator, don't grow the file

Any change to `features/common/skills/welcome-ai-badger/scripts/scaffold.py` that adds ≥10 lines fails `test_scaffold_py_keeps_headroom_under_the_too_many_lines_ceiling`
(`MODULE_LINE_BUDGET = 850`; pylint C0302 refuses at 1000). The gate message IS the instruction: "Extract a collaborator instead of compressing a comment." Pattern (verified 0.81.0): a small module in the same `scripts/` dir (e.g.
`local_invariants.py`), imported in scaffold.py's bottom import block with `# noqa: E402`, and scaffold.py-owned pure functions (`demote_headings`) passed in as Callable parameters — collaborators must never import scaffold (circular
import). `sync_plugin_skills.py`'s whole-dir copytree propagates the new module to `skills/` and `.ai-badger/skills/` automatically; the freshness guard then wants the self-scaffold refresh too.

### Self-scaffold without AI_BADGER_MCP_AVAILABILITY=all pollutes the tree

Running the self-scaffold command by hand without `AI_BADGER_MCP_AVAILABILITY=all`
regenerates host-dependent files: on a host without `hermes` on PATH, `.github/mcp.json`
loses the hermes MCP block (the freshness guard then fails until an env-var'd run restores it) and a stray `.mcp.json.bak-<timestamp>` backup appears at the repo root — delete it before committing. Always self-scaffold with the env var
exactly as
`gates/scaffold_freshness_guard.py` does (it re-scaffolds a COPY, so its verdict is host-independent). Fast recovery if a run already polluted `.github/mcp.json`:
`git checkout HEAD -- .github/mcp.json` — the committed shape IS the availability=all shape; the guard then passes without re-running the scaffold (verified twice 2026-08-06).

### Adding files under docs/work/ without updating its README map

`test_docs_tree_is_canonical.py` requires every file in `docs/work/` to be named in
`docs/work/README.md` (a "Files" table row). Any new work record — including a baseline commit that adds a session JSONL — must add its row in the same commit, or the FULL suite fails at the end (`docs/work/README.md omits: <file>`). Check
`git log` when a docs-canonicality failure appears mid-branch: the omission may predate your work packages (a phase-0 baseline commit is a common culprit) — fix the README map in its own commit and report it as pre-existing.

### Version literals out of sync

The pre-commit hook checks that version literals in `.claude-plugin/plugin.json`,
`.claude-plugin/marketplace.json`, and `index.json` match `VERSION`. After bumping VERSION, run `python3 scripts/version_sync.py` to propagate the new version to all files.

### Putting stack-local skills in SKILL_SCOPES

A stack-local skill (e.g. auto-wm in `features/claude/skills/`) must NOT be in SKILL_SCOPES. The stack directory IS the declaration. The Scaffolder discovers it via
`bl.stack_local_skills()` during `run()`. Adding it to SKILL_SCOPES creates a contradiction between scope declaration and scaffold behavior.

### Over-engineering when user signals to stop

When the user says "stop" or sends an `f:` (feedback) marker, pause immediately and present current state. Do not continue adding logic or refactoring. The user may have a simpler solution in mind or the approach may be wrong.

### Adding filtering logic when the source-of-truth change propagates

Before adding guard/filter logic at every call site, check whether the single source of truth already drives the behavior. In this codebase `SKILL_SCOPES` feeds into index build, scaffold defaults, refresh, and sync — changing one value
there propagates everywhere. If the fix is a one-line scope change, don't build a filtering layer on top.

### Implementing before analyzing the data flow

When the user says "before implementing anything, create a flow diagram" or "show me the current flow", they want to understand the call chain and data flow FIRST. Use code-review-graph tools (`semantic_search_nodes_tool`,
`query_graph_tool`,
`callers_of`, `callees_of`) to trace the actual graph, then write a current-vs-proposed flow document. The user reviews the flow before any code changes happen.

### TDD cycle without stashing first

The user mandates: stash → baseline → failing tests → implement → verify. Do NOT skip the stash step — it establishes a clean baseline to compare against. Do NOT write implementation before the test exists and is verified to fail.

### Stack-local skills leaking to other agents via adjustments

When the scaffold runs `run_adjustments()`, it passes a `skills` list to each agent's adjustment script. Stack-local skills (e.g. auto-wm from claude) must be filtered to only the relevant agent. The filtering logic: `agent_stacks = [s for s in self.stacks if s in
("common", agent_name)]`, then filter skills by those stacks using `bl.skills_for_stack()`. Without this, a claude-only skill gets symlinked into `.github/skills/` (copilot).

### Agent-specific code in features/common

Never put agent-specific logic (agent env-var names, sqlite/state.db parsing, agent-named CLI flags) in `features/common/` — every scaffold, including other agents', carries it. Ship it as an `adjustments/adjust_*.py` copy + a guarded
import in the common module. If the committed `.ai-badger/` gains an adjustment-delivered file, commit it byte-identical to the adjustment source, or `gates/scaffold_freshness_guard.py` fails (non-stamp files are compared byte-for-byte
against a re-scaffold). Two traps when implementing:

- The delivered module must be fully self-contained: even a lazy import of the common module inside a function trips pylint R0401 cyclic-import (the guarded import already creates the edge). Duplicate the one-line helper (e.g. `_now_iso()`)
  instead of importing.
- Name the framework-side module by its contract name (the name the common code imports), e.g. `session_sources.py` — `gates/deps_guard.py` classifies first-party as "exists in the tree", so a differently-named source (e.g.
  `hermes_session_source.py`) makes the guarded import read as undeclared third-party.
