"""Unit tests for retrieval_tuning.corpus — schema validation, anchors, disjointness.

NOTE: named *_corpus_harness* deliberately — lane 2 owns test_retrieval_tuning_corpus.py
(its test-set-10.json tests); this file tests the harness's corpus module instead.
"""

import sqlite3

import pytest

from retrieval_tuning.corpus import (
    category_counts,
    check_disjoint,
    load_corpus,
    validate_entries,
)


def entry(**overrides):
    base = {
        "id": "E001",
        "category": "ADR (Decision)",
        "query": "what does adr 42 decide?",
        "expectedSource": "docs:adr:0042-*.md",
        "targetProjectId": "ai-raccoon",
        "targetScope": "project",
        "searchLimit": 5,
        "relevanceGrade": 5,
        "negativeTest": False,
        "difficulty": "medium",
        "nonFileTarget": False,
    }
    base.update(overrides)
    return base


def non_file_entry(**overrides):
    base = {
        "id": "E099",
        "category": "non-file",
        "query": "what did we decide about the token budget?",
        "expectedHash": "abc123def456",
        "expectedSource": None,
        "targetProjectId": "hermes-default",
        "targetScope": "project",
        "searchLimit": 5,
        "relevanceGrade": 5,
        "negativeTest": False,
        "difficulty": "hard",
        "nonFileTarget": True,
    }
    base.update(overrides)
    return base


@pytest.fixture
def copy_db(tmp_path):
    """A minimal stand-in for the memory-db copy: the entries columns corpus.py reads."""
    db_path = tmp_path / "memory-copy.db"
    con = sqlite3.connect(db_path)
    con.execute(
        "CREATE TABLE entries (hash TEXT, source_file TEXT, heading_path TEXT, section TEXT)"
    )
    con.executemany(
        "INSERT INTO entries (hash, source_file, heading_path, section) VALUES (?, ?, ?, ?)",
        [
            ("abc123def4567890", "docs/adr/0042-widget.md", "Overview > Decision", "Decision"),
            ("abc124def4567890", "docs/adr/0042-widget.md", "Overview > Context", "Context"),
            ("deadbeef1234", None, None, None),
        ],
    )
    con.commit()
    con.close()
    return db_path


class TestLoadCorpus:
    def test_list_form(self, tmp_path):
        path = tmp_path / "corpus.json"
        path.write_text('[{"id": "E1", "query": "q"}]')
        entries = load_corpus(path)
        assert entries == [{"id": "E1", "query": "q"}]

    def test_queries_key_form(self, tmp_path):
        path = tmp_path / "corpus.json"
        path.write_text('{"queries": [{"id": "E1", "query": "q"}]}')
        entries = load_corpus(path)
        assert entries == [{"id": "E1", "query": "q"}]

    def test_missing_file_raises(self, tmp_path):
        with pytest.raises(FileNotFoundError):
            load_corpus(tmp_path / "nope.json")

    def test_invalid_json_raises(self, tmp_path):
        path = tmp_path / "corpus.json"
        path.write_text("{not json")
        with pytest.raises(ValueError):
            load_corpus(path)


class TestValidateEntries:
    def test_valid_entries_have_no_problems(self):
        problems = validate_entries([entry(), non_file_entry()])
        assert problems == []

    def test_duplicate_ids(self):
        problems = validate_entries([entry(id="E1"), entry(id="E1")])
        assert any("duplicate" in p.lower() for p in problems)

    def test_missing_required_fields(self):
        problems = validate_entries([{"id": "E1"}])
        assert any("query" in p for p in problems)
        assert any("searchLimit" in p for p in problems)

    def test_zero_search_limit(self):
        problems = validate_entries([entry(searchLimit=0)])
        assert any("searchLimit" in p for p in problems)

    def test_non_file_query_requires_expected_hash(self):
        problems = validate_entries([non_file_entry(expectedHash=None)])
        assert any("expectedHash" in p for p in problems)

    def test_non_file_query_must_not_have_expected_source(self):
        problems = validate_entries(
            [non_file_entry(expectedSource="docs:adr:0042-*.md")]
        )
        assert any("expectedSource" in p for p in problems)

    def test_file_target_needs_expected_source_or_hash(self):
        problems = validate_entries([entry(expectedSource=None, expectedHash=None)])
        assert any("expectedSource" in p for p in problems)

    def test_bad_expected_hash_format(self):
        problems = validate_entries([entry(expectedSource=None, expectedHash="abc")])
        assert any("expectedHash" in p for p in problems)

    def test_bad_scope(self):
        problems = validate_entries([entry(targetScope="galaxy")])
        assert any("targetScope" in p for p in problems)

    def test_expected_count_mismatch(self):
        problems = validate_entries([entry()], expected_count=100)
        assert any("100" in p for p in problems)

    def test_empty_corpus_is_a_problem(self):
        problems = validate_entries([])
        assert problems


