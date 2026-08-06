# Hermes Skill Discovery via Namespaced Global Directory

## Problem

ai-badger scaffolds skills into `.ai-badger/skills/`. Hermes discovers skills from:
1. `~/.hermes/skills/` — bundled and hub-installed skills (always scanned)
2. `skills.external_dirs` — explicitly configured directories in `~/.hermes/config.yaml`

Project-local `.hermes/skills/` is NOT auto-discovered. Using `external_dirs` for
multiple projects causes skill name conflicts — every project has a `task` skill, and
last-match-wins semantics means the wrong project's skill gets loaded.

## Solution: namespace per project

Symlink each project's skills into `~/.hermes/skills/<project-name>/` as a namespace
directory. Each project gets isolated skills that don't conflict with other projects.
No `external_dirs` changes needed.

```
~/.hermes/skills/
  ai-badger/              # namespace for ai-badger project
    task -> /path/to/ai-badger/.ai-badger/skills/task
    prompt-markers -> ...
  arasz-home-page/        # namespace for arasz-home-page project
    task -> /path/to/arasz-home-page/.ai-badger/skills/task
    prompt-markers -> ...
  job-search-ai-assistant/
    task -> ...
```

Skills are picked up on the next Hermes session start (`/reset` or fresh `hermes`).

## Implementation in scaffold.py

```python
def symlink_hermes_skills(self) -> None:
    """Symlink project skills into ~/.hermes/skills/<project>/."""
    if "hermes" not in self.config.get("agents", []):
        return
    project_name = self.config.get("project", {}).get("name", "unknown")
    global_skills = Path.home() / ".hermes" / "skills"
    global_skills.mkdir(parents=True, exist_ok=True)
    namespace_dir = global_skills / project_name
    if namespace_dir.is_symlink() or namespace_dir.exists():
        if namespace_dir.is_dir() and not namespace_dir.is_symlink():
            import shutil
            shutil.rmtree(namespace_dir)
        else:
            namespace_dir.unlink()
    namespace_dir.mkdir(parents=True, exist_ok=True)
    for skill_name in self.skills:
        src = self.aib / "skills" / skill_name
        dst = namespace_dir / skill_name
        if not src.is_dir():
            continue
        dst.symlink_to(os.path.relpath(src, dst.parent))
```

## Why NOT external_dirs

`external_dirs` is a global shared list. When multiple projects register their
`.hermes/skills/` paths:
- All projects' skills are merged into one flat namespace
- Skills with the same name (e.g., `task`) conflict — last-match-wins
- Test paths from pytest runs leak into the global config
- Stale entries accumulate when projects are deleted

The namespace approach avoids all of these by giving each project its own directory.

## Coverage

- **welcome-ai-badger scaffold**: Creates namespace + symlinks when `hermes` is in `config.agents`
- **den-refresh re-scaffold**: Recreates namespace via `Scaffolder.run()`
- **Idempotent**: Namespace dir is recreated on every run

## Pitfalls

1. **`.hermes/skills/` in project dir is unused.** The scaffold does NOT create
   `.hermes/skills/` in the project directory anymore. Skills go directly to
   `~/.hermes/skills/<project>/`.

2. **Project name must be unique.** The namespace is `config.project.name`. If two
   projects have the same name, their skills will overwrite each other.

3. **Skills available on next session start only.** After scaffolding, skills don't
   appear in the current session — the user must `/reset` or start fresh `hermes`.

4. **Stale namespaces from deleted projects.** If a project is deleted, its namespace
   dir in `~/.hermes/skills/` persists (pointing at dead symlinks). Hermes silently
   skips broken symlinks, but manual cleanup is recommended.
