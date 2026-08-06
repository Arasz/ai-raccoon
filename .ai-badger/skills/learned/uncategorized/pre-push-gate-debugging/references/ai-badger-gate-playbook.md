# ai-badger gate playbook

Lane-specific knowledge for the ai-badger repo (RiderProjects/ai-badger). The gate is
`.lefthook/pre-push/verify.sh` (all lanes) or `verify.sh <lane>` (single lane); per-lane logs
under `$TMPDIR/ai-badger-verify/<hash>/<lane>.log` — find by mtime, the hash is per-worktree.

## Lane list (order matters, cheap first)

`version-sync index plugin-skills deps docs release paths validate scaffold tdd js pylint pytest`

- `version-sync` — VERSION vs plugin.json/marketplace.json/index.json; run
  `tooling/version_sync.py` then `tooling/index_build.py` after any VERSION bump.
- `index-build` — `tooling/index_build.py --check`; run `index_build.py` after touching the
  features catalog (new adjustments, skills, stack files).
- `plugin-skills-sync` — `tooling/sync_plugin_skills.py --check`; the plugin `skills/` copy
  must mirror features/. Run `sync_plugin_skills.py` after changing any shipped skill.
- `deps-guard` — `gates/deps_guard.py`: every third-party import must be declared in
  engine/requirements.txt. First-party = files existing in the tree under engine/, tooling/,
  features/, gates/ — so a NEW agent module (e.g. features/hermes/adjustments/x.py) becomes
  first-party automatically once the file exists. Dynamic imports via
  `importlib.import_module(name)` are NOT flagged (not an ast.Import node).
- `scaffold-freshness-guard` — `gates/scaffold_freshness_guard.py`: re-scaffolds the whole
  tree in a temp copy and diffs. EVERY committed `.ai-badger/` file must equal what a
  re-scaffold produces, byte-for-byte (stamps exempt). After changing features/ or
  adjustments, re-run the scaffolder and commit the regenerated tree:
  `AI_BADGER_MCP_AVAILABILITY=all python3 features/common/skills/welcome-ai-badger/scripts/scaffold.py --config .ai-badger/config.json --target . --root . --no-install --skills ''`
- `pylint` — changed-file scope: `pylint $(git ls-files '*.py' | grep -v '^tests/')` when run
  by hand, but the LANE can differ — a full-tree run rated 10.00/10 while the lane flagged
  W0108 `unnecessary-lambda` on a freshly added `resolve=lambda: f()`. Reproduce the lane,
  not the full-tree equivalent.
- `tdd-guard` — `gates/tdd_guard.py --base origin/main`: every code change needs a test
  change alongside.

## The interpreter trap (macOS)

The bare `python3` on macOS resolves to the CommandLineTools build
(`/usr/bin/python3` → CLT 3.9) which has NO site-packages. Running pytest/pylint with it
fails with `ModuleNotFoundError: No module named 'jsonschema'` — the scaffold-freshness
guard, deps-guard, and pytest all need jsonschema. ALWAYS run gates/tests with the repo
venv python (`/Users/arasz/RiderProjects/ai-badger/.venv/bin/python`), or let verify.sh's
`_resolve_python` pick it (it prefers `$PWD/.venv`, then the main checkout's venv).

## Worktree .venv shadowing

A worktree whose own `.venv` exists but is broken (no pip / no deps) shadows the main
checkout's populated venv in `_resolve_python` → every lane fails on missing modules. Fix:
delete the worktree's `.venv` so resolution falls back to the main checkout. Symptom seen:
`ModuleNotFoundError: No module named 'jsonschema'` in version-sync/index lanes of a fresh
worktree whose main checkout venv is fine.

## MCP availability determinism (AI_BADGER_MCP_AVAILABILITY)

The scaffold's MCP availability gate probes the host PATH, which made the committed tree
host-dependent (a machine with `hermes` installed emitted the Hermes MCP block, CI did not).
The override `AI_BADGER_MCP_AVAILABILITY=all` forces every declared server available so the
freshness guard's re-scaffold is deterministic. BUT it only covers the availability probe —
`_home_relative_command`'s filesystem probe (user tool dirs like `~/.local/bin`) is a
SEPARATE host dependence: a binary present in `~/.local/bin` on the author's machine became
`${HOME}/...` in `.mcp.json` while CI kept the bare command, flipping `.github/mcp.json`'s
#193 verdict between hosts. When adding a determinism override, audit EVERY host-dependent
probe (PATH lookup, filesystem existence, $HOME-relative rewriting), not just the one that
motivated the override. The same `=all` override must short-circuit all of them.

## Session-source registry pattern (task skill)

The task skill's tracker has no built-in session source (no `DEFAULT_SOURCE`): every agent
registers equally. Each agent's adjustment installs a `<agent>_session_source.py` module
beside `tracker_lib.py` exposing `register(tracker_lib)`; tracker_lib discovers all
`*_session_source.py` siblings at import. Registry contract: `env_var`, `resolve()` (the
source owns session identification), `checkpoint()`, `resume()`, `delegation_usage()`,
`transcript` (the source an explicit `--session-id` is attributed to). Tests register the
source under test explicitly (`module.register(module.lib)`), never rely on a default.
Module is self-contained (no top-level `import tracker_lib`) to avoid pylint cyclic-import.

## Freshness-guard remediation trap: .github/mcp.json (measured 2026-08-06)

When the freshness guard fails, its printed remediation command OMITS the
`AI_BADGER_MCP_AVAILABILITY=all` override:

```
python3 features/common/skills/welcome-ai-badger/scripts/scaffold.py \
    --config .ai-badger/config.json --target . --root . --no-install --skills ''
```

Running that verbatim on a dev machine with `hermes`/`ai-raccoon` installed rewrites
`.github/mcp.json` WITHOUT those servers (the #193 declared-differently verdict flips:
`.mcp.json` renders `${HOME}/...` commands, `.github/mcp.json` plain), takes a
`.mcp.json.bak-<ts>` backup, and the guard then fails on the regenerated file — while the
guard's OWN temp run (with `=all`) keeps the servers. The committed file is the
CI-correct one. Fix: `git checkout -- .github/mcp.json` (and delete the stray
`.mcp.json.bak-<ts>`), do NOT commit the regenerated file. Add the override to the
remediation command yourself: `AI_BADGER_MCP_AVAILABILITY=all python3 ... scaffold.py ...`
— with it, generation matches the committed tree and the guard passes. Symptom of this
trap: guard says "1 of 1118 path(s): .github/mcp.json (content differs, regenerates
differently)" right after you ran its own remediation.

