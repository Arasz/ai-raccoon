# Mock Framework Test Pattern for ai-badger Scripts

When testing ai-badger scripts that call scaffold.py or drift.py, create a
minimal mock framework directory with just enough structure for the target
script to function.

## Helper functions

```python
def _write_config(target, **overrides):
    """Write a minimal valid config.json to target/.ai-badger/."""
    aib = target / ".ai-badger"
    aib.mkdir(parents=True, exist_ok=True)
    config = {
        "$schema": "./schemas/config.schema.json",
        "frameworkVersion": "0.3.0",
        "project": {"name": "test-proj", "summary": "A test project", "domain": "testing"},
        "stacks": ["dotnet"],
        "agents": ["claude"],
        "sourceControl": {"platform": "none", "repoUrl": None, "projectUrl": None},
        "commands": {},
        "personaRouting": [],
        "pluginScope": "default",
        "docs": {},
    }
    config.update(overrides)
    (aib / "config.json").write_text(json.dumps(config), encoding="utf-8")
    return config


def _write_manifest(target, entries, version="0.3.0"):
    """Write a manifest.json to target/.ai-badger/."""
    aib = target / ".ai-badger"
    aib.mkdir(parents=True, exist_ok=True)
    manifest = {
        "$schema": "../schemas/manifest.schema.json",
        "frameworkVersion": version,
        "frameworkCommit": None,
        "frameworkDirty": False,
        "generatedAt": "2026-07-22T00:00:00Z",
        "agents": ["claude"],
        "pluginScope": "default",
        "entries": entries,
    }
    (aib / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
    return manifest


def _make_fw_file(fw, relpath, content="framework content v1\n"):
    """Create a framework feature file at relpath under fw."""
    p = fw / relpath
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(content, encoding="utf-8")
    return p


def _write_fw_index(fw, version="0.3.0"):
    """Write a minimal index.json to a mock framework so the Scaffolder can read it."""
    index = {
        "$schema": "./schemas/index.schema.json",
        "frameworkVersion": version,
        "stacks": {
            "common": {
                "invariants": [
                    {"name": "tdd", "path": "features/common/invariants/tdd.md"},
                ],
                "templates": [
                    {"name": "CLAUDE.md.tmpl", "path": "features/common/templates/CLAUDE.md.tmpl"},
                    {"name": "HERMES.md.tmpl", "path": "features/common/templates/HERMES.md.tmpl"},
                    {"name": "state.json", "path": "features/common/templates/state.json"},
                ],
            },
            "dotnet": {
                "personas": [],
                "invariants": [],
                "instructions": [],
            },
        },
    }
    (fw / "index.json").write_text(json.dumps(index), encoding="utf-8")
```

## Minimal mock framework setup

```python
def test_example(tmp_path, load_script, root):
    # 1. Create mock framework directory
    fw = tmp_path / "fw"
    fw.mkdir()  # CRITICAL: must mkdir before writing files inside
    (fw / "VERSION").write_text("0.3.0\n", encoding="utf-8")

    # 2. Copy real schema (needed for config validation)
    (fw / "schemas").mkdir()
    (fw / "schemas" / "config.schema.json").write_text(
        (root / "schemas" / "config.schema.json").read_text(encoding="utf-8"),
        encoding="utf-8",
    )

    # 3. Create templates directory (parent only, NOT CLAUDE.md.tmpl as dir!)
    (fw / "features" / "common" / "templates").mkdir(parents=True)
    (fw / "features" / "common" / "templates" / "CLAUDE.md.tmpl").write_text(
        "# {{PROJECT_NAME}}\n\n{{PROJECT_SUMMARY}}\n\n## Invariants\n\n{{INVARIANTS}}\n",
        encoding="utf-8",
    )
    (fw / "features" / "common" / "templates" / "HERMES.md.tmpl").write_text(
        "# {{PROJECT_NAME}}\n\n{{PROJECT_SUMMARY}}\n",
        encoding="utf-8",
    )

    # 4. Create feature files using helper
    src = _make_fw_file(fw, "features/common/invariants/tdd.md", "- TDD is mandatory.\n")

    # 5. Write minimal index.json (needed by Scaffolder)
    _write_fw_index(fw)

    # 6. Create mock project
    proj = tmp_path / "proj"
    config = _write_config(proj, frameworkVersion="0.3.0")

    # 7. Load and run the script under test
    my_script = load_script("skills/my-skill/scripts/my_script.py")
    rc = my_script.main(["--target", str(proj), "--root", str(fw)])
    assert rc == 0
```

## Scaffolding integration test helper

For tests that exercise `_apply_scaffolding()` with custom scaffolding.json
content, use a helper that creates a complete mock framework with the
scaffolding.json:

