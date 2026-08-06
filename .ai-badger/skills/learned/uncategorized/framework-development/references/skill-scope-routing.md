# Skill Scope Routing — Call Graph

Traced via code-review-graph `callers_of`/`callees_of` queries. Updated 2026-07-28 after stack-local skill discovery (ADR-0010).

## Source of truth

`scripts/badger_lib.py`:

- `SKILL_SCOPES` dict — maps skill name → `"default"` or `"optIn"` (UNIVERSAL skills only)
- `SKILL_SCOPE_DEFAULT = "default"` / `SKILL_SCOPE_OPT_IN = "optIn"`
- `skill_scope(name)` — lookup helper
- `default_skill_names()` — all default-scoped names, global
- `default_skills_in(skills_dir)` — default-scoped skills that exist in a given directory
- `stack_local_skills(skills_dir)` — skills NOT in SKILL_SCOPES (stack-specific)
- `skills_for_stack(root, stack)` — combined: universal defaults for common, stack-local for others
- `feature_items(index, stack, feature)` — index lookup per stack
- `find_skill_in_stacks(index, stacks, name)` — locate a skill across multiple stacks
- `iter_feature_dirs(root)` — yields `(stack, feature, dir)` for all `features/<stack>/<feature>/`

## Consumers

### scaffold.py (welcome-ai-badger)

`DEFAULT_SKILLS = bl.default_skills_in(root / "features" / "common" / "skills")` — module-level constant, common-only. Used as CLI `--skills` default.

`Scaffolder.run()` discovers stack-local skills via `bl.stack_local_skills()` before calling
`scaffold_skills()`. The search uses `bl.find_skill_in_stacks()` to locate items across configured stacks.

`Scaffolder.run_adjustments()` filters skills by agent-relevant stacks before passing to adjustment scripts — stack-local skills are NOT sent to other agents.

### refresh.py (den-refresh)

`re_scaffold()` unions manifest skills with `default_skills_in(common/skills)`. Stack-local skills are discovered by `Scaffolder.run()`, not by re_scaffold (to respect config.exclude).

### sync_plugin_skills.py

`COMMON_SKILLS = bl.skills_for_stack(ROOT, "common")`,
`CLAUDE_SKILLS = bl.skills_for_stack(ROOT, "claude")` — single entry point.

## Call chain: Scaffolder

```
Scaffolder.__init__
  self.stacks = bl.resolve_stacks(config)
  self.skills = [s for s in skills if s not in self.excluded["skills"]]

Scaffolder.run()
  → discover stack-local skills via bl.stack_local_skills() per stack
  → scaffold_skills()
    → for skill_name in self.skills:
        bl.find_skill_in_stacks(index, stacks, name)
        record("skills", stack, name, src, dest)
  → run_adjustments()
    → filter skills by agent-relevant stacks before passing to adjust()
```

## Tests that exercise this path

- `test_auto_wm_is_not_a_universal_default` — auto-wm NOT in SKILL_SCOPES
- `test_scaffolder_discovers_stack_local_skill_for_configured_stack` — auto-wm scaffolded for claude
- `test_scaffolder_does_not_discover_stack_local_skill_for_other_stack` — auto-wm NOT for dotnet
- `test_stack_local_skill_not_symlinked_to_other_agent` — auto-wm NOT in copilot adjustments
- `test_default_skills_are_the_common_catalog_not_every_declared_scope` — DEFAULT_SKILLS == common
- `test_every_catalog_skill_is_reachable_by_a_declared_route` — common skills in SKILL_SCOPES
- `test_repo_plugin_copy_is_in_sync` — catches stale plugin copies after sync
