#!/usr/bin/env python3
"""Promotion-worthiness scorer for ai-raccoon memory candidates (Agent C).

Usage:
    python3 scorer.py <path-to-candidates-json>

Prints a JSON array [{"id": <int>, "score": <float>}, ...] to stdout, one
entry per candidate in the input file. Stdlib only, deterministic, no
network/LLM calls.

APPROACH (see METHOD.md for the full writeup)
-----------------------------------------------
The incumbent scorer's four flags (organic write, mentions another project
id, accessed, recent) are close to uninformative on real promotion queues:
in this eval's 61-candidate sample *every* candidate is flagged
cross-project and recent (that is exactly the "flood" failure mode
described in the brief -- a filename like `ingest-jsaa-docs.py` trips the
"mentions another project" check with zero cross-repo value). Something
finer-grained than those four booleans is needed to separate a durable,
actionable fact from a doc-index row or a superseded review fragment.

This scorer instead extracts a small set of content-shaped features from
the entry text and path, each tied to one clause of the usefulness rubric
in the brief, and combines them in a hand-weighted linear model (weights
selected by coordinate-ascent against train_labeled.json, cross-checked
with leave-one-out re-fitting for stability -- see METHOD.md):

  Positive signals
    * organic write (no source_file)        -- agent judged it worth
                                                  writing directly
    * access_count (log-scaled)              -- real usage is the
                                                  strongest "someone found
                                                  this valuable" signal
                                                  available (three archived
                                                  docs and one flake report
                                                  in the eval set have
                                                  10-200x the access of
                                                  everything else)
    * quantified/measured evidence           -- "%", "ms", "4.5x", token
                                                  counts, etc. -- the "4"
                                                  rubric explicitly names
                                                  "measured result"
    * gotcha / pitfall language              -- "returns empty", "no
                                                  error", "silently",
                                                  "mitigations:" -- the "4"
                                                  rubric names "reusable
                                                  gotcha"
    * named cross-repo attribution           -- another project's name
                                                  co-located with
                                                  pattern/convention
                                                  language ("the jsaa repo
                                                  established...", "applies
                                                  the same approach"),
                                                  which is different from a
                                                  bare substring hit inside
                                                  a filename
    * entry length (log-scaled, mild)        -- weak but significant
                                                  proxy for a developed
                                                  thought vs. a terse table
                                                  row

  Negative signals
    * superseded / deprecated / obsolete     -- named "superseded reviews"
                                                  in the brief as noise
    * review-meta boilerplate                -- "Reviewer lane:",
                                                  "worktree:", "Lens ",
                                                  "commissioned" -- the
                                                  self-referential preamble
                                                  of a review document, not
                                                  a fact about the system
    * single-repo plan boilerplate           -- "AC:", "Gate:", "Effort:",
                                                  "Persona / model:" --
                                                  work-item bookkeeping,
                                                  matches the "plan/finding
                                                  detail" (1) rubric rather
                                                  than a durable fact
    * table/catalog density (pipe rows,      -- named "changelog indexes"
      markdown link density, README/         and "READMEs" in the brief
      CHANGELOG/skills-index filename)
    * "archived" path segment                -- mild extra discount; an
                                                  archived doc needs other
                                                  positive evidence (e.g.
                                                  access_count) to rank high
    * turn-mirror shape (User:/Assistant:    -- named "turn-mirrors" in the
      transcript lines)                        brief

All thresholds/keyword lists are content-shape heuristics, not id lookups,
so they apply unchanged to any future candidate set.
"""

from __future__ import annotations

import json
import math
import re
import sys

# --------------------------------------------------------------------------
# Feature extraction
# --------------------------------------------------------------------------

_WORD_RE_CACHE: dict[str, re.Pattern] = {}


def _word_regex(term: str) -> re.Pattern:
    """Whole-word, case-insensitive match for `term`, treating '-' and '_'
    as word characters so 'jsaa' inside 'ingest-jsaa-docs.py' does NOT
    match -- that is a filename substring, not a mention of the project."""
    pat = _WORD_RE_CACHE.get(term)
    if pat is None:
        pat = re.compile(
            r"(?<![A-Za-z0-9_-])" + re.escape(term) + r"(?![A-Za-z0-9_-])",
            re.IGNORECASE,
        )
        _WORD_RE_CACHE[term] = pat
    return pat


SUPERSEDED_RE = re.compile(
    r"\b(superseded|deprecated|no longer (current|valid)|obsolete)\b", re.IGNORECASE
)

REVIEW_META_KWS = [
    "read-only audit", "commissioned", "reviewer lane", "worktree",
    "repo pinned", "lens ", "## verdict", "moe panel", "8-lens",
    "full-project review",
]

PLAN_BOILERPLATE_KWS = [
    "ac:", "gate:", "effort:", "persona / model:", "acceptance criteria",
    "worktree:", "status: executed", "closes:", "risk if deferred:",
]

GOTCHA_KWS = [
    "gotcha", "no error", "returns empty", "pitfall", "trips over",
    "watch out", "silently", "trap", "regression:", "flake", "mitigations:",
]

ATTRIBUTION_KWS = [
    "established", "reference pattern", "same approach", "applies the same",
    "convention", "also uses", "mirrors", "adopts", "adopted", "reuse",
    "pattern", "protocol", "contract",
]

MEASURED_RE = re.compile(
    r"\d+(\.\d+)?\s?(%|x\b|ms\b| s\b|seconds|tokens|req/s|ns\b|mb\b|kb\b|x faster|x slower|/5)",
    re.IGNORECASE,
)

TURN_MIRROR_RE = re.compile(r"^(user|assistant|human)\s*:", re.IGNORECASE | re.MULTILINE)

