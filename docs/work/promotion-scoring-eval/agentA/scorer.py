#!/usr/bin/env python3
"""Mechanical shared-tier promotion scorer (Agent A revision).

Improves on the baseline SharedExtractionService algorithm (organic +2 /
cross-project-mention +2 / accessed +1 / recent +0.5) by replacing the two
signals that were found to saturate across nearly the whole candidate pool
(a bare "mentions another project id" match, and a "created in last 30
days" flag when every extraction pass is itself recent) with source-path
taxonomy and content-shape features that track the *usefulness* rubric
directly: durable protocol/contract text, measured results, imperative
gotchas and explicit cross-project language score up; doc-index chunks,
status-dump openers, superseded markers and uncertainty caveats score down.

Usage:
    python3 scorer.py <path-to-candidates-json>

Prints a JSON array [{"id": <int>, "score": <float>}, ...] to stdout,
covering every candidate in the input file. Stdlib only, deterministic.
"""

from __future__ import annotations

import json
import os
import re
import sys
from dataclasses import dataclass, field

# ---------------------------------------------------------------------------
# Source-path taxonomy
# ---------------------------------------------------------------------------
# Ordered (first match wins). Mirrors the docs-tree conventions this project
# already uses (docs/adr, docs/work, docs/plans, docs/explanation,
# docs/reference, changelog) plus a catch-all "readme index" and "other".

_BUCKET_RULES: list[tuple[str, str]] = [
    ("adr", "/adr/"),
    ("changelog", "changelog"),
    ("work", "docs/work"),
    ("plans", "docs/plans"),
    ("explanation", "docs/explanation"),
    ("reference", "docs/reference"),
]

_BUCKET_BASE_SCORE: dict[str, float] = {
    "organic": 3.0,       # no source_file: a deliberate agent write, not an ingested doc chunk
    "adr": 2.0,            # ratified decision / contract text
    "explanation": 1.8,    # durable architecture/protocol semantics
    "reference": 1.2,
    "work": 0.5,           # review/status notes: wide quality variance, content-shape decides
    "other": 0.3,
    "plans": 0.0,          # pre-decision / speculative by default; content-shape can lift it
    "changelog": -0.5,     # historical log entries, mostly point-in-time and non-actionable
    "readme_index": -1.0,  # catalog/table-of-contents file
}


def source_bucket(source_file: str | None, path: str) -> str:
    if not source_file:
        return "organic"
    p = source_file.lower()
    basename = p.rsplit("/", 1)[-1]
    for bucket, needle in _BUCKET_RULES:
        if needle in p:
            return bucket
    if basename == "readme.md":
        return "readme_index"
    return "other"


# ---------------------------------------------------------------------------
# Content-shape signals (regex over the entry's full text)
# ---------------------------------------------------------------------------

_INDEX_ROW_RE = re.compile(r"^\|.*\[[^\]]+\]\([^)]+\).*\|\s*$")
_CATALOG_ROW_RE = re.compile(r"^\|\s*`[^`]+\.(md|py|json|ya?ml|cs|ts|txt)`\s*\|")
_TABLE_ROW_RE = re.compile(r"^\s*\|")

_MEASURED_RE = re.compile(
    r"\b(measured|Measured|\d+\s?ms\b|\d+\s?MB\b|\d+\s?KB\b|wall[- ]clock|wall time|"
    r"\d+(\.\d+)?%|\d+(\.\d+)?s\b|\bRSS\b|\bp(50|90|95|99)\b|benchmark(ed)?)",
)

_IMPERATIVE_RE = re.compile(
    r"\b(must|never|always|do not|don't|critical constraint|required to|has to)\b",
    re.IGNORECASE,
)

_GENERALIZATION_RE = re.compile(
    r"\b(every project|all projects|any project|any consumer|all consumers|"
    r"framework-wide|bites every|reusable across|applies to (all|every))\b",
    re.IGNORECASE,
)

_SUPERSEDED_RE = re.compile(
    r"\b(superseded|deprecated|reopened|\bstale\b|no longer (applies|valid|true)|orphaned|"
    r"obsolete|out of date)\b",
    re.IGNORECASE,
)

_UNCERTAINTY_RE = re.compile(
    r"\b(cannot say|did not (attempt|query|verify|check)|not verified|not in-repo|"
    r"i did not|is moot|unverified|out of scope|no figure)\b",
    re.IGNORECASE,
)

_STATUS_DUMP_RE = re.compile(
    r"^(---|Date:|Integrator:|Corpus:|Updated:|updated:|Status:\s*$)",
)

_TURN_MIRROR_RE = re.compile(
    r"^(User:|Assistant:|Human:|AI:|You are |I'll now|Sure,? here|As you asked|"
    r"Let me |Here's the plan)",
    re.IGNORECASE,
)


def _nonempty_lines(value: str) -> list[str]:
    return [line for line in value.splitlines() if line.strip()]


def feat_index_row(value: str) -> bool:
    """True when the entry is mostly a catalog/table-of-contents table: rows that
    are either a markdown link (`[text](target)`) or lead with a bare filename
    (`` `name.md` ``) — the two shapes a doc index or changelog listing takes."""
    lines = _nonempty_lines(value)
    if not lines:
        return False
    hits = sum(
        1 for line in lines
        if _INDEX_ROW_RE.match(line.strip()) or _CATALOG_ROW_RE.match(line.strip())
    )
    return hits / len(lines) > 0.3


