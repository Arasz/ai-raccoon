"""P2 corpus gates (plan §P2, AC2.1–AC2.4) for the per-project 100-query corpus.

Four tests mirroring the four ACs, each fail-capable:

- test_determinism_two_generate_runs_are_byte_identical (AC2.1): two generate()
  runs on a synthetic bank are byte-identical; when the copy matching the
  committed header's snapshotSha256 is present (AI_RACCOON_EVAL_COPY or the
  make_memory_copy default), a fresh generation is byte-identical to the
  committed artifact.
- test_shape_exactly_100_queries_with_required_schema (AC2.2): ids C001..C100,
  required keys incl. projectId / expectedHash|null / holdout:false, no
  duplicate query text (exact + normalized) — on the synthetic output AND on
  the committed artifact.
- test_anchors_resolve (AC2.3): every expectedHash resolves to exactly one row
  with matching project_id/scope, content markers unique in their bucket; the
  generator raises loudly on a duplicate anchor hash and on a marker-starved
  project (fail-capability proofs).
- test_coverage_and_holdout (AC2.4): every fold-consistent project with >=1
  embedded row appears >=1x; fold-divergent projects excluded and documented;
  holdout candidates appear in zero queries — and the leak detector is proven
  to fail on an injected leak.

Synthetic tmp sqlite fixtures only for the core gates — no live bank needed.
The committed-artifact halves are gated on the copy matching the artifact
header's snapshotSha256 (H3) and skip with a precise reason otherwise.
"""
from __future__ import annotations

import hashlib
import importlib.util
import json
import os
import re
import sqlite3
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parents[2]
GENERATOR_PATH = REPO_ROOT / "scripts" / "retrieval_tuning" / "build_project_corpus.py"
ARTIFACT_PATH = REPO_ROOT / "scripts" / "retrieval_tuning" / "corpora" / "project-corpus-100.json"
COPY_PATH = Path(
    os.environ.get(
        "AI_RACCOON_EVAL_COPY",
        "/tmp/continue-testing-algorithm/datasets/memory-copy.db",
    )
)

SEED = 42
TOTAL_QUERIES = 100
QUERY_CAP = 20

HEADER_KEYS = {
    "generator",
    "seed",
    "queryCount",
    "snapshotSha256",
    "aliasFoldSources",
    "excludedProjects",
    "projects",
}
QUERY_KEYS = {
    "id",
    "projectId",
    "targetProjectId",
    "targetScope",
    "category",
    "query",
    "expectedHash",
    "contentMarker",
    "expectedSource",
    "answerSpan",
    "frame",
    "searchLimit",
    "holdout",
}
PROJECT_KEYS = {
    "embeddedRows",
    "fileCandidates",
    "contentCandidates",
    "holdoutCandidates",
    "holdoutFiles",
    "holdoutHashes",
    "queries",
}


# ------------------------------------------------------------------ helpers


def _load_generator():
    assert GENERATOR_PATH.exists(), f"generator missing: {GENERATOR_PATH}"
    spec = importlib.util.spec_from_file_location("build_project_corpus", GENERATOR_PATH)
    mod = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(mod)
    return mod


def _sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def _normalize(text: str) -> str:
    return re.sub(r"\s+", " ", text.lower()).strip()


def _snapshot_mismatch_reason() -> str | None:
    """None when the committed artifact pins exactly the copy at COPY_PATH (H3);
    otherwise a precise reason why the committed-artifact half must skip."""
    if not ARTIFACT_PATH.exists():
        return f"committed artifact missing: {ARTIFACT_PATH}"
    if not COPY_PATH.exists():
        return f"memory-db copy missing: {COPY_PATH}"
    header = json.loads(ARTIFACT_PATH.read_text(encoding="utf-8"))["header"]
    if header.get("snapshotSha256") != _sha256(COPY_PATH):
        return (
            "copy at " f"{COPY_PATH} is not the snapshot the artifact header pins "
            f"({header.get('snapshotSha256', '?')[:12]}... != {_sha256(COPY_PATH)[:12]}...); "
            "regenerate the artifact from this copy to re-arm the check"
        )
    return None


# ------------------------------------------------------------------ synthetic bank fixture


