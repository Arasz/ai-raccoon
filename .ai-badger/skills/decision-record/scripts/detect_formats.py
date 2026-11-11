#!/usr/bin/env python3
"""Report which decision-record and spec formats a project already uses, and which CLIs are on PATH.

The tree decides the format; an installed CLI only decides whether to write through it. Stdlib only.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import sys
from pathlib import Path
from typing import Callable, Dict, List, Optional

LOG_DIR_NAMES = {"adr", "adrs", "decisions"}
SKIPPED_DIRS = {"node_modules", "vendor", "worktrees", "dist", "build", "target"}
SEARCHED_HIDDEN_DIRS = {".github", ".gitlab"}
MAX_DEPTH = 4
NUMBERED = re.compile(r"^(\d{3,})-.+\.md$")
DATED = re.compile(r"^(\d{8}|\d{4}-\d{2}-\d{2})-.+\.md$")
INDEX_NAMES = ("README.md", "index.md")
LOG4BRAINS_FOLDER = re.compile(r"^\s*adrFolder:\s*['\"]?([^'\"#\s]+)", re.MULTILINE)
SPEC_MARKERS = (
    ("openspec", "openspec", ("openspec/config.yaml", "openspec/specs")),
    ("spec-kit", ".specify", (".specify",)),
    ("kiro", ".kiro/specs", (".kiro/specs",)),
)
CLIS = ("openspec", "specify", "adr", "log4brains")
FALLBACK_DIR = "docs/decisions"

Which = Callable[[str], Optional[str]]


def _rel(root: Path, path: Path) -> str:
    return path.relative_to(root).as_posix()


def _inside(root: Path, marker: str, target: str, warnings: List[str]) -> Optional[str]:
    """The declared folder as a normalised root-relative path, or None (with a warning) if it escapes."""
    candidate = Path(target) if Path(target).is_absolute() else root / target
    resolved = Path(os.path.normpath(candidate))
    if resolved != root and root not in resolved.parents:
        warnings.append(f"{marker} points outside the project ({target}); ignored")
        return None
    return _rel(root, resolved)


def _read(path: Path, warnings: List[str]) -> Optional[str]:
    try:
        return path.read_text(encoding="utf-8")
    except (OSError, UnicodeDecodeError) as error:
        warnings.append(f"{path.name} is unreadable ({error.__class__.__name__}); ignored")
        return None


def _declared_logs(root: Path, warnings: List[str]) -> Dict[str, str]:
    """Map each ADR folder a tool's marker file declares to that tool."""
    declared: Dict[str, str] = {}
    sources = ((".adr-dir", "adr-tools", lambda text: [text.strip()]),
               (".log4brains.yml", "log4brains", LOG4BRAINS_FOLDER.findall))
    for marker, tool, folders in sources:
        if not (root / marker).is_file():
            continue
        text = _read(root / marker, warnings)
        for target in folders(text) if text else []:
            rel = _inside(root, marker, target, warnings) if target else None
            if rel is not None:
                declared[rel] = tool
    return declared


def _is_record(name: str) -> bool:
    return bool(NUMBERED.match(name) or DATED.match(name))


def _discovered_logs(root: Path) -> List[str]:
    """Folders named like an ADR log that hold at least one record, symlinked folders included."""
    found, seen = [], set()
    for current, dirs, files in os.walk(root, followlinks=True):
        real = os.path.realpath(current)
        if real in seen:
            dirs[:] = []
            continue
        seen.add(real)
        depth = len(Path(current).relative_to(root).parts)
        dirs[:] = [d for d in sorted(dirs)
                   if (not d.startswith(".") or d in SEARCHED_HIDDEN_DIRS)
                   and d not in SKIPPED_DIRS and depth < MAX_DEPTH]
        if Path(current).name in LOG_DIR_NAMES and any(_is_record(f) for f in files):
            found.append(_rel(root, Path(current)))
    return found


def _describe_log(root: Path, rel: str, tool: str) -> Dict[str, object]:
    folder = root / rel
    names = sorted(p.name for p in folder.glob("*.md")) if folder.is_dir() else []
    records = [n for n in names if _is_record(n)]
    dated = tool == "log4brains" or any(DATED.match(n) for n in records)
    if tool == "unknown" and rel == "doc/adr":
        tool = "adr-tools"
    if dated:
        next_number, newest = None, max(records, default=None)
    else:
        numbers = [NUMBERED.match(n).group(1) for n in records]
        width = max((len(n) for n in numbers), default=4)
        next_number = str(max((int(n) for n in numbers), default=0) + 1).zfill(width)
        newest = max(records, key=lambda n: int(NUMBERED.match(n).group(1)), default=None)
    index = next((name for name in INDEX_NAMES if (folder / name).is_file()), None)
    return {
        "dir": rel,
        "tool": tool,
        "naming": "date" if dated else "sequence",
        "count": len(records),
        "next": next_number,
        "newest": f"{rel}/{newest}" if newest else None,
        "index": f"{rel}/{index}" if index else None,
    }


def _adr_logs(root: Path, warnings: List[str]) -> List[Dict[str, object]]:
    logs = _declared_logs(root, warnings)
    for rel in _discovered_logs(root):
        logs.setdefault(rel, "unknown")
    return [_describe_log(root, rel, tool) for rel, tool in logs.items()]


def _spec_frameworks(root: Path) -> List[Dict[str, str]]:
    return [{"framework": name, "dir": folder}
            for name, folder, markers in SPEC_MARKERS
            if any((root / marker).exists() for marker in markers)]


def detect(root: Path, which: Which = shutil.which) -> Dict[str, object]:
    """Describe the project's ADR logs, spec frameworks, CLIs and the resulting write mode."""
    root = Path(root).resolve()
    warnings: List[str] = []
    adr = _adr_logs(root, warnings)
    specs = _spec_frameworks(root)
    mode = {(True, True): "combination", (True, False): "adr",
            (False, True): "spec", (False, False): "none"}[(bool(adr), bool(specs))]
    return {
        "mode": mode,
        "adr": adr,
        "specs": specs,
        "clis": {name: which(name) for name in CLIS},
        "fallback": {"dir": FALLBACK_DIR, "next": "0001"},
        "warnings": warnings,
    }


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("root", nargs="?", default=".", help="project root (default: cwd)")
    parser.add_argument("--json", action="store_true", help="print the full result as JSON")
    args = parser.parse_args(argv)
    result = detect(Path(args.root))
    if args.json:
        print(json.dumps(result, indent=2))
        return 0
    print(f"mode: {result['mode']}")
    for log in result["adr"]:
        upcoming = log["next"] or "dated by today"
        print(f"adr: {log['dir']} ({log['tool']}, {log['count']} records, next {upcoming})")
    for spec in result["specs"]:
        print(f"spec: {spec['framework']} at {spec['dir']}")
    installed = [name for name, path in result["clis"].items() if path]
    print(f"clis: {', '.join(installed) or 'none'}")
    for warning in result["warnings"]:
        print(f"warning: {warning}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
