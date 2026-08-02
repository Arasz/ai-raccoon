#!/usr/bin/env python3
"""Report the unanswered questions in a Gherkin spec: rules with no example, examples with no steps.

The elicitation loop stops on structure, not on feel. Stdlib only — Gherkin is line-oriented, so
the narrow question asked here needs a scanner, not a parser.
"""
from __future__ import annotations

import argparse
import json
import sys
from typing import List, NamedTuple

RULE_KEYWORDS = ("Rule:",)
EXAMPLE_KEYWORDS = ("Example:", "Scenario:", "Scenario Outline:", "Scenario Template:")
FEATURE_KEYWORDS = ("Feature:", "Ability:", "Business Need:")
BACKGROUND_KEYWORDS = ("Background:",)
STEP_KEYWORDS = ("Given ", "When ", "Then ", "And ", "But ", "* ")
DOC_STRING_DELIMITERS = ('"""', "```")

DEFERRED_TAG = "@deferred"


class Hole(NamedTuple):
    """One outstanding question, located in the spec that raised it."""

    kind: str
    title: str
    line: int
    deferred: bool


def _starts_with(text: str, keywords) -> str:
    for keyword in keywords:
        if text.startswith(keyword):
            return keyword
    return ""


def _title_after(text: str, keyword: str) -> str:
    return text[len(keyword):].strip()


class _Block:
    """A rule or example awaiting the thing that would complete it."""

    def __init__(self, kind: str, title: str, line: int, deferred: bool) -> None:
        self.kind = kind
        self.title = title
        self.line = line
        self.deferred = deferred
        self.satisfied = False

    def to_hole(self) -> Hole:
        return Hole(kind=self.kind, title=self.title, line=self.line, deferred=self.deferred)


def scan(text: str) -> List[Hole]:
    """Return every outstanding question in `text`, in document order."""
    holes: List[Hole] = []
    open_rule: _Block = None
    open_example: _Block = None
    in_doc_string = False
    doc_string_delimiter = ""
    pending_deferral = False
    in_background = False

    def close_example() -> None:
        nonlocal open_example
        if open_example is not None and not open_example.satisfied:
            holes.append(open_example.to_hole())
        open_example = None

    def close_rule() -> None:
        nonlocal open_rule
        if open_rule is not None and not open_rule.satisfied:
            holes.append(open_rule.to_hole())
        open_rule = None

    for number, raw in enumerate(text.splitlines(), start=1):
        stripped = raw.strip()

        if in_doc_string:
            if stripped.startswith(doc_string_delimiter):
                in_doc_string = False
            continue

        delimiter = _starts_with(stripped, DOC_STRING_DELIMITERS)
        if delimiter:
            in_doc_string = True
            doc_string_delimiter = delimiter
            continue

        if not stripped or stripped.startswith("#"):
            continue

        if stripped.startswith("@"):
            pending_deferral = DEFERRED_TAG in stripped.split()
            continue

        keyword = _starts_with(stripped, FEATURE_KEYWORDS)
        if keyword:
            close_example()
            close_rule()
            in_background = False
            pending_deferral = False
            continue

        keyword = _starts_with(stripped, BACKGROUND_KEYWORDS)
        if keyword:
            close_example()
            in_background = True
            pending_deferral = False
            continue

        keyword = _starts_with(stripped, RULE_KEYWORDS)
        if keyword:
            close_example()
            close_rule()
            in_background = False
            open_rule = _Block("rule-without-example", _title_after(stripped, keyword),
                               number, pending_deferral)
            pending_deferral = False
            continue

        keyword = _starts_with(stripped, EXAMPLE_KEYWORDS)
        if keyword:
            close_example()
            in_background = False
            if open_rule is not None:
                open_rule.satisfied = True
            open_example = _Block("example-without-steps", _title_after(stripped, keyword),
                                  number, pending_deferral)
            pending_deferral = False
            continue

        if _starts_with(stripped, STEP_KEYWORDS) and not in_background:
            if open_example is not None:
                open_example.satisfied = True
            pending_deferral = False

    close_example()
    close_rule()
    return sorted(holes, key=lambda hole: hole.line)


def _main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("spec", help="path to a .feature file")
    parser.add_argument("--json", action="store_true", help="emit machine-readable output")
    args = parser.parse_args(argv)

    with open(args.spec, encoding="utf-8") as handle:
        holes = scan(handle.read())

    # The exit code is the gate; the flag chooses a format, never a verdict.
    open_count = sum(1 for hole in holes if not hole.deferred)

    if args.json:
        print(json.dumps([hole._asdict() for hole in holes], indent=2))
    elif not holes:
        print(f"{args.spec}: complete — no outstanding questions")
    else:
        for hole in holes:
            mark = "deferred" if hole.deferred else "OPEN"
            print(f"{args.spec}:{hole.line}: [{mark}] {hole.kind}: {hole.title}")
        print(f"{len(holes)} hole(s), {open_count} still open")

    return 1 if open_count else 0


if __name__ == "__main__":
    sys.exit(_main())
