"""Every third-party import under scripts/ must be declared in pyproject.toml.

Both sides of the comparison are derived from source, never hand-listed: the
"used" set comes from parsing every .py file under scripts/ with ast; the
"declared" set comes from parsing pyproject.toml's [project.dependencies].
Stdlib modules (sys.stdlib_module_names) and this project's own modules
(any .py file under scripts/) are excluded from "third-party".

See .ai-badger/invariants/derive-or-delete-the-list.md — a hand-maintained
expected list here would drift the moment someone adds an import.
"""

from __future__ import annotations

import ast
import sys
import tomllib
from pathlib import Path

SCRIPTS_ROOT = Path(__file__).resolve().parent.parent
PYPROJECT = SCRIPTS_ROOT.parent / "pyproject.toml"

# Import name -> PyPI distribution name, only for the handful of packages
# where they differ (pyproject.toml declares distribution names).
_IMPORT_TO_DISTRIBUTION = {
    "sklearn": "scikit-learn",
}


def _local_module_names() -> set[str]:
    """Module names this project defines itself — never third-party.

    Flat modules are their file stems; packages are their directory names
    (the directory containing __init__.py), which no stem can express.
    """
    names = {path.stem for path in SCRIPTS_ROOT.rglob("*.py")}
    names.update(path.parent.name for path in SCRIPTS_ROOT.rglob("*/__init__.py"))
    return names


def _top_level_module(dotted_name: str) -> str:
    return dotted_name.split(".", 1)[0]


def _imports_in_file(path: Path) -> set[str]:
    tree = ast.parse(path.read_text(encoding="utf-8"), filename=str(path))
    modules: set[str] = set()
    for node in ast.walk(tree):
        if isinstance(node, ast.Import):
            for alias in node.names:
                modules.add(_top_level_module(alias.name))
        elif isinstance(node, ast.ImportFrom):
            if node.level and node.level > 0:
                continue  # relative import: always local
            if node.module:
                modules.add(_top_level_module(node.module))
    return modules


def _third_party_imports() -> dict[str, set[Path]]:
    stdlib = set(sys.stdlib_module_names) | {"__main__"}
    local = _local_module_names()
    found: dict[str, set[Path]] = {}
    for path in sorted(SCRIPTS_ROOT.rglob("*.py")):
        for module in _imports_in_file(path):
            if module in stdlib or module in local:
                continue
            found.setdefault(module, set()).add(path)
    return found


def _declared_dependencies() -> set[str]:
    data = tomllib.loads(PYPROJECT.read_text(encoding="utf-8"))
    raw = data.get("project", {}).get("dependencies", [])
    declared: set[str] = set()
    for requirement in raw:
        name = requirement
        for sep in ("[", ">", "<", "=", "!", "~", ";", " "):
            name = name.split(sep, 1)[0]
        declared.add(name.strip().lower())
    return declared


def test_every_third_party_import_is_declared_in_pyproject() -> None:
    third_party = _third_party_imports()
    declared = _declared_dependencies()

    missing = {
        module: files
        for module, files in third_party.items()
        if _IMPORT_TO_DISTRIBUTION.get(module, module).lower() not in declared
    }

    assert not missing, "third-party imports missing from [project.dependencies] in pyproject.toml: " + "; ".join(
        f"{module} (used in {', '.join(str(f.relative_to(SCRIPTS_ROOT.parent)) for f in sorted(files))})"
        for module, files in sorted(missing.items())
    )


def test_the_scan_sees_third_party_imports_at_all() -> None:
    """An empty scan passes the check above for the same reason a correct one does.

    If SCRIPTS_ROOT ever resolves wrong, or rglob stops matching, `missing` is
    empty and the real check goes green while measuring nothing. This is the
    Python half of the guard `EnvGateReaderRuleTests` carries on the C# side.
    """
    third_party = _third_party_imports()

    assert len(third_party) >= 3, (
        "the dependency check scans this set; if it empties, it stops being able to fail. "
        f"Found: {sorted(third_party)}"
    )