## server.md 15-line budget (measured 2026-08-06)

`tests/test_mcp_catalog_instructions.py::test_every_shipped_server_md_stays_within_the_line_budget`
requires every `features/*/mcp/*/server.md` to be ≤ 15 lines (`strip().splitlines()`).
server.md is embedded VERBATIM in every agent instruction file, so it is budget-constrained
by policy. The budget test lives in `test_mcp_catalog_instructions.py` — NOT in
`test_common_ai_raccoon_mcp_server.py` (the focused catalog file), so a change that keeps
the focused file green can still fail the pytest lane. When editing an existing server.md:
compress additions into existing lines (e.g. append to the setup line) rather than adding
paragraphs, and verify the count programmatically before committing —
`python3 -c "print(len(open('features/common/mcp/ai-raccoon/server.md',encoding='utf-8').read().strip().splitlines()))"`.
Counting by hand is unreliable — a 16-line result that "looks like 15" fails the lane.

## release lane: shipped-surface changes demand a VERSION bump (measured 2026-08-06)

The `release` lane fails with "shipped surface changed since ai-badger--v{version} but
VERSION is still {version}" for ANY change under `features/`, `schemas/`, `skills/`, or
`index.json` — including a `meta.json`/`server.md` content edit in an existing catalog
entry (not just new features). The lane compares against the last release TAG. Fix per
RELEASING.md: (1) bump `VERSION` — 0.MINOR for anything that changes scaffold output
(server.md lands in every agent file), 0.x.PATCH for content-only fixes that don't
change scaffold shape; (2) add `docs/changelog/{version}-{slug}.md`; (3) run
`tooling/changelog_index.py` + `tooling/version_sync.py`; (4) `version_sync.py --check`,
`changelog_index.py --check`, `gates/release_guard.py` all green. Then commit feature +
release tail in ONE commit (the pre-commit chain validates the whole tree — see the
"Commit shape" section).

## Verify "pre-existing" before concluding it (measured 2026-08-06)

A repo-wide guard failure listing many paths that your diff doesn't touch is NOT proof
of pre-existing drift. The scaffold-freshness-guard's first failure on a server.md edit
listed 7 agent-instruction files (.ai-badger/CLAUDE.md, HERMES.md, .hermes.md,
copilot-instructions variants) — none of them the edited file, and three even labeled
"hand-edited". It LOOKED pre-existing; a clean-tree run proved otherwise:
`git stash -u && <guard> && git stash pop` PASSED clean, because server.md is the
scaffold SOURCE for those 7 files — the guard's temp re-scaffold embeds the new content,
and the committed agent files no longer match. The guard stashes unstaged changes during
its own run and restores them. Discipline: whenever a guard lists files outside your
diff, run it on a stashed-clean tree first; only then decide between "pre-existing"
(bypass/restore) and "my change regenerates them" (re-scaffold + commit the regenerated
files).

## pre-commit framework execution (macOS)

The pre-commit hooks run through the main repo's `.git/hooks/pre-commit` template, which
execs `/Library/Developer/CommandLineTools/usr/bin/python3 -m pre_commit` (the
`INSTALL_PYTHON` in the template) — the `pre-commit` binary is NOT on PATH and NOT in
the repo venv. To run a single hook by hand:
`/Library/Developer/CommandLineTools/usr/bin/python3 -m pre_commit run <hook-id>`
(e.g. `scaffold-freshness-guard`) from the worktree root. Worktrees share the main
repo's hooks; a worktree's `.git/hooks/pre-commit` is not the chain.

## Commit shape: ONE commit, not two (measured 2026-08-06)

The ai-badger pre-commit chain (changelog-index, docs-guard, scaffold-freshness-guard)
validates the WHOLE tree, not the staged set. Splitting a feature from its release tail
(VERSION + changelog entry + changelog README + version_sync outputs + re-scaffolded
stamps) into two commits FAILS the hooks on the intermediate state: changelog-index
("docs/changelog/README.md is stale"), docs-guard ("0.81.0-*.md no row in README"),
freshness-guard (".ai-badger/manifest.json regenerates differently"). The working shape is
ONE commit carrying feature + release together — matches repo history
(`feat(hermes): ship hooks as a real directory plugin (0.80.0)`). The "two-commit shape
the hook chain forces" claim (framework-development skill) is wrong for this repo —
empirically the chain forces the opposite. Plan for one combined commit.
