"""Chunk size vs granite's 128-token local attention window: recall and resource cost per arm.

Runs the bundled engine's own fp16 ONNX graph and tokenizer.json (pinned by SHA-256 in its
manifest), one row per session run as the product does, on the CPU or, on osx-arm64, through the
onnxruntime MLX plugin with the rewritten model_fp16_mlx.onnx graph (ADR-0110). The
chunkers are the ports in chunk_ports. Search is brute-force cosine plus an FTS5 leg fused by
RRF (k=60), an approximation of the product's hybrid search, not a copy of it.
"""

from __future__ import annotations

import json
import random
import re
import sqlite3
import statistics
import time
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Sequence

import numpy as np

from retrieval_tuning import scoring
from retrieval_tuning.chunk_ports import chunk_code, chunk_markdown, distinct_in_order
from retrieval_tuning.padding import pad_row
from retrieval_tuning.process_memory import memory_kib

SPECIAL_TOKEN_RESERVATION = 2
LOCAL_ATTENTION = 128
PAD_ID = 50283
CODE_EXCLUDED_LANGUAGES = frozenset({"CSS", "HTML", "SQL"})
LEG_DEPTH = 50
RRF_K = 60
POSITION_BUCKETS = ((0, 64), (64, 128), (128, 256), (256, 512), (512, 1 << 30))


# ---------------------------------------------------------------------------------------------
# Engine


class Engine:
    """The bundled granite fp16 graph, one row per run (OnnxEmbeddingGenerator.RunEachRow).

    device "cpu" runs model_fp16.onnx on the CPU provider; "mlx" registers the plugin EP from
    mlx_dir and runs model_fp16_mlx.onnx on it, spinning off, as OnnxEmbeddingGenerator does.
    """

    def __init__(self, model_dir: Path, threads: int, device: str = "cpu", mlx_dir: Path | None = None,
                 pad_to: int = 0) -> None:
        import onnxruntime as ort
        from tokenizers import Tokenizer

        self.tokenizer = Tokenizer.from_file(str(model_dir / "tokenizer.json"))
        options = ort.SessionOptions()
        options.intra_op_num_threads = threads
        if device == "mlx":
            options.add_session_config_entry("session.intra_op.allow_spinning", "0")
            if mlx_dir is None:
                raise ValueError("device mlx needs mlx_dir (the folder holding libonnxruntime_mlx_ep.dylib)")
            ort.register_execution_provider_library("MLXExecutionProvider",
                                                    str(mlx_dir / "libonnxruntime_mlx_ep.dylib"))
            options.add_provider_for_devices(
                [d for d in ort.get_ep_devices() if d.ep_name == "MLXExecutionProvider"], {})
            self.session = ort.InferenceSession(str(model_dir / "model_fp16_mlx.onnx"), options)
        elif device == "cpu":
            self.session = ort.InferenceSession(str(model_dir / "model_fp16.onnx"), options,
                                                providers=["CPUExecutionProvider"])
        else:
            raise ValueError(f"unknown device {device!r}")
        self.device = device
        self.pad_to = pad_to
        self.seen_lengths: set[int] = set()
        manifest = json.loads((model_dir / "ai-raccoon.manifest.json").read_text())
        self.window = manifest["contextWindowTokens"]
        self.dimensions = manifest["dimensions"]

    def count(self, text: str) -> int:
        return len(self.tokenizer.encode(text, add_special_tokens=False).ids)

    def embed(self, text: str) -> tuple[np.ndarray, int, float, float]:
        """(unit vector, tokens incl. specials, wall ms, process CPU ms) for one row; new_length says it was a first-seen shape."""
        ids = self.tokenizer.encode(text, add_special_tokens=True).ids[: self.window]
        padded, mask = pad_row(ids, self.pad_to, PAD_ID)
        input_ids = np.asarray([padded], dtype=np.int64)
        wall, cpu = time.perf_counter(), time.process_time()
        out = self.session.run(["sentence_embedding"],
                               {"input_ids": input_ids, "attention_mask": np.asarray([mask], dtype=np.int64)})[0][0]
        wall_ms = (time.perf_counter() - wall) * 1000
        cpu_ms = (time.process_time() - cpu) * 1000
        self.new_length = len(padded) not in self.seen_lengths
        self.seen_lengths.add(len(padded))
        vector = out.astype(np.float32)
        return vector / np.linalg.norm(vector), len(ids), wall_ms, cpu_ms


# ---------------------------------------------------------------------------------------------
# Corpora


@dataclass(frozen=True)
class Stored:
    source: str
    text: str
    line_start: int = 0
    line_end: int = 0
    sections: tuple[str, ...] = ()


