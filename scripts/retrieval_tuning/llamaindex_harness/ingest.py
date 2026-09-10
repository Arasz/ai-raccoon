"""Bank-copy -> Chroma + FTS5 ingestion (P1; import-safe, argparse + main(argv)).

Dataset rule (M1, verified against SearchContexts.cs + MemorySql.cs):
- resolved project buckets (explicit --buckets or the corpus header's
  projects): scope IN ('project','custom') — scope=project searches the
  project context plus its custom labels (plus the workspace context, which
  the corpus never targets).
- shared: ALL projects' scope='shared' rows — the shared context is global
  (SelectEntryByHashForRead: project_id = @projectId OR scope = 'shared').

Canonical record: a llama_index Document (id_=hash, text=value, metadata carries
path/source_file/section/chunk_index/total_chunks/project_id/scope). The
metadata key list below is the frozen Doc contract — no bespoke second type.

M7 coercion: null source_file/section/heading_path -> "" (Chroma rejects None);
chunk_index kept verbatim (incl. -1); "" counts as null in affinity (fusion.py).
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import sqlite3
import sys
import time
from dataclasses import dataclass
from pathlib import Path

import chromadb
import torch
from llama_index.core.schema import Document
from llama_index.embeddings.huggingface import HuggingFaceEmbedding

from . import fts as fts_plan
from . import scopes
from retrieval_tuning import repo_data

# Frozen Doc contract: every metadata key a Document carries through the harness.
METADATA_KEYS = (
    "path", "source_file", "section", "chunk_index", "total_chunks",
    "project_id", "scope",
)

# P3 AC2: every behavior constant below is read from data/knobs.json — the
# literals live there and nowhere in logic (test_no_hardcoded_knobs.py).
MODEL_NAME = repo_data.KNOBS["MODEL_NAME"]

# Frozen replication contract (verified from bank settings + SearchDefaults.cs).
PARAMS = dict(repo_data.KNOBS["PARAMS"])

# Frozen model-weights revision: the HF snapshot the F1 golden was built
# against. A cache advance changes every vector leg, so ingest refuses until
# the pin is reviewed and moved (see check_pinned_revision). Recorded with
# its byte size into params.json for audit.
PINNED_MODEL_REVISION = repo_data.KNOBS["PINNED_MODEL_REVISION"]

# MemorySql's bm25 column weights; the SQL is built from this tuple so the
# bank-vs-harness parity probe can never drift from the FTS leg.
BM25_WEIGHTS = tuple(repo_data.KNOBS["BM25_WEIGHTS"])
EMBED_BATCH_SIZE = repo_data.KNOBS["EMBED_BATCH_SIZE"]

# Chroma upsert batching: one call trips the server max-batch cap (5461 at
# chromadb 1.5.9; production content holds 11,816 rows). 4000 leaves headroom
# for payload variance; a future lower cap still fails loud (InternalError).
_UPSERT_BATCH_SIZE = repo_data.KNOBS["UPSERT_BATCH_SIZE"]


def model_weights_info(model_name: str = MODEL_NAME, hub_dir=None) -> tuple[str, int]:
    """(revision, bytes) of the cached HF snapshot for a model repo.

    hub_dir is the HF hub root (injected in tests; defaults to the real
    cache, honoring HF_HOME). Raises when the cache cannot resolve — an
    unresolvable weight set must fail loud, never embed silently."""
    root = Path(hub_dir) if hub_dir is not None else (
        Path(os.environ.get("HF_HOME", str(Path.home() / ".cache" / "huggingface"))) / "hub")
    base = root / ("models--" + model_name.replace("/", "--"))
    try:
        revision = (base / "refs" / "main").read_text().strip()
        snap = base / "snapshots" / revision
        size = sum(p.stat().st_size for p in snap.rglob("*") if p.is_file())
    except OSError as exc:
        raise ValueError(f"model weights for {model_name!r} unresolvable under "
                         f"{root} (reuse the pinned cache or fetch first): {exc}") from exc
    if not revision or size <= 0:
        raise ValueError(f"model weights for {model_name!r} resolve empty "
                         f"(revision={revision!r}, bytes={size})")
    return revision, size


def check_pinned_revision(revision: str) -> None:
    """Freeze gate: the weights under test must be the pinned revision."""
    if revision != PINNED_MODEL_REVISION:
        raise ValueError(
            f"model weights revision {revision!r} != pinned {PINNED_MODEL_REVISION!r}: "
            "vectors would drift from the frozen golden — review and move the pin, "
            "never embed past it")


def _sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with Path(path).open("rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def default_store_dir() -> Path:
    """Repo tmp/llamaindex-harness — gitignored via tmp/ (S9: never the harness dir)."""
    return Path(__file__).resolve().parents[3] / "tmp" / "llamaindex-harness"


def open_copy_readonly(copy_path: str) -> sqlite3.Connection:
    """The make_memory_copy.py pattern: read-only URI + query_only (never writable)."""
    conn = sqlite3.connect(f"file:{Path(copy_path).resolve()}?mode=ro", uri=True)
    conn.execute("PRAGMA query_only=ON")
    return conn


def load_rows(copy_path: str, buckets: tuple[str, ...] | list[str]
              ) -> tuple[list[dict], int, list[str]]:
    """All harness rows from a bank copy: resolved buckets + global shared tier.

    buckets comes from scopes.resolve_buckets (explicit --buckets or corpus
    header) — never a frozen constant, so a corpus query outside the old
    2-bucket rule ingests instead of silently scoring empty."""
    bucket_list = tuple(buckets)
    if not bucket_list:
        raise ValueError("load_rows: empty buckets (resolve via scopes.resolve_buckets)")
    where, where_args = scopes.ingest_predicate(bucket_list)
    conn = open_copy_readonly(copy_path)
    try:
        rows = conn.execute(
            "SELECT hash, path, value, scope, project_id, source_file, section,"
            " heading_path, chunk_index, total_chunks FROM entries"
            f" WHERE {where} ORDER BY id",
            where_args,
        ).fetchall()
        copy_entries = conn.execute("SELECT count(*) FROM entries").fetchone()[0]
    finally:
        conn.close()
    keys = ("hash", "path", "value", "scope", "project_id", "source_file",
            "section", "heading_path", "chunk_index", "total_chunks")
    docs = [dict(zip(keys, row)) for row in rows]
    kept, dropped = dedupe_rows(docs)
    return kept, copy_entries, dropped


def dedupe_rows(rows: list[dict]) -> tuple[list[dict], list[str]]:
    """Multi-home dedupe: one Chroma id per hash (first-by-id wins).

    The bank multi-homes identical rows under several projects (measured:
    1 hash under hermes-default/project + jsaa/project, byte-identical).
    Same values collapse to the lowest-id row and the drop is audited;
    DIFFERING values under one hash are corruption — still fail loud, since
    serving either silently would poison the store. Rows must arrive id-
    ordered (load_rows ORDERs BY id) for the win to be deterministic."""
    first_at: dict[str, int] = {}
    by_hash: dict[str, list[dict]] = {}
    for i, row in enumerate(rows):
        first_at.setdefault(row["hash"], i)
        by_hash.setdefault(row["hash"], []).append(row)
    kept, dropped = [], []
    for hash_, group in by_hash.items():
        if len(group) == 1:
            kept.append(group[0])
        elif all(g["value"] == group[0]["value"] for g in group):
            kept.append(group[0])
            dropped.append(hash_)
        else:
            raise ValueError(
                "duplicate hashes with differing values in ingest set "
                f"(Chroma ids must be unique, either row would lie): {[hash_]}")
    kept.sort(key=lambda r: first_at[r["hash"]])  # restore id order
    return kept, sorted(dropped)


def coerce_row(row: dict) -> dict:
    """M7 coercion: null text metadata -> ''; chunk_index verbatim (incl. -1)."""
    out = dict(row)
    for key in ("source_file", "section", "heading_path"):
        if out.get(key) is None:
            out[key] = ""
    return out


def to_documents(rows: list[dict]) -> list[Document]:
    """Rows -> llama_index Documents (the frozen record; metadata keys = METADATA_KEYS)."""
    docs = []
    for row in (coerce_row(r) for r in rows):
        docs.append(
            Document(
                id_=row["hash"],
                text=row["value"] or "",
                metadata={k: row[k] for k in METADATA_KEYS},
            )
        )
    return docs


def heading_of(row: dict) -> str:
    """The structure text: stored heading_path ('' == no structure, scores 0 in fusion)."""
    value = row.get("heading_path")
    return value if isinstance(value, str) else ""


def create_embedding_model(model_name: str = MODEL_NAME, offline: bool = False):
    """HF weights via llama_index HuggingFaceEmbedding + two documented repairs.

    1. trust_remote_code=True: the checkpoint ships custom modeling code
       (Alibaba-NLP/new-impl) — loading refuses without it.
    2. position_ids buffer repair: the published safetensors carries a garbage
       non-persistent position_ids tensor (random int64, e.g. 7453010313431162915
       at index 0); without the repair every encode dies with
       ``IndexError: index 35 is out of bounds for dimension 0 with size 6``.
       Restoring arange() is init semantics; a future fixed checkpoint makes
       this a no-op.
    3. float32 (model_kwargs dtype): bfloat16 weights NaN on this CPU path
       (embeddings finite, encoder layer 0 NaN); fp32 is finite and exact.
    """
    if offline:
        os.environ["HF_HUB_OFFLINE"] = "1"
    model = HuggingFaceEmbedding(
        model_name=model_name,
        trust_remote_code=True,
        device="cpu",
        model_kwargs={"dtype": torch.float32},
    )
    auto = model._model[0].auto_model
    with torch.no_grad():
        auto.embeddings.position_ids.copy_(
            torch.arange(auto.embeddings.position_ids.numel())
        )
    return model


def embed_texts(embed_fn, texts: list[str], batch_size: int = EMBED_BATCH_SIZE,
                progress_every: int = 0) -> list[list[float]]:
    """Batched embedding with a finiteness gate (a NaN vector poisons every cosine).

    progress_every=N prints one heartbeat line every N batches (long-run
    observability); 0 (default) stays silent for library/test callers."""
    out: list[list[float]] = []
    total_batches = (len(texts) + batch_size - 1) // batch_size if texts else 0
    for n, i in enumerate(range(0, len(texts), batch_size), 1):
        out.extend(embed_fn(texts[i:i + batch_size]))
        if progress_every and n % progress_every == 0:
            print(f"embed: batch {n}/{total_batches} ({len(out)}/{len(texts)} rows)",
                  flush=True)
    for vec in out:
        if not all(v == v and abs(v) != float("inf") for v in vec):
            raise ValueError("non-finite embedding vector produced")
    return out


@dataclass
class StoreHandle:
    """An opened harness store: Chroma content+structure, FTS5 sqlite, params."""

    store_dir: Path
    client: object
    content: object
    structure: object
    fts: sqlite3.Connection
    params: dict

    def fts_count(self) -> int:
        return self.fts.execute("SELECT count(*) FROM docs_fts").fetchone()[0]

    def close(self) -> None:
        self.fts.close()


def _bm25_terms(table: str) -> str:
    """`bm25(<table>, w0, w1, w2)` — one spelling for harness and bank legs."""
    weights = ", ".join(str(w) for w in BM25_WEIGHTS)
    return f"bm25({table}, {weights})"


def _upsert_in_batches(collection, ids, embeddings, documents, metadatas) -> None:
    total = (len(ids) + _UPSERT_BATCH_SIZE - 1) // _UPSERT_BATCH_SIZE if ids else 0
    for n, i in enumerate(range(0, len(ids), _UPSERT_BATCH_SIZE), 1):
        batch = slice(i, i + _UPSERT_BATCH_SIZE)
        collection.upsert(ids=ids[batch], embeddings=embeddings[batch],
                          documents=documents[batch], metadatas=metadatas[batch])
        print(f"upsert: batch {n}/{total} ({min(i + _UPSERT_BATCH_SIZE, len(ids))}/{len(ids)} ids)",
              flush=True)


_FTS_SCHEMA = """
CREATE VIRTUAL TABLE IF NOT EXISTS docs_fts USING fts5(
    value, source_file, section,
    hash UNINDEXED, path UNINDEXED, project_id UNINDEXED, scope UNINDEXED,
    chunk_index UNINDEXED, total_chunks UNINDEXED);
