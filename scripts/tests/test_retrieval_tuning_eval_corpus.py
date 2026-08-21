"""Gate G2 / WP3 tests for the 100-query eval corpus (plan §5.3, §5.4, §5.5, §12).

Checks, on the GENERATED artifact `scripts/retrieval_tuning/corpora/eval-set-100.json`:
- exactly 100 entries (75 ADR file-targeted + 25 non-file), schema per plan §5.3
- all anchors resolve in the memory-db copy (expectedSource suffix match >= 1
  source_file; expectedHash resolves to exactly the intended chunk)
- no duplicated query text; no overlap with the 4 reserved test-set ADR files
- every non-file query has expectedHash (and expectedSource == null)
- generator determinism: two runs -> byte-identical JSON, identical to the
  committed artifact
"""
from __future__ import annotations

import fnmatch
import importlib.util
import json
import os
import re
import sqlite3
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parents[2]
CORPUS_PATH = REPO_ROOT / "scripts" / "retrieval_tuning" / "corpora" / "eval-set-100.json"
GENERATOR_PATH = REPO_ROOT / "scripts" / "retrieval_tuning" / "build_eval_corpus.py"
COPY_PATH = Path(
    os.environ.get(
        "AI_RACCOON_EVAL_COPY",
        "/tmp/continue-testing-algorithm/datasets/memory-copy.db",
    )
)
TEST_SET_PATH = REPO_ROOT / "scripts" / "retrieval_tuning" / "corpora" / "test-set-10.json"

# The 4 ADR files RESERVED for the 10-query test set — the eval set must never
# target them (plan §5.2 / §5.4 document-level holdout discipline).
RESERVED_TEST_FILES = {
    "0006-rrf-parameter-optimization.md",
    "0056-a-retrieval-gate-measured-off-its-tuning-set.md",
    "0070-maintenance-is-a-list-of-jobs-with-a-ledger.md",
    "0078-the-no-fusion-regression-rule-is-an-order-and-ships-default-off.md",
}

REQUIRED_KEYS = {
    "id",
    "category",
    "query",
    "expectedSource",
    "expectedHash",
    "answerSpan",
    "targetProjectId",
    "targetScope",
    "searchLimit",
    "relevanceGrade",
    "negativeTest",
    "difficulty",
    "nonFileTarget",
}
DIFFICULTIES = {"easy", "medium", "hard", "very-hard"}
SCOPES = {"project", "shared", "all"}


def _load_corpus() -> list[dict]:
    assert CORPUS_PATH.exists(), f"corpus file missing: {CORPUS_PATH}"
    with CORPUS_PATH.open(encoding="utf-8") as fh:
        data = json.load(fh)
    assert isinstance(data, list), "corpus root must be a JSON array"
    return data


def _load_generator():
    assert GENERATOR_PATH.exists(), f"generator missing: {GENERATOR_PATH}"
    spec = importlib.util.spec_from_file_location("build_eval_corpus", GENERATOR_PATH)
    mod = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(mod)
    return mod


def _copy_conn() -> sqlite3.Connection:
    assert COPY_PATH.exists(), f"memory-db copy missing: {COPY_PATH}"
    conn = sqlite3.connect(f"file:{COPY_PATH}?mode=ro", uri=True)
    conn.row_factory = sqlite3.Row
    return conn


def _anchor_file(expected_source: str) -> str:
    """'docs:adr:0004-dual-vector-structure-signal.md#decision' -> '0004-*.md'."""
    assert expected_source.startswith("docs:adr:"), expected_source
    rest = expected_source[len("docs:adr:") :]
    return rest.split("#", 1)[0]


def _slugify(section: str) -> str:
    return re.sub(r"[^a-z0-9]+", "-", section.lower()).strip("-")


# ---------------------------------------------------------------- existence & counts


def test_corpus_exists_and_is_exactly_100() -> None:
    data = _load_corpus()
    assert len(data) == 100, f"expected exactly 100 entries, got {len(data)}"


def test_split_is_75_adr_plus_25_non_file() -> None:
    data = _load_corpus()
    file_q = [e for e in data if e["nonFileTarget"] is False]
    nonfile_q = [e for e in data if e["nonFileTarget"] is True]
    assert len(file_q) == 75, f"expected 75 file-targeted queries, got {len(file_q)}"
    assert len(nonfile_q) == 25, f"expected 25 non-file queries, got {len(nonfile_q)}"
    # 25 distinct ADR files x 3 queries each
    files = {_anchor_file(e["expectedSource"]) for e in file_q}
    assert len(files) == 25, f"expected 25 distinct ADR target files, got {len(files)}"
    counts: dict[str, int] = {}
    for e in file_q:
        counts[_anchor_file(e["expectedSource"])] = (
            counts.get(_anchor_file(e["expectedSource"]), 0) + 1
        )
    assert set(counts.values()) == {3}, f"expected 3 queries per file, got {counts}"


# ---------------------------------------------------------------- schema (plan §5.3)


def test_schema_and_ids() -> None:
    data = _load_corpus()
    ids = [e["id"] for e in data]
    assert len(set(ids)) == 100, "ids must be unique"
    assert all(re.fullmatch(r"E\d{3}", i) for i in ids), f"bad id format: {ids}"
    for e in data:
        assert set(e.keys()) == REQUIRED_KEYS, f"unexpected key set in {e['id']}: {set(e.keys()) ^ REQUIRED_KEYS}"
        assert isinstance(e["query"], str) and e["query"].strip()
        assert isinstance(e["category"], str) and e["category"].strip()
        assert e["searchLimit"] == 5
        assert e["relevanceGrade"] == 5
        assert e["negativeTest"] is False
        assert e["difficulty"] in DIFFICULTIES, f"{e['id']}: bad difficulty {e['difficulty']}"
        assert e["targetProjectId"] and e["targetScope"] in SCOPES
        assert isinstance(e["expectedHash"], str) and len(e["expectedHash"]) == 64


