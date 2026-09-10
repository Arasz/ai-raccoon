#!/usr/bin/env python3
"""P2 — per-project 100-query corpus generator (plan §P2, AC2.1–AC2.4).

Reads the sanctioned memory-db COPY (read-only, made by make_memory_copy.py —
reused, not re-implemented) and emits corpora/project-corpus-100.json: a header
plus exactly 100 eval queries, every one carrying its own projectId (the
scoping trap: per-project scoping is load-bearing — a jsaa-domain query run
under project ai-raccoon silently serves 0/8 overlap).

Contract (pinned; deviations are plan-visible only):

- Projects are enumerated with ``SELECT DISTINCT project_id FROM entries WHERE
  project_id IS NOT NULL`` — NEVER the ``projects`` table, which is a strict
  subset (5 vs 13 ids at plan time; enumerating it would silently drop
  deepseek-harness at 12,890 rows and 7 others).
- Alias-fold exclusion (review M2): a project whose raw ``entries.project_id``
  folds under the search gate to a DIFFERENT canonical id (aib→ai-badger,
  job-search-ai-assistant→jsaa — 2 projects, 117 rows at plan time) is excluded
  entirely and the exclusion is documented in the header: the gate folds the
  query's projectId, so a raw-spelled anchor could never be served. The fold
  map is read from ``project_id_aliases`` and cross-checked against
  ``repair_requests.map_json`` before it is encoded; disagreement is fatal.
- Candidate pool per project = file-targeted (distinct ``source_file`` with ≥1
  embedded chunk; anchor = the file's first embedded chunk by chunk_index,
  hash tie-break) + content-targeted (embedded rows without source_file; anchor
  = the row, asserted via a verbatim marker unique within the project/scope
  bucket). Rows with no unique verbatim span are pruned before allocation.
- Holdout is reserved up front at candidate level (review S5): seeded ~10% per
  project (``n // 10`` — so projects with <10 candidates reserve none, else
  1-row projects would lose their only candidate). Holdout candidates are never
  turned into queries; their identities are recorded in the header so the
  zero-leak discipline is machine-checkable.
- Exactly 100 eval queries. Sizes scale with sqrt(embedded rows), cap 20 for
  the largest, post-cap deficit redistributed by largest remainder (ties:
  project id ascending), then a >=1-query guarantee per project with >=1
  embedded row, funded by the largest allocation (ties: id ascending).
- 3 paraphrase frames per target, rotated (frame = target ordinal % 3); a
  normalized-text collision first advances the target to its next frame, then
  re-renders with a deterministic target discriminator (source-file name, then
  the full source path, or the content-hash prefix); exhausted, generation
  fails loudly.
- File-targeted queries carry ``expectedHash`` (resolved from the copy, asserted
  unique) + ``targetProjectId`` + ``targetScope``; content-targeted carry
  ``expectedHash: null`` + a unique verbatim ``contentMarker``.
- Determinism (AC2.1): SEED constant, stable sort + seeded tie-break, no
  timestamps; two runs on the same copy are byte-identical.
- Header carries ``snapshotSha256`` of the copy (H3) and ``seed``.

Run:
    python scripts/retrieval_tuning/build_project_corpus.py \
        [--copy /tmp/continue-testing-algorithm/datasets/memory-copy.db] \
        [--output scripts/retrieval_tuning/corpora/project-corpus-100.json]
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import random
import re
import sqlite3
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from retrieval_tuning import repo_data  # noqa: E402

# P3 AC2: generator constants live in data/corpora/project-corpus-100.json.
_GENERATOR = repo_data.CORPORA["project-corpus-100"]
SEED = _GENERATOR["SEED"]  # determinism contract (AC2.1): byte-identical JSON
TOTAL_QUERIES = _GENERATOR["TOTAL_QUERIES"]
QUERY_CAP = _GENERATOR["QUERY_CAP"]
SEARCH_LIMIT = _GENERATOR["SEARCH_LIMIT"]  # top-8 set overlap: the corpus asks for it

DEFAULT_COPY = Path("/tmp/continue-testing-algorithm/datasets/memory-copy.db")
DEFAULT_OUTPUT = (
    Path(__file__).resolve().parents[2]
    / "scripts" / "retrieval_tuning" / "corpora" / "project-corpus-100.json"
)

# Paraphrase frames per target, rotated by target ordinal (plan §P2-2c).
FRAMES = tuple(_GENERATOR["FRAMES"])
_TOPIC_MAX_CHARS = 100
_TOPIC_MIN_CUT = 8
_MARKER_MIN_CHARS = 8
_MARKER_WINDOW_LENS = (120, 240)
_ANSWER_SPAN_CHARS = 160


def _sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _normalize(text: str) -> str:
    return re.sub(r"\s+", " ", text.lower()).strip()


def _derive_topic(value: str) -> str:
    """First topical span of the chunk text: leading markdown heading marker
    stripped, first sentence (or ':' clause) kept, hard-capped at 100 chars on
    a word boundary. Never empty."""
    text = re.sub(r"\s+", " ", value).strip()
    text = re.sub(r"^#{1,6}\s+", "", text)
    m = re.search(r"[.;:!?](\s|$)", text)
    cut = m.start() if m and m.start() >= _TOPIC_MIN_CUT else min(len(text), _TOPIC_MAX_CHARS)
    topic = text[:cut]
    if len(topic) > _TOPIC_MAX_CHARS:
        topic = topic[:_TOPIC_MAX_CHARS].rsplit(" ", 1)[0]
    topic = topic.strip().rstrip(".,;:!?'\"`*)]—–-")
    topic = re.sub(r"^<!--+\s*", "", topic)
    topic = re.sub(r"\s*-+->$", "", topic)
    return topic or text[:_TOPIC_MAX_CHARS].strip() or "this note"


def _answer_span(value: str) -> str:
    return re.sub(r"\s+", " ", value).strip()[:_ANSWER_SPAN_CHARS]


# ------------------------------------------------------------------ fold map (review M2)


def _load_alias_fold_map(conn: sqlite3.Connection) -> dict[str, str]:
    """Gate fold map: alias -> canonical winner, read from project_id_aliases
    (kind='alias') and cross-checked against repair_requests.map_json — the two
    sources must agree before any exclusion is encoded (plan M2)."""
    fold: dict[str, str] = {}
    for row in conn.execute("SELECT alias, winner FROM project_id_aliases WHERE kind='alias'"):
        if row["alias"] is None or row["winner"] is None:
            raise RuntimeError("project_id_aliases: alias row with NULL alias or winner")
        fold[row["alias"]] = row["winner"]
    row = conn.execute(
        "SELECT map_json FROM repair_requests WHERE kind='project-ids'").fetchone()
    if row is None or row["map_json"] is None:
        raise RuntimeError(
            "repair_requests has no project-ids map_json; cannot cross-check the fold map")
    declared = {
        entry.get("Alias"): entry.get("Canonical")
        for entry in json.loads(row["map_json"]).get("Aliases", [])
    }
    if declared != fold:
        raise RuntimeError(
            f"alias fold sources disagree: project_id_aliases={fold} vs "
            f"repair_requests.map_json={declared}")
    return fold


# ------------------------------------------------------------------ enumeration + candidates


def _enumerate_projects(conn: sqlite3.Connection) -> list[str]:
    """Distinct project ids FROM ENTRIES — never the projects table (strict
    subset; see module docstring)."""
    return [
        row[0]
        for row in conn.execute(
            "SELECT DISTINCT project_id FROM entries "
            "WHERE project_id IS NOT NULL ORDER BY project_id"
        )
    ]


def _embedded_row_counts(conn: sqlite3.Connection) -> dict[str, int]:
    """Embedded row counts per project. Workspace sandbox rows (workspace_id set)
    are excluded throughout: they are isolated outbox entries, never served by a
    project-scoped search, and can never be anchors."""
    return {
        row[0]: row[1]
        for row in conn.execute(
            "SELECT project_id, count(*) FROM entries "
            "WHERE project_id IS NOT NULL AND embed_state='embedded' AND workspace_id IS NULL "
            "GROUP BY project_id"
        )
    }


def _embedded_scope_counts(conn: sqlite3.Connection, project_id: str) -> tuple[int, int]:
    """(committed, shared) embedded rows for one project.

    Committed = scope IN ('project','custom') — the rows the search gate's
    project predicate can reach; a raw-spelled project_id can never match it.
    Shared = scope='shared' — global, served by shared/all queries regardless
    of the project_id spelling, so those rows are NOT lost to the fold.
    NULL-scope rows count as neither (the ingest rule never sees them)."""
    committed = shared = 0
    for scope, n in conn.execute(
        "SELECT scope, count(*) FROM entries "
        "WHERE project_id=? AND embed_state='embedded' AND workspace_id IS NULL "
        "GROUP BY scope",
        (project_id,),
    ):
        if scope in ("project", "custom"):
            committed += n
        elif scope == "shared":
            shared += n
    return committed, shared


def _exclusion_reason(project_id: str, canonical_id: str, committed: int, shared: int) -> str:
    """Per-project reason naming which half of the exclusion is actually unservable."""
    base = (f"raw entries.project_id {project_id!r} folds to canonical "
            f"{canonical_id!r} under the search gate")
    if shared == 0:
        shared_part = "it has no scope='shared' rows"
    elif shared == 1:
        shared_part = ("its 1 scope='shared' row is global, remains ingested "
                       "and is served by shared/all queries")
    else:
        shared_part = (f"its {shared} scope='shared' rows are global, remain ingested "
                       "and are served by shared/all queries")
    if committed:
        return (f"{base}, so its {committed} committed project/custom rows can never be "
                f"served; {shared_part}")
    return f"{base}; it has no committed project/custom rows, and {shared_part}"


def _file_candidates(conn: sqlite3.Connection, project_id: str) -> list[dict]:
    """Distinct source_file with >=1 embedded chunk; the anchor is the file's
    first embedded chunk (lowest chunk_index, hash tie-break)."""
    cands: dict[str, dict] = {}
    for row in conn.execute(
        "SELECT source_file, hash, scope FROM entries "
        "WHERE project_id=? AND embed_state='embedded' AND workspace_id IS NULL "
        "AND source_file IS NOT NULL AND source_file != '' "
        "ORDER BY source_file, chunk_index, hash",
        (project_id,),
    ):
        if row["source_file"] not in cands:
            cands[row["source_file"]] = {
                "kind": "file",
                "kind_rank": 0,
                "key": row["source_file"],
                "cand_id": f"file:{row['source_file']}",
                "anchor_hash": row["hash"],
                "scope": row["scope"],
            }
    return [cands[key] for key in sorted(cands)]


def _derive_unique_marker(
    conn: sqlite3.Connection, project_id: str, scope: str, value: str
) -> str | None:
    """Verbatim span search, longest-window-first per line, until a span that
    matches exactly one row in the project/scope bucket is found. None when the
    value shares every candidate span with a sibling row."""
    tried: set[str] = set()
    for raw_line in value.split("\n"):
        line = raw_line.strip()
        if not line:
            continue
        for want in (*_MARKER_WINDOW_LENS, len(line)):
            span = line[:want].strip()
            if len(span) < _MARKER_MIN_CHARS or span in tried:
                continue
            tried.add(span)
            n = conn.execute(
                "SELECT count(*) FROM entries "
                "WHERE project_id=? AND scope=? AND instr(value, ?) > 0",
                (project_id, scope, span),
            ).fetchone()[0]
            if n == 1:
                return span
    return None


def _content_candidates(conn: sqlite3.Connection, project_id: str) -> list[dict]:
    """Embedded rows without a source_file, each anchored by a verbatim marker
    unique within the project/scope bucket. Rows with no unique span are pruned
    before allocation — they could never be anchor-verified."""
    out: list[dict] = []
    for row in conn.execute(
        "SELECT hash, value, scope FROM entries "
        "WHERE project_id=? AND embed_state='embedded' AND workspace_id IS NULL "
        "AND (source_file IS NULL OR source_file='') ORDER BY hash",
        (project_id,),
    ):
        marker = _derive_unique_marker(conn, project_id, row["scope"], row["value"])
        if marker is None:
            continue
        out.append({
            "kind": "content",
            "kind_rank": 1,
            "key": row["hash"],
            "cand_id": f"content:{row['hash']}",
            "hash": row["hash"],
            "scope": row["scope"],
            "marker": marker,
            "value": row["value"],
        })
    return out


def _assert_hash_unique(conn: sqlite3.Connection, hash_value: str) -> None:
    n = conn.execute(
        "SELECT count(*) FROM entries WHERE hash=?", (hash_value,)).fetchone()[0]
    if n != 1:
        raise RuntimeError(
            f"anchor hash {hash_value[:16]}... is not unique in the copy ({n} rows)")


# ------------------------------------------------------------------ allocation (pinned)


def _allocate_queries(weights: dict[str, float], total: int, cap: int) -> dict[str, int]:
    """Pinned allocation (plan §P2-2c): sqrt(embedded rows) shares; iterative
    cap at ``cap`` for the largest (renormalising over the uncapped rest); the
    post-cap deficit redistributed by largest remainder (ties: project id
    ascending); finally a >=1-query guarantee per project, funded by the largest
    allocation (ties: id ascending) — deterministic, no implementer judgment."""
    remaining = set(weights)
    alloc: dict[str, int] = {}
    budget = total
    shares: dict[str, float] = {}
    while remaining:
        weight_sum = sum(weights[p] for p in remaining)
        shares = {p: budget * weights[p] / weight_sum for p in remaining}
        capped = {p for p in remaining if shares[p] > cap}
        if not capped:
            break
        for p in capped:
            alloc[p] = cap
            budget -= cap
            remaining.discard(p)
        if not remaining:
            raise RuntimeError(
                f"cannot distribute {total} queries under the cap of {cap}: every project capped")
    floors = {p: int(shares[p]) for p in remaining}
    deficit = budget - sum(floors.values())
    if deficit < 0:
        raise RuntimeError("allocation floors exceed the budget — cap logic inconsistent")
    order = sorted(remaining, key=lambda p: (-(shares[p] - floors[p]), p))
    for p in order[:deficit]:
        floors[p] += 1
    alloc.update(floors)
    for zero in sorted(p for p in alloc if alloc[p] == 0):
        donor = min(alloc, key=lambda p: (-alloc[p], p))
        if alloc[donor] < 2:
            raise RuntimeError(
                f"cannot guarantee >=1 query for {zero}: no project has a spare query")
        alloc[donor] -= 1
        alloc[zero] += 1
    return alloc


# ------------------------------------------------------------------ query rendering


def _render_query(
    value: str, frame_base: int, seen_norm: set[str], disambiguators: list[str]
) -> tuple[str, int]:
    """Frame ``frame_base`` first; on a normalized-text collision advance to the
    next paraphrase frame, then re-render with each deterministic discriminator
    (source-file name, full path / content-hash prefix); exhausted -> loud
    failure. Returns (query text, frame actually used)."""
    topic = _derive_topic(value)
    attempts: list[tuple[str, int]] = []
    for offset in range(len(FRAMES)):
        frame_idx = (frame_base + offset) % len(FRAMES)
        attempts.append((FRAMES[frame_idx].replace("{topic}", topic), frame_idx))
    for discriminator in disambiguators:
        if not discriminator:
            continue
        for offset in range(len(FRAMES)):
            frame_idx = (frame_base + offset) % len(FRAMES)
            attempts.append((
                FRAMES[frame_idx].replace("{topic}", f"{topic} ({discriminator})"),
                frame_idx,
            ))
    for text, frame_idx in attempts:
        if _normalize(text) not in seen_norm:
            return text, frame_idx
    raise RuntimeError(f"all paraphrase frames collide for topic {topic!r}")


def _build_project_queries(
    conn: sqlite3.Connection,
    project_id: str,
    allocation: int,
    seen_norm: set[str],
) -> tuple[dict, list[dict]]:
    """Candidates -> seeded holdout (n // 10; none under 10 candidates, review
    S5) -> the first ``allocation`` surviving targets -> query records."""
    candidates = _file_candidates(conn, project_id) + _content_candidates(conn, project_id)
    if not candidates:
        raise RuntimeError(f"project {project_id}: no candidates but allocation {allocation}")

    # stable sort + seeded tie-break: keys are unique today; the seeded jitter
    # is the deterministic tie-break layer should a future bank produce ties.
    rng = random.Random(f"{SEED}:{project_id}")
    for cand, jitter in zip(candidates, [rng.random() for _ in candidates]):
        cand["jitter"] = jitter
    candidates.sort(key=lambda c: (c["kind_rank"], c["key"], c["jitter"]))

    holdout_count = len(candidates) // 10
    holdout_ids: set[str] = set()
    if holdout_count:
        holdout_ids = {
            candidates[i]["cand_id"]
            for i in rng.sample(range(len(candidates)), holdout_count)
        }
    pool = [c for c in candidates if c["cand_id"] not in holdout_ids]
    if allocation > len(pool):
        raise RuntimeError(
            f"project {project_id}: allocation {allocation} exceeds {len(pool)} "
            "available candidates after holdout")
    targets = pool[:allocation]

    holdout_files = sorted(
        c["key"] for c in candidates if c["kind"] == "file" and c["cand_id"] in holdout_ids)
    holdout_hashes = sorted(
        c["hash"] for c in candidates if c["kind"] == "content" and c["cand_id"] in holdout_ids)
    record = {
        "embeddedRows": None,  # filled by the caller
        "fileCandidates": sum(1 for c in candidates if c["kind"] == "file"),
        "contentCandidates": sum(1 for c in candidates if c["kind"] == "content"),
        "holdoutCandidates": len(holdout_ids),
        "holdoutFiles": holdout_files,
        "holdoutHashes": holdout_hashes,
    }

    queries: list[dict] = []
    for ordinal, target in enumerate(targets):
        frame_base = ordinal % 3
        if target["kind"] == "file":
            _assert_hash_unique(conn, target["anchor_hash"])
            value = conn.execute(
                "SELECT value FROM entries WHERE hash=?", (target["anchor_hash"],)
            ).fetchone()[0]
            discriminators = [Path(target["key"]).name, target["key"]]
            text, frame_idx = _render_query(value, frame_base, seen_norm, discriminators)
            seen_norm.add(_normalize(text))
            queries.append({
                "projectId": project_id,
                "targetProjectId": project_id,
                "targetScope": target["scope"],
                "category": "file-targeted",
                "query": text,
                "expectedHash": target["anchor_hash"],
                "contentMarker": None,
                "expectedSource": target["key"],
                "answerSpan": _answer_span(value),
                "frame": frame_idx,
                "searchLimit": SEARCH_LIMIT,
                "holdout": False,
            })
        else:
            discriminators = [target["hash"][:12]]
            text, frame_idx = _render_query(target["value"], frame_base, seen_norm, discriminators)
            seen_norm.add(_normalize(text))
            queries.append({
                "projectId": project_id,
                "targetProjectId": project_id,
                "targetScope": target["scope"],
                "category": "content-targeted",
                "query": text,
                "expectedHash": None,
                "contentMarker": target["marker"],
                "expectedSource": None,
                "answerSpan": _answer_span(target["value"]),
                "frame": frame_idx,
                "searchLimit": SEARCH_LIMIT,
                "holdout": False,
            })
    return record, queries


# ------------------------------------------------------------------ generate


def generate(copy_path: Path, output_path: Path) -> dict:
    """Build the 100-query per-project corpus from the copy and write it as
    JSON (sorted keys, indent 2, trailing newline — byte-identical reruns)."""
    copy_path = Path(copy_path)
    output_path = Path(output_path)
    if not copy_path.exists():
        raise RuntimeError(f"memory-db copy not found: {copy_path}")
    snapshot_sha = _sha256_file(copy_path)

    conn = sqlite3.connect(f"file:{copy_path}?mode=ro", uri=True)
    conn.row_factory = sqlite3.Row
    try:
        fold_map = _load_alias_fold_map(conn)
        projects = _enumerate_projects(conn)
        embedded = _embedded_row_counts(conn)

        excluded: list[dict] = []
        weights: dict[str, float] = {}
        zero_embedded: list[str] = []
        for pid in projects:
            canonical = fold_map.get(pid, pid)
            if canonical != pid:
                committed, shared = _embedded_scope_counts(conn, pid)
                excluded.append({
                    "projectId": pid,
                    "canonicalId": canonical,
                    "embeddedRows": embedded.get(pid, 0),
                    "committedRows": committed,
                    "sharedRows": shared,
                    "reason": _exclusion_reason(pid, canonical, committed, shared),
                })
            elif embedded.get(pid, 0) >= 1:
                weights[pid] = math.sqrt(embedded[pid])
            else:
                zero_embedded.append(pid)

        allocation = _allocate_queries(weights, TOTAL_QUERIES, QUERY_CAP)

        header_projects: dict[str, dict] = {}
        queries: list[dict] = []
        seen_norm: set[str] = set()
        for pid in sorted(set(weights) | set(zero_embedded)):
            if pid in weights:
                record, project_queries = _build_project_queries(
                    conn, pid, allocation[pid], seen_norm)
                record["embeddedRows"] = embedded[pid]
                record["queries"] = len(project_queries)
                header_projects[pid] = record
                queries.extend(project_queries)
            else:
                header_projects[pid] = {
                    "embeddedRows": 0,
                    "fileCandidates": 0,
                    "contentCandidates": 0,
                    "holdoutCandidates": 0,
                    "holdoutFiles": [],
                    "holdoutHashes": [],
                    "queries": 0,
                }
    finally:
        conn.close()

    if len(queries) != TOTAL_QUERIES:
        raise RuntimeError(f"expected {TOTAL_QUERIES} queries, built {len(queries)}")
    for i, query in enumerate(queries, start=1):
        query["id"] = f"C{i:03d}"  # C001..C100, in project-sorted generation order
    corpus = {
        "header": {
            "generator": "build_project_corpus.py",
            "seed": SEED,
            "queryCount": len(queries),
            "snapshotSha256": snapshot_sha,
            "aliasFoldSources": ["project_id_aliases", "repair_requests.map_json"],
            "excludedProjects": excluded,
            "projects": header_projects,
        },
        "queries": queries,
    }
    output_path.parent.mkdir(parents=True, exist_ok=True)
    with output_path.open("w", encoding="utf-8") as fh:
        json.dump(corpus, fh, indent=2, sort_keys=True, ensure_ascii=False)
        fh.write("\n")
    return corpus


def main() -> None:
    parser = argparse.ArgumentParser(
        description="P2 per-project 100-query corpus generator (plan §P2)")
    parser.add_argument("--copy", type=Path, default=DEFAULT_COPY,
                        help="memory-db copy, read-only (make_memory_copy.py)")
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT,
                        help="output corpus JSON path")
    args = parser.parse_args()
    corpus = generate(args.copy, args.output)
    header = corpus["header"]
    print(f"wrote {header['queryCount']} queries to {args.output}")
    print(f"snapshot sha256: {header['snapshotSha256']}")
    print("per project (embedded rows / candidates / holdout / queries):")
    for pid in sorted(header["projects"]):
        rec = header["projects"][pid]
        print(f"  {pid:28s} {rec['embeddedRows']:6d} / "
              f"{rec['fileCandidates'] + rec['contentCandidates']:5d} / "
              f"{rec['holdoutCandidates']:4d} / {rec['queries']:3d}")
    for entry in header["excludedProjects"]:
        print(f"  EXCLUDED {entry['projectId']} -> {entry['canonicalId']} "
              f"({entry['embeddedRows']} embedded rows): {entry['reason']}")


if __name__ == "__main__":
    main()