@dataclass(frozen=True)
class Query:
    id: str
    text: str
    source: str
    anchor: str | None = None
    lines: tuple[int, int] | None = None


def memory_corpus(repo: Path) -> tuple[list[tuple[str, str]], list[Query]]:
    """Every ADR under docs/adr plus eval-set-100's file-targeted (section-anchored) queries."""
    docs = [(f"docs/adr/{p.name}", p.read_text(encoding="utf-8")) for p in sorted((repo / "docs/adr").glob("*.md"))]
    raw = json.loads((repo / "scripts/retrieval_tuning/corpora/eval-set-100.json").read_text())["queries"]
    queries = []
    for q in raw:
        if q["nonFileTarget"] or q["negativeTest"]:
            continue
        path, _, anchor = q["expectedSource"].partition("#")
        queries.append(Query(q["id"], q["query"], path.replace(":", "/"), anchor or None))
    return docs, queries


def code_corpus(repo: Path) -> tuple[list[tuple[str, str]], list[Query]]:
    """The 12-language code corpus minus CSS/HTML/SQL, which the product has no chunker for."""
    root = repo / "scripts/retrieval_tuning/code-corpus"
    manifest = json.loads((root / "MANIFEST.json").read_text())["files"]
    prefix = "scripts/retrieval_tuning/code-corpus/files/"
    sources = [(f["vendoredPath"][len(prefix):], (repo / f["vendoredPath"]).read_text(encoding="utf-8"))
               for f in manifest if f["language"] not in CODE_EXCLUDED_LANGUAGES]
    queries = [Query(q["id"], q["query"], q["expectedSource"], lines=tuple(q["expectedLines"]))
               for q in json.loads((root / "queries.json").read_text())
               if not q["negativeTest"] and q["language"] not in CODE_EXCLUDED_LANGUAGES]
    return sources, queries


# ---------------------------------------------------------------------------------------------
# Index and search


def _open_index(path: Path) -> sqlite3.Connection:
    path.unlink(missing_ok=True)
    db = sqlite3.connect(path)
    db.executescript("""
        CREATE TABLE chunks(id INTEGER PRIMARY KEY, source TEXT, line_start INT, line_end INT,
                            sections TEXT, value TEXT);
        CREATE TABLE vectors(id INTEGER PRIMARY KEY, embedding BLOB);
        CREATE VIRTUAL TABLE chunks_fts USING fts5(value, content='chunks', content_rowid='id');
    """)
    return db


def _fts_query(text: str) -> str:
    terms = distinct_in_order([t for t in re.findall(r"\w+", text.lower()) if len(t) >= 2])
    return " OR ".join(f'"{t}"' for t in terms)


def rrf(*rankings: Sequence[int], k: int = RRF_K) -> list[int]:
    scores: dict[int, float] = {}
    for ranking in rankings:
        for rank, item in enumerate(ranking):
            scores[item] = scores.get(item, 0.0) + 1.0 / (k + rank + 1)
    return sorted(scores, key=lambda item: -scores[item])


def _percentile(values: Sequence[float], p: float) -> float:
    ordered = sorted(values)
    return ordered[min(len(ordered) - 1, int(round(p / 100 * (len(ordered) - 1))))]


def _timing(values: Sequence[float]) -> dict[str, float]:
    return {"mean": statistics.fmean(values), "p50": _percentile(values, 50), "p95": _percentile(values, 95)}


def _gains(query: Query, ranked: Sequence[int], chunks: Sequence[Stored]) -> dict[str, list[int]]:
    files = distinct_in_order([chunks[i].source for i in ranked])
    gains = {"file": [int(f == query.source) for f in files]}
    if query.anchor is not None:
        gains["span"] = [int(chunks[i].source == query.source and query.anchor in chunks[i].sections)
                         for i in ranked]
    if query.lines is not None:
        gains["span"] = [int(chunks[i].source == query.source and
                             scoring.line_overlap_gain((chunks[i].line_start, chunks[i].line_end), query.lines))
                         for i in ranked]
    return gains


def _metrics(gains: dict[str, list[int]]) -> dict[str, float]:
    out: dict[str, float] = {}
    for kind, g in gains.items():
        out[f"{kind}_hit1"] = scoring.hit_at_k(g, 1)
        out[f"{kind}_recall5"] = scoring.hit_at_k(g, 5)
        out[f"{kind}_recall10"] = scoring.hit_at_k(g, 10)
        out[f"{kind}_mrr10"] = scoring.mrr_at_k(g, 10)
        out[f"{kind}_ndcg10"] = scoring.ndcg_at_k(g, 10)
    return out


