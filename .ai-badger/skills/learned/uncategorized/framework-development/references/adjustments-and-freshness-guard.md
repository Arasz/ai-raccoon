# Adjustments, freshness guard, plugin sync, guarded imports — verified mechanics

Verified against PR #301 (2026-08-03): plan review at branch head 4e0ca08, implementation verified at c62652c (0.77.1). All paths relative to repo root.

## common stays generic; agent-specific via adjustments

- Rule: `features/common/` is agent-agnostic. Agent-specific behavior ships as
  `features/<agent>/adjustments/adjust_*.py`, declared in
  `features/<agent>/adjustments/adjustment.json`, run at scaffold time. Review comments on PR #301 enforced this: hermes sqlite/state.db code baked into
  `features/common/skills/task/scripts/` was sent back with "Extract all hermes specific entries to adjustments".
- `scaffold.py::run_adjustments()` lives in
  `features/common/skills/welcome-ai-badger/scripts/scaffold.py` (NOT engine/). It iterates
  `config["agents"]`, loads each manifest script by path via
  `importlib.util.spec_from_file_location`, and calls `adjust(context)`.
- Context dict keys: `framework_root`, `config`, `feature_dir` (the agent's adjustments dir), `target_dir` (= `<project>/.ai-badger`), `target` (project root), `skills`
  (agent-filtered), `personas`, `index`, `mcp_servers`, `mcp_declarations`, `mcp_declined`.
- `adjust()` returns `{'applied': bool, 'files': list[str], 'notes': str}`. Paths in
  `files` are relative to `target_dir.parent` (project root) — e.g. adjust_hooks.py returns
  `.ai-badger/hooks/ai_badger_hooks.py`. Returned files are recorded into the manifest via
  `self.record("adjustments", agent_name, ...)`.
- Ordering: skill delivery (`scaffold_skills()`) runs BEFORE `run_adjustments()`
  (scaffold.py run (): line ~693 vs ~706), so an adjustment can copy a module into
  `.ai-badger/skills/<name>/scripts/` and it survives the skill copy. Still use
  `dst.parent.mkdir(parents=True, exist_ok=True)` (adjust_hooks pattern) — the destination may not exist if the skill is not delivered.
- User-scope copies (e.g. `~/.hermes/plugins/`) are deliberately NOT in `files` — the scaffolder records files relative to the project target, which a home path cannot be.

## Guarded optional import (agent module beside a common module)

The pattern that lets a common script pick up an agent-delivered sibling module:

```python
try:
    import session_sources
    session_sources.register(sys.modules[__name__])
except ImportError:
    pass
```

- Put it at the BOTTOM of the common module: by then the module is fully initialized, so the sibling's `register(sys.modules[__name__])` sees a complete module.
- `sys.modules[__name__]` is correct under BOTH loadings: production imports the common module top-level (`import tracker_lib`), tests load it dotted (`features.common.skills.task.scripts.tracker_lib`).
- pylint-safe: `import-error` (E0401) is globally disabled in pyproject.toml.
- **The sibling module must be FULLY self-contained — do NOT `import tracker_lib` even lazily inside functions.** Verified in the 0.77.0→0.77.1 implementation (PR #301, commit c62652c): a lazy `import tracker_lib` inside
  `make_hermes_checkpoint` still trips pylint R0401 `cyclic-import` (the guarded `import session_sources` in tracker_lib + the sibling's import form a module-level cycle the checker sees regardless of where the import sits). The working
  fix: duplicate the one-line helper (`_now_iso()` mirroring `tracker_lib.now_iso`)
  instead of importing. `register(tracker_lib)` already receives the lib as an argument, so the sibling needs no import for registration — only for shared helpers, and those are the cycle's price.
- **Name the framework-side module by its CONTRACT name.** `gates/deps_guard.py` parses every `*.py` under `engine/ tooling/ features/ gates/` and sorts imported names into first-party (exists in tree) / stdlib / third-party (everything
  else, INCLUDING a name that resolves nowhere). A framework module named `hermes_session_source.py` while tracker_lib imports `session_sources` fails the deps guard as "undeclared third-party import: session_sources" because no file with
  that stem exists in the tree. Keep
  `features/hermes/adjustments/session_sources.py` (contract name) and copy verbatim — same name both sides, mirroring how adjust_hooks copies `ai_badger_hooks.py`.

## Scaffold freshness guard (gates/scaffold_freshness_guard.py)

- Re-scaffolds a throwaway copy of the whole tree (`--config`, `--target`, `--root`,
  `--no-install`, `--skills ""`) and diffs it against the committed tree. The committed
  `.ai-badger/` must EXACTLY match what a re-scaffold produces.
- `--skills ""` means "reuse the target's existing manifest skill list", NOT "no skills"
  (scaffold.py main (): explicitly empty `--skills` is 'unchanged', issue #129). So the re-scaffold re-delivers the same skills AND runs adjustments — adjustment-delivered files (e.g. `.ai-badger/skills/task/scripts/session_sources.py`) are
  part of the comparison.
- Comparison: Python/text files byte-for-byte; JSON parsed with stamp keys stripped (`frameworkVersion`, `frameworkCommit`, `frameworkDirty`, `generatedAt`, `configHash`); the "Scaffolded by ai-badger <version>" line normalized. A committed
  adjustment-delivered file must be byte-identical to the adjustment source module.
- Runs with `AI_BADGER_MCP_AVAILABILITY=all` so the MCP-availability probe is host-independent (a machine with `hermes` installed emits the Hermes MCP block; CI does not). Keep this env override in any self-scaffold/test fixture.
- Adjustments are driven by the repo's OWN `config.json` `agents` array — self-scaffolding repos run their own adjustments, so a new adjustment-delivered file IS exercised by the guard.

## Plugin skills sync (tooling/sync_plugin_skills.py)

- Copies `features/common/skills/` + `features/claude/skills/` into `skills/` — the one directory Claude Code reads for a plugin (ADR-0008). NEVER copies `features/<agent>/`, so agent-specific modules cannot leak into the plugin by
  construction.
- `--check` mode exits 1 on divergence — verify without writing.
- SKILL.md body is replaced by a pointer; the full text goes to SKILL.full.md.
- No CI gate covers this; running it is a manual/plan step.

## Test-fixture facts (tests/conftest.py)

- `load_script` imports by dotted repo-relative path (`features.common.skills.task.scripts.tracker_lib`). The dotted form is load-bearing for mutmut (mutant dispatch matches `__module__` exactly — do NOT rename the form).
- Each `load_script` call creates a FRESH module instance, BUT `import tracker_lib` inside a loaded script resolves via sys.path to a top-level module that is CACHED in
  `sys.modules` and SHARED across tests. Registering anything on that shared module (e.g. a session-source registry) persists across tests — isolate/restore per test.
- The `tt`-style fixture prepends `features/common/skills/task/scripts` to sys.path; the committed `.ai-badger/` copies are NOT on sys.path, so a guarded `import session_sources`
  is inert in tests unless the test loads the agent module explicitly and calls
  `register(lib)` with the exact lib object the CLI uses (the top-level cached one).
- The lint gate (`git ls-files '*.py' | grep -v '^tests/'`) covers the tracked `.ai-badger/`
  AND `skills/` copies — modules moved/copied into them need their own
  `# pylint: disable=missing-function-docstring,invalid-name` headers.
- Test the guarded import's PRESENT branch too, not just the absent one: write a sibling
  `session_sources.py` into a tmp dir, `monkeypatch.syspath_prepend` it, load the common module, and assert the registry gained the probe source. Every test that loads the common scripts dir without such a file only exercises the
  `ImportError` branch — the adjustment-delivery test (`test_adjust_task_hermes.py`) pins `register(lib)` but the auto-registration-on-import path is separate and needs its own pin (reviewer finding P3 on PR #301).
- Test-fixture isolation: `SESSION_SOURCES` lives on the module-cached top-level
  `tracker_lib`, so a `register()` from one test persists into the next. The fixture must
  `monkeypatch.setattr(module.lib, "SESSION_SOURCES", {})` per test to give each test its own registry.

## Repo invariants (from .ai-badger/CLAUDE.md)

TDD mandatory (failing test before production code), one PR per task, always bump VERSION +
`docs/changelog/{version}-{slug}.md`, minimal comments (1-3 line contracts), plain names, done-means-proven, "ask if a simpler shape would do" before calling a design finished.

## Release guard forces a VERSION bump on shipped-surface changes

`gates/release_guard.py` (a pre-push lane) fails when the shipped surface changed since the last `ai-badger--v<tag>` and `VERSION` is unchanged. Any framework change that lands on a branch that already carried a tag (e.g. a review-fix on a
PR whose feature commit was tagged)
needs a bump: `echo x.y.z > VERSION`, then `tooling/version_sync.py` (propagates into
`.claude-plugin/plugin.json`, `marketplace.json`, `index.json`), `tooling/changelog_index.py`
(regenerates `docs/changelog/README.md`), re-scaffold + `sync_plugin_skills.py` again (the new version re-stamps agent files and plugin copies), then commit as its own
`release: x.y.z —` commit so the fix commit stays a fix. The freshness guard exempts stamp churn, so re-scaffolding after the bump does not fail it.

## Pre-push gate python resolution (worktree gotcha)

`.lefthook/pre-push/verify.sh` `_resolve_python()` picks `$PWD/.venv/bin/python3` FIRST, then the main checkout's venv (parent of the git common dir), then `command -v python3`. A linked worktree that has a bare/broken `.venv` (e.g. one
created with no pip and no deps) SHADOWS the main checkout's working venv and every lane fails with
`ModuleNotFoundError: No module named 'jsonschema'` — including lanes that pass when run by hand with the main venv. Fix: `rm -rf <worktree>/.venv` so the fallback to the main checkout's venv engages (the main checkout's `.venv` carries
jsonschema + pytest). System `/usr/bin/python3` (macOS CommandLineTools 3.9) lacks jsonschema, so run pytest and the gate lanes with the main checkout's venv python explicitly:
`/Users/arasz/RiderProjects/ai-badger/.venv/bin/python -m pytest -q`.

## Plan docs must not pollute the freshness guard

The guard copies tracked + untracked-not-ignored paths (`git ls-files -co
--exclude-standard`) into the throwaway tree and flags anything the re-scaffold does not write. A plan doc written to `.hermes/plans/` (untracked, unignored in this repo) becomes a finding ("the re-scaffold no longer writes it"). Put
working plans in `.tmp/` (gitignored, the repo's documented home for plans/reviews/designs) instead of `.hermes/plans/` when working in this repo.