_FIXTURE_SCHEMA = """
CREATE TABLE entries (
    id INTEGER PRIMARY KEY,
    hash TEXT NOT NULL,
    value TEXT NOT NULL,
    scope TEXT,
    project_id TEXT,
    embed_state TEXT NOT NULL DEFAULT 'pending',
    source_file TEXT,
    chunk_index INTEGER NOT NULL DEFAULT 0,
    workspace_id TEXT
);
CREATE TABLE project_id_aliases (
    alias TEXT PRIMARY KEY,
    winner TEXT,
    kind TEXT NOT NULL,
    applied_at INTEGER NOT NULL
);
CREATE TABLE repair_requests (
    kind TEXT PRIMARY KEY,
    requested_at INTEGER NOT NULL,
    finished_at INTEGER,
    map_json TEXT
);
"""

# files per project (x2 embedded chunks each); delta/tiny exercise the
# content-targeted path (few files, many source_file-less rows).
_FILE_PROJECTS = {"alpha": 200, "beta": 100, "gamma": 60, "epsilon": 30, "zeta": 30,
                  "eta": 20, "theta": 10, "delta": 2}
_CONTENT_ROWS = {"alpha": 12, "delta": 30}


def _make_db(path: Path, *, dup_value_content: bool = False, dup_anchor_hash: bool = False) -> None:
    """Build a tiny synthetic bank: 9 fold-consistent projects (alpha..tiny),
    one alias-fold-divergent project (folddiv -> alpha), one zero-embedded
    project (ghost). Sized so the plan's allocation lands at exactly 100."""
    conn = sqlite3.connect(path)
    conn.executescript(_FIXTURE_SCHEMA)
    seq = [0]

    def add(project: str, value: str, *, source_file: str | None, chunk_index: int = 0,
            state: str = "embedded", scope: str = "project", force_hash: str | None = None) -> str:
        seq[0] += 1
        h = force_hash if force_hash is not None else f"{seq[0]:064x}"
        conn.execute(
            "INSERT INTO entries (hash, value, scope, project_id, embed_state, "
            "source_file, chunk_index, workspace_id) VALUES (?,?,?,?,?,?,?,NULL)",
            (h, value, scope, project, state, source_file, chunk_index),
        )
        return h

    for project, n_files in _FILE_PROJECTS.items():
        for n in range(n_files):
            # poison: both chunks of delta/file-0000 claim one hash
            shared = f"{seq[0] + 1:064x}" if (dup_anchor_hash and project == "delta" and n == 0) else None
            for chunk in range(2):
                if shared is not None:
                    add(project, f"{project} file doc 0000 chunk {chunk}: covers the {project} "
                                 f"storage layout, retry policy and quota notes for module 0.",
                        source_file=f"docs/{project}/file-0000.md", chunk_index=chunk,
                        force_hash=shared)
                else:
                    add(project, f"{project} file doc {n:04d} chunk {chunk}: covers the {project} "
                                 f"storage layout, retry policy and quota notes for module {n}.",
                        source_file=f"docs/{project}/file-{n:04d}.md", chunk_index=chunk)
    for project, n_rows in _CONTENT_ROWS.items():
        for n in range(n_rows):
            value = (
                f"{project} shared observation: identical text reused across every "
                f"content row to defeat marker uniqueness."
                if dup_value_content and project == "delta"
                else f"{project} content observation {n:02d}: distinctive marker phrase "
                     f"{project}-{n} about the incident postmortem."
            )
            add(project, value, source_file=None)
    # tiny: 1 file candidate + 2 content rows — no holdout (<10 candidates, review S5)
    add("tiny", "tiny file doc 0000 chunk 0: covers the tiny storage layout and quota notes.",
        source_file="docs/tiny/file-0000.md")
    for n in range(2):
        add("tiny", f"tiny content observation {n:02d}: distinctive marker phrase tiny-{n}.",
            source_file=None)
    # folddiv: raw project_id folds to alpha under the search gate -> excluded (review M2)
    add("folddiv", "folddiv file doc 0000 chunk 0: unreachable anchor while spelled raw.",
        source_file="docs/folddiv/file-0000.md")
    for n in range(2):
        add("folddiv", f"folddiv content observation {n:02d}: marker phrase folddiv-{n}.",
            source_file=None)
    # folddiv's shared row IS servable (global tier): the exclusion manifest
    # must distinguish it from the committed rows that can never be served.
    add("folddiv", "folddiv shared observation: global tier row under the raw spelling.",
        source_file=None, scope="shared")
    # sharedonly: a raw spelling whose only rows are shared -> committed=0.
    add("sharedonly", "sharedonly shared observation: global tier under a raw spelling.",
        source_file=None, scope="shared")
    # ghost: enumerated but zero embedded rows -> no candidates, no queries
    for n in range(5):
        add("ghost", f"ghost pending row {n}: never embedded.", source_file=None, state="pending")

    conn.executemany(
        "INSERT INTO project_id_aliases (alias, winner, kind, applied_at) VALUES (?,?,?,1)",
        [("folddiv", "alpha", "alias"), ("ghost-alias", "beta", "alias"),
         ("sharedonly", "beta", "alias"), ("qa-noise", None, "drop")],
    )
    map_json = json.dumps({
        "Aliases": [{"Alias": "folddiv", "Canonical": "alpha"},
                    {"Alias": "ghost-alias", "Canonical": "beta"},
                    {"Alias": "sharedonly", "Canonical": "beta"}],
        "Canonicals": ["alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta",
                       "theta", "tiny", "ghost"],
        "Dropped": ["qa-noise"],
    })
    conn.execute(
        "INSERT INTO repair_requests (kind, requested_at, finished_at, map_json) "
        "VALUES ('project-ids', 1, 1, ?)", (map_json,))
    conn.commit()
    conn.close()