@dataclass
class ArmResult:
    corpus: str
    chunk_tokens: int
    threads: int
    documents: int
    chunks: int
    chunk_tokens_mean: float
    chunk_tokens_p95: float
    chunks_over_local_window: float
    tokens_embedded: int
    ingest_wall_s: float
    ingest_cpu_s: float
    embed_ms: dict[str, float]
    embed_cpu_ms: dict[str, float]
    embed_ms_per_1k_tokens: float
    rss_kib_after_load: int
    rss_kib_peak_ingest: int
    rss_kib_peak_total: int
    footprint_kib_after_load: int
    footprint_kib_peak_ingest: int
    footprint_kib_peak_total: int
    device: str
    embed_ms_first_length: dict[str, float]
    embed_ms_repeat_length: dict[str, float]
    distinct_lengths: int
    pad_to: int
    disk_bytes: int
    vector_bytes: int
    query_ms: dict[str, float]
    query_embed_ms: dict[str, float]
    query_cpu_ms: dict[str, float]
    gpu: str
    vector: dict[str, float] = field(default_factory=dict)
    hybrid: dict[str, float] = field(default_factory=dict)
    per_query: list[dict[str, object]] = field(default_factory=list)
    gold_position: list[dict[str, object]] = field(default_factory=list)


def run_arm(repo: Path, model_dir: Path, corpus: str, chunk_tokens: int, threads: int,
            work_dir: Path, device: str = "cpu", mlx_dir: Path | None = None, pad_to: int = 0) -> ArmResult:
    """Chunk, embed and index one corpus at one budget, then run every query twice (second pass timed)."""
    documents, queries = (memory_corpus if corpus == "memory" else code_corpus)(repo)
    engine = Engine(model_dir, threads, device, mlx_dir, pad_to)
    mem_load = memory_kib()
    work_dir.mkdir(parents=True, exist_ok=True)
    db_path = work_dir / f"{corpus}-{chunk_tokens}.db"
    db = _open_index(db_path)

    wall0, cpu0 = time.perf_counter(), time.process_time()
    chunks: list[Stored] = []
    for source, text in documents:
        if corpus == "memory":
            chunks += [Stored(source, c.text, sections=c.sections)
                       for c in chunk_markdown(text, chunk_tokens, engine.count)]
        else:
            chunks += [Stored(source, c.text, c.line_start, c.line_end)
                       for c in chunk_code(text, chunk_tokens, engine.count)]
    vectors = np.zeros((len(chunks), engine.dimensions), dtype=np.float32)
    lengths, embed_ms, embed_cpu, first_ms, repeat_ms = [], [], [], [], []
    for i, chunk in enumerate(chunks):
        vectors[i], n, ms, cpu = engine.embed(chunk.text)
        lengths.append(n - SPECIAL_TOKEN_RESERVATION)
        embed_ms.append(ms)
        embed_cpu.append(cpu)
        (first_ms if engine.new_length else repeat_ms).append(ms)
        db.execute("INSERT INTO chunks VALUES(?,?,?,?,?,?)",
                   (i, chunk.source, chunk.line_start, chunk.line_end, ",".join(chunk.sections), chunk.text))
        db.execute("INSERT INTO vectors VALUES(?,?)", (i, vectors[i].tobytes()))
    db.execute("INSERT INTO chunks_fts(chunks_fts) VALUES('rebuild')")
    db.commit()
    ingest_wall, ingest_cpu = time.perf_counter() - wall0, time.process_time() - cpu0
    mem_ingest = memory_kib()
    db.execute("VACUUM")

    per_query: list[dict[str, object]] = []
    q_ms, q_embed, q_cpu = [], [], []
    for timed in (False, True):
        for query in queries:
            wall, cpu = time.perf_counter(), time.process_time()
            qv, _, e_ms, _ = engine.embed(query.text)
            sims = vectors @ qv
            depth = min(LEG_DEPTH, len(chunks))
            top = np.argpartition(-sims, depth - 1)[:depth]
            vector_rank = [int(i) for i in top[np.argsort(-sims[top])]]
            match = _fts_query(query.text)
            fts_rank = [r[0] for r in db.execute(
                "SELECT rowid FROM chunks_fts WHERE chunks_fts MATCH ? ORDER BY bm25(chunks_fts) LIMIT ?",
                (match, LEG_DEPTH))] if match else []
            fused = rrf(vector_rank, fts_rank)
            if not timed:
                continue
            q_ms.append((time.perf_counter() - wall) * 1000)
            q_cpu.append((time.process_time() - cpu) * 1000)
            q_embed.append(e_ms)
            per_query.append({"id": query.id,
                              "vector": _metrics(_gains(query, vector_rank, chunks)),
                              "hybrid": _metrics(_gains(query, fused, chunks)),
                              "vector_rank": vector_rank[:10]})

    gold_position = _gold_positions(queries, chunks, vectors, engine) if corpus == "code" else []
    db.close()
    lengths_arr = np.asarray(lengths)

    def mean_of(leg: str) -> dict[str, float]:
        keys = per_query[0][leg].keys()  # type: ignore[union-attr]
        return {k: statistics.fmean(q[leg][k] for q in per_query) for k in keys}  # type: ignore[index]

    return ArmResult(
        corpus=corpus, chunk_tokens=chunk_tokens, threads=threads, documents=len(documents),
        chunks=len(chunks), chunk_tokens_mean=float(lengths_arr.mean()),
        chunk_tokens_p95=float(np.percentile(lengths_arr, 95)),
        chunks_over_local_window=float((lengths_arr + SPECIAL_TOKEN_RESERVATION > LOCAL_ATTENTION).mean()),
        tokens_embedded=int(lengths_arr.sum() + SPECIAL_TOKEN_RESERVATION * len(lengths)),
        ingest_wall_s=ingest_wall, ingest_cpu_s=ingest_cpu,
        embed_ms=_timing(embed_ms), embed_cpu_ms=_timing(embed_cpu),
        embed_ms_per_1k_tokens=sum(embed_ms) / (lengths_arr.sum() / 1000),
        rss_kib_after_load=mem_load.rss, rss_kib_peak_ingest=mem_ingest.rss_peak,
        rss_kib_peak_total=memory_kib().rss_peak,
        footprint_kib_after_load=mem_load.footprint, footprint_kib_peak_ingest=mem_ingest.footprint_peak,
        footprint_kib_peak_total=memory_kib().footprint_peak, device=device,
        embed_ms_first_length=_timing(first_ms) if first_ms else {},
        embed_ms_repeat_length=_timing(repeat_ms) if repeat_ms else {},
        distinct_lengths=len(engine.seen_lengths), pad_to=pad_to,
        disk_bytes=db_path.stat().st_size, vector_bytes=int(vectors.nbytes),
        query_ms=_timing(q_ms), query_embed_ms=_timing(q_embed), query_cpu_ms=_timing(q_cpu),
        gpu=("MLX plugin EP (unified memory: GPU buffers count in footprint_kib_*)" if device == "mlx"
             else "not used: CPU provider"),
        vector=mean_of("vector"), hybrid=mean_of("hybrid"), per_query=per_query, gold_position=gold_position,
    )