def test_file_queries_have_expected_source_nonfile_queries_have_hash() -> None:
    data = _load_corpus()
    for e in data:
        if e["nonFileTarget"]:
            assert e["expectedSource"] is None, f"{e['id']}: non-file query must have expectedSource null"
            assert e["expectedHash"], f"{e['id']}: non-file query must carry expectedHash"
        else:
            assert e["expectedSource"] and e["expectedSource"].startswith("docs:adr:"), (
                f"{e['id']}: file query must carry a docs:adr: expectedSource"
            )
            assert e["expectedHash"], f"{e['id']}: file query must carry expectedHash"
        assert isinstance(e["answerSpan"], str) and e["answerSpan"].strip()


# ---------------------------------------------------------------- query-text hygiene


def test_no_duplicate_query_text() -> None:
    data = _load_corpus()
    texts = [e["query"] for e in data]
    assert len(texts) == len(set(texts)), "duplicate query text found (exact)"
    normalized = [re.sub(r"\s+", " ", t.lower()).strip() for t in texts]
    assert len(normalized) == len(set(normalized)), "duplicate query text found (normalized)"


def test_no_overlap_with_reserved_test_files() -> None:
    data = _load_corpus()
    for e in data:
        if e["nonFileTarget"]:
            continue
        assert _anchor_file(e["expectedSource"]) not in RESERVED_TEST_FILES, (
            f"{e['id']}: targets reserved test file {e['expectedSource']}"
        )


def test_no_shared_query_text_with_test_set_when_present() -> None:
    if not TEST_SET_PATH.exists():
        pytest.skip("test-set-10.json not present yet (lane 2); check activates when it lands")
    with TEST_SET_PATH.open(encoding="utf-8") as fh:
        test_data = json.load(fh)
    eval_texts = {e["query"] for e in _load_corpus()}
    test_texts = {e["query"] for e in test_data}
    overlap = eval_texts & test_texts
    assert not overlap, f"query text shared between test set and eval set: {overlap}"


# ---------------------------------------------------------------- anchor resolution (G2)


def test_all_anchors_resolve_in_copy() -> None:
    data = _load_corpus()
    with _copy_conn() as conn:
        rows = conn.execute(
            "SELECT hash, source_file, section, project_id, scope, chunk_index "
            "FROM entries"
        ).fetchall()
    by_hash: dict[str, list[sqlite3.Row]] = {}
    for r in rows:
        by_hash.setdefault(r["hash"], []).append(r)

    for e in data:
        matches = by_hash.get(e["expectedHash"])
        assert matches, f"{e['id']}: expectedHash {e['expectedHash'][:16]}... not found in copy"
        assert len(matches) == 1, (
            f"{e['id']}: expectedHash {e['expectedHash'][:16]}... is not unique "
            f"({len(matches)} rows)"
        )
        row = matches[0]
        assert row["project_id"] == e["targetProjectId"], (
            f"{e['id']}: targetProjectId {e['targetProjectId']} != copy {row['project_id']}"
        )
        assert row["scope"] == e["targetScope"], (
            f"{e['id']}: targetScope {e['targetScope']} != copy {row['scope']}"
        )
        if e["nonFileTarget"]:
            continue
        file_glob = _anchor_file(e["expectedSource"])
        assert fnmatch.fnmatch(row["source_file"].rsplit("/", 1)[-1], file_glob), (
            f"{e['id']}: hash row source {row['source_file']} does not match {e['expectedSource']}"
        )
        section_slug = e["expectedSource"].split("#", 1)[1] if "#" in e["expectedSource"] else None
        if section_slug:
            assert _slugify(row["section"] or "") == section_slug, (
                f"{e['id']}: section slug mismatch: copy={row['section']!r} "
                f"anchor={section_slug!r}"
            )


def test_expected_source_suffix_matches_at_least_one_source_file() -> None:
    data = _load_corpus()
    with _copy_conn() as conn:
        files = {
            r[0].rsplit("/", 1)[-1]
            for r in conn.execute("SELECT DISTINCT source_file FROM entries WHERE source_file IS NOT NULL")
        }
    for e in data:
        if e["nonFileTarget"]:
            continue
        file_glob = _anchor_file(e["expectedSource"])
        assert any(fnmatch.fnmatch(f, file_glob) for f in files), (
            f"{e['id']}: no source_file in copy matches {file_glob}"
        )


# ---------------------------------------------------------------- determinism (G2)


def test_generator_is_deterministic_and_matches_committed_corpus(tmp_path: Path) -> None:
    mod = _load_generator()
    out1 = tmp_path / "run1.json"
    out2 = tmp_path / "run2.json"
    mod.generate(copy_path=COPY_PATH, output_path=out1)
    mod.generate(copy_path=COPY_PATH, output_path=out2)
    b1, b2 = out1.read_bytes(), out2.read_bytes()
    assert b1 == b2, "two generator runs produced different bytes"
    assert b1 == CORPUS_PATH.read_bytes(), (
        "committed eval-set-100.json differs from a fresh generation of the same inputs"
    )
