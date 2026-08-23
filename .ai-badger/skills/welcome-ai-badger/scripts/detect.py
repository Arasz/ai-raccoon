#!/usr/bin/env python3
"""Best-effort detection of a target repo's stacks, agents, source control, and commands.

Emits a PROPOSED config.json to stdout for the agent to refine (the agent resolves
ambiguity and fills project.summary/domain + persona routing, then validates). MECHANICAL:
no LLM. Network is used only for `git remote` (local git), which is optional.

Usage: detect.py [--target <dir>] [--root <framework>]
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
from pathlib import Path
from typing import Dict, List


def _bootstrap_lib() -> Path:
    """Put the framework's engine/ and tooling/ on sys.path and return its root.

    One predicate, shared with badger_lib.is_framework_root: schemas/ + features/ +
    engine/badger_lib.py. Ordered inputs: --root, an ancestor walk, $AI_BADGER, the root
    recorded in a .ai-badger/manifest.json above this file, then ~/.ai-badger/framework
    (ADR-0009). Duplicated verbatim in every entry point because locating badger_lib is
    what it is for.
    """

    def is_root(path):
        return ((path / "schemas").is_dir() and (path / "features").is_dir()
                and (path / "engine" / "badger_lib.py").is_file())

    def argv_root():
        # sys.argv is ours only when this file is the program being run; these modules are
        # also imported into hosts whose own --root means something else entirely.
        try:
            if not sys.argv or Path(sys.argv[0]).resolve() != Path(__file__).resolve():
                return None
        except (OSError, ValueError):
            return None
        argv = sys.argv[1:]
        for i, arg in enumerate(argv):
            if arg == "--root" and i + 1 < len(argv):
                return argv[i + 1]
            if arg.startswith("--root="):
                return arg.split("=", 1)[1]
        return None

    def checked(value, source):
        root = Path(value).expanduser()
        if not is_root(root):
            raise RuntimeError(
                f"{source} is {root}, which is not an ai-badger framework root "
                f"(no schemas/ + features/ + engine/badger_lib.py)"
            )
        return root

    def manifests(start):
        # Above this file only. A working directory belongs to whatever repo the user
        # opened, and no repo may steer the sys.path of a hook that runs on session start.
        for anc in [start, *start.parents]:
            manifest = (anc / "manifest.json" if anc.name == ".ai-badger"
                        else anc / ".ai-badger" / "manifest.json")
            if not manifest.is_file():
                continue
            try:
                data = json.loads(manifest.read_text(encoding="utf-8"))
            except (OSError, ValueError):
                continue
            if isinstance(data, dict):
                yield manifest, data

    def recorded(start):
        for manifest, data in manifests(start):
            value = data.get("frameworkRoot")
            if not value:
                continue
            candidate = Path(value).expanduser()
            if not candidate.is_absolute():
                candidate = manifest.parent.parent / candidate
            if is_root(candidate):
                return candidate.resolve()
        return None

    def warn_on_cache_skew(root, start):
        # The cache is last in the order and never updated in place, so its engine can be
        # many releases behind the caller. Say so; never break a session over it.
        if root.resolve() != cache.resolve():
            return
        try:
            have = (cache / "VERSION").read_text(encoding="utf-8").strip()
        except OSError:
            return
        want = next((d.get("frameworkVersion") for _, d in manifests(start)
                     if d.get("frameworkVersion")), None)
        if have and want and have != want:
            print(f"ai-badger: {cache} is version {have}, but this project was scaffolded "
                  f"by {want}. The cache is never updated in place — remove it, or pass "
                  f"--root <framework checkout>.", file=sys.stderr)

    here = Path(__file__).resolve()
    cache = Path.home() / ".ai-badger" / "framework"
    value = argv_root()
    if value:
        root = checked(value, "--root")
    else:
        root = next((anc for anc in [here, *here.parents] if is_root(anc)), None)
        if root is None and os.environ.get("AI_BADGER"):
            root = checked(os.environ["AI_BADGER"], "$AI_BADGER")
        root = root or recorded(here) or (cache if is_root(cache) else None)
    if root is None:
        raise RuntimeError(
            f"could not locate the ai-badger framework: none above {here.parent}, no "
            f"$AI_BADGER, no frameworkRoot in a .ai-badger/manifest.json above it, and no "
            f"cache at {cache} — pass --root <framework> or clone "
            f"https://github.com/Arasz/ai-badger"
        )
    warn_on_cache_skew(root, here)
    sys.path.insert(0, str(root / "tooling"))
    sys.path.insert(0, str(root / "engine"))
    return root.resolve()


FRAMEWORK_ROOT = _bootstrap_lib()
import badger_lib as bl

# vendored / build / framework-owned directories whose contents must not trigger stack
# detection: `.claude` and `.ai-badger` are the framework's own scaffolded output, and
# `bl.BACKUP_DIR_NAME` is den-refresh's backup of `.ai-badger` (#134) — none of the three is
# the target project's stack, so none may ever propose one.
_IGNORE_DIRS = {"node_modules", ".git", ".venv", "venv", "__pycache__", ".terraform", "dist",
                ".claude", ".ai-badger", bl.BACKUP_DIR_NAME}


def _has(target: Path, *globs: str) -> bool:
    """True if any glob matches a path under `target`, ignoring vendored/build dirs."""
    for g in globs:
        for p in target.rglob(g):
            if not any(part in _IGNORE_DIRS for part in p.relative_to(target).parts):
                return True
    return False


def _read(path: Path) -> str:
    try:
        return path.read_text(encoding="utf-8", errors="ignore")
    except OSError:
        return ""


def _signal_globs(signals: List[str]) -> List[str]:
    """Keep only machine-matchable path/glob signals; prose signals (deps, usage) contain spaces."""
    return [s for s in signals if s and " " not in s]


def _own_package_jsons(target: Path) -> List[Path]:
    """Every package.json under `target` that belongs to the project itself: recursive (so a
    monorepo's package.json under e.g. frontend/ is found, not just the repo root), excluding
    vendored/build dirs (_IGNORE_DIRS) so a dependency's own package.json under node_modules/
    never contributes."""
    return [p for p in target.rglob("package.json")
            if not any(part in _IGNORE_DIRS for part in p.relative_to(target).parts)]


def _dependency_stacks(target: Path) -> List[str]:
    """Stacks whose detectionSignals are dependency/content facts a file glob can't express
    (package.json deps, `.csproj` package references). Scans every package.json under `target`
    (not just the root one) so a monorepo with the dependency-bearing project in a subdirectory
    (e.g. frontend/package.json) is still detected."""
    found: List[str] = []
    deps: Dict = {}
    for pkg in _own_package_jsons(target):
        try:
            pkg_json = json.loads(_read(pkg))
        except json.JSONDecodeError:
            continue
        deps.update(pkg_json.get("dependencies", {}))
        deps.update(pkg_json.get("devDependencies", {}))
    if "typescript" in deps:
        found.append("ts")
    if "react" in deps:
        found.append("react")
    if any(d.startswith("@angular/") for d in deps):
        found.append("angular")
    if "azure" in " ".join(deps).lower():
        found.append("azure")
    if "@azure/cosmos" in deps or any("cosmos" in _read(p).lower()
                                      for p in target.rglob("*.csproj")):
        found.append("cosmos")
    return found


def detect_stacks(target: Path, index: Dict) -> List[str]:
    """Detect stacks from each stack's `detectionSignals` (data-driven, read from the index),
    plus dependency/content heuristics that prose signals can't express. A new stack needs no
    change here: give its stack.json file/glob detectionSignals and it is detected automatically."""
    stacks: List[str] = []
    for stack, data in index.get("stacks", {}).items():
        if stack == "common":
            continue
        globs = _signal_globs(data.get("meta", {}).get("detectionSignals", []))
        if globs and _has(target, *globs):
            stacks.append(stack)
    stacks.extend(_dependency_stacks(target))

    # Handle .venv signal: if .venv contains user packages, signal python stack;
    # if .venv is tool-only and no user Python manifest exists, suppress false-positive python stack.
    has_venv = (target / ".venv").is_dir() or (target / "venv").is_dir()
    if has_venv:
        has_user_manifest = _has(target, "pyproject.toml", "requirements.txt", "setup.py", "setup.cfg", "Pipfile", "tox.ini")
        is_tool_venv = _is_tool_only_venv(target, index)
        if not is_tool_venv and "python" not in stacks:
            stacks.append("python")
        elif is_tool_venv and not has_user_manifest and "python" in stacks:
            stacks.remove("python")

    # de-dupe preserving order
    seen: set = set()
    return [s for s in stacks if not (s in seen or seen.add(s))]


def _is_tool_only_venv(target: Path, index: Dict) -> bool:
    """True if .venv/venv exists but contains ONLY packages for catalog MCP tools + standard venv tooling."""
    venv_dirs = [target / ".venv", target / "venv"]
    found_venv = None
    for vd in venv_dirs:
        if vd.is_dir():
            found_venv = vd
            break
    if not found_venv:
        return False

    # Standard venv + common transitive MCP tool packages
    allowed_packages = {
        "pip", "setuptools", "wheel", "_virtualenv", "distutils",
        "mcp", "pydantic", "pydantic_core", "typing_extensions", "annotated_types",
        "starlette", "uvicorn", "httpx", "anyio", "sniffio", "idna", "certifi",
        "tree_sitter", "tree_sitter_python", "htbuilder"
    }

    # Add packages declared in MCP catalog meta.json files
    features_dir = FRAMEWORK_ROOT / "features"
    for meta_file in features_dir.glob("*/mcp/*/meta.json"):
        try:
            meta_data = json.loads(_read(meta_file))
            pkg = meta_data.get("package")
            if pkg:
                allowed_packages.add(pkg.lower().replace("-", "_"))
        except (json.JSONDecodeError, OSError):
            continue

    # Scan site-packages dist-info
    installed = set()
    for site_pkg in found_venv.glob("**/site-packages"):
        for dist in site_pkg.glob("*.dist-info"):
            clean_name = dist.name.split("-")[0].lower().replace("-", "_")
            installed.add(clean_name)

    if not installed:
        return True

    # If all installed packages are allowed, it's an MCP tool venv
    return installed.issubset(allowed_packages)


def expand_requires(stacks: List[str], index: Dict) -> List[str]:
    """Transitively add each detected stack's declared `requires` (from stack.json meta)."""
    out = list(stacks)
    changed = True
    while changed:
        changed = False
        for s in list(out):
            meta = index.get("stacks", {}).get(s, {}).get("meta", {})
            for req in meta.get("requires", []):
                if req not in out:
                    out.append(req)
                    changed = True
    return out


def detect_agents(target: Path) -> List[str]:
    """Detect which coding agents (claude/copilot/hermes) this repo already has traces of."""
    home = Path.home()
    agents: List[str] = []
    if (target / "CLAUDE.md").exists() or (home / ".claude").exists():
        agents.append("claude")
    if ((target / ".github" / "copilot-instructions.md").exists()
            or (target / ".github" / "instructions").is_dir()
            or (target / "COPILOT_INSTRUCTIONS.md").exists()
            or (home / ".copilot").exists()):
        agents.append("copilot")
    # Hermes detection: .hermes.md / HERMES.md in repo, or ~/.hermes/ in user scope
    if ((target / ".hermes.md").exists()
            or (target / "HERMES.md").exists()
            or (home / ".hermes").is_dir()):
        agents.append("hermes")
    return agents or ["claude"]


def detect_source_control(target: Path) -> Dict:
    """Detect the git remote's hosting platform and normalize its URL."""
    sc: Dict = {"platform": "none", "repoUrl": None, "projectUrl": None}
    if not (target / ".git").exists():
        return sc
    try:
        url = subprocess.run(["git", "-C", str(target), "remote", "get-url", "origin"],
                             capture_output=True, text=True, timeout=10, check=False).stdout.strip()
    except (subprocess.SubprocessError, OSError):
        url = ""
    if not url:
        return sc
    low = url.lower()
    if "github.com" in low:
        sc["platform"] = "github"
    elif "gitlab" in low:
        sc["platform"] = "gitlab"
    elif "dev.azure.com" in low or "visualstudio.com" in low:
        sc["platform"] = "azure-devops"
    # normalize git@ / .git → https URL
    web = url
    if web.startswith("git@"):
        web = "https://" + web[4:].replace(":", "/", 1)
    if web.endswith(".git"):
        web = web[:-4]
    sc["repoUrl"] = web
    return sc


def detect_commands(target: Path, stacks: List[str]) -> Dict[str, str]:
    """Detect build/test/lint/run commands from package.json scripts (or dotnet defaults)."""
    cmds: Dict[str, str] = {}
    pkg = target / "package.json"
    if pkg.exists():
        try:
            scripts = json.loads(_read(pkg)).get("scripts", {})
        except json.JSONDecodeError:
            scripts = {}
        has_bun_lock = (target / "bun.lock").exists() or (target / "bun.lockb").exists()
        runner = "bun run" if has_bun_lock else "npm run"
        for key, names in (("build", ["build"]), ("test", ["test"]),
                           ("lint", ["lint"]), ("run", ["dev", "start"])):
            for n in names:
                if n in scripts:
                    cmds[key] = f"{runner} {n}"
                    break
    if "dotnet" in stacks:
        cmds.setdefault("build", "dotnet build")
        cmds.setdefault("test", "dotnet test")
    return cmds


def main(argv=None) -> int:
    """CLI entry point: emit a proposed config.json for `--target` to stdout."""
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--target", default=".")
    ap.add_argument("--root")
    args = ap.parse_args(argv)
    target = Path(args.target).resolve()
    root = Path(args.root).resolve() if args.root else bl.find_root()
    index = bl.read_index(root)

    stacks = expand_requires(detect_stacks(target, index), index)
    # keep only stacks the framework actually knows about
    known = set(index.get("stacks", {}).keys())
    stacks = [s for s in stacks if s in known] or stacks

    proposed = {
        "$schema": "./schemas/config.schema.json",
        "frameworkVersion": index["frameworkVersion"],
        "project": {"name": target.name, "summary": "", "domain": ""},
        "stacks": stacks,
        "agents": detect_agents(target),
        "sourceControl": detect_source_control(target),
        "commands": detect_commands(target, stacks),
        "personaRouting": [],
        "skillScope": "default",
        "docs": {},
    }
    print(json.dumps(proposed, indent=2, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
