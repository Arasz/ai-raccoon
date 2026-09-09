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
from llamaindex_harness import scopes


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


def test_fts_parity_probe_covers_nondefault_project_bucket(tmp_path):
    # The ingest rule spans every project bucket, so the parity probe must too:
    # a probe hitting a hermes-default row must compare against the bank's
    # hermes leg, not silently drop it via the ai-raccoon-only filter.
    copy = tmp_path / "copy.db"
    _fixture_copy(copy)
    store = tmp_path / "store"
    handle = _run_ingest(copy, store)
    try:
        assert ingest.fts_parity_probe(copy, handle, probe="hermes") == []
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


def test_embed_texts_progress_logs_every_n_batches(tmp_path, capsys):
    # Long-run heartbeat: the silent 2h ingest looked dead under delegation.
    texts = [f"row {i} padding text" for i in range(8)]
    ingest.embed_texts(_toy_embed, texts, batch_size=2, progress_every=2)
    out = capsys.readouterr().out
    assert "batch 2/4" in out and "batch 4/4" in out


def test_embed_texts_silent_by_default(tmp_path, capsys):
    ingest.embed_texts(_toy_embed, ["a", "b"], batch_size=1)
    assert capsys.readouterr().out == ""


def test_upsert_in_batches_logs_each_batch(capsys):
    class FakeColl:
        def __init__(self):
            self.calls = []

        def upsert(self, ids, embeddings, documents, metadatas):
            self.calls.append(list(ids))

    ingest._upsert_in_batches(FakeColl(), ["a", "b", "c"],
                              [[0.1]] * 3, ["x", "y", "z"], [{}, {}, {}])
    assert "upsert" in capsys.readouterr().out


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


# --- P1 bucket-extension gates (TDD RED) ---

def _header_corpus(path: Path, projects=("ai-raccoon", "hermes-default"), queries=()):
    path.write_text(json.dumps({
        "header": {
            "generator": "test",
            "seed": 42,
            "snapshotSha256": "ab" * 32,
            "projects": {p: {"embeddedRows": 10} for p in projects},
            "excludedProjects": [
                {"projectId": "aib", "canonicalId": "ai-badger",
                 "embeddedRows": 1, "reason": "alias fold"},
            ],
        },
        "queries": list(queries),
    }))


def _third_bucket_copy(path: Path):
    """Two-bucket fixture plus a third-bucket row the frozen rule blinds."""
    _fixture_copy(path)
    conn = sqlite3.connect(path)
    conn.execute(
        "INSERT INTO entries (hash, path, value, scope, project_id, source_file,"
        " section, heading_path, chunk_index, total_chunks) VALUES (?,?,?,?,?,?,?,?,?,?)",
        ("h6", "j.md", "jsaa quilting ledger compass rose", "project", "jsaa",
         "docs:jsaa.md", "context", "Jsaa", 0, 1))
    conn.commit()
    conn.close()


def test_resolve_buckets_derives_from_corpus_header(tmp_path):
    corpus = tmp_path / "corpus.json"
    _header_corpus(corpus, projects=("jsaa", "ai-raccoon"))
    buckets, excluded = scopes.resolve_buckets(None, str(corpus), None)
    assert buckets == ("ai-raccoon", "jsaa")
    assert excluded == [{"projectId": "aib", "canonicalId": "ai-badger",
                         "embeddedRows": 1, "reason": "alias fold"}]


def test_resolve_buckets_explicit_beats_header(tmp_path):
    corpus = tmp_path / "corpus.json"
    _header_corpus(corpus, projects=("jsaa", "ai-raccoon"))
    buckets, _ = scopes.resolve_buckets(None, str(corpus), "jsaa")
    assert buckets == ("jsaa",)


def test_resolve_buckets_fails_loud_without_source():
    # No explicit buckets and no corpus header: fail, never a frozen fallback.
    with pytest.raises(ValueError, match="buckets"):
        scopes.resolve_buckets(None, None, None)


def test_resolve_buckets_rejects_unknown_bucket_against_copy(tmp_path):
    copy = tmp_path / "copy.db"
    _fixture_copy(copy)
    corpus = tmp_path / "corpus.json"
    _header_corpus(corpus)
    with pytest.raises(ValueError, match="no-such-bucket"):
        scopes.resolve_buckets(str(copy), str(corpus), "ai-raccoon,no-such-bucket")