def _corpus_db(tmp_path: Path, **kwargs) -> Path:
    db = tmp_path / "synthetic-memory-copy.db"
    _make_db(db, **kwargs)
    return db


def _generate_to(tmp_path: Path, db: Path, name: str) -> Path:
    mod = _load_generator()
    out = tmp_path / name
    mod.generate(copy_path=db, output_path=out)
    assert out.exists()
    return out


def _load_corpus(path: Path) -> dict:
    data = json.loads(path.read_text(encoding="utf-8"))
    assert isinstance(data, dict) and set(data.keys()) == {"header", "queries"}, (
        f"corpus root must be {{header, queries}}, got {sorted(data.keys())}")
    return data


def _holdout_leaks(corpus: dict, conn: sqlite3.Connection) -> list[str]:
    """Return a reason per query whose anchor resolves to a holdout candidate
    (held-out file or held-out content row) of its own project."""
    header = corpus["header"]
    leaks: list[str] = []
    conn.row_factory = sqlite3.Row
    holdout_files = {p: set(rec.get("holdoutFiles") or []) for p, rec in header["projects"].items()}
    holdout_hashes = {p: set(rec.get("holdoutHashes") or []) for p, rec in header["projects"].items()}
    for q in corpus["queries"]:
        pid = q["projectId"]
        if q["expectedHash"] is not None:
            rows = conn.execute(
                "SELECT source_file, hash FROM entries WHERE hash=?", (q["expectedHash"],)
            ).fetchall()
            assert len(rows) == 1, f"{q['id']}: anchor hash not unique in the bank"
            if rows[0]["source_file"] and rows[0]["source_file"] in holdout_files.get(pid, set()):
                leaks.append(f"{q['id']}: anchors held-out file {rows[0]['source_file']}")
            if q["expectedHash"] in holdout_hashes.get(pid, set()):
                leaks.append(f"{q['id']}: anchors held-out content row {q['expectedHash'][:12]}...")
        if q["contentMarker"] is not None:
            rows = conn.execute(
                "SELECT hash FROM entries WHERE project_id=? AND scope=? AND instr(value, ?) > 0",
                (pid, q["targetScope"], q["contentMarker"]),
            ).fetchall()
            assert len(rows) == 1, f"{q['id']}: contentMarker not unique in its bucket"
            if rows[0]["hash"] in holdout_hashes.get(pid, set()):
                leaks.append(f"{q['id']}: anchors held-out content row {rows[0]['hash'][:12]}...")
    return leaks


# ------------------------------------------------------------------ AC2.1 determinism


