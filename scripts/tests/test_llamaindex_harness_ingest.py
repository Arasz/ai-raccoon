"""Ingest gates (P1 AC1-AC3 + M3/M7/S6/S9): idempotent re-ingest, SHA-256
chunk-faithful verify, FTS parity probe, null coercion, params.json knobs.

The embedding engine is a seam: tests pass a deterministic toy embedder;
production wires HuggingFaceEmbedding (see ingest.create_embedding_model).
"""

import hashlib
import json
import os
import sqlite3
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "retrieval_tuning"))

os.environ.setdefault("ANONYMIZED_TELEMETRY", "False")

from llamaindex_harness import ingest


def _toy_embed(texts):
    """Deterministic 8-dim vectors: finite, distinct per text, no heavy deps."""
    vecs = []
    for t in texts:
        h = int(hashlib.sha256(t.encode()).hexdigest()[:16], 16)
        vecs.append([float((h >> (8 * i)) & 0xFF) / 255.0 + 0.01 for i in range(8)])
    return vecs


def _fixture_copy(path: Path):
    """Minimal bank-shaped copy: entries with every column ingest reads."""
    conn = sqlite3.connect(path)
    conn.executescript(
        """
        CREATE TABLE entries (
            id INTEGER PRIMARY KEY, hash TEXT, path TEXT, value TEXT,
            scope TEXT, project_id TEXT, source_file TEXT, section TEXT,
            heading_path TEXT, chunk_index INTEGER, total_chunks INTEGER);
        -- faithful miniature of MemorySchema.cs: FTS over (value, source_file, section).
        CREATE VIRTUAL TABLE entries_fts USING fts5(value, source_file, section);
        CREATE TRIGGER entries_fts_ai AFTER INSERT ON entries BEGIN
            INSERT INTO entries_fts(rowid, value, source_file, section)
            VALUES (new.id, new.value, new.source_file, new.section);
        END;
        """
    )
    rows = [
        # hash, path, value, scope, project, source_file, section, heading, chunk, total
        ("h1", "a.md", "alpha beta gamma content here", "project", "ai-raccoon",
         "docs:alpha.md", "context", "Alpha", 0, 2),
        ("h2", "a.md", "second chunk more delta text", "project", "ai-raccoon",
         "docs:alpha.md", "decision", "Alpha", 1, 2),
        ("h3", "s.md", "shared manual note without source", "shared", "ai-raccoon",
         None, None, "", -1, 0),  # M7: null source_file/section + chunk -1
        ("h4", "t.md", "hermes transcript ServerProbe refactor", "project", "hermes-default",
         None, "log", "", -1, 0),
        ("h5", "c.md", "custom labelled row", "custom", "ai-raccoon",
         "docs:custom.md", None, "Custom", 0, 1),
    ]
    conn.executemany(
        "INSERT INTO entries (hash, path, value, scope, project_id, source_file,"
        " section, heading_path, chunk_index, total_chunks) VALUES (?,?,?,?,?,?,?,?,?,?)",
        rows,
    )
    conn.commit()
    conn.close()


def _run_ingest(copy: Path, store: Path):
    # Buckets are the frozen M1 rule (project buckets + global shared), not CLI knobs.
    rc = ingest.main(["--copy", str(copy), "--store-dir", str(store)], embed=_toy_embed)
    assert rc == 0
    return ingest.open_store(store)


def test_ingest_twice_is_idempotent(tmp_path):
    copy = tmp_path / "copy.db"
    _fixture_copy(copy)
    store = tmp_path / "store"
    s1 = _run_ingest(copy, store)
    n1 = (s1.content.count(), s1.structure.count(), s1.fts_count())
    s1.close()
    s2 = _run_ingest(copy, store)
    n2 = (s2.content.count(), s2.structure.count(), s2.fts_count())
    s2.close()
    assert n1 == n2 == (5, 3, 5)  # 3 headed rows (h1, h2, h5) in structure