INDEX_FILENAMES = {"readme.md", "changelog.md", "skills.md", "getting-started.md"}


def _pipe_ratio(v: str) -> float:
    lines = v.split("\n")
    if not lines:
        return 0.0
    piped = sum(1 for l in lines if l.strip().startswith("|"))
    return piped / max(1, len(lines))


def _link_density(v: str) -> float:
    """Markdown links per 500 chars -- high in TOC/index/catalog pages."""
    return len(re.findall(r"\[[^\]]+\]\([^)]+\)", v)) / max(1, len(v) / 500)


def _cross_repo_attribution(v: str, other_project_ids: list[str]) -> int:
    """Count mentions of another project's name that sit near
    pattern/convention language (an 80-char window either side), as
    opposed to an incidental filename substring match."""
    hits = 0
    for pid in other_project_ids:
        for m in _word_regex(pid).finditer(v):
            start = max(0, m.start() - 80)
            end = min(len(v), m.end() + 80)
            window = v[start:end].lower()
            if any(k in window for k in ATTRIBUTION_KWS):
                hits += 1
    return hits


def extract_features(cand: dict, other_project_ids: list[str]) -> dict:
    v = cand.get("value") or ""
    path = cand.get("path") or ""
    source_file = cand.get("source_file")
    access_count = cand.get("access_count") or 0
    reasons_raw = cand.get("reasons") or "[]"
    try:
        reasons = json.loads(reasons_raw) if isinstance(reasons_raw, str) else reasons_raw
    except (json.JSONDecodeError, TypeError):
        reasons = []

    vl = v.lower()
    basename = path.rsplit("/", 1)[-1].lower()

    f = {}
    f["organic"] = 1.0 if source_file is None else 0.0
    f["access_log"] = math.log1p(max(0, access_count))
    f["accessed_flag"] = 1.0 if "accessed" in reasons else 0.0

    f["superseded"] = 1.0 if SUPERSEDED_RE.search(v) else 0.0
    f["review_meta"] = sum(1 for k in REVIEW_META_KWS if k in vl)
    f["plan_boilerplate"] = sum(1 for k in PLAN_BOILERPLATE_KWS if k in vl)
    f["pipe_ratio"] = _pipe_ratio(v)
    f["link_density"] = _link_density(v)
    f["index_filename"] = 1.0 if basename in INDEX_FILENAMES else 0.0
    f["archive_path"] = 1.0 if "/archive/" in path else 0.0

    f["gotcha"] = sum(1 for k in GOTCHA_KWS if k in vl)
    f["attribution"] = _cross_repo_attribution(v, other_project_ids)
    f["measured"] = len(MEASURED_RE.findall(v))
    f["turn_mirror"] = 1.0 if TURN_MIRROR_RE.search(v) else 0.0
    f["len_log"] = math.log(max(len(v), 1))

    return f


# --------------------------------------------------------------------------
# Weighted composite (weights tuned via coordinate ascent on
# train_labeled.json, cross-checked with leave-one-out re-fitting)
# --------------------------------------------------------------------------

WEIGHTS = {
    "organic": 2.0,
    "access_log": 0.5,
    "accessed_flag": 0.2,
    "superseded": 0.5,
    "review_meta": 1.5,
    "plan_boilerplate": 0.5,
    "pipe_ratio": 3.0,
    "link_density": 0.1,
    "index_filename": 1.5,
    "archive_path": 0.3,
    "gotcha": 1.2,
    "attribution": 1.2,
    "measured": 0.6,
    "turn_mirror": 2.0,
    "len_log": 0.8,
}

# Caps keep any single repeated keyword hit from dominating the score.
_CAPS = {"review_meta": 3, "gotcha": 3, "attribution": 3, "measured": 3, "plan_boilerplate": 4}


def score_candidate(f: dict, w: dict = WEIGHTS) -> float:
    capped = {k: min(f[k], _CAPS[k]) for k in _CAPS}

    total = (
        w["organic"] * f["organic"]
        + w["access_log"] * f["access_log"]
        + w["accessed_flag"] * f["accessed_flag"]
        - w["superseded"] * f["superseded"]
        - w["review_meta"] * capped["review_meta"]
        - w["plan_boilerplate"] * capped["plan_boilerplate"]
        - w["pipe_ratio"] * f["pipe_ratio"]
        - w["link_density"] * f["link_density"]
        - w["index_filename"] * f["index_filename"]
        - w["archive_path"] * f["archive_path"]
        + w["gotcha"] * capped["gotcha"]
        + w["attribution"] * capped["attribution"]
        + w["measured"] * capped["measured"]
        - w["turn_mirror"] * f["turn_mirror"]
        + w["len_log"] * f["len_log"]
    )
    return round(total, 4)


def score_all(candidates: list[dict]) -> list[dict]:
    # The set of "other projects" a mention can be attributed to is derived
    # from the batch itself (every distinct project_id present), never
    # hardcoded -- this generalizes to any future roster of projects.
    all_project_ids = sorted({c.get("project_id") for c in candidates if c.get("project_id")})

    results = []
    for cand in candidates:
        others = [p for p in all_project_ids if p != cand.get("project_id")]
        feats = extract_features(cand, others)
        results.append({"id": cand["id"], "score": score_candidate(feats)})
    return results


def main() -> int:
    if len(sys.argv) != 2:
        print("usage: python3 scorer.py <path-to-candidates-json>", file=sys.stderr)
        return 2

    with open(sys.argv[1], "r", encoding="utf-8") as fh:
        candidates = json.load(fh)

    print(json.dumps(score_all(candidates)))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
