"""Shared constants and utility functions used across scaffold domain modules.

This module exists to break cyclic imports between scaffold.py and its
domain modules (extensions.py, template_rendering.py, etc.).
"""
from __future__ import annotations

import re
import shutil
from pathlib import Path
from typing import Any, Dict, List


def _within(parent: Path, candidate: Path) -> bool:
    """True when `candidate` resolves to `parent` itself or something inside it.

    Read by every join a config value reaches — `project.name` arrives as any non-empty
    string, so containment is asserted at the join, not assumed upstream (security I1).
    """
    try:
        candidate.resolve().relative_to(parent.resolve())
    except (ValueError, OSError):
        return False
    return True

# Test files and eval suites — applied to every copytree to keep framework-only
# content out of scaffolded repos.
_test_ignore = shutil.ignore_patterns(
    "test_*.py", "*_test.py", "tests", "evals", "__pycache__", "*.pyc"
)

PROJECT_LOCAL_FILE = "project-local.md"

MANAGED_HEADER = (
    "<!-- Managed by ai-badger. Source of truth: .ai-badger/{name}. "
    "Do not edit this copy by hand; edit the source and re-run welcome-ai-badger. -->\n\n"
)

# Stable leading text every managed copy begins with (the part before the {name} slot).
_MANAGED_PREFIX = MANAGED_HEADER.split("{name}", 1)[0]

# Preserved regions: the project-authored opt-out from the managed header above. Every file
# the managed header goes on is Markdown, so an HTML comment is the marker in all of them.
_KEEP_START_RE = re.compile(r"^\s*<!--\s*ai-badger:keep-start\s*-->\s*$")
_KEEP_END_RE = re.compile(r"^\s*<!--\s*ai-badger:keep-end\s*-->\s*$")


class MalformedKeepRegion(ValueError):
    """A file's keep markers are unbalanced or nested, so its regions cannot be read."""


def extract_keep_regions(text: str) -> List[str]:
    """Return each keep-start/keep-end block of `text` verbatim, markers included, in order.

    Raises MalformedKeepRegion rather than guessing: a caller that cannot read the regions
    must refuse to rewrite the file (docs/getting-started.md, "Preserved regions").
    """
    regions: List[str] = []
    current: List[str] = []
    open_at = 0
    for num, line in enumerate(text.splitlines(), 1):
        if _KEEP_START_RE.match(line):
            if open_at:
                raise MalformedKeepRegion(
                    f"ai-badger:keep-start at line {num} opens inside the region opened "
                    f"at line {open_at}"
                )
            current, open_at = [line], num
        elif _KEEP_END_RE.match(line):
            if not open_at:
                raise MalformedKeepRegion(
                    f"ai-badger:keep-end at line {num} has no matching keep-start"
                )
            current.append(line)
            regions.append("\n".join(current))
            current, open_at = [], 0
        elif open_at:
            current.append(line)
    if open_at:
        raise MalformedKeepRegion(
            f"ai-badger:keep-start at line {open_at} has no matching keep-end"
        )
    return regions


def carry_keep_regions(existing: str, body: str) -> str:
    """Return `body` carrying every keep region of `existing`, in order.

    A region fills the matching keep slot the template ships, so re-scaffolding a template
    that carries its own keep region does not append a second copy of it every run. Regions
    the body has no slot for land at the end, because those templates carry no anchors to
    re-seat them at.
    """
    regions = extract_keep_regions(existing)
    if not regions:
        return body
    slots = extract_keep_regions(body)
    if not slots:
        return body.rstrip("\n") + "\n\n" + "\n\n".join(regions) + "\n"

    out, rest = [], body
    for slot, region in zip(slots, regions):
        head, _, rest = rest.partition(slot)
        out.append(head)
        out.append(region)
    out.append(rest)
    carried = "".join(out)

    spare = regions[len(slots):]
    if spare:
        carried = carried.rstrip("\n") + "\n\n" + "\n\n".join(spare) + "\n"
    return carried


def cfg_get(config: Dict[str, Any], dotted: str) -> Any:
    """Look up a dotted path (e.g. 'project.name') in config, or None if any part is missing."""
    node: Any = config
    for part in dotted.split("."):
        if isinstance(node, dict) and part in node:
            node = node[part]
        else:
            return None
    return node


def _condition_met(config: Dict[str, Any], cond: str) -> bool:
    """Evaluate a single condition (no OR). Supports ==, =, and presence checks."""
    if "==" in cond:
        path, expected = (s.strip() for s in cond.split("==", 1))
        val = cfg_get(config, path)
        if isinstance(val, list):
            return expected in val
        return str(val) == expected
    if "=" in cond:
        path, expected = (s.strip() for s in cond.split("=", 1))
        val = cfg_get(config, path)
        if isinstance(val, list):
            return expected in val
        return str(val) == expected
    val = cfg_get(config, cond)
    return val not in (None, "", [], {})


def requirement_met(config: Dict[str, Any], req: str) -> bool:
    """Evaluate an extension requirement.

    Syntax:
        'sourceControl.platform==github'  — value equality
        'stacks=dotnet'                   — value equality; list membership
        'sourceControl.repoUrl'           — presence check
        'stacks=dotnet||stacks=node'      — OR: true if any sub-condition is met
    """
    if "||" in req:
        return any(_condition_met(config, c) for c in req.split("||"))
    return _condition_met(config, req)