def test_verify_only_passes_on_faithful_store(tmp_path):
    copy = tmp_path / "copy.db"
    _fixture_copy(copy)
    store = tmp_path / "store"
    _run_ingest(copy, store).close()
    assert ingest.main(["--copy", str(copy), "--store-dir", str(store), "--verify-only"]) == 0


def test_verify_only_fails_on_tampered_value(tmp_path):
    copy = tmp_path / "copy.db"
    _fixture_copy(copy)
    store = tmp_path / "store"
    _run_ingest(copy, store).close()
    conn = sqlite3.connect(copy)
    conn.execute("UPDATE entries SET value='tampered' WHERE hash='h1'")
    conn.commit()
    conn.close()
    assert ingest.main(["--copy", str(copy), "--store-dir", str(store), "--verify-only"]) == 1


def test_fts_parity_probe_matches_bank_bm25_order(tmp_path):
    # Same 1-token probe, same weights, same order as bank FTS SQL over the copy.
    copy = tmp_path / "copy.db"
    _fixture_copy(copy)
    store = tmp_path / "store"
    handle = _run_ingest(copy, store)
    try:
        assert ingest.fts_parity_probe(copy, handle, probe="alpha") == []
    finally:
        handle.close()


def test_null_metadata_coerced_and_searchable(tmp_path):
    copy = tmp_path / "copy.db"
    _fixture_copy(copy)
    store = tmp_path / "store"
    handle = _run_ingest(copy, store)
    got = handle.content.get(ids=["h3"])
    assert got["metadatas"][0]["source_file"] == ""
    assert got["metadatas"][0]["section"] == ""
    assert got["metadatas"][0]["chunk_index"] == -1
    handle.close()


def test_params_json_carries_all_knobs(tmp_path):
    copy = tmp_path / "copy.db"
    _fixture_copy(copy)
    store = tmp_path / "store"
    _run_ingest(copy, store).close()
    params = json.loads((store / "params.json").read_text())
    for key in ("rrfK", "ftsWeight", "vectorWeight", "limit", "minRelativeScore",
                "sourceLambda", "consolidationThreshold", "docScoreFormula",
                "candidateWindow", "structureAlpha", "fusionNoRegression",
                "scope", "kind", "model", "buckets", "counts"):
        assert key in params, f"params.json missing {key}"
    assert params["structureAlpha"] == 0.5
    assert params["fusionNoRegression"] is False
    assert params["limit"] == 8
    assert params["kind"] == "memory"


def test_import_has_no_side_effects(tmp_path):
    # Import-safe: importing the module creates no store, no client, no dirs.
    before = set(tmp_path.iterdir())
    import importlib
    importlib.reload(ingest)
    assert set(tmp_path.iterdir()) == before


def test_chroma_store_lives_under_store_dir_not_repo(tmp_path):
    copy = tmp_path / "copy.db"
    _fixture_copy(copy)
    store = tmp_path / "store"
    _run_ingest(copy, store).close()
    assert (store / "chroma").is_dir()
    assert (store / "fts.db").is_file()


def test_real_volume_upsert_exceeding_chroma_batch_cap(tmp_path):
    # Production shape: 11,816 content rows tripped Chroma's max batch 5461
    # (InternalError) — build_store must split upserts for BOTH collections.
    # 6000 > 5461 trips the same cap with toy vectors (cap is a fixed constant).
    conn = sqlite3.connect(tmp_path / "copy.db")
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
        [(f"big{i:05d}", "b.md", f"bulk row {i} padding text for volume",
          "project", "ai-raccoon", "docs:bulk.md", "context", "Bulk", i, 6000)
         for i in range(6000)],
    )
    conn.commit()
    conn.close()
    store = tmp_path / "store"
    assert ingest.main(["--copy", str(tmp_path / "copy.db"),
                        "--store-dir", str(store)], embed=_toy_embed) == 0
    handle = ingest.open_store(store)
    try:
        assert handle.content.count() == 6000
        assert handle.structure.count() == 6000  # all headed: structure trips too
        assert handle.fts_count() == 6000
    finally:
        handle.close()