```python
def _make_test_framework(tmp_path, root, scaffolding_json=None):
    """Create a minimal framework tree with a test agent."""
    features = tmp_path / "features"
    test_agent = features / "test-agent"
    (test_agent / "templates").mkdir(parents=True)
    (test_agent / "templates" / "hello.md").write_text(
        "# Hello from test-agent\n", encoding="utf-8")
    (test_agent / "templates" / "hello.tmpl").write_text(
        "# {{PROJECT_NAME}} — hello\n", encoding="utf-8")
    if scaffolding_json:
        (test_agent / "scaffolding.json").write_text(
            json.dumps(scaffolding_json), encoding="utf-8")
    (test_agent / "stack.json").write_text(json.dumps({
        "name": "test-agent", "description": "test",
    }), encoding="utf-8")

    shutil.copytree(root / "schemas", tmp_path / "schemas")
    (tmp_path / "VERSION").write_text("0.2.0\n", encoding="utf-8")
    index = {
        "$schema": "./schemas/index.schema.json",
        "frameworkVersion": "0.2.0",
        "stacks": {"common": {"skills": [
            {"name": "prompt-markers",
             "path": "features/common/skills/prompt-markers"}
        ]}},
    }
    (tmp_path / "index.json").write_text(json.dumps(index), encoding="utf-8")
    pm_src = root / "features" / "common" / "skills" / "prompt-markers"
    pm_dst = tmp_path / "features" / "common" / "skills" / "prompt-markers"
    shutil.copytree(pm_src, pm_dst)
    return tmp_path
```

**Usage:** pass `scaffolding_json` to control what the scaffolder processes:
```python
fw = _make_test_framework(tmp_path, root, scaffolding_json={
    "agent": "test-agent",
    "files": [{
        "source": "templates/hello.tmpl", "target": "HELLO.md",
        "managed": True, "template": True,
    }],
})
```

**Pitfall:** Don't use symlinks in `tmp_path` — the path relationships differ
from the real repo. Copy template content instead.

## Common pitfalls

### IsADirectoryError on template files

```python
# WRONG — creates a directory named CLAUDE.md.tmpl:
(fw / "features" / "common" / "templates" / "CLAUDE.md.tmpl").mkdir(parents=True)

# RIGHT — creates the parent dir, then writes the file:
(fw / "features" / "common" / "templates").mkdir(parents=True)
(fw / "features" / "common" / "templates" / "CLAUDE.md.tmpl").write_text("...")
```

### FileNotFoundError for VERSION

```python
# WRONG — fw doesn't exist as a directory yet:
fw = tmp_path / "fw"
(fw / "VERSION").write_text("...")

# RIGHT:
fw = tmp_path / "fw"
fw.mkdir()
(fw / "VERSION").write_text("...")
```

### index.json missing for Scaffolder

The `Scaffolder.__init__()` calls `bl.read_index(root)` which reads
`<root>/index.json`. Mock frameworks need a minimal one. Use `_write_fw_index(fw)`.

### _load_script can't find drift.py/scaffold.py in mock framework

Scripts that call `_load_script()` should fall back to the script's own repo
root when the given base doesn't have the target file. Pattern:

```python
def _load_script(relpath, base):
    candidates = [base]
    # Fall back to this script's own repo root
    script_repo = Path(__file__).resolve()
    for anc in script_repo.parents:
        if (anc / "scripts" / "badger_lib.py").exists() and (anc / "schemas").is_dir():
            candidates.append(anc)
            break
    for cand in candidates:
        path = cand / relpath
        if path.exists():
            # ... import it
    raise FileNotFoundError(f"could not find {relpath}")
```

### Seed-once files need their template in the mock

Tests for seed-once preservation (state.json, markers-context.json) need the
template files in the mock framework:

```python
(fw / "features" / "common" / "templates" / "state.json").parent.mkdir(parents=True, exist_ok=True)
(fw / "features" / "common" / "templates" / "state.json").write_text(
    '{"tasks": [], "lastUpdated": null}\n', encoding="utf-8",
)
```

### Stray `import scripts.xxx` passes locally but fails CI

A bare `import scripts.badger_lib` in a test file works locally because
`pytest` adds the working directory to `sys.path`, but fails in CI with
`ModuleNotFoundError: No module named 'scripts'`. Always use the
`load_script` fixture instead:

```python
# WRONG — CI explodes:
import scripts.badger_lib as bl_fw

# RIGHT:
bl = load_script("scripts/badger_lib.py")
```

This is especially easy to introduce when experimenting interactively in a
test body and forgetting to clean up before committing.

### New Python files fail pylint C0304 (missing final newline)

`write_file` does not append a trailing newline. CI pylint flags:
```
skills/my-skill/scripts/my_script.py:188:0: C0304: Final newline missing
```
Fix: `echo "" >> path/to/file.py` or ensure the file content ends with `\n`.

### Hook callbacks trigger pylint W0613 (unused-argument)

Hermes plugin hook callbacks must accept `**kwargs` for forward compatibility,
but unused named params fire W0613. Prefix unused catch-all with underscore:

```python
# W0613 on session_id, platform, **kwargs:
def my_hook(session_id, platform, **kwargs): ...

# Clean — pylint ignores **_kwargs:
def my_hook(cwd="", **_kwargs): ...
```