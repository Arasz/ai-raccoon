#!/usr/bin/env python3
"""Check and install dependencies declared in dependencies.json.

Reads features/common/dependencies.json from the framework root, matches entries
against the scaffolded feature set, ensures a Python venv exists when needed,
and installs packages. Reports what was installed, already present, or failed.

Usage:
  dependency_check.py --root <framework> --target <project> [--features f1,f2]

MECHANICAL ONLY — no LLM. The agent's role is to present the report and act
on installation instructions.
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional


def _load_dependencies(root: Path) -> List[Dict[str, Any]]:
    """Load dependencies.json from the framework root."""
    deps_path = root / "features" / "common" / "dependencies.json"
    if not deps_path.exists():
        return []
    data = json.loads(deps_path.read_text(encoding="utf-8"))
    return data.get("dependencies", [])


def _check_uv_available() -> bool:
    """Return True if uv is on PATH."""
    try:
        result = subprocess.run(
            ["uv", "--version"],
            capture_output=True, text=True, timeout=10, check=False,
        )
        return result.returncode == 0
    except (FileNotFoundError, subprocess.TimeoutExpired):
        return False


def _ensure_venv(target: Path) -> Path:
    """Create .venv in target if it doesn't exist. Return the venv path."""
    venv_path = target / ".venv"
    if venv_path.exists():
        return venv_path
    subprocess.run(
        [sys.executable, "-m", "venv", str(venv_path)],
        check=True, capture_output=True, text=True,
    )
    return venv_path


def _install_python(
    dep: Dict[str, Any], venv_path: Path, uv_available: bool,
) -> List[str]:
    """Install Python packages for one dependency entry.

    Returns a list of error strings (empty on success).
    """
    errors: List[str] = []
    pkg = dep["package"]
    extras = dep.get("extras", "")
    spec = f"{pkg}{extras}"

    if uv_available:
        env = dict(os.environ)
        env["VIRTUAL_ENV"] = str(venv_path)
        pip_cmd = ["uv", "pip", "install", spec]
    else:
        pip_path = str(venv_path / "bin" / "pip")
        pip_cmd = [pip_path, "install", spec]

    try:
        result = subprocess.run(
            pip_cmd, capture_output=True, text=True, timeout=120, check=False,
        )
        if result.returncode != 0:
            errors.append(f"{pkg}: pip install failed — {result.stderr.strip()}")
    except subprocess.TimeoutExpired:
        errors.append(f"{pkg}: pip install timed out (120s)")
    except FileNotFoundError:
        errors.append(f"{pkg}: pip not found at {pip_cmd[0]}")
    return errors


def _install_node(dep: Dict[str, Any]) -> List[str]:
    """Install Node packages for one dependency entry.

    Returns a list of error strings (empty on success).
    """
    errors: List[str] = []
    pkg = dep["package"]
    try:
        result = subprocess.run(
            ["npm", "install", "-g", pkg],
            capture_output=True, text=True, timeout=120, check=False,
        )
        if result.returncode != 0:
            errors.append(f"{pkg}: npm install failed — {result.stderr.strip()}")
    except subprocess.TimeoutExpired:
        errors.append(f"{pkg}: npm install timed out (120s)")
    except FileNotFoundError:
        errors.append(f"{pkg}: npm not found on PATH")
    return errors


def get_venv_python(target: Path) -> Optional[str]:
    """Return the venv python3 path if a .venv exists in target, else None."""
    venv_path = target / ".venv"
    if not venv_path.exists():
        return None
    return str(venv_path / "bin" / "python3")


def _module_importable(python: str, module: str) -> bool:
    """Return True if `import module` succeeds under the given interpreter."""
    try:
        result = subprocess.run(
            [python, "-c", f"import {module}"],
            capture_output=True, text=True, timeout=10, check=False,
        )
        return result.returncode == 0
    except (FileNotFoundError, subprocess.TimeoutExpired):
        return False


def _find_host_python(target: Path) -> Optional[str]:
    """Find the interpreter that already has code-review-graph installed.

    Checks the project .venv first (dependency_check's own install target),
    then falls back to whatever `python3` resolves to on PATH — the MCP
    server command declared for code-review-graph launches via bare
    `python3`, so PATH resolution is what actually runs at MCP-server time.
    Returns the first candidate with `code_review_graph` importable, or the
    first candidate at all if neither has it (best-effort naming for the hint).
    """
    candidates: List[str] = []
    venv_python = get_venv_python(target)
    if venv_python:
        candidates.append(venv_python)
    which_python3 = shutil.which("python3")
    if which_python3 and which_python3 not in candidates:
        candidates.append(which_python3)

    for candidate in candidates:
        if _module_importable(candidate, "code_review_graph"):
            return candidate
    return candidates[0] if candidates else None