def test_determinism_two_generate_runs_are_byte_identical(tmp_path: Path) -> None:
    mod = _load_generator()
    db = _corpus_db(tmp_path)
    out1 = tmp_path / "run1.json"
    out2 = tmp_path / "run2.json"
    mod.generate(copy_path=db, output_path=out1)
    mod.generate(copy_path=db, output_path=out2)
    b1, b2 = out1.read_bytes(), out2.read_bytes()
    assert b1 == b2, "two generator runs on the same bank produced different bytes"

    # Committed-artifact half (AC2.1's second clause): armed only when the copy
    # at COPY_PATH is the exact snapshot the artifact header pins (H3).
    reason = _snapshot_mismatch_reason()
    if reason is not None:
        pytest.skip(f"committed-artifact equality not checkable: {reason}")
    out3 = tmp_path / "run3.json"
    mod.generate(copy_path=COPY_PATH, output_path=out3)
    assert out3.read_bytes() == ARTIFACT_PATH.read_bytes(), (
        "committed project-corpus-100.json differs from a fresh generation of "
        "the snapshot its header pins")


# ------------------------------------------------------------------ AC2.2 shape


def _assert_shape(corpus: dict, source: str) -> None:
    header, queries = corpus["header"], corpus["queries"]
    assert set(header.keys()) == HEADER_KEYS, f"{source}: unexpected header keys"
    assert header["seed"] == SEED, f"{source}: header seed must be the pinned SEED"
    assert header["queryCount"] == len(queries) == TOTAL_QUERIES, (
        f"{source}: expected exactly {TOTAL_QUERIES} eval queries")
    assert len(header["projects"]) > 0, f"{source}: header records no projects"
    for pid, rec in header["projects"].items():
        assert set(rec.keys()) == PROJECT_KEYS, f"{source}: bad per-project keys for {pid}"

    ids = [q["id"] for q in queries]
    assert len(set(ids)) == TOTAL_QUERIES, f"{source}: duplicate ids"
    assert ids == [f"C{i:03d}" for i in range(1, TOTAL_QUERIES + 1)], (
        f"{source}: ids must be exactly C001..C100")

    seen_exact, seen_norm = set(), set()
    for q in queries:
        assert set(q.keys()) == QUERY_KEYS, (
            f"{source}: unexpected key set in {q.get('id')}: {set(q.keys()) ^ QUERY_KEYS}")
        assert isinstance(q["projectId"], str) and q["projectId"], f"{q['id']}: missing projectId"
        assert q["targetProjectId"] == q["projectId"], (
            f"{q['id']}: the scoping trap — a query must carry its own projectId")
        assert q["targetScope"] in {"project", "shared", "custom"}, f"{q['id']}: bad scope"
        assert q["category"] in {"file-targeted", "content-targeted"}, f"{q['id']}: bad category"
        if q["category"] == "file-targeted":
            assert isinstance(q["expectedHash"], str) and re.fullmatch(r"[0-9a-f]{64}", q["expectedHash"]), (
                f"{q['id']}: file-targeted query must carry a 64-hex expectedHash")
            assert q["contentMarker"] is None and q["expectedSource"], (
                f"{q['id']}: file-targeted query must carry expectedSource, no contentMarker")
        else:
            assert q["expectedHash"] is None, f"{q['id']}: content-targeted expectedHash must be null"
            assert isinstance(q["contentMarker"], str) and q["contentMarker"], (
                f"{q['id']}: content-targeted query must carry a verbatim contentMarker")
            assert q["expectedSource"] is None, f"{q['id']}: content-targeted expectedSource must be null"
        assert q["holdout"] is False, f"{q['id']}: holdout candidates never become queries"
        assert q["searchLimit"] == 8, f"{q['id']}: searchLimit must be 8 (P3 top-8 arithmetic)"
        assert q["frame"] in {0, 1, 2}, f"{q['id']}: frame must be one of the 3 paraphrase frames"
        assert isinstance(q["query"], str) and q["query"].strip(), f"{q['id']}: empty query text"
        assert isinstance(q["answerSpan"], str) and q["answerSpan"].strip(), f"{q['id']}: empty answerSpan"
        seen_exact.add(q["query"])
        seen_norm.add(_normalize(q["query"]))
    assert len(seen_exact) == TOTAL_QUERIES, f"{source}: duplicate query text (exact)"
    assert len(seen_norm) == TOTAL_QUERIES, f"{source}: duplicate query text (normalized)"


def test_shape_exactly_100_queries_with_required_schema(tmp_path: Path) -> None:
    out = _generate_to(tmp_path, _corpus_db(tmp_path), "synthetic-corpus.json")
    _assert_shape(_load_corpus(out), "synthetic")
    assert ARTIFACT_PATH.exists(), f"committed artifact missing: {ARTIFACT_PATH}"
    _assert_shape(_load_corpus(ARTIFACT_PATH), "committed artifact")