def feat_table_heavy(value: str) -> bool:
    lines = _nonempty_lines(value)
    if not lines:
        return False
    hits = sum(1 for line in lines if _TABLE_ROW_RE.match(line))
    return hits / len(lines) > 0.5


def feat_measured(value: str) -> bool:
    return bool(_MEASURED_RE.search(value))


def feat_imperative(value: str) -> bool:
    return bool(_IMPERATIVE_RE.search(value))


def feat_generalization(value: str) -> bool:
    return bool(_GENERALIZATION_RE.search(value))


def feat_superseded(value: str) -> bool:
    return bool(_SUPERSEDED_RE.search(value))


def feat_uncertainty(value: str) -> bool:
    return bool(_UNCERTAINTY_RE.search(value))


def feat_status_dump(value: str) -> bool:
    head = value.strip()
    return bool(_STATUS_DUMP_RE.match(head))


def feat_turn_mirror(value: str) -> bool:
    head = value.strip()
    return bool(_TURN_MIRROR_RE.match(head))


# ---------------------------------------------------------------------------
# Cross-project mention (kept from the baseline, weight sharply reduced: in
# the observed candidate pool this fires on essentially every row because
# most rows come from a multi-project memory bank whose docs casually name
# sibling projects, so on its own it carries almost no ranking information).
# ---------------------------------------------------------------------------


def mentions_other_project(value: str, source_file: str | None, project_id: str, all_project_ids: list[str]) -> bool:
    hay_value = value
    hay_source = source_file or ""
    for other in all_project_ids:
        if other == project_id:
            continue
        if other.lower() in hay_value.lower() or other.lower() in hay_source.lower():
            return True
    return False


# ---------------------------------------------------------------------------
# Weights
# ---------------------------------------------------------------------------

W_CROSS_PROJECT = 0.3
W_GENERALIZATION = 1.5
W_MEASURED = 1.0
W_IMPERATIVE = 0.2
W_INDEX_ROW = -1.5
W_TABLE_HEAVY = -0.3
W_STATUS_DUMP = -1.2
W_TURN_MIRROR = -1.5
W_SUPERSEDED = -0.8
W_UNCERTAINTY = -1.0
W_ACCESSED = 0.4
W_RECENT = 0.2
RECENT_WINDOW_SECONDS = 30 * 24 * 3600


@dataclass
class Candidate:
    id: int
    project_id: str
    path: str
    value: str
    source_file: str | None
    created_at: float
    access_count: int
    rating: float
    entry_created_at: float
    reasons: list[str] = field(default_factory=list)


def score_candidate(row: Candidate, all_project_ids: list[str]) -> float:
    bucket = source_bucket(row.source_file, row.path)
    score = _BUCKET_BASE_SCORE[bucket]

    if mentions_other_project(row.value, row.source_file, row.project_id, all_project_ids):
        score += W_CROSS_PROJECT
    if feat_generalization(row.value):
        score += W_GENERALIZATION
    if feat_measured(row.value):
        score += W_MEASURED
    if feat_imperative(row.value):
        score += W_IMPERATIVE
    if feat_index_row(row.value):
        score += W_INDEX_ROW
    elif feat_table_heavy(row.value):
        # Only penalize generic table density when it isn't already caught
        # by the (stronger) doc-index-row penalty above.
        score += W_TABLE_HEAVY
    if feat_status_dump(row.value):
        score += W_STATUS_DUMP
    if feat_turn_mirror(row.value):
        score += W_TURN_MIRROR
    if feat_superseded(row.value):
        score += W_SUPERSEDED
    if feat_uncertainty(row.value):
        score += W_UNCERTAINTY

    if row.access_count > 0 or row.rating > 0.5:
        score += W_ACCESSED
    if row.created_at - row.entry_created_at <= RECENT_WINDOW_SECONDS:
        score += W_RECENT

    return round(score, 4)


def _row_from_json(obj: dict) -> Candidate:
    return Candidate(
        id=obj["id"],
        project_id=obj.get("project_id", ""),
        path=obj.get("path", ""),
        value=obj.get("value", ""),
        source_file=obj.get("source_file"),
        created_at=float(obj.get("created_at", 0)),
        access_count=int(obj.get("access_count", 0)),
        rating=float(obj.get("rating", 0.0)),
        entry_created_at=float(obj.get("entry_created_at", 0)),
    )


def main(argv: list[str]) -> int:
    if len(argv) != 2:
        print("usage: python3 scorer.py <path-to-candidates-json>", file=sys.stderr)
        return 2

    with open(argv[1], encoding="utf-8") as f:
        candidates_raw = json.load(f)

    rows = [_row_from_json(obj) for obj in candidates_raw]
    all_project_ids = sorted({row.project_id for row in rows})
    meta_path = os.path.join(os.path.dirname(os.path.abspath(argv[1])), "meta.json")
    if os.path.isfile(meta_path):
        try:
            with open(meta_path, encoding="utf-8") as f:
                meta = json.load(f)
            ids = meta.get("allProjectIds")
            if isinstance(ids, list) and ids:
                all_project_ids = ids
        except (OSError, json.JSONDecodeError):
            pass  # fall back to the derived set below

    out = [
        {"id": row.id, "score": score_candidate(row, all_project_ids)}
        for row in rows
    ]
    print(json.dumps(out))
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