"""


def _write_fts(fts: sqlite3.Connection, docs: list[Document]) -> None:
    """Rewrite the FTS mirror from the canonical documents (idempotent)."""
    fts.executescript(_FTS_SCHEMA)
    fts.execute("DELETE FROM docs_fts")
    fts.executemany(
        "INSERT INTO docs_fts (value, source_file, section, hash, path, project_id,"
        " scope, chunk_index, total_chunks) VALUES (?,?,?,?,?,?,?,?,?)",
        [(d.text, d.metadata["source_file"], d.metadata["section"], d.id_,
          d.metadata["path"], d.metadata["project_id"], d.metadata["scope"],
          d.metadata["chunk_index"], d.metadata["total_chunks"]) for d in docs],
    )
    fts.commit()


def _store_params(rows: list[dict], docs: list[Document], *, content_count: int,
                  structure_count: int, fts_count: int, headed_count: int,
                  copy_entries: int, buckets, excluded, corpus_name, corpus_snapshot,
                  model_info: dict, dupes_dropped: int, copy_path, copy_sha) -> dict:
    """The params.json payload: frozen knobs + the audit/provenance block."""
    params = dict(PARAMS)
    bucket_counts: dict[str, int] = {}
    for r in rows:
        spelling = f"{r['project_id']}/{r['scope']}"
        bucket_counts[spelling] = bucket_counts.get(spelling, 0) + 1
    params["buckets"] = sorted(bucket_counts)
    params["bucketCounts"] = bucket_counts  # observed spellings -> store row counts
    params["resolvedBuckets"] = sorted(buckets)  # the input rule (audit)
    params["excludedProjects"] = list(excluded or [])  # seed-equal manifest
    params["corpus"] = corpus_name
    params["corpusSnapshotSha256"] = corpus_snapshot
    params["copyPath"] = copy_path
    params["copySnapshotSha256"] = copy_sha
    params["modelRevision"] = model_info["revision"]
    params["modelBytes"] = model_info["bytes"]
    params["dupesDropped"] = dupes_dropped
    params["counts"] = {
        "rows": len(docs),
        "content": content_count,
        "structure": structure_count,
        "fts": fts_count,
        "headed": headed_count,
        "copyEntries": copy_entries,
    }
    params["ingestedAt"] = time.strftime("%Y-%m-%dT%H:%M:%S%z")
    return params


def refresh_params(store: StoreHandle, rows: list[dict], docs: list[Document], *,
                   buckets, excluded, corpus_name, corpus_snapshot, copy_path: str,
                   copy_sha: str, copy_entries: int, model_info: dict,
                   dupes_dropped: int) -> None:
    """Metadata-only refresh: rewrite FTS + params, refusing any row drift (C6).

    Header/metadata edits (manifest, corpus snapshot, copy SHA) must not cost
    a re-embed. Validate that the fresh row universe (id set, text hashes,
    structure id set, model revision) matches the store, then rewrite the FTS
    mirror and params.json — never embed or upsert. Any mismatch refuses loud
    (a full ingest is required), so a fast path can never lie about the store."""
    got = store.content.get(include=["documents"])
    stored_ids = set(got["ids"])
    fresh_ids = {d.id_ for d in docs}
    if fresh_ids != stored_ids:
        missing = sorted(fresh_ids - stored_ids)[:3]
        extra = sorted(stored_ids - fresh_ids)[:3]
        raise ValueError(
            f"refresh refused: row id set changed (fresh={len(fresh_ids)}, "
            f"store={len(stored_ids)}; missing={missing}, extra={extra}) — a full "
            "ingest is required")
    fresh_text = {d.id_: hashlib.sha256((d.text or "").encode()).hexdigest()
                  for d in docs}
    for doc_id, text in zip(got["ids"], got["documents"]):
        if fresh_text[doc_id] != hashlib.sha256((text or "").encode()).hexdigest():
            raise ValueError(
                f"refresh refused: text changed for {doc_id[:12]} — a full ingest is required")
    headed = [d for d, r in zip(docs, rows) if heading_of(coerce_row(r))]
    if {d.id_ for d in headed} != set(store.structure.get(include=[])["ids"]):
        raise ValueError("refresh refused: structure id set changed — a full ingest is required")
    if (model_info["revision"] != store.params.get("modelRevision")
            or model_info["bytes"] != store.params.get("modelBytes")):
        raise ValueError(
            f"refresh refused: model revision {model_info['revision']!r} != store's "
            f"{store.params.get('modelRevision')!r} — a full re-embed is required")
    _write_fts(store.fts, docs)
    params = _store_params(rows, docs, content_count=store.content.count(),
                           structure_count=store.structure.count(),
                           fts_count=store.fts_count(), headed_count=len(headed),
                           copy_entries=copy_entries, buckets=buckets, excluded=excluded,
                           corpus_name=corpus_name, corpus_snapshot=corpus_snapshot,
                           model_info=model_info, dupes_dropped=dupes_dropped,
                           copy_path=copy_path, copy_sha=copy_sha)
    (store.store_dir / "params.json").write_text(json.dumps(params, indent=2, sort_keys=True))
    store.params = params


def query_fts(handle: StoreHandle, expression: str, project_id: str, scope: str,
              limit: int) -> list[tuple[str, float]]:
    """FTS leg query: bm25(1.0,8.0,4.0) ascending — the bank's MemorySql weights.

    Hash tiebreak mirrors the parity probe: a total order keeps RRF ranks
    deterministic when bm25 scores tie."""
    predicate, args = scopes.scope_predicate(project_id, scope)
    rows = handle.fts.execute(
        f"SELECT hash, {_bm25_terms('docs_fts')} AS rank FROM docs_fts"
        f" WHERE docs_fts MATCH ? AND {predicate}"
        " ORDER BY rank, hash LIMIT ?",
        (expression, *args, limit),
    ).fetchall()
    return [(h, r) for h, r in rows]


def build_store(store_dir: Path, docs: list[Document], rows: list[dict],
                embed_fn, copy_entries: int, batch_size: int = EMBED_BATCH_SIZE,
                progress_every: int = 0, *, buckets: tuple[str, ...] | list[str],
                excluded: list[dict] | None = None,
                corpus_name: str | None = None,
                corpus_snapshot: str | None = None,
                model_info: dict | None = None,
                dupes_dropped: int = 0,
                copy_path: str | None = None,
                copy_sha: str | None = None) -> StoreHandle:
    """Idempotent build: upsert current ids, delete stale ids, rewrite params.json."""
    store_dir.mkdir(parents=True, exist_ok=True)
    client = chromadb.PersistentClient(path=str(store_dir / "chroma"))
    content = client.get_or_create_collection("content", metadata={"hnsw:space": "cosine"})
    structure = client.get_or_create_collection("structure", metadata={"hnsw:space": "cosine"})

    texts = [d.text for d in docs]
    print(f"embed: {len(texts)} content texts in batches of {batch_size}", flush=True)
    vectors = embed_texts(embed_fn, texts, batch_size, progress_every)
    ids = [d.id_ for d in docs]
    metadatas = [dict(d.metadata) for d in docs]
    _upsert_in_batches(content, ids, vectors, texts, metadatas)

    headed = [(d, r) for d, r in zip(docs, rows) if heading_of(coerce_row(r))]
    if headed:
        struct_vectors = embed_texts(embed_fn, [heading_of(coerce_row(r)) for _, r in headed],
                                     batch_size, progress_every)
        struct_ids = [d.id_ for d, _ in headed]
        struct_docs = [heading_of(coerce_row(r)) for _, r in headed]
        struct_meta = [dict(d.metadata) for d, _ in headed]
        _upsert_in_batches(structure, struct_ids, struct_vectors, struct_docs, struct_meta)
    for collection, wanted in ((content, set(ids)), (structure, {d.id_ for d, _ in headed})):
        stale = set(collection.get(include=[])["ids"]) - wanted
        if stale:
            collection.delete(ids=sorted(stale))

    fts_path = store_dir / "fts.db"
    fts = sqlite3.connect(fts_path)
    _write_fts(fts, docs)

    model_info = model_info or {"revision": "test-seam", "bytes": 0}
    params = _store_params(
        rows, docs, content_count=content.count(), structure_count=structure.count(),
        fts_count=fts.execute("SELECT count(*) FROM docs_fts").fetchone()[0],
        headed_count=len(headed), copy_entries=copy_entries, buckets=buckets,
        excluded=excluded, corpus_name=corpus_name, corpus_snapshot=corpus_snapshot,
        model_info=model_info, dupes_dropped=dupes_dropped, copy_path=copy_path,
        copy_sha=copy_sha)
    (store_dir / "params.json").write_text(json.dumps(params, indent=2, sort_keys=True))
    return StoreHandle(store_dir, client, content, structure, fts, params)


def open_store(store_dir: Path) -> StoreHandle:
    """Open a built store (read path for retrieve/evaluate)."""
    store_dir = Path(store_dir)
    client = chromadb.PersistentClient(path=str(store_dir / "chroma"))
    fts = sqlite3.connect(store_dir / "fts.db")
    params = json.loads((store_dir / "params.json").read_text())
    return StoreHandle(
        store_dir, client,
        client.get_collection("content"), client.get_collection("structure"),
        fts, params,
    )


def verify_store(copy_path: str, store: StoreHandle,
                 buckets: tuple[str, ...] | list[str]) -> list[str]:
    """SHA-256 chunk-faithful verify: every Chroma id in the copy, byte-identical value."""
    bucket_list = tuple(buckets)
    conn = open_copy_readonly(copy_path)
    try:
        expected = {h: v for h, v in conn.execute("SELECT hash, value FROM entries")}
    finally:
        conn.close()
    problems = []
    got = store.content.get(include=["documents"])
    for doc_id, text in zip(got["ids"], got["documents"]):
        want = expected.get(doc_id)
        if want is None:
            problems.append(f"store id not in copy: {doc_id[:12]}")
        elif hashlib.sha256((text or "").encode()).hexdigest() != \
                hashlib.sha256((want or "").encode()).hexdigest():
            problems.append(f"value mismatch: {doc_id[:12]}")
    missing = []
    # Id-set parity against the ingest rule (project buckets + global shared).
    where, where_args = scopes.ingest_predicate(bucket_list)
    conn = open_copy_readonly(copy_path)
    try:
        wanted = {r[0] for r in conn.execute(
            f"SELECT hash FROM entries WHERE {where}",
            where_args,
        )}
    finally:
        conn.close()
    stored = set(store.content.get(include=[])["ids"])
    for doc_id in sorted(wanted - stored):
        problems.append(f"copy row missing from store: {doc_id[:12]}")
    for doc_id in sorted(stored - wanted):
        problems.append(f"store id outside ingest rule: {doc_id[:12]}")
    if store.fts_count() != store.content.count():
        problems.append(f"fts/content count skew: {store.fts_count()} vs {store.content.count()}")
    return problems


def _bank_fts_order(conn: sqlite3.Connection, probe: str, predicate: str,
                   args: tuple) -> list[str]:
    """One bank FTS leg: 1-token probe, bank bm25 weights, rank ascending.

    Hash tiebreak: bm25 ties are real (identical scores over short docs), and
    without a total order the comparison against the harness leg is noise.
    """
    return [r[0] for r in conn.execute(
        "SELECT e.hash FROM entries_fts"
        " JOIN entries e ON e.id = entries_fts.rowid"
        f" WHERE entries_fts MATCH ? AND {predicate}"
        f" ORDER BY {_bm25_terms('entries_fts')}, e.hash",
        (probe, *args),
    ).fetchall()]


def fts_parity_probe(copy_path: str, store: StoreHandle, probe: str,
                     buckets: tuple[str, ...] | list[str]) -> list[str]:
    """S3 parity: same 1-token probe, same bm25 weights, same ORDER as bank FTS SQL.

    Per-bucket: the ingest rule spans every resolved bucket plus the global
    shared tier, so each leg is compared under its own predicate — a single
    ai-raccoon-wide comparison silently drops other buckets' rows (a probe
    hitting hermes-default reported skew at 0 with byte-identical stores).
    """
    legs = [("project", bucket, *scopes.scope_predicate(bucket, "project", prefix="e."))
            for bucket in buckets]
    legs.append(("shared", "global", *scopes.scope_predicate("", "shared", prefix="e.")))
    conn = open_copy_readonly(copy_path)
    try:
        problems = []
        for scope, project, predicate, args in legs:
            bank_order = _bank_fts_order(conn, probe, predicate, args)
            harness_order = [h for h, _ in query_fts(store, probe, project, scope, 100000)]
            if bank_order != harness_order:
                problems.extend(
                    f"[{project}/{scope}] order skew at {i}: bank={b[:8]} "
                    f"harness={harness_order[i][:8] if i < len(harness_order) else '-'}"
                    for i, b in enumerate(bank_order)
                    if i >= len(harness_order) or harness_order[i] != b)[:5]
        return problems
    finally:
        conn.close()


def main(argv: list[str] | None = None, embed=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--copy", required=True, help="read-only bank copy path")
    parser.add_argument("--store-dir", default=str(default_store_dir()))
    parser.add_argument("--model", default=MODEL_NAME)
    parser.add_argument("--embed-batch-size", type=int, default=EMBED_BATCH_SIZE)
    parser.add_argument("--offline", action="store_true",
                        help="reuse cached HF weights; fail instead of downloading")
    parser.add_argument("--buckets", default=None,
                        help="explicit comma-separated project buckets; wins over --corpus")
    parser.add_argument("--corpus", default=None,
                        help="header-shaped corpus JSON: derives buckets + exclusion manifest"
                        " + snapshot SHA (required unless --buckets is given)")
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--verify-only", action="store_true")
    mode.add_argument("--refresh-params", action="store_true",
                      help="metadata-only refresh (dedupe + FTS rewrite + params + verify); "
                      "refuses when the row id set, text hashes, structure ids or model "
                      "revision differ from the store — never embeds")
    args = parser.parse_args(argv)
    store_dir = Path(args.store_dir)

    if args.verify_only:
        try:
            handle = open_store(store_dir)
        except Exception as exc:  # noqa: BLE001 — any open failure is a failed gate
            print(f"FAIL: cannot open store: {exc}")
            return 1
        try:
            buckets, _ = scopes.resolve_buckets(args.copy, args.corpus, args.buckets)
        except ValueError as exc:
            print(f"FAIL: {exc}")
            return 2
        problems = verify_store(args.copy, handle, buckets)
        handle.close()
        if problems:
            print(f"FAIL: {len(problems)} verify problems (first 5):")
            for p in problems[:5]:
                print(f"  {p}")
            return 1
        print("VERIFIED: store is chunk-faithful to the copy")
        return 0

    if args.refresh_params:
        try:
            handle = open_store(store_dir)
        except Exception as exc:  # noqa: BLE001 — any open failure is a failed gate
            print(f"FAIL: cannot open store: {exc}")
            return 1
        try:
            buckets, excluded = scopes.resolve_buckets(args.copy, args.corpus, args.buckets)
        except ValueError as exc:
            print(f"FAIL: {exc}")
            handle.close()
            return 2
        corpus_name, corpus_snapshot = None, None
        if args.corpus is not None:
            header, _ = scopes.load_corpus(args.corpus)
            corpus_name = Path(args.corpus).name
            corpus_snapshot = (header or {}).get("snapshotSha256")
        copy_path = str(Path(args.copy).resolve())
        copy_sha = _sha256_file(args.copy)
        if corpus_snapshot and copy_sha != corpus_snapshot:
            print(f"WARNING: copy sha {copy_sha[:12]}... != corpus snapshot "
                  f"{corpus_snapshot[:12]}... (corpus drift; both legs share this copy)",
                  flush=True)
        rows, copy_entries, dupes_dropped = load_rows(args.copy, buckets)
        docs = to_documents(rows)
        if embed is not None:  # test seam: mirrors the build path's model_info
            model_info = {"revision": "test-seam", "bytes": 0}
        else:
            try:
                revision, nbytes = model_weights_info(args.model)
                check_pinned_revision(revision)
            except ValueError as exc:
                print(f"FAIL: {exc}")
                handle.close()
                return 1
            model_info = {"revision": revision, "bytes": nbytes}
        try:
            refresh_params(handle, rows, docs, buckets=buckets, excluded=excluded,
                           corpus_name=corpus_name, corpus_snapshot=corpus_snapshot,
                           copy_path=copy_path, copy_sha=copy_sha,
                           copy_entries=copy_entries, model_info=model_info,
                           dupes_dropped=len(dupes_dropped))
        except ValueError as exc:
            print(f"FAIL: {exc}")
            handle.close()
            return 1
        problems = verify_store(args.copy, handle, buckets)
        fts_diff = fts_parity_probe(args.copy, handle, probe="memory", buckets=buckets)
        handle.close()
        if problems or fts_diff:
            print(f"FAIL: verify={len(problems)} fts_parity={len(fts_diff)}")
            for p in (problems + fts_diff)[:5]:
                print(f"  {p}")
            return 1
        print(f"refreshed: rows={len(docs)} copyEntries={copy_entries} "
              f"dupesDropped={len(dupes_dropped)} (no re-embed)")
        print("VERIFIED: refreshed store is chunk-faithful to the copy; FTS parity probe clean")
        return 0

    try:
        buckets, excluded = scopes.resolve_buckets(args.copy, args.corpus, args.buckets)
    except ValueError as exc:
        print(f"FAIL: {exc}")
        return 2
    corpus_name, corpus_snapshot = None, None
    if args.corpus is not None:
        header, _ = scopes.load_corpus(args.corpus)
        corpus_name = Path(args.corpus).name
        corpus_snapshot = (header or {}).get("snapshotSha256")
    copy_path = str(Path(args.copy).resolve())
    copy_sha = _sha256_file(args.copy)
    if corpus_snapshot and copy_sha != corpus_snapshot:
        print(f"WARNING: copy sha {copy_sha[:12]}... != corpus snapshot "
              f"{corpus_snapshot[:12]}... (corpus drift; both legs share this copy)",
              flush=True)
    rows, copy_entries, dupes_dropped = load_rows(args.copy, buckets)
    if dupes_dropped:
        print(f"deduped: {len(dupes_dropped)} multi-homed hashes (first-by-id wins): "
              f"{[h[:12] for h in dupes_dropped[:5]]}", flush=True)
    docs = to_documents(rows)
    if embed is not None:
        embed_fn = embed  # test seam: (texts) -> vectors
        model_info = {"revision": "test-seam", "bytes": 0}
    else:
        try:
            revision, nbytes = model_weights_info(args.model)
            check_pinned_revision(revision)
        except ValueError as exc:
            print(f"FAIL: {exc}")
            return 1
        model_info = {"revision": revision, "bytes": nbytes}
        model = create_embedding_model(args.model, args.offline)
        embed_fn = model.get_text_embedding_batch
    print(f"buckets: {','.join(buckets)} (excluded={len(excluded)})", flush=True)
    handle = build_store(store_dir, docs, rows, embed_fn, copy_entries, args.embed_batch_size,
                         progress_every=10, buckets=buckets, excluded=excluded,
                         corpus_name=corpus_name, corpus_snapshot=corpus_snapshot,
                         model_info=model_info, dupes_dropped=len(dupes_dropped),
                         copy_path=copy_path, copy_sha=copy_sha)
    problems = verify_store(args.copy, handle, buckets)
    fts_diff = fts_parity_probe(args.copy, handle, probe="memory", buckets=buckets)
    handle.close()
    print(f"ingested: rows={len(docs)} copyEntries={copy_entries} store={store_dir}")
    if problems or fts_diff:
        print(f"FAIL: verify={len(problems)} fts_parity={len(fts_diff)}")
        for p in (problems + fts_diff)[:5]:
            print(f"  {p}")
        return 1
    print("VERIFIED: store is chunk-faithful to the copy; FTS parity probe clean")
    return 0


if __name__ == "__main__":
    sys.exit(main())
