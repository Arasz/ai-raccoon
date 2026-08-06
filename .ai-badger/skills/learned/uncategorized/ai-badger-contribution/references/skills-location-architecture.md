# Skills Location Architecture

## Current state (post-refactor)

Skills live at `features/<stack>/skills/` — discovered by `iter_feature_dirs` like
any other stack feature. No special-casing needed. Common skills live at
`features/common/skills/`, agent-specific skills at `features/<agent>/skills/`
(e.g. `features/claude/skills/auto-wm/`).

### Cross-stack skill discovery

`default_skills_in(skills_dir)` in `badger_lib.py` checks whether a skill
directory has a `SKILL.md`, is declared in `SKILL_SCOPES` with scope `"default"`,
and actually exists. Both `DEFAULT_SKILLS` in `scaffold.py` and the skill-list
assembly in `refresh.py` use `bl.iter_feature_dirs()` to scan ALL stack
directories, not just common. This means a skill declared in
`features/claude/skills/` is discovered alongside `features/common/skills/`.

### How index_build.py handles it

Single-pass via `iter_feature_dirs`: scans `features/<stack>/<feature>/` for all
features including skills. The old hardcoded root skills injection (lines 95-100)
was removed.

### Scaffolding.json

Each agent has `features/<agent>/scaffolding.json` that declares what files to
scaffold. `scaffold.py` reads these via `_apply_scaffolding()` — no hardcoded
fallback. Schema at `schemas/scaffolding.schema.json`.

### Extension mechanism

Skill extensions (e.g., `task` → `github` or `hermes`) live under
`features/<stack>/skills/<base>-extensions/<ext>/`. `index_build.py` merges them
into the base skill's `extensions` array. `scaffold.py` embeds extensions into
the scaffolded skill directory when config requirements are met.

### Template symlinks

Agent templates are symlinked from `features/<agent>/templates/` to
`features/common/templates/`. Example:
```
features/claude/templates/CLAUDE.md.tmpl -> ../../common/templates/CLAUDE.md.tmpl
```

Symlink depth: `features/<agent>/templates/` is 2 levels from `features/`, so
the relative path to `features/common/templates/` is `../../common/templates/`.

### Hooks/hooks.json

The drift notice hook path is:
`${CLAUDE_PLUGIN_ROOT}/features/common/skills/task/scripts/drift_notice_hook.py`

### drift_notice_hook.py find_plugin_root

Walks ancestors looking for `VERSION` + `features/common/skills/` (not `skills/`).