def _gold_positions(queries: Sequence[Query], chunks: Sequence[Stored], vectors: np.ndarray,
                    engine: Engine) -> list[dict[str, object]]:
    """For each code query: token offset of the target's first line inside the chunk holding it, and that chunk's rank."""
    out: list[dict[str, object]] = []
    for query in queries:
        assert query.lines is not None
        gold = next((i for i, c in enumerate(chunks)
                     if c.source == query.source and c.line_start <= query.lines[0] <= c.line_end), None)
        if gold is None:
            continue
        chunk = chunks[gold]
        before = "".join(chunk.text.splitlines(keepends=True)[: query.lines[0] - chunk.line_start])
        qv = engine.embed(query.text)[0]
        sims = vectors @ qv
        rank = int((sims > sims[gold]).sum()) + 1
        out.append({"id": query.id, "offset_tokens": engine.count(before), "gold_rank": rank})
    return out


def position_buckets(rows: Sequence[dict[str, object]]) -> list[dict[str, object]]:
    """Gold-chunk hit@5 grouped by where the target starts inside its chunk (token offset)."""
    out = []
    for lo, hi in POSITION_BUCKETS:
        inside = [r for r in rows if lo <= int(r["offset_tokens"]) < hi]  # type: ignore[arg-type]
        if inside:
            out.append({"bucket": f"{lo}-{hi if hi < 1 << 30 else 'end'}", "n": len(inside),
                        "gold_hit5": statistics.fmean(int(int(r["gold_rank"]) <= 5) for r in inside)})  # type: ignore[arg-type]
    return out


def paired_bootstrap(a: Sequence[float], b: Sequence[float], resamples: int = 4000,
                     seed: int = 1) -> tuple[float, float, float]:
    """(low, mean, high) 95% interval of mean(b - a) over paired per-query values."""
    diffs = [y - x for x, y in zip(a, b)]
    rng = random.Random(seed)
    means = sorted(statistics.fmean(rng.choices(diffs, k=len(diffs))) for _ in range(resamples))
    return means[int(0.025 * resamples)], statistics.fmean(diffs), means[int(0.975 * resamples) - 1]


def to_json(result: ArmResult) -> str:
    return json.dumps(asdict(result), indent=1)
