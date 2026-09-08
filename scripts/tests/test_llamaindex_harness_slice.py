"""slice_copy gates: deterministic bucket-cap slice + forced corpus anchors,
FTS consistency after DELETE (triggers), stale-anchor warn-not-fatal."""

import json
import os
import sqlite3
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "retrieval_tuning"))

os.environ.setdefault("ANONYMIZED_TELEMETRY", "False")

from llamaindex_harness import slice_copy


def _fixture_copy(path: Path):
    """Bank-shaped copy with EXTERNAL-CONTENT FTS + sync triggers (live schema shape)."""
    conn = sqlite3.connect(path)
    conn.executescript(
        """
        CREATE TABLE entries (
            id INTEGER PRIMARY KEY, hash TEXT, path TEXT, value TEXT,
            scope TEXT, project_id TEXT, source_file TEXT, section TEXT,
            heading_path TEXT, chunk_index INTEGER, total_chunks INTEGER);
        CREATE VIRTUAL TABLE entries_fts USING fts5(value, source_file, section,
            content='entries', content_rowid='id');
        CREATE TRIGGER entries_fts_ai AFTER INSERT ON entries BEGIN
            INSERT INTO entries_fts(rowid, value, source_file, section)
            VALUES (new.id, new.value, new.source_file, new.section);
        END;
        CREATE TRIGGER entries_fts_ad AFTER DELETE ON entries BEGIN
            INSERT INTO entries_fts(entries_fts, rowid, value, source_file, section)
            VALUES ('delete', old.id, old.value, old.source_file, old.section);
        END;
        CREATE TRIGGER entries_fts_au AFTER UPDATE OF value, source_file, section ON entries BEGIN
            INSERT INTO entries_fts(entries_fts, rowid, value, source_file, section)
            VALUES ('delete', old.id, old.value, old.source_file, old.section);
            INSERT INTO entries_fts(rowid, value, source_file, section)
            VALUES (new.id, new.value, new.source_file, new.section);
        END;
        """
    )
    rows = [
        # hash, path, value, scope, project, source_file, section, heading, chunk, total
        ("keep1", "a.md", "alpha slice content", "project", "ai-raccoon",
         "docs:a.md", "context", "", 0, 3),
        ("keep2", "a.md", "beta slice content", "project", "ai-raccoon",
         "docs:a.md", "context", "", 1, 3),
        ("forced", "a.md", "gamma forced anchor", "project", "ai-raccoon",
         "docs:a.md", "context", "", 2, 3),
        ("shared1", "s.md", "shared tier row", "shared", "other-proj",
         None, None, "", -1, 0),
        ("hermes1", "h.md", "hermes bucket row", "project", "hermes-default",
         None, "log", "", -1, 0),
        ("dropme", "z.md", "unrelated project row", "project", "jsaa",
         None, None, "", -1, 0),
    ]
    conn.executemany(
        "INSERT INTO entries (hash, path, value, scope, project_id, source_file,"
        " section, heading_path, chunk_index, total_chunks) VALUES (?,?,?,?,?,?,?,?,?,?)",
        rows,
    )
    conn.commit()
    conn.close()


def _corpus(path: Path, hashes):
    path.write_text(json.dumps([{"id": f"T-{i}", "expectedHash": h} for i, h in enumerate(hashes)]))


def _hashes(db: Path):
    conn = sqlite3.connect(f"file:{db.resolve()}?mode=ro", uri=True)
    try:
        return {r[0] for r in conn.execute("SELECT hash FROM entries")}
    finally:
        conn.close()


def test_slice_keeps_shared_cap_and_forced(tmp_path):
    src = tmp_path / "src.db"
    _fixture_copy(src)
    corpus = tmp_path / "corpus.json"
    _corpus(corpus, ["forced"])
    dst = tmp_path / "slice.db"
    rc = slice_copy.main(["--source", str(src), "--target", str(dst),
                          "--corpus", str(corpus), "--cap-per-bucket", "1"])
    assert rc == 0
    # cap 1 keeps keep1 (first by id) per bucket; forced pulls 'forced' back in;
    # shared kept whole; hermes cap 1 keeps hermes1; jsaa bucket dropped.
    assert _hashes(dst) == {"keep1", "forced", "shared1", "hermes1"}


def test_slice_fts_stays_consistent(tmp_path):
    src = tmp_path / "src.db"
    _fixture_copy(src)
    corpus = tmp_path / "corpus.json"
    _corpus(corpus, ["forced"])
    dst = tmp_path / "slice.db"
    assert slice_copy.main(["--source", str(src), "--target", str(dst),
                            "--corpus", str(corpus), "--cap-per-bucket", "1"]) == 0
    conn = sqlite3.connect(f"file:{dst.resolve()}?mode=ro", uri=True)
    try:
        n_entries = conn.execute("SELECT count(*) FROM entries").fetchone()[0]
        n_fts = conn.execute("SELECT count(*) FROM entries_fts").fetchone()[0]
        assert (n_entries, n_fts) == (4, 4)
        probe = conn.execute("SELECT count(*) FROM entries_fts WHERE entries_fts MATCH 'slice'").fetchone()[0]
        assert probe == 1  # only keep1 carries 'slice'; dropped rows invisible
    finally:
        conn.close()


def test_slice_stale_forced_hash_warns_not_fails(tmp_path, capsys):
    src = tmp_path / "src.db"
    _fixture_copy(src)
    corpus = tmp_path / "corpus.json"
    _corpus(corpus, ["no-such-hash"])
    dst = tmp_path / "slice.db"
    assert slice_copy.main(["--source", str(src), "--target", str(dst),
                            "--corpus", str(corpus), "--cap-per-bucket", "1"]) == 0
    assert "no-such-hash" in capsys.readouterr().out
