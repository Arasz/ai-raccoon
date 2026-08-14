---
name: spec-driven-refactoring
description: "Use when the user says 'refactor', 'migrate', or 'rename across the codebase', or a change touches 5+ files across schemas, scripts, tests, and docs: write a spec, run two review gates (pre-implementation consistency + post-implementation quality), then implement against it. Covers schema migrations, concept renames, structural reorganizations."
version: 1.0.0
author: ai-badger
license: MIT
platforms: [linux, macos, windows]
scope: optIn
metadata:
  hermes:
    tags: [refactoring, specs, planning, migration]
    related_skills: [refactor-safely, scripts-tooling-refactor]
---

# Spec-Driven Refactoring

Large refactors (schema migrations, concept renames, structural reorganizations) have more
surface area for stale assumptions than normal features. This workflow adds two review gates
to catch inconsistencies before they compound.

## When to use

- Multi-file structural changes (5+ files across schemas, scripts, tests, docs)
- Schema migrations (field renames, type changes, file relocations)
- Concept renames that ripple through the entire codebase
- Any refactor where "I thought X was at line Y" mistakes would be expensive

## Procedure

### 1. Analyze

Read all affected files. Identify every contract the refactor touches:
- Schemas (JSON Schema, TypeScript interfaces, etc.)
- Scripts (build, scaffold, validate, install)
- Tests (which test files reference the old structure)
- Docs (architecture docs, authoring guides, README)

### 2. Write spec

Produce a markdown specification covering:
- **Problem statement** — what's wrong and why
- **Design decisions** — numbered, explicit choices with rationale
- **Proposed structure** — new/modified/removed files in a table
- **Schemas** — full JSON Schema definitions for new contracts
- **Implementation order** — phased, with validation gates between phases
- **Risks** — what could go wrong

Save to `docs/specs/` (or project equivalent).

### 3. Spec review

