"""P3 AC1 gates: the ONE scope/bucket builder (SQL + Chroma emitters).

The consolidated home is `retrieval_tuning.scopes`; the harness module of the
same name is a re-export shim, and the identity assertions below are the gate
that it stays one implementation (a second copy would pass equality but fail
`is`). Pinned expected strings are the byte-level contracts the old spellings
used — derived from the 12a72dfb sources, not re-invented.
"""

import json
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "retrieval_tuning"))

from retrieval_tuning import scopes  # noqa: E402


class TestScopeTables:
    def test_bank_scopes_are_the_p1_table_unchanged(self):
        # P1's table, moved — not redefined. `custom` is corpus-only.
        assert scopes.VALID_SCOPES == ("project", "shared", "all")
        assert scopes.SCOPE_FALLBACK == {"custom": "project"}

    def test_corpus_scopes_derive_from_the_one_table(self):
        # F6: the corpus validator must accept `custom` (a real corpus scope),
        # while the bank table stays untouched.
        assert scopes.CORPUS_SCOPES == frozenset({"project", "shared", "all", "custom"})
        assert scopes.CORPUS_SCOPES - set(scopes.SCOPE_FALLBACK) == set(scopes.VALID_SCOPES)

    def test_default_scopes_table(self):
        assert scopes.DEFAULT_QUERY_SCOPE == "project"
        assert scopes.DEFAULT_SERVER_SCOPE == "all"

    def test_normalize_scope_maps_custom_only(self):
        assert scopes.normalize_scope("custom") == "project"
        assert scopes.normalize_scope("galaxy") == "galaxy"
        assert scopes.normalize_scope("shared") == "shared"


class TestSqlEmitters:
    def test_ingest_predicate_exact(self):
        sql, args = scopes.ingest_predicate(("ai-raccoon", "hermes-default"))
        assert sql == ("(project_id IN (?,?) AND scope IN ('project','custom'))"
                       " OR scope = 'shared'")
        assert args == ("ai-raccoon", "hermes-default")

    def test_ingest_predicate_is_the_old_load_rows_sql(self):
        # The spelled-out string from ingest.load_rows at 12a72dfb.
        buckets = ("ai-raccoon", "hermes-default", "jsaa")
        placeholders = ",".join("?" for _ in buckets)
        legacy = (f" WHERE (project_id IN ({placeholders}) AND scope IN ('project','custom'))"
                  " OR scope = 'shared'")
        sql, args = scopes.ingest_predicate(buckets)
        assert f" WHERE {sql}" == legacy
        assert args == buckets

    def test_scope_predicate_per_query(self):
        assert scopes.scope_predicate("proj", "project") == (
            "project_id = ? AND scope IN ('project','custom')", ("proj",))
        assert scopes.scope_predicate("proj", "shared") == ("scope = 'shared'", ())
        assert scopes.scope_predicate("proj", "all") == (
            "((project_id = ? AND scope IN ('project','custom')) OR scope = 'shared')",
            ("proj",))

    def test_scope_predicate_normalizes_custom(self):
        assert scopes.scope_predicate("proj", "custom") == \
            scopes.scope_predicate("proj", "project")

    def test_scope_predicate_rejects_unknown_after_normalization(self):
        with pytest.raises(ValueError, match="unknown scope"):
            scopes.scope_predicate("proj", "galaxy")

    def test_scope_predicate_prefixes_every_reference(self):
        sql, args = scopes.scope_predicate("proj", "project", prefix="e.")
        assert sql == "e.project_id = ? AND e.scope IN ('project','custom')"
        assert args == ("proj",)
        assert scopes.scope_predicate("proj", "shared", prefix="e.") == \
            ("e.scope = 'shared'", ())

    def test_slice_keep_clauses_exact(self):
        clauses, params = scopes.slice_keep_clauses(("ai-raccoon", "jsaa"), 1500)
        assert clauses == [
            "scope = 'shared'",
            "id IN (SELECT id FROM entries WHERE project_id = ?"
            " AND scope IN ('project','custom') ORDER BY id LIMIT ?)",
            "id IN (SELECT id FROM entries WHERE project_id = ?"
            " AND scope IN ('project','custom') ORDER BY id LIMIT ?)",
        ]
        assert params == ["ai-raccoon", 1500, "jsaa", 1500]


class TestChromaEmitter:
    def test_project_and_custom_collapse(self):
        assert scopes.chroma_where("ai-badger", "project") == {
            "$and": [{"project_id": {"$eq": "ai-badger"}},
                     {"scope": {"$in": ["project", "custom"]}}]}
        assert scopes.chroma_where("ai-badger", "custom") == \
            scopes.chroma_where("ai-badger", "project")

    def test_shared_and_all(self):
        assert scopes.chroma_where("ai-badger", "shared") == \
            {"scope": {"$eq": "shared"}}
        assert scopes.chroma_where("ai-badger", "all") == {
            "$or": [{"$and": [{"project_id": {"$eq": "ai-badger"}},
                              {"scope": {"$in": ["project", "custom"]}}]},
                    {"scope": {"$eq": "shared"}}]}

    def test_unknown_falls_to_all_like_the_old_spelling(self):
        # 12a72dfb behaviour: _chroma_where validated nothing; unknown scope
        # fell through to the all-branch (the FTS leg is the fail-loud one).
        assert scopes.chroma_where("ai-badger", "galaxy") == \
            scopes.chroma_where("ai-badger", "all")


class TestCorpusLoader:
    def test_header_form(self, tmp_path):
        path = tmp_path / "corpus.json"
        path.write_text(json.dumps({
            "header": {"projects": {"a": {}}, "excludedProjects": [1]},
            "queries": [{"id": "C1"}],
        }))
        header, entries = scopes.load_corpus(path)
        assert header == {"projects": {"a": {}}, "excludedProjects": [1]}
        assert entries == [{"id": "C1"}]

    def test_bare_list_form(self):
        header, entries = scopes.load_corpus([{"id": "E1"}])
        assert header is None
        assert entries == [{"id": "E1"}]

    def test_loaded_mapping_form(self, tmp_path):
        data = {"header": {"projects": {}}, "queries": [{"id": "C1"}]}
        header, entries = scopes.load_corpus(data)
        assert header == {"projects": {}}
        assert entries == [{"id": "C1"}]

    def test_invalid_shape_raises(self, tmp_path):
        with pytest.raises(ValueError, match="corpus must be"):
            scopes.load_corpus(42)


class TestOneHome:
    def test_harness_shim_reexports_the_same_objects(self):
        from llamaindex_harness import scopes as harness_scopes

        assert harness_scopes.VALID_SCOPES is scopes.VALID_SCOPES
        assert harness_scopes.SCOPE_FALLBACK is scopes.SCOPE_FALLBACK
        assert harness_scopes.resolve_buckets is scopes.resolve_buckets
        assert harness_scopes.load_corpus is scopes.load_corpus
        assert harness_scopes.normalize_scope is scopes.normalize_scope
        assert harness_scopes.ingest_predicate is scopes.ingest_predicate
        assert harness_scopes.scope_predicate is scopes.scope_predicate
        assert harness_scopes.chroma_where is scopes.chroma_where
        assert harness_scopes.slice_keep_clauses is scopes.slice_keep_clauses

    def test_corpus_module_uses_the_one_scope_set(self):
        from retrieval_tuning import corpus

        assert corpus.VALID_SCOPES is scopes.CORPUS_SCOPES
