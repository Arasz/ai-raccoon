"""Tests for scripts/retrieval_tuning/corpora/test-set-10.json (WP2, plan §5.2/§5.3, gate G2).

Self-contained: reads the committed JSON plus (optionally) the memory-db copy for
anchor resolution. The copy is a runtime scratch artifact, so anchor-resolution
tests skip when it is absent (CI without the copy still validates structure).
"""

import json
import os
import re
import sqlite3
from pathlib import Path

import pytest

_CORPUS = Path(__file__).resolve().parents[1] / "retrieval_tuning" / "corpora" / "test-set-10.json"
COPY_DB = os.environ.get("MEMORY_COPY_DB", "/tmp/continue-testing-algorithm/datasets/memory-copy.db")

GRADES = {"good", "could-be-improved", "just-wrong"}
BUCKET_TABLE = "ADR (Table)"
BUCKET_EXACT = "ADR (Exact)"
NON_FILE_CATEGORIES = {"Non-file (hermes)", "Non-file (shared)"}


@pytest.fixture(scope="module")
def corpus():
    return json.loads(_CORPUS.read_text(encoding="utf-8"))


def _bucket(entry: dict) -> str:
    if entry.get("nonFileTarget"):
        return "non-file"
    if entry.get("category") == BUCKET_EXACT:
        return "exact"
    return "table"


# ---------------------------------------------------------------- structure

def test_corpus_has_exactly_ten_entries(corpus):
    assert isinstance(corpus, list)
    assert len(corpus) == 10


def test_corpus_bucket_counts_are_4_table_3_exact_3_nonfile(corpus):
    counts = {}
    for entry in corpus:
        counts[_bucket(entry)] = counts.get(_bucket(entry), 0) + 1
    assert counts == {"table": 4, "exact": 3, "non-file": 3}


def _source_file(entry: dict) -> str:
    """Strip the 'docs:adr:' anchor prefix and any #section from expectedSource."""
    return entry["expectedSource"].removeprefix("docs:adr:").split("#")[0]


def test_corpus_table_bucket_targets_only_pinned_adr_files(corpus):
    pinned = {
        "0006-rrf-parameter-optimization.md",
        "0056-a-retrieval-gate-measured-off-its-tuning-set.md",
        "0070-maintenance-is-a-list-of-jobs-with-a-ledger.md",
        "0078-the-no-fusion-regression-rule-is-an-order-and-ships-default-off.md",
    }
    for entry in corpus:
        if _bucket(entry) == "table":
            assert _source_file(entry) in pinned, entry["id"]


def test_corpus_exact_bucket_queries_come_from_pinned_files(corpus):
    pinned = {
        "0006-rrf-parameter-optimization.md",
        "0070-maintenance-is-a-list-of-jobs-with-a-ledger.md",
        "0078-the-no-fusion-regression-rule-is-an-order-and-ships-default-off.md",
    }
    for entry in corpus:
        if _bucket(entry) == "exact":
            assert _source_file(entry) in pinned, entry["id"]


def test_corpus_every_entry_graded_with_rationale(corpus):
    for entry in corpus:
        assert entry["grade"] in GRADES, entry["id"]
        assert isinstance(entry["gradeRationale"], str) and len(entry["gradeRationale"]) >= 20, entry["id"]


def test_corpus_grades_cover_a_mix(corpus):
    present = {entry["grade"] for entry in corpus}
    assert present == GRADES, "the 10-query set must contain good, could-be-improved AND just-wrong queries"


def test_corpus_query_texts_unique(corpus):
    texts = [entry["query"] for entry in corpus]
    assert len(texts) == len(set(texts))


def test_corpus_ids_unique(corpus):
    ids = [entry["id"] for entry in corpus]
    assert len(ids) == len(set(ids))


def test_corpus_non_file_entries_carry_required_fields(corpus):
    for entry in corpus:
        if entry.get("nonFileTarget"):
            assert entry["expectedSource"] is None, entry["id"]
            assert re.fullmatch(r"[0-9a-f]{64}", entry["expectedHash"]), entry["id"]
            assert entry["targetScope"] in ("project", "shared"), entry["id"]
        else:
            assert entry["expectedSource"] is not None, entry["id"]
            assert re.fullmatch(r"[0-9a-f]{64}", entry["expectedHash"]), entry["id"]


def test_corpus_schema_fields_present(corpus):
    required = {
        "id", "category", "query", "expectedSource", "expectedHash", "answerSpan",
        "targetProjectId", "targetScope", "searchLimit", "relevanceGrade",
        "negativeTest", "difficulty", "nonFileTarget", "grade", "gradeRationale",
    }
    for entry in corpus:
        assert required <= set(entry), entry["id"]
        assert entry["searchLimit"] == 5
        assert entry["negativeTest"] is False
        assert entry["relevanceGrade"] == 5
        assert entry["difficulty"] in ("easy", "medium", "hard", "very-hard")


def test_corpus_file_entries_resolve_project_and_scope(corpus):
    for entry in corpus:
        if not entry.get("nonFileTarget"):
            assert entry["targetProjectId"] == "ai-raccoon"
            assert entry["targetScope"] == "project"


# ---------------------------------------------------------------- anchor resolution (against the copy)

def _copy_conn():
    return sqlite3.connect(f"file:{COPY_DB}?mode=ro", uri=True)


@pytest.mark.skipif(not os.path.exists(COPY_DB), reason=f"memory copy not present at {COPY_DB}")
def test_corpus_expected_hashes_resolve_in_copy(corpus):
    conn = _copy_conn()
    try:
        for entry in corpus:
            row = conn.execute(
                "SELECT project_id, scope, source_file FROM entries WHERE hash=?", (entry["expectedHash"],)
            ).fetchone()
            assert row is not None, f"{entry['id']}: hash {entry['expectedHash'][:12]}… not in copy"
    finally:
        conn.close()


@pytest.mark.skipif(not os.path.exists(COPY_DB), reason=f"memory copy not present at {COPY_DB}")
def test_corpus_target_project_and_scope_match_copy_rows(corpus):
    conn = _copy_conn()
    try:
        for entry in corpus:
            row = conn.execute(
                "SELECT project_id, scope, source_file FROM entries WHERE hash=?", (entry["expectedHash"],)
            ).fetchone()
            assert row[0] == entry["targetProjectId"], f"{entry['id']}: project_id mismatch"
            assert row[1] == entry["targetScope"], f"{entry['id']}: scope mismatch"
            if entry["expectedSource"] is not None:
                assert row[2].endswith(_source_file(entry)), f"{entry['id']}: source_file {row[2]} does not end with {_source_file(entry)}"
    finally:
        conn.close()


@pytest.mark.skipif(not os.path.exists(COPY_DB), reason=f"memory copy not present at {COPY_DB}")
def test_corpus_answer_spans_appear_in_target_chunk(corpus):
    conn = _copy_conn()
    try:
        for entry in corpus:
            row = conn.execute(
                "SELECT value FROM entries WHERE hash=?", (entry["expectedHash"],)
            ).fetchone()
            span = entry.get("answerSpan") or ""
            if span:
                assert span in row[0], f"{entry['id']}: answerSpan not found in target chunk"
    finally:
        conn.close()