Delegate a review sub-agent. Feed it the spec + all key context files. Ask it to check:
- Logical gaps or missing pieces
- Naming consistency (especially across old→new renames)
- Migration path correctness
- Schema completeness and self-consistency
- Implementation order dependencies (does Phase 2 need Phase 1's output?)

Categorize findings: Critical / Medium / Low. Return GO/NO-GO with conditions.

### 4. Fix spec findings

Address all Critical and Medium findings. Low findings can be deferred to implementation
but should be tracked in the spec's review tracker.

### 5. Pre-implementation consistency check

Delegate a SECOND sub-agent to verify the spec's claims against the actual codebase:
- Do claimed line numbers match?
- Do file paths exist?
- Do schema fields match the spec's descriptions?
- Do test files reference the paths the spec claims?
- Are there files the spec forgot to mention?

This catches stale assumptions the spec author made during analysis.

### 6. Fix consistency findings

Update the spec and any affected files (e.g., aligning agent enum ordering).

### 7. Implement

Follow the spec's implementation order. Between each phase:
- Run the project's validation/test commands
- Verify the phase's changes don't break the next phase's prerequisites

### 8. Post-implementation consistency + quality review

**First: establish filesystem ground truth.** Before reading any files:
1. `git branch --show-current` — confirm you're on the refactoring branch, not `main`
2. `git status --short` — confirm working tree matches expectations
3. `git diff main..<branch> --stat` — get the actual file list

Use `terminal` for these, NOT `execute_code`/`read_file` — those tools may operate in a
sandboxed environment with pre-seeded content that doesn't match the real filesystem. A
review built on sandbox data is worthless. See pitfalls below.

Then verify the completed implementation:
- All new schemas validate
- Script pipeline aligns (discovery ↔ index ↔ validation)
- Docs match the actual code
- Every new contract has tests
- No dead code or stale references to removed structures
- Existing tests still pass

Fix findings before merging.

### 9. Verify scaffold works end-to-end

Run the project's scaffold/refresh tool against a real target (or the project itself)
to verify the refactored scaffolding actually works:
- den-refresh / welcome-ai-badger / equivalent
- Verify symlinks, config registration, and manifest generation
- Check that the scaffolded output validates against the updated schemas

This catches integration issues that unit tests miss (e.g., a schema change that
passes validation in isolation but breaks scaffold.py's manifest generation).

### 10. Issue management

Check the project's issue tracker for issues addressed or invalidated by the refactor:
- Update open issues with status comments (what was fixed, what remains)
- Close issues that are fully resolved
- Note partial progress on multi-step issues

### 11. File move reference sweep

When moving files (not just renaming fields), search ALL references:
- Test files (`load_script("old/path")`)
- Doc files (installation instructions, path examples)
- Cross-referencing files (extension.md pointing at hook scripts)
- CI config (workflow files referencing paths)

Use `grep -r "old/path" .` to catch what IDE cleanup misses.

Explicit checks the quality review sub-agent should run:
- **Orphaned test files** — every test file must have a corresponding implementation file. If `tests/test_foo.py` exists but `scripts/foo.py` doesn't, either remove the test or implement the script.
- **Schema enum ↔ code constant alignment** — manifest entry feature enum, validate KIND_TO_SCHEMA keys, and index.schema.json feature keys must all match `badger_lib.FEATURES`. Mismatches are silent until a scaffold run hits them.
- **Docstring accuracy** — validate.py's docstring lists `--kind` options; if they don't match `KIND_TO_SCHEMA`, the CLI help is wrong.
- **Dead code from removed features** — after removing a feature, search for deprecated functions, branches, and references. `grep -r "old_feature" scripts/ tests/` catches what IDE cleanup misses.
- **Scaffold version propagation** — after re-scaffold, does the config's frameworkVersion update? If not, the next drift detection will re-scaffold unnecessarily.

## Gotchas
- **`execute_code`/`read_file` sandbox may show pre-seeded content, not real files.** When
  reviewing a refactoring on a feature branch, the sandbox environment may be populated with
  mock data for the *target* state rather than reflecting the actual filesystem. You can build
  an entire multi-step analysis — schema validation, test results, file existence checks — on
  phantom data. **Always use `terminal` for ground-truth filesystem checks** (git status,
  ls, cat, python3 -m pytest). If `execute_code` returns file contents that `terminal` says
  don't exist, trust `terminal`. Caught when: review of refactoring branch produced 20+ tool
  calls of fabricated analysis before discovering the real repo was on `main`.

- **jsonschema `$ref` to sibling files fails without a custom resolver.** The Python
  `jsonschema` library's `Draft202012Validator` cannot resolve relative `$ref` paths like
  `"$ref": "agents.schema.json#/$defs/agentName"` without a custom `RefResolver`. Either
  inline the enum in every schema or provide a resolver that maps URIs to local files.
  Discovered via: test failure `Unresolvable: agents.schema.json#/$defs/agentName`.

- **Schema renames must happen atomically with code that writes the old field.** Renaming
  `pluginScope` → `skillScope` in a schema breaks any script that still writes `pluginScope`.
  Do the rename + script update in the same phase, or the intermediate state fails validation.

- **Agent enum ordering drifts silently.** When the same enum appears in multiple schemas,
  different files end up with different orderings (e.g., `junie, hermes` vs `hermes, junie`).
  This doesn't affect validation but creates confusion about the canonical order. Align once
  and add "keep in sync" comments.

- **Phase ordering matters: remove old data before running validation.** If you update a
  schema to remove a feature key (e.g., `plugins`) but don't remove the data directory that
  produces those entries (e.g., `features/*/plugins/`), the next `index_build.py` run will
  produce entries the updated schema rejects. Remove old data in the same phase as the schema
  change.

- **Two extension.json files can exist for different scopes.** A refactor that removes
  agent-level extension.json must not accidentally remove stack-level extension.json.
  Verify every file path individually.

- **Manifest schema transitions need both old and new fields.** When renaming a field
  in `manifest.schema.json` (e.g. `pluginScope` → `skillScope`), the manifest is
  written by `scaffold.py`. If you rename the schema field without updating scaffold.py
  in the same commit, validation fails. The safe pattern: (1) add the new field to
  manifest.schema.json alongside the old one, (2) update scaffold.py to write both
  during transition, (3) commit both together, (4) remove the old field in a follow-up
  once all scaffolded projects have migrated.

- **User may add scope mid-implementation.** During a long refactor, the user may
  request additional features (breaking version support, new docs, safety wrapping).
  Incorporate these into the current PR if they're closely related; otherwise note
  them as follow-up PRs. Don't block the current work waiting for perfect scope.

- **"Ensure X always Y" is a non-negotiable invariant.** When the user says
  "ensure that hooks will always catch an exception and log it" or similar safety
  directives, treat it as a hard requirement — implement it across ALL entry points,
  test it, and don't defer it. The pattern: wrap every public hook/adjustment function
  in `try/except Exception` with `logger.debug(..., exc_info=True)` so a broken hook
  never crashes the host process. Apply to hooks, adjustments, and plugin scripts.

- **Scaffolded config.frameworkVersion must be updated after re-scaffold.** When a
  scaffolding/refresh tool re-scaffolds a project, it updates `manifest.json` with the
  current framework version but often preserves the original `config.json`'s
  `frameworkVersion`. This causes the next drift detection to re-scaffold again
  unnecessarily. Fix: update `config.frameworkVersion` to the current version after
  a successful re-scaffold. Caught when: den-refresh re-scaffolded but config still
  showed the original 0.2.0 scaffold version.

- **Version + changelog on EVERY commit that touches production code.** User explicitly
  requires: bump VERSION (semver), add `docs/changelog/{version}-{slug}.md`, and enforce
  as an invariant. No exceptions — even small fixes get a patch bump. The invariant file
  at `features/common/invariants/version-changelog-required.md` codifies this.

- **Hermes skills must be namespaced per project, NOT in shared external_dirs.** Hermes
  does NOT auto-discover `.hermes/skills/` in the CWD. It only scans `~/.hermes/skills/`
  (global) and `skills.external_dirs` in `~/.hermes/config.yaml`. Using `external_dirs`
  for multiple projects causes skill name conflicts (e.g., every project has a `task` skill)
  with last-match-wins semantics. The correct approach: symlink each project's skills into
  `~/.hermes/skills/<project-name>/` as a namespace directory. Each project gets isolated
  skills that don't conflict with other projects. Do NOT register in global `external_dirs`.
  Caught when: the maintainer-home-page's task skill was shadowed by the reference repo's older
  task skill because the latter was last in external_dirs.

- **Test isolation for config file writes.** When testing code that writes to
  `~/.hermes/config.yaml`, mock `Path.home()` to return a temp dir AND create the
  `.hermes/` subdirectory structure in the temp dir. The method resolves
  `Path.home() / ".hermes" / "config.yaml"`, not `Path.home() / "config.yaml"`.
  Caught when: test paths leaked into the real config because the temp dir structure
  was wrong.

- **Breaking version semver boundary is exclusive on the lower bound.**
  `is_breaking_transition(from, to, root)` checks `from_v < breaking <= to_v`. A
  project already at the breaking version (e.g., 0.7.0 → 0.8.0 with 0.7.0 as
  breaking) does NOT trigger — because `0.7.0 < 0.7.0` is false. This is correct:
  the project was scaffolded at or after the breaking version, so no migration is
  needed. Only projects crossing the boundary (e.g., 0.6.0 → 0.8.0) need the full
  re-scaffold with backup.

## Python mixin-based module split

For splitting a single large Python file into domain-specific modules while preserving the
public API — read `references/python-mixin-module-split.md` when splitting a Python module. Covers the mixin inheritance pattern,
dynamic script loading (`sys.path`), circular import handling, and multi-copy sync.

## Feature design spec patterns

When writing a **feature design specification** (not a refactoring spec), see
`references/feature-design-spec-patterns.md` for section structure, requirement ID conventions,
domain type documentation, testing strategy, and acceptance criteria patterns observed in
DDD/.NET projects.

## Multi-PR sequential merge pattern

For large refactors (8+ phases), split into independently-mergeable PRs. See
read `references/multi-sequential-pr-pattern.md` for the full workflow when the change needs multiple sequential PRs.

## References

- `references/plugin-to-skills-migration.md` — worked case: migrating a plugin layout to per-stack skills; read when migrating a plugin layout.
