# Bulk Refactoring Workflow for ai-badger

Patterns learned from moving `skills/` to `features/common/skills/` and adding
the scaffolding.json concept (PRs #37, #38).

## Plan-review-before-implementation (mandatory for multi-file refactors)

1. **Gather knowledge**: search ALL references to the old state across `.py`,
   `.md`, `.json`, `.gitignore`, CI workflows. Use `search_files` with multiple
   patterns.
2. **Write detailed plan** to `.hermes/plans/<slug>.md` with:
   - Step-by-step tasks (each 2-5 min)
   - Complete file lists (create/move/modify/delete)
   - Risks and mitigations
3. **Delegate plan review** to a sub-agent before ANY implementation:
   ```python
   delegate_task(
       goal="Review the refactoring plan for completeness",
       context="<plan + file paths + constraints>",
       role="leaf"
   )
   ```
   The reviewer should grep the codebase for missed references. In the skills
   move, the review found 11 missing files including a critical `hooks/hooks.json`
   path that would have caused silent runtime failures.
4. **Incorporate findings**, then start TDD implementation.

## TDD enforcement for refactors

When the user says "use tdd so first cover with tests":
1. Write the failing test FIRST (RED)
2. Confirm it fails with the right error
3. Write the minimal code to make it pass (GREEN)
4. Refactor if needed

Do NOT write the code and then the test. Do NOT batch multiple fixes into one
commit without individual test verification.

## Comprehensive reference sweep checklist

After a path move (`git mv old/ new/`), search for ALL of these:

| Pattern | Where to search |
|---------|----------------|
| `load_script("old/...")` | `tests/*.py` |
| `root / "old"` | `tests/*.py` |
| `"old/"` in strings | `*.py`, `*.md`, `*.json` |
| `${VAR}/old/` | `hooks/*.json`, `*.json` |
| `old/` in docstrings | `*.py` |
| `old/` in mermaid diagrams | `*.md` |
| `old/` in CLI examples | `*.md`, `SKILL.md` |
| `old/` in extension.json | `features/*/skills/*/` |
| `old/` in stack.json | `features/*/` |

**Critical files often missed:**
- `hooks/hooks.json` — plugin hook paths (silent failure if wrong)
- Extension `.md` files — prose references to paths
- Test fixtures that create mock framework trees
- `release_guard.py` SHIPPED_PATHS
- `conftest.py` and test helper fixtures

## Symlink relative path pitfall

When creating symlinks from `features/<agent>/templates/` to
`features/common/templates/`:

```
features/claude/templates/   # depth 2 from features/
features/common/templates/   # also depth 2 from features/
```

Correct: `ln -s ../../common/templates/FILE features/agent/templates/FILE`
Wrong: `ln -s ../../../common/templates/FILE features/agent/templates/FILE`

**Always verify**: `cat <symlink> | head -1` after creating.

## Index rebuild after structural changes

After `git mv` or creating new feature directories:
```bash
python3 scripts/index_build.py        # rebuild
python3 scripts/index_build.py --check # verify
```

Tests using the real framework root will fail until the index is rebuilt because
`scaffold.py` reads `index.json` to find skill paths.

## Mock framework test updates

When adding new framework files (e.g., `scaffolding.json`), tests that create
mock framework trees must include the new files. Pattern:

```python
# Add scaffolding.json to mock framework
(fw / "features" / "hermes").mkdir(parents=True)
(fw / "features" / "hermes" / "scaffolding.json").write_text(json.dumps({...}))
(fw / "features" / "hermes" / "templates").mkdir(parents=True)
# Copy template (don't symlink in tmp_path — paths differ)
(fw / "features" / "hermes" / "templates" / "HERMES.md.tmpl").write_text(...)
# Copy schema needed for validation
shutil.copyfile(root / "schemas" / "scaffolding.schema.json",
                fw / "schemas" / "scaffolding.schema.json")
```

## Code review sub-agent (end of task)

After implementation, delegate a code review:
```python
delegate_task(
    goal="Comprehensive code review of the refactoring",
    context="<what changed + files to review + what to check>",
    role="leaf"
)
```

Check: remaining stale references, schema soundness, edge cases, test gaps,
symlink correctness, backward compatibility.

## Documentation update after path moves

After `git mv` or structural changes, docs fall into two categories:

**Living docs (UPDATE):**
- `docs/framework-architecture.md` — architecture, paths, data flow
- `docs/authoring-a-feature.md` — how-to guides with path references
- `docs/scripts.md` — CLI examples with script paths
- `README.md` — overview, architecture diagram

**Historical docs (DO NOT change):**
- `docs/adr/*.md` — decisions at time of writing; changing them falsifies history
- `docs/ai-badger-framework-design.md` — original design doc; treat as historical
- `docs/known-gaps.md` — snapshot; update only when gaps are actually resolved

**Pattern:** After the path sweep, grep `*.md` in `docs/` for the old path.
Update living docs. Leave ADRs and design docs untouched — they document
decisions at the time they were made.

## Scaffolding.json edge cases

**Pitfall**: `managed=False` + `template=True` must write the RENDERED content,
not `shutil.copyfile(source, target)`. The raw `.tmpl` file contains
`{{PLACEHOLDER}}` syntax that is useless to the target project. Fix:

```python
if managed:
    self._copy_with_header(target, entry["target"], content)
else:
    target.parent.mkdir(parents=True, exist_ok=True)
    if is_template:
        target.write_text(content, encoding="utf-8")  # rendered!
    else:
        shutil.copyfile(source, target)  # raw copy OK for non-templates
```

Same pattern applies to `alsoTarget` — don't `copyfile` a `.tmpl` there either.

**Pitfall**: `seedOnce=True` with `alsoTarget` — when `seedOnce` triggers
(skip because target exists), the `continue` also skips writing `alsoTarget`.
This is correct behavior (both copies represent the same logical file) but the
schema should document it: "When combined with alsoTarget, both copies are
skipped together."
