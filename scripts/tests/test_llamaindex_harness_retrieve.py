"""Retrieval gates (P2): path goldens, fallback trigger wiring, skipped-leg
wiring, served-shape property test over 20 corpus queries.

Leg semantics (RRF/affinity math) are pinned in test_llamaindex_harness_fusion.py;
here the wiring is pinned: which legs run, which rows can be served, final shape.
"""

import json
import os
import sqlite3
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "retrieval_tuning"))

os.environ.setdefault("ANONYMIZED_TELEMETRY", "False")

from llamaindex_harness import ingest, retrieve

import hashlib


def _toy_embed(texts):
    vecs = []
    for t in texts:
        h = int(hashlib.sha256(t.encode()).hexdigest()[:16], 16)
        vecs.append([float((h >> (8 * i)) & 0xFF) / 255.0 + 0.01 for i in range(8)])
    return vecs


def _fixture_copy(path: Path):
    conn = sqlite3.connect(path)
    conn.executescript(
        """
        CREATE TABLE entries (
            id INTEGER PRIMARY KEY, hash TEXT, path TEXT, value TEXT,
            scope TEXT, project_id TEXT, source_file TEXT, section TEXT,
            heading_path TEXT, chunk_index INTEGER, total_chunks INTEGER);
        CREATE VIRTUAL TABLE entries_fts USING fts5(value, source_file, section);
        CREATE TRIGGER entries_fts_ai AFTER INSERT ON entries BEGIN
            INSERT INTO entries_fts(rowid, value, source_file, section)
            VALUES (new.id, new.value, new.source_file, new.section);
        END;
        """
    )
    rows = [
        ("f1", "guide.md", "alpha introduction common token here", "project", "ai-raccoon",
         "docs:guide.md", "intro", "Guide", 0, 3),
        ("f2", "guide.md", "beta details common token mid", "project", "ai-raccoon",
         "docs:guide.md", "context", "Guide", 1, 3),
        ("f3", "guide.md", "gamma summary common token end", "project", "ai-raccoon",
         "docs:guide.md", "decision", "Guide", 2, 3),
        ("o1", "other.md", "unrelated zebra content", "project", "ai-raccoon",
         "docs:other.md", None, "", -1, 0),
        ("s1", "s.md", "shared note common token", "shared", "ai-raccoon",
         None, None, "", -1, 0),
    ]
    conn.executemany(
        "INSERT INTO entries (hash, path, value, scope, project_id, source_file,"
        " section, heading_path, chunk_index, total_chunks) VALUES (?,?,?,?,?,?,?,?,?,?)",
        rows,
    )
    conn.commit()
    conn.close()


@pytest.fixture()
def store(tmp_path):
    copy = tmp_path / "copy.db"
    _fixture_copy(copy)
    store_dir = tmp_path / "store"
    assert ingest.main(["--copy", str(copy), "--store-dir", str(store_dir),
                       "--buckets", "ai-raccoon"],
                       embed=_toy_embed) == 0
    handle = ingest.open_store(store_dir)
    yield handle
    handle.close()


def _retriever(handle, project_id="ai-raccoon", scope="project"):
    # query_embed seam is single-text -> vector (prod: model.get_query_embedding).
    return retrieve.FusionRetriever(handle, query_embed=lambda t: _toy_embed([t])[0],
                                    project_id=project_id, scope=scope)


def test_path_query_serves_the_files_chunks(store):
    # Path queries rewrite the FTS leg (column-scoped) and force lambda=0;
    # the vector leg still contributes, so pin the FTS leg exactly, retrieve loosely.
    r = _retriever(store)
    fts_rows, plan = r.fts_leg("guide.md", 8)
    assert plan.is_path_query is True
    assert {h for h, _ in fts_rows} == {"f1", "f2", "f3"}
    served = r.retrieve("guide.md")
    assert {"f1", "f2", "f3"} <= {n.node.node_id for n in served}


def test_path_query_with_section_prefers_section(store):
    served = _retriever(store).retrieve("guide.md#context")
    assert served[0].node.node_id == "f2"


def test_fallback_fires_on_primary_under_match(store):
    # "alpha zebra": no row has both -> primary 0 hits -> OR fallback serves f1 + o1.
    served = _retriever(store).retrieve("alpha zebra")
    assert {n.node.node_id for n in served} >= {"f1", "o1"}


def test_stopword_only_query_is_vector_leg_only(store):
    r = _retriever(store)
    served = r.retrieve("what is the")
    vec_only = r.vector_leg("what is the", limit=100)
    assert served, "vector leg must still serve on a toy store"
    assert {n.node.node_id for n in served} <= {h for h, _ in vec_only}


def test_reserved_only_query_is_vector_leg_only(store):
    served = _retriever(store).retrieve("and or not")
    assert all(n.score == pytest.approx(n.score) for n in served)  # finite, ordered below
    scores = [n.score for n in served]
    assert scores == sorted(scores, reverse=True)


def test_served_shape_floor_and_order_over_corpus_queries(store):
    corpus = json.loads(
        (Path(__file__).resolve().parents[1] / "retrieval_tuning" / "corpora"
         / "eval-set-100.json").read_text())
    r = _retriever(store)
    for entry in corpus[:20]:
        served = r.retrieve(entry["query"])
        assert len(served) <= 8
        scores = [n.score for n in served]
        assert scores == sorted(scores, reverse=True)
        assert all(s == s and abs(s) != float("inf") for s in scores)
        if scores:
            assert all(s >= 0.6 * scores[0] for s in scores)


