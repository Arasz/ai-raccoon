#!/usr/bin/env python3
"""Count invariants in an instruction file and warn when too many.

Rule 8B: long negative instruction lists are brittle.  This script counts bullet
points under the 'Non-negotiable invariants' section and compares to a threshold
(default 35).  Returns exit 1 when the count exceeds the threshold.

Usage: constraint_count_lint.py <file> [--threshold N]
"""
from __future__ import annotations

import argparse
import re
import sys
from dataclasses import dataclass
from pathlib import Path

_SECTION_RE = re.compile(r"^##\s+Non-negotiable invariants\s*$", re.IGNORECASE)
_BULLET_RE = re.compile(r"^\s*-\s+")
_HEADING_RE = re.compile(r"^##\s+")


@dataclass(frozen=True)
class Result:
    """Outcome of a constraint count check."""
    passed: bool
    count: int
    threshold: int


def count_invariants(path: Path) -> int:
    """Count bullet points under the 'Non-negotiable invariants' heading.

    Raises FileNotFoundError when *path* does not exist.
    """
    if not path.exists():
        raise FileNotFoundError(f"File not found: {path}")

    lines = path.read_text(encoding="utf-8").splitlines()
    in_section = False
    count = 0

    for line in lines:
        if _SECTION_RE.match(line):
            in_section = True
            continue
        if in_section and _HEADING_RE.match(line):
            break  # next section
        if in_section and _BULLET_RE.match(line):
            count += 1

    return count


def check(path: Path, threshold: int = 35) -> Result:
    """Run the constraint count check against *path*."""
    count = count_invariants(path)
    return Result(passed=count <= threshold, count=count, threshold=threshold)


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("file", type=Path, help="Instruction file to check (CLAUDE.md or copilot-instructions.md)")
    ap.add_argument("--threshold", type=int, default=35, help="Max allowed invariant count (default 35)")
    args = ap.parse_args(argv)

    try:
        result = check(args.file, threshold=args.threshold)
    except FileNotFoundError as exc:
        print(f"Error: {exc}", file=sys.stderr)
        return 1

    if result.passed:
        print(f"OK: {result.count} invariant(s) (threshold {result.threshold})")
        return 0

    print(
        f"CONSTRAINT COUNT EXCEEDED: {result.count} invariant(s) "
        f"(threshold {result.threshold}). "
        f"Consider consolidating or removing invariants."
    )
    return 1


if __name__ == "__main__":
    sys.exit(main())
