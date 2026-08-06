# Spec Review Gate Pattern

When starting a large refactor or multi-phase task, delegate the specification/plan to a review sub-agent before implementing. This catches structural issues cheaply.

## How it works (three gates)

### Gate 1: Spec review (structural)
1. Write the spec/plan document (markdown, with schemas, file lists, implementation order)
2. Delegate a review sub-agent with: the spec path, all key context files (schemas, scripts, architecture docs), and specific review criteria
3. Review criteria should include:
   - Logical gaps or missing pieces
   - Naming consistency
   - Phase ordering / dependency correctness
   - Schema completeness and self-consistency
   - Edge cases (e.g. empty arrays, missing files, cross-file references)
   - Integration with existing scripts (not just data structure design)
4. Incorporate findings before implementation begins

### Gate 2: Pre-implementation consistency check (factual)
After Gate 1 findings are fixed, delegate a SECOND sub-agent to verify the spec's claims against the actual codebase:
- Do claimed line numbers match? (e.g., "scaffold.py line 460" — is install_plugins() really there?)
- Do file paths exist? (e.g., spec says "scripts/scaffold.py" but it's at "features/common/skills/welcome-ai-badger/scripts/scaffold.py")
- Do schema fields match the spec's descriptions?
- Do test files reference the paths the spec claims?
- Are there files the spec forgot to mention? (e.g., manifest.schema.json also has the field being renamed)

This catches stale assumptions the spec author made during analysis. Gate 1 checks logic; Gate 2 checks facts.

### Gate 3: Post-implementation quality review (integration)
After all implementation phases complete, delegate a final sub-agent to verify:
- All new schemas validate against their own definitions
- Script pipeline aligns (discovery ↔ index ↔ validation ↔ schema)
- Docs match the actual code structure
- Every new contract has test coverage
- No dead code or stale references to removed structures
- Existing tests still pass

Fix findings before merging.

## Example Gate 1 findings (from ai-badger 0.7.0 refactor)

The review caught these issues that would have been expensive to fix during implementation:

- **C1: Naming contradiction** — "merge plugins into skills" but introduced `plugins-instructions.json`
- **C2: Missing schema** — `hooks-manifest.json` had no schema (breaks framework pattern)
- **C3: Schema not updated** — `index.schema.json` needed new feature keys
- **C4: Phase ordering** — data files created before scripts could validate them
- **M1: Undefined integration** — `scaffold.py` had existing `install_plugins()` that needed refactoring
- **M2: Missing wiring** — adjustments not wired into scaffold pipeline

## Template for review sub-agent prompt

```
Review the specification at <path> for completeness, consistency, and correctness.

Key context files: <list all relevant files>

Review criteria:
- Are there logical gaps or missing pieces?
- Is the naming consistent?
- Does the migration/transformation path make sense?
- Are the schemas complete and self-consistent?
- Is the implementation order correct (dependencies between phases)?
- Are there edge cases not covered?
- Does it integrate with the existing script pipeline?
- Any conflicts with the existing codebase structure?

Return: issues found (Critical/Medium/Low), suggestions, go/no-go recommendation.
```

## Example Gate 2 findings (from ai-badger 0.7.0 refactor)

Gate 2 caught facts that Gate 1 missed because it checked logic, not file paths:

- **scaffold.py path wrong** — spec said `scripts/scaffold.py` but actual path is `features/common/skills/welcome-ai-badger/scripts/scaffold.py`
- **manifest.schema.json also had `pluginScope`** — spec only mentioned config.schema.json
- **Two extension.json files existed** — spec only mentioned the hermes one, not the github one
- **Agent enum ordering drifted** — config.schema.json had `junie, hermes` while agents.schema.json had `hermes, junie`
- **Phase ordering created validation gap** — removing old plugins/ dir needed to happen before first validation run
- **Test hardcoded old path** — `test_mcp_index_hooks.py` pointed to the old hooks location

## Example Gate 3 findings (from ai-badger 0.7.0 refactor)

Gate 3 caught integration issues invisible to Gate 1 (logic) and Gate 2 (facts):

- **Orphaned test file** — `tests/test_install_plugins.py` (7 tests) existed but `scripts/install_plugins.py` was never created (deferred to next PR). Test file shouldn't have been merged with the implementation PR.
- **Manifest entry feature enum stale** — `manifest.schema.json` entries feature enum still had `"plugins"` and was missing `"hooks"`/`"adjustments"`. Scaffolded projects couldn't record hooks/adjustments entries.
- **validate.py docstring stale** — listed `--kind {config|manifest|index|plugin-entry|marketplaces}` but actual choices were different after the refactor.
- **Dead code** — `_plugin_items()` function and `elif feature == "plugins":` branch in `index_build.py` could never execute since `plugins` was removed from `FEATURES`.
- **Doc accuracy** — README.md, framework-architecture.md, and authoring-a-feature.md all referenced the removed `plugins/` structure, `marketplaces.json`, and `pluginScope`.

## Pitfalls

- **Gate 3 must use the real filesystem, not sandbox.** The `execute_code`/`read_file` sandbox may show pre-seeded content that doesn't match the actual branch state. Always use `terminal` for filesystem checks (git status, ls, pytest). See spec-driven-refactoring skill pitfalls for the full rationale.
