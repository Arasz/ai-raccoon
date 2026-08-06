# Invariant subsystem, config-schema constraints & release mechanics

Verified 2026-08-06 on 0.80.0 (issue #313 review phase, task/issue-313-project-local-invariants). All paths under `features/common/skills/welcome-ai-badger/scripts/` unless noted; root `skills/`
is a symlink farm to `features/common/skills/` (both spellings identical — edit the features one).

## Invariant scaffolding flow

- `collect_invariants()` (scaffold.py:443-451): for each stack, for item in
  `self.items(stack, "invariants")`: `copy_file()` into `.ai-badger/invariants/`, then
  `demote_headings(text)`, append to the returned rendered list.
- `items()` (scaffold.py:299-303) = `bl.feature_items(index, stack, feature)` minus config.exclude names for that feature.
- `run()` line 690 calls `collect_invariants()`; the rendered list feeds
  `rendering.write_delegation_map` (699), `assemble_instructions_doc` (701),
  `agent_files.write_agent_files` (702).
- `compute_doc_slots` (template_rendering.py:119): `inv_md = "\n\n".join(invariants)` or
  `"_None yet._"` — ONE `INVARIANTS` slot shared by CLAUDE.md.tmpl, HERMES.md.tmpl (`{{INVARIANTS}}` at line 11 of both) and delegation.md.tmpl.
- `demote_headings`: H1→H3, H2→H4, fenced-code-aware (test_invariant_rendering.py). Catalog convention: every `features/*/invariants/*.md` source leads with a single H1;
  `common` invariants must NOT assert repo-file layout (docs/authoring-a-feature.md generalization test).

## Manifest entries & drift coherence (the "can we record it?" test)

- `record()` (scaffold.py:339-378) builds `{feature, stack, name, source:
  source.relative_to(self.root), target, hash, frameworkVersion}` — **source must live under the framework root**.
- drift.compare for FILE entries (drift.py:466) re-hashes `root/source_rel` against the entry hash → "changed"; a missing source → "removed" (drift.py:442-444). A manifest entry pointing at a project-owned file is incoherent unless drift.py
  is also taught to handle it.
- **Config edits are self-executing drift**: manifest `configHash` (scaffold.py:737,
  `bl.config_hash`) vs `_config_drift` (drift.py:363-382) → `configChanged` → den-refresh re-scaffolds (#128). A new config key reaches projects with NO manifest machinery.
- File features mark `hashes_source` (ADR-0006): record () hashes the framework SOURCE because drift re-hashes the source; any other choice can never match.
- Directory entries (skills) carry `hash` (target) + `sourceHash` (source) — two questions, two hashes (#110).

## Config schema constraints (schemas/config.schema.json)

- **Top-level `additionalProperties: false`** (line 13) — every new top-level key needs an explicit property or validation refuses.
- `exclude.invariants` exists (lines 199-206); `include` currently has ONLY `skills`
  (163-177). `required`: frameworkVersion, project, stacks, agents.
- Schema is draft 2020-12; validated by jsonschema (required dep) — invalid config refuses, never silently passes.

## Release mechanics (RELEASING.md, verified)

- **0.MINOR** = anything that changes what scaffolding does to a consumer repo: removed/ renamed features, changed target paths, changed hook contracts, changed detection, **new schemas**, new feature types. A new config key is a schema
  change → minor bump.
- `BREAKING_VERSIONS` gets the version ONLY when a re-scaffold is REQUIRED (den-refresh then backs up `.ai-badger/` → `.ai-badger.bckp/` and full-scaffolds). An additive optional key is not breaking.
- Cut order: edit `VERSION` → add `docs/changelog/{version}-{slug}.md` →
  `python3 tooling/changelog_index.py` (regenerates the README release table — never hand-edit it) → `python3 tooling/version_sync.py` (propagates to
  `.claude-plugin/plugin.json`, `.claude-plugin/marketplace.json`, `index.json`) → gates:
  `version_sync.py --check` + `changelog_index.py --check` + `gates/release_guard.py` → full pytest + pylint.
- `release_guard.py` compares against the **last release TAG** (not the previous commit) and refuses a VERSION below the last tag; multiple PRs may land at one unreleased version, tag once. The tag (`ai-badger--v{version}`) is cut
  automatically by a workflow after merge — verify it reached the remote (`git ls-remote --tags origin`), never assume.
- Changelog entry style: `# 0.80.0 — <slug title>` + bullets + a
  "**What a consumer must know:**" paragraph (see docs/changelog/0.80.0-hermes-plugin-fix.md).
- 2026-08-06 state: last tag `ai-badger--v0.80.0`, VERSION 0.80.0 — 0.81.0 was a clean next release for the issue-#313 feature.

## Issue #313 context (project-local invariants: add, not just exclude)

- Use case: AiRaccoon project rule "static classes only for extensions/constants/pure functions; stateful/IO/deps → injectable component" — no scaffold mechanism to ADD an invariant today.
- Existing workaround (keep markers) is confirmed suboptimal: `carry_keep_regions`
  (_shared.py:86-94) appends preserved regions at the END of the regenerated body, outside the `## Non-negotiable invariants` section.
- Candidate shapes weighed at review: (a) config key — rides configHash self-execution, needs an explicit schema property; (b) convention dir `.ai-badger/invariants/local/` — never touched by copy_file (it writes only `src.name` into
  `invariants/` directly), so collision-free and manifest-free. Both work for welcome-ai-badger AND den-refresh (same scaffold.py code path).