class TestGradePresence:
    def test_grade_required_but_missing(self):
        problems = validate_entries([entry()], require_grade=True)
        assert any("grade" in p for p in problems)

    def test_unknown_grade_value(self):
        problems = validate_entries([entry(grade="maybe", gradeRationale="why")], require_grade=True)
        assert any("grade" in p for p in problems)

    def test_grade_without_rationale(self):
        problems = validate_entries([entry(grade="good")], require_grade=True)
        assert any("gradeRationale" in p for p in problems)

    def test_valid_grade_and_rationale(self):
        problems = validate_entries(
            [entry(grade="good", gradeRationale="target at rank 1, clean result")],
            require_grade=True,
        )
        assert problems == []


class TestAnchorResolution:
    def test_expected_source_anchor_resolves(self, copy_db):
        problems = validate_entries(
            [entry(expectedSource="docs:adr:0042-*.md#decision")], db_path=copy_db
        )
        assert problems == []

    def test_expected_source_unresolved(self, copy_db):
        problems = validate_entries(
            [entry(expectedSource="docs:adr:9999-*.md")], db_path=copy_db
        )
        assert any("resolve" in p.lower() for p in problems)

    def test_anchor_mismatch_against_copy(self, copy_db):
        problems = validate_entries(
            [entry(expectedSource="docs:adr:0042-*.md#consequences")], db_path=copy_db
        )
        assert any("anchor" in p.lower() or "section" in p.lower() for p in problems)

    def test_expected_hash_resolves(self, copy_db):
        problems = validate_entries([non_file_entry()], db_path=copy_db)
        assert problems == []

    def test_expected_hash_unresolved(self, copy_db):
        problems = validate_entries(
            [non_file_entry(expectedHash="ffffffff99")], db_path=copy_db
        )
        assert any("resolve" in p.lower() for p in problems)

    def test_no_db_path_skips_anchor_checks(self):
        problems = validate_entries(
            [entry(expectedSource="docs:adr:9999-*.md"), non_file_entry(expectedHash="ffffffff99")],
            db_path=None,
        )
        assert problems == []


class TestDisjointness:
    def test_shared_query_text(self):
        problems = check_disjoint(
            [entry(id="T1", query="What is ADR-0042 about?")],
            [entry(id="E1", query="what is adr-0042 ABOUT?")],
        )
        assert any("query" in p.lower() for p in problems)

    def test_shared_target_file(self):
        problems = check_disjoint(
            [entry(id="T1", expectedSource="docs:adr:0042-*.md")],
            [entry(id="E1", expectedSource="docs:adr:0042-*.md")],
        )
        assert any("file" in p.lower() for p in problems)

    def test_disjoint_sets_are_clean(self):
        problems = check_disjoint(
            [entry(id="T1", query="a", expectedSource="docs:adr:0006-*.md")],
            [entry(id="E1", query="b", expectedSource="docs:adr:0011-*.md")],
        )
        assert problems == []


class TestCategoryCounts:
    def test_file_vs_non_file_counts(self):
        counts = category_counts(
            [entry(id="E1"), entry(id="E2"), non_file_entry(id="E3"), non_file_entry(id="E4")]
        )
        assert counts == {"file": 2, "non-file": 2}