# ------------------------------------------------------------------ AC2.3 anchors


def _assert_anchors_resolve(corpus: dict, db: Path, source: str) -> None:
    conn = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
    conn.row_factory = sqlite3.Row
    try:
        for q in corpus["queries"]:
            if q["expectedHash"] is not None:
                rows = conn.execute(
                    "SELECT project_id, scope, source_file FROM entries WHERE hash=?",
                    (q["expectedHash"],)).fetchall()
                assert len(rows) == 1, (
                    f"{source} {q['id']}: expectedHash resolves to {len(rows)} rows, expected exactly 1")
                assert rows[0]["project_id"] == q["targetProjectId"], (
                    f"{source} {q['id']}: anchor project_id mismatch")
                assert rows[0]["scope"] == q["targetScope"], (
                    f"{source} {q['id']}: anchor scope mismatch")
                assert rows[0]["source_file"] == q["expectedSource"], (
                    f"{source} {q['id']}: anchor source_file mismatch")
            if q["contentMarker"] is not None:
                rows = conn.execute(
                    "SELECT hash FROM entries WHERE project_id=? AND scope=? AND instr(value, ?) > 0",
                    (q["projectId"], q["targetScope"], q["contentMarker"])).fetchall()
                assert len(rows) == 1, (
                    f"{source} {q['id']}: contentMarker matches {len(rows)} rows in its "
                    "bucket, expected exactly 1")
    finally:
        conn.close()


def test_anchors_resolve(tmp_path: Path) -> None:
    db = _corpus_db(tmp_path)
    out = _generate_to(tmp_path, db, "synthetic-corpus.json")
    _assert_anchors_resolve(_load_corpus(out), db, "synthetic")

    # fail-capability 1: a duplicate anchor hash must raise, not slip through
    bad_dir = tmp_path / "poison-hash"
    bad_dir.mkdir()
    bad_db = _corpus_db(bad_dir, dup_anchor_hash=True)
    mod = _load_generator()
    with pytest.raises(RuntimeError, match="not unique"):
        mod.generate(copy_path=bad_db, output_path=bad_dir / "poisoned.json")

    # fail-capability 2: a project whose content rows share one value has no
    # unique marker left; its allocation can no longer be met -> loud failure
    poison_dir = tmp_path / "poison-marker"
    poison_dir.mkdir()
    bad_db2 = _corpus_db(poison_dir, dup_value_content=True)
    with pytest.raises(RuntimeError, match="delta"):
        mod.generate(copy_path=bad_db2, output_path=poison_dir / "poisoned.json")

    # committed-artifact half, armed on the pinned snapshot (H3)
    reason = _snapshot_mismatch_reason()
    if reason is not None:
        pytest.skip(f"committed-artifact anchors not checkable: {reason}")
    _assert_anchors_resolve(_load_corpus(ARTIFACT_PATH), COPY_PATH, "committed artifact")


# ------------------------------------------------------------------ exclusion manifest (F2)


def test_excluded_manifest_splits_committed_and_shared_rows(tmp_path: Path) -> None:
    # The old manifest counted all scopes as one number and claimed the whole
    # project "could never be served" — false for its scope='shared' rows
    # (global tier, ingested, served by shared/all). The manifest must split
    # the counts and say which half is servable.
    mod = _load_generator()
    db = _corpus_db(tmp_path)
    out = tmp_path / "corpus.json"
    corpus = mod.generate(copy_path=db, output_path=out)
    excluded = {e["projectId"]: e for e in corpus["header"]["excludedProjects"]}

    folddiv = excluded["folddiv"]
    assert folddiv["committedRows"] == 3
    assert folddiv["sharedRows"] == 1
    assert folddiv["embeddedRows"] == 4
    assert "3 committed project/custom rows can never be served" in folddiv["reason"]
    assert "1 scope='shared' row" in folddiv["reason"]
    assert "served by shared/all" in folddiv["reason"]

    sharedonly = excluded["sharedonly"]
    assert sharedonly["committedRows"] == 0
    assert sharedonly["sharedRows"] == 1
    assert "no committed project/custom rows" in sharedonly["reason"]
    assert "served by shared/all" in sharedonly["reason"]


# ------------------------------------------------------------------ AC2.4 coverage + holdout