def test_limit_is_respected(store):
    # Fixture serves f1/f2/f3 above the floor for 'common token': Take(2)
    # pins the count exactly, not just the upper bound.
    assert len(_retriever(store).retrieve("common token", limit=2)) == 2


def test_no_match_query_serves_vector_only_without_error(store):
    # Unknown tokens: FTS leg empty, vector KNN still serves (same as the bank).
    r = _retriever(store)
    fts_rows, _ = r.fts_leg("qqqq zzzz xxxx", 8)
    assert fts_rows == []
    served = r.retrieve("qqqq zzzz xxxx")
    vec_only = {h for h, _ in r.vector_leg("qqqq zzzz xxxx", 8)}
    assert served
    assert {n.node.node_id for n in served} <= vec_only


def test_shared_scope_searches_global_shared_tier(store):
    r = _retriever(store, scope="shared")
    served = r.retrieve("shared note")
    assert [n.node.node_id for n in served] == ["s1"]


def test_nonfinite_distances_are_dropped_from_vector_hits():
    # A NaN/inf Chroma distance must not become a served similarity.
    got = retrieve._finite_hits(["a", "b", "c", "d"],
                                [0.2, float("nan"), float("inf"), 0.5])
    assert got == {"a": pytest.approx(0.8), "d": pytest.approx(0.5)}


# --- P1 custom-scope vector-leg gates (TDD RED) ---

def test_custom_scope_chroma_where_matches_project():
    # Vector leg: custom must compile to the project predicate (SearchContexts:
    # scope=project covers custom labels), never the all-scope $or fallthrough.
    from llamaindex_harness import retrieve as retrieve_mod
    assert retrieve_mod._chroma_where("ai-badger", "custom") == \
        retrieve_mod._chroma_where("ai-badger", "project")
    assert retrieve_mod._chroma_where("ai-badger", "custom") != \
        retrieve_mod._chroma_where("ai-badger", "all")


def test_custom_scope_retrieve_matches_project_scope(tmp_path):
    # End to end on a store with custom rows: custom serves exactly project.
    import sqlite3 as _sqlite3
    from llamaindex_harness import ingest as ingest_mod
    from llamaindex_harness import retrieve as retrieve_mod
    copy = tmp_path / "copy.db"
    conn = _sqlite3.connect(copy)
    conn.executescript(
        """
        CREATE TABLE entries (
            id INTEGER PRIMARY KEY, hash TEXT, path TEXT, value TEXT,
            scope TEXT, project_id TEXT, source_file TEXT, section TEXT,
            heading_path TEXT, chunk_index INTEGER, total_chunks INTEGER);
        CREATE VIRTUAL TABLE entries_fts USING fts5(value, source_file, section);
        CREATE TRIGGER entries_fts_ai AFTER INSERT ON entries BEGIN
            INSERT INTO entries_fts(rowid, value, source_file, section)
            VALUES (new.id, new.value, new.source_file, new.section);
        END;
        """
    )
    conn.executemany(
        "INSERT INTO entries (hash, path, value, scope, project_id, source_file,"
        " section, heading_path, chunk_index, total_chunks) VALUES (?,?,?,?,?,?,?,?,?,?)",
        [("c1", "c.md", "custom wardrobe tailoring ledger entry", "custom", "ai-badger",
          "docs:c.md", "context", "Custom", 0, 1),
         ("p1", "p.md", "project wardrobe standard pattern draft", "project", "ai-badger",
          "docs:p.md", "context", "Project", 0, 1),
         ("s9", "s.md", "shared wardrobe notion", "shared", "ai-badger",
          None, None, "", -1, 0)],
    )
    conn.commit()
    conn.close()
    store_dir = tmp_path / "store"
    assert ingest_mod.main(["--copy", str(copy), "--store-dir", str(store_dir),
                            "--buckets", "ai-badger"], embed=_toy_embed) == 0
    handle = ingest_mod.open_store(store_dir)
    try:
        vec_custom = retrieve_mod.FusionRetriever(
            handle, query_embed=lambda t: _toy_embed([t])[0],
            project_id="ai-badger", scope="custom").vector_leg("wardrobe", 8)
        vec_project = retrieve_mod.FusionRetriever(
            handle, query_embed=lambda t: _toy_embed([t])[0],
            project_id="ai-badger", scope="project").vector_leg("wardrobe", 8)
        assert vec_custom == vec_project and vec_custom
        served_custom = retrieve_mod.FusionRetriever(
            handle, query_embed=lambda t: _toy_embed([t])[0],
            project_id="ai-badger", scope="custom").retrieve("wardrobe")
        served_project = retrieve_mod.FusionRetriever(
            handle, query_embed=lambda t: _toy_embed([t])[0],
            project_id="ai-badger", scope="project").retrieve("wardrobe")
        assert {n.node.node_id for n in served_custom} == \
            {n.node.node_id for n in served_project} >= {"c1", "p1"}
        assert "s9" not in {n.node.node_id for n in served_custom}  # not widened
    finally:
        handle.close()