def _check_optional_dependency(dep: Dict[str, Any], target: Path) -> Dict[str, Any]:
    """Detect an optional dependency's presence; never installs it.

    Returns {"present": True} when importable in the host interpreter, or
    {"present": False, "hint": "<actionable install hint>"} when missing.
    """
    import_name = dep.get("importName", dep["package"].replace("-", "_"))
    host = _find_host_python(target)

    if host and _module_importable(host, import_name):
        return {"present": True}

    pkg_spec = f"{dep['package']}{dep.get('extras', '')}"
    interpreter = host or (
        "the interpreter that runs your code-review-graph MCP server "
        "(check the `command` in .mcp.json)"
    )
    hint = f"{dep['name']}: not installed. Install with: {interpreter} -m pip install \"{pkg_spec}\""
    note = dep.get("note")
    if note:
        hint += f" — {note}"
    hint += (
        " Without it, semantic search silently falls back to keyword "
        "matching (list_graph_stats_tool will report 0 nodes embedded)."
    )
    return {"present": False, "hint": hint}


def run_dependency_check(
    root: Path,
    target: Path,
    features: Optional[List[str]] = None,
    allow_install: bool = False,
) -> Dict[str, Any]:
    """Check declared dependencies for the given features; install only with consent.

    Installing writes outside the project — a venv in the target, and `npm install -g`
    into the machine's global prefix. That is not a side effect a scaffold may take on its
    own (security I7), so `allow_install` defaults to False: every required dependency is
    reported as a hint naming the command that would run, and nothing is executed.

    Optional dependencies (`optional: true`) are never installed either way — they
    are detected in the host interpreter and surfaced as a hint when missing.

    Returns {"installed": [...], "already_present": [...], "errors": [...], "hints": [...]}.
    """
    empty_result = {"installed": [], "already_present": [], "errors": [], "hints": []}
    deps = _load_dependencies(root)
    if not deps:
        return empty_result

    installed: List[str] = []
    already_present: List[str] = []
    errors: List[str] = []
    hints: List[str] = []

    # Filter to requested features
    active = [
        d for d in deps
        if features is None or d["feature"] in features
    ]

    # Check which required Python deps need venv (optional deps never trigger this)
    needs_venv = any(
        dep.get("venv") and dep["ecosystem"] == "python" and not dep.get("optional")
        for entry in active
        for dep in entry["dependencies"]
    )
    venv_path: Optional[Path] = None
    uv_available = False

    if needs_venv and allow_install:
        venv_path = _ensure_venv(target)
        uv_available = _check_uv_available()

    for entry in active:
        for dep in entry["dependencies"]:
            if dep.get("optional"):
                check = _check_optional_dependency(dep, target)
                if check["present"]:
                    already_present.append(dep["package"])
                else:
                    hints.append(check["hint"])
                continue

            eco = dep["ecosystem"]
            pkg = dep["package"]

            if not allow_install:
                hints.append(_pending_hint(dep))
                continue

            if eco == "python":
                if venv_path is None:
                    errors.append(f"{pkg}: venv required but not created")
                    continue
                dep_errors = _install_python(dep, venv_path, uv_available)
            elif eco == "node":
                dep_errors = _install_node(dep)
            else:
                dep_errors = [f"{pkg}: unknown ecosystem '{eco}'"]

            if dep_errors:
                errors.extend(dep_errors)
            else:
                installed.append(pkg)

    return {
        "installed": installed,
        "already_present": already_present,
        "errors": errors,
        "hints": hints,
    }


def _pending_hint(dep: Dict[str, Any]) -> str:
    """What would be installed, and what to run to allow it."""
    pkg = dep["package"]
    if dep["ecosystem"] == "node":
        where = "globally (npm install -g)"
    elif dep.get("venv"):
        where = "into the project venv"
    else:
        where = "into the host interpreter"
    return (f"{pkg}: not installed — would install {where}. Re-run with --execute to "
            f"allow it, or install it yourself.")


def detect_new_deps(
    root: Path,
    scaffolded_features: List[str],
) -> List[Dict[str, Any]]:
    """Return dependency entries whose feature is not in scaffolded_features."""
    deps = _load_dependencies(root)
    return [d for d in deps if d["feature"] not in scaffolded_features]


def main(argv: Optional[List[str]] = None) -> int:
    """CLI entry point: check dependencies, install if missing, report."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", required=True, help="framework repo root")
    parser.add_argument("--target", required=True, help="project root")
    parser.add_argument(
        "--features", default=None,
        help="comma-separated feature names to check (default: all)",
    )
    parser.add_argument(
        "--execute", action="store_true",
        help="Actually install missing dependencies. Default: report what would be installed.",
    )
    args = parser.parse_args(argv)

    root = Path(args.root).resolve()
    target = Path(args.target).resolve()
    features = (
        [f.strip() for f in args.features.split(",")]
        if args.features else None
    )

    result = run_dependency_check(root, target, features=features,
                                  allow_install=args.execute)

    if result["installed"]:
        print(f"installed: {', '.join(result['installed'])}")
    if result["hints"]:
        for hint in result["hints"]:
            print(f"hint: {hint}")
    if result["errors"]:
        for err in result["errors"]:
            print(f"error: {err}", file=sys.stderr)

    print(json.dumps(result, indent=2, ensure_ascii=False))
    return 0 if not result["errors"] else 1


if __name__ == "__main__":
    sys.exit(main())