def _assert_coverage_and_holdout(corpus: dict, db: Path, source: str) -> None:
    header = corpus["header"]
    projects = header["projects"]
    excluded = {e["projectId"]: e for e in header["excludedProjects"]}
    queried_projects = {q["projectId"] for q in corpus["queries"]}

    assert len(corpus["queries"]) == TOTAL_QUERIES, f"{source}: corpus must hold exactly 100 queries"
    per_project_counts: dict[str, int] = {}
    for q in corpus["queries"]:
        per_project_counts[q["projectId"]] = per_project_counts.get(q["projectId"], 0) + 1
    for pid, count in per_project_counts.items():
        assert count <= QUERY_CAP, (
            f"{source}: {pid} got {count} queries, above the cap of {QUERY_CAP}")

    for pid, rec in projects.items():
        assert rec["queries"] == per_project_counts.get(pid, 0), (
            f"{source}: header query count for {pid} disagrees with the queries")
        if rec["embeddedRows"] >= 1:
            assert rec["queries"] >= 1, (
                f"{source}: fold-consistent project {pid} with {rec['embeddedRows']} embedded "
                "rows must appear at least once")
        else:
            assert rec["queries"] == 0, f"{source}: zero-embedded project {pid} must not be queried"
        assert rec["holdoutCandidates"] == 0 or rec["holdoutCandidates"] == (
            (rec["fileCandidates"] + rec["contentCandidates"]) // 10
            if (rec["fileCandidates"] + rec["contentCandidates"]) >= 10 else 0), (
            f"{source}: {pid} holdout count violates the seeded ~10% / <10-candidates rule")

    for pid, e in excluded.items():
        assert pid not in queried_projects, (
            f"{source}: fold-divergent project {pid} (folds to {e['canonicalId']}) was queried")
        assert pid not in projects, f"{source}: excluded project {pid} must not also be a project record"
        assert e["canonicalId"] != pid, f"{source}: excluded project {pid} does not fold elsewhere"

    conn = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
    conn.row_factory = sqlite3.Row
    try:
        leaks = _holdout_leaks(corpus, conn)
    finally:
        conn.close()
    assert not leaks, f"{source}: holdout candidates leaked into queries: {leaks}"


def test_coverage_and_holdout(tmp_path: Path) -> None:
    db = _corpus_db(tmp_path)
    out = _generate_to(tmp_path, db, "synthetic-corpus.json")
    corpus = _load_corpus(out)

    # the fixture must actually exercise the exclusion + the holdout machinery
    assert any(e["projectId"] == "folddiv" for e in corpus["header"]["excludedProjects"]), (
        "fixture: folddiv must be excluded via the alias fold")
    assert any(rec["holdoutCandidates"] > 0 for rec in corpus["header"]["projects"].values()), (
        "fixture: at least one project must reserve a holdout")
    assert corpus["header"]["projects"]["tiny"]["holdoutCandidates"] == 0, (
        "fixture: tiny (<10 candidates) must reserve no holdout (review S5)")

    _assert_coverage_and_holdout(corpus, db, "synthetic")

    # fail-capability: the leak detector must flag an injected holdout leak
    leaks_before = _holdout_leaks(corpus, sqlite3.connect(f"file:{db}?mode=ro", uri=True))
    assert leaks_before == []
    held_file = next(rec["holdoutFiles"][0] for rec in corpus["header"]["projects"].values()
                     if rec["holdoutFiles"])
    conn = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
    conn.row_factory = sqlite3.Row
    held_hash = conn.execute(
        "SELECT hash FROM entries WHERE source_file=? LIMIT 1", (held_file,)).fetchone()[0]
    conn.close()
    mutated = json.loads(json.dumps(corpus))
    mutated["queries"][0]["expectedHash"] = held_hash
    mutated["queries"][0]["category"] = "file-targeted"
    mutated["queries"][0]["contentMarker"] = None
    conn = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
    leaks_after = _holdout_leaks(mutated, conn)
    conn.close()
    assert leaks_after, "leak detector failed to flag an injected holdout leak"

    # committed-artifact half, armed on the pinned snapshot (H3)
    reason = _snapshot_mismatch_reason()
    if reason is not None:
        pytest.skip(f"committed-artifact coverage not checkable: {reason}")
    _assert_coverage_and_holdout(_load_corpus(ARTIFACT_PATH), COPY_PATH, "committed artifact")