def test_corpus_buckets_covered_or_excluded():
    # AC1: every project-corpus-100 query projectId resolves into the ingest
    # buckets or the seed-equal exclusion manifest — no query silently blind.
    repo = Path(__file__).resolve().parents[1] / "retrieval_tuning"
    corpus_path = repo / "corpora" / "project-corpus-100.json"
    data = json.loads(corpus_path.read_text())
    buckets, excluded = scopes.resolve_buckets(None, data, None)
    manifest_ids = {e["projectId"] for e in excluded} | {e["canonicalId"] for e in excluded}
    assert excluded == data["header"]["excludedProjects"]  # closed to seed-equality
    uncovered = [q["id"] for q in data["queries"]
                 if q["targetProjectId"] not in buckets
                 and q["targetProjectId"] not in manifest_ids]
    assert uncovered == []


def test_third_bucket_rows_ingested_not_silent(tmp_path):
    copy = tmp_path / "copy.db"
    _third_bucket_copy(copy)
    rows, _ = ingest.load_rows(str(copy), ("ai-raccoon", "hermes-default", "jsaa"))
    assert "h6" in {r["hash"] for r in rows}


def test_fts_parity_probe_covers_third_bucket(tmp_path):
    copy = tmp_path / "copy.db"
    _third_bucket_copy(copy)
    store = tmp_path / "store"
    buckets = ("ai-raccoon", "hermes-default", "jsaa")
    assert ingest.main(["--copy", str(copy), "--store-dir", str(store),
                        "--buckets", ",".join(buckets)], embed=_toy_embed) == 0
    handle = ingest.open_store(store)
    try:
        assert "h6" in {h for h, _ in ingest.query_fts(handle, "quilting", "jsaa",
                                                       "project", 100000)}
        assert ingest.fts_parity_probe(str(copy), handle, probe="quilting",
                                       buckets=buckets) == []
    finally:
        handle.close()


def test_custom_scope_fts_matches_bank_project_order(tmp_path):
    # Three-legged custom->project mapping, FTS leg: harness scope=custom must
    # serve exactly the bank's scope=project order (SearchContexts.cs: project
    # covers custom labels), never raise, never widen to all-scope.
    copy = tmp_path / "copy.db"
    _fixture_copy(copy)
    store = tmp_path / "store"
    _run_ingest(copy, store).close()
    handle = ingest.open_store(store)
    try:
        custom = ingest.query_fts(handle, "custom", "ai-raccoon", "custom", 100)
        project = ingest.query_fts(handle, "custom", "ai-raccoon", "project", 100)
        assert custom == project and custom
        conn = ingest.open_copy_readonly(str(copy))
        try:
            bank_order = [r[0] for r in conn.execute(
                "SELECT e.hash FROM entries_fts"
                " JOIN entries e ON e.id = entries_fts.rowid"
                " WHERE entries_fts MATCH ?"
                " AND e.project_id = ? AND e.scope IN ('project','custom')"
                " ORDER BY bm25(entries_fts, 1.0, 8.0, 4.0), e.hash",
                ("custom", "ai-raccoon")).fetchall()]
        finally:
            conn.close()
        assert [h for h, _ in custom] == bank_order
    finally:
        handle.close()


def test_params_records_buckets_excluded_snapshot_and_model(tmp_path):
    copy = tmp_path / "copy.db"
    _fixture_copy(copy)
    corpus = tmp_path / "corpus.json"
    _header_corpus(corpus)
    store = tmp_path / "store"
    assert ingest.main(["--copy", str(copy), "--store-dir", str(store),
                        "--corpus", str(corpus)], embed=_toy_embed) == 0
    params = json.loads((store / "params.json").read_text())
    assert params["resolvedBuckets"] == ["ai-raccoon", "hermes-default"]
    assert params["excludedProjects"] == [
        {"projectId": "aib", "canonicalId": "ai-badger",
         "embeddedRows": 1, "reason": "alias fold"}]
    assert params["corpusSnapshotSha256"] == "ab" * 32
    assert params["modelRevision"] == "test-seam" and params["modelBytes"] == 0


def test_model_weights_info_reads_cache_layout(tmp_path):
    hub = tmp_path / "hub" / "models--org--model"
    snap = hub / "snapshots" / ("cd" * 20)
    (snap / "weights").mkdir(parents=True)
    (hub / "refs").mkdir()
    (hub / "refs" / "main").write_text("cd" * 20)
    (snap / "weights" / "a.safetensors").write_bytes(b"x" * 100)
    revision, nbytes = ingest.model_weights_info("org/model", hub_dir=hub.parent)
    assert revision == "cd" * 20
    assert nbytes == 100


def test_pinned_revision_mismatch_fails_loud():
    with pytest.raises(ValueError, match="[Pp]inned"):
        ingest.check_pinned_revision("00" * 20)
    ingest.check_pinned_revision(ingest.PINNED_MODEL_REVISION)  # must not raise
