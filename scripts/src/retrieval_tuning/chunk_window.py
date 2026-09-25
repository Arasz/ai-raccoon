"""Chunk size vs granite's 128-token local attention window: recall and resource cost per arm.

Runs the bundled engine's own fp16 ONNX graph and tokenizer.json (pinned by SHA-256 in its
manifest), one row per session run as the product does, on the CPU or, on osx-arm64, through the
onnxruntime MLX plugin with the rewritten model_fp16_mlx.onnx graph (ADR-0110). The
chunkers are the ports in chunk_ports. Search is brute-force cosine plus an FTS5 leg fused by
RRF (k=60), an approximation of the product's hybrid search, not a copy of it.
"""

from __future__ import annotations

import ctypes
import json
import os
import random
import re
import sqlite3
import statistics
import subprocess
import time
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Sequence

import numpy as np

from retrieval_tuning import coreml, scoring
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


def _spin_off(options: "ort.SessionOptions") -> None:  # noqa: F821 - onnxruntime is a lazy import
    options.add_session_config_entry("session.intra_op.allow_spinning", "0")
    options.add_session_config_entry("session.inter_op.allow_spinning", "0")


def _graph_input_shapes(model_path: Path) -> dict[str, list[object]]:
    """input name -> ordered dim list (int or symbolic str), read from the graph without a session."""
    import onnx

    model = onnx.load(str(model_path), load_external_data=False)
    return {inp.name: [dim.dim_param or dim.dim_value for dim in inp.type.tensor_type.shape.dim]
            for inp in model.graph.input}


def _capture_stderr(build):
    """Runs build() with process fd 2 redirected to a temp file; returns (result, captured text).

    ORT's CoreML EP logs partition/compute-plan info straight to C++ stderr, invisible to Python's
    own logging, so redirecting the fd is the only way to recover it from inside the process.
    """
    import tempfile

    fd, path = tempfile.mkstemp(prefix="coreml-ep-log-")
    os.close(fd)
    saved = os.dup(2)
    log_fd = os.open(path, os.O_WRONLY | os.O_TRUNC)
    try:
        os.dup2(log_fd, 2)
        result = build()
    finally:
        os.dup2(saved, 2)
        os.close(saved)
        os.close(log_fd)
    text = Path(path).read_text(errors="replace")
    Path(path).unlink(missing_ok=True)
    return result, text


def _dir_size_kib(root: Path) -> int:
    return sum(p.stat().st_size for p in root.rglob("*") if p.is_file()) // 1024 if root.exists() else 0


def _ps_time_seconds(value: str) -> float:
    seconds = 0.0
    for part in value.split(":"):
        seconds = seconds * 60 + float(part)
    return seconds


def ane_compiler_cpu_s() -> float:
    """Summed CPU time (seconds) of aned and ANECompilerService right now, from `ps -A`."""
    out = subprocess.run(["ps", "-A", "-o", "time=,comm="], capture_output=True, text=True, check=False).stdout
    total = 0.0
    for line in out.splitlines():
        parts = line.split(None, 1)
        if len(parts) != 2:
            continue
        time_str, comm = parts
        if comm.strip().rsplit("/", 1)[-1] in ("aned", "ANECompilerService"):
            total += _ps_time_seconds(time_str)
    return total


class Engine:
    """The bundled granite fp16 graph, one row per run (OnnxEmbeddingGenerator.RunEachRow).

    device "cpu" runs model_fp16.onnx on the CPU provider; "mlx" registers the plugin EP from
    mlx_dir and runs model_fp16_mlx.onnx on it; "coreml" builds one lazily-cached session per
    length bucket (CoreML only accepts static shapes). All three run with ORT spinning off.
    """

    def __init__(self, model_dir: Path, threads: int, device: str = "cpu", mlx_dir: Path | None = None,
                 pad_to: int = 0, buckets: Sequence[int] = (), cache_limit_mib: int | None = None,
                 compute_units: str | None = None, max_sessions: int = 0, coreml_cache_dir: Path | None = None,
                 profile_compute_plan: bool = False, specialization: str | None = None) -> None:
        import onnxruntime as ort
        from tokenizers import Tokenizer

        self.tokenizer = Tokenizer.from_file(str(model_dir / "tokenizer.json"))
        manifest = json.loads((model_dir / "ai-raccoon.manifest.json").read_text())
        self.window = manifest["contextWindowTokens"]
        self.dimensions = manifest["dimensions"]
        self.device = device
        self.pad_to = pad_to
        self.buckets = tuple(buckets)
        self.seen_lengths: set[int] = set()
        self.padded_tokens = 0
        self.coreml_nodes: int | None = None
        self.coreml_partitions: int | None = None
        self.compute_plan: dict[str, int] = {}
        self.bucket_load_s: dict[int, float] = {}
        self.bucket_first_run_ms: dict[int, float] = {}

        options = ort.SessionOptions()
        options.intra_op_num_threads = threads
        _spin_off(options)
        if device == "mlx":
            if mlx_dir is None:
                raise ValueError("device mlx needs mlx_dir (the folder holding libonnxruntime_mlx_ep.dylib)")
            ort.register_execution_provider_library("MLXExecutionProvider",
                                                    str(mlx_dir / "libonnxruntime_mlx_ep.dylib"))
            options.add_provider_for_devices(
                [d for d in ort.get_ep_devices() if d.ep_name == "MLXExecutionProvider"], {})
            self.session = ort.InferenceSession(str(model_dir / "model_fp16_mlx.onnx"), options)
            self.mlx = ctypes.CDLL(str(mlx_dir / "libmlxc.dylib"))
            if cache_limit_mib is not None:
                previous = ctypes.c_size_t()
                self.mlx.mlx_set_cache_limit(ctypes.byref(previous), ctypes.c_size_t(cache_limit_mib << 20))
        elif device == "cpu":
            self.session = ort.InferenceSession(str(model_dir / "model_fp16.onnx"), options,
                                                providers=["CPUExecutionProvider"])
        elif device == "coreml":
            if not self.buckets:
                raise ValueError("device coreml needs buckets (bucket_lengths(pad_to, top_bucket))")
            if compute_units is None:
                raise ValueError("device coreml needs compute_units")
            if coreml_cache_dir is None:
                raise ValueError("device coreml needs coreml_cache_dir")
            self._threads = threads
            self._model_path = model_dir / "model_fp16.onnx"
            self._model_sha = manifest["onnx"]["files"][0]["sha256"]
            self._compute_units = compute_units
            self.coreml_cache_root = Path(coreml_cache_dir)
            self._profile_compute_plan = profile_compute_plan
            self._specialization = specialization
            self._input_shapes = _graph_input_shapes(self._model_path)
            self._first_build_pending = True
            self.sessions = coreml.BucketSessions(factory=self._build_coreml_session, max_live=max_sessions)
        else:
            raise ValueError(f"unknown device {device!r}")

    def _build_coreml_session(self, bucket: int) -> object:
        import onnxruntime as ort

        so = ort.SessionOptions()
        so.intra_op_num_threads = self._threads
        _spin_off(so)
        if self._first_build_pending:
            so.log_severity_level = 0
            so.log_verbosity_level = 0
        for name, value in coreml.free_dim_overrides(self._input_shapes, bucket).items():
            so.add_free_dimension_override_by_name(name, value)
        cache_dir = coreml.cache_dir_for(self.coreml_cache_root, self._model_sha, ort.__version__,
                                         self._compute_units, bucket)
        cache_dir.mkdir(parents=True, exist_ok=True)
        provider_options = {"ModelFormat": "MLProgram", "MLComputeUnits": self._compute_units,
                            "RequireStaticInputShapes": "1", "ModelCacheDirectory": str(cache_dir)}
        if self._profile_compute_plan:
            provider_options["ProfileComputePlan"] = "1"
        if self._specialization:
            provider_options["SpecializationStrategy"] = self._specialization

        def build() -> object:
            return ort.InferenceSession(str(self._model_path), so,
                                        providers=[("CoreMLExecutionProvider", provider_options),
                                                   "CPUExecutionProvider"])

        t0 = time.perf_counter()
        if self._first_build_pending:
            self._first_build_pending = False
            session, log_text = _capture_stderr(build)
            self._read_coreml_log(log_text)
        else:
            session = build()
        self.bucket_load_s[bucket] = time.perf_counter() - t0
        return session

    def _read_coreml_log(self, text: str) -> None:
        try:
            info = coreml.parse_partition_log(text)
        except ValueError:
            return
        self.coreml_nodes = info["nodes_supported"]
        self.coreml_partitions = info["partitions"]
        if self._profile_compute_plan:
            self.compute_plan = coreml.parse_compute_plan(text)

    def mlx_memory_mib(self) -> dict[str, int]:
        """MLX allocator counters (active, free-buffer cache, peak) in MiB; empty off the MLX device."""
        if self.device != "mlx":
            return {}
        out = {}
        for name in ("active", "cache", "peak"):
            value = ctypes.c_size_t()
            getattr(self.mlx, f"mlx_get_{name}_memory")(ctypes.byref(value))
            out[name] = value.value >> 20
        return out

    def count(self, text: str) -> int:
        return len(self.tokenizer.encode(text, add_special_tokens=False).ids)

    def embed(self, text: str) -> tuple[np.ndarray, int, float, float]:
        """(unit vector, tokens incl. specials, wall ms, process CPU ms) for one row; new_length says it was a first-seen shape."""
        ids = self.tokenizer.encode(text, add_special_tokens=True).ids[: self.window]
        if self.device == "coreml" and len(ids) > self.buckets[-1]:
            raise ValueError(f"row of {len(ids)} tokens exceeds the top bucket ({self.buckets[-1]})")
        padded, mask = pad_row(ids, self.pad_to, PAD_ID, self.buckets)
        input_ids = np.asarray([padded], dtype=np.int64)
        session = self.sessions.get(len(padded)) if self.device == "coreml" else self.session
        wall, cpu = time.perf_counter(), time.process_time()
        out = session.run(["sentence_embedding"],
                          {"input_ids": input_ids, "attention_mask": np.asarray([mask], dtype=np.int64)})[0][0]
        wall_ms = (time.perf_counter() - wall) * 1000
        cpu_ms = (time.process_time() - cpu) * 1000
        if self.device == "coreml":
            self.bucket_first_run_ms.setdefault(len(padded), wall_ms)
        self.padded_tokens += len(padded) - len(ids)
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
    buckets: list[int]
    cache_limit_mib: int | None
    mlx_memory_mib: dict[str, int]
    padded_tokens: int
    disk_bytes: int
    vector_bytes: int
    query_ms: dict[str, float]
    query_embed_ms: dict[str, float]
    query_cpu_ms: dict[str, float]
    gpu: str
    coreml_nodes: int | None = None
    coreml_partitions: int | None = None
    compute_units: str | None = None
    bucket_load_s: dict[int, float] = field(default_factory=dict)
    bucket_first_run_ms: dict[int, float] = field(default_factory=dict)
    sessions_live_peak: int = 0
    evictions: int = 0
    neural_kib_peak: int = 0
    cache_disk_kib: int = 0
    compiler_cpu_s: float = 0.0
    energy_nj: int = 0
    loadavg: float = 0.0
    vector: dict[str, float] = field(default_factory=dict)
    hybrid: dict[str, float] = field(default_factory=dict)
    per_query: list[dict[str, object]] = field(default_factory=list)
    gold_position: list[dict[str, object]] = field(default_factory=list)


def run_arm(repo: Path, model_dir: Path, corpus: str, chunk_tokens: int, threads: int,
            work_dir: Path, device: str = "cpu", mlx_dir: Path | None = None, pad_to: int = 0,
            buckets: Sequence[int] = (), cache_limit_mib: int | None = None,
            compute_units: str | None = None, max_sessions: int = 0, coreml_cache_dir: Path | None = None,
            profile_compute_plan: bool = False, specialization: str | None = None) -> ArmResult:
    """Chunk, embed and index one corpus at one budget, then run every query twice (second pass timed)."""
    documents, queries = (memory_corpus if corpus == "memory" else code_corpus)(repo)
    engine = Engine(model_dir, threads, device, mlx_dir, pad_to, buckets, cache_limit_mib,
                    compute_units, max_sessions, coreml_cache_dir, profile_compute_plan, specialization)
    compiler_cpu_before = ane_compiler_cpu_s() if device == "coreml" else 0.0
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
    mem_final = memory_kib()
    compiler_cpu_s = ane_compiler_cpu_s() - compiler_cpu_before if device == "coreml" else 0.0

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
        rss_kib_peak_total=mem_final.rss_peak,
        footprint_kib_after_load=mem_load.footprint, footprint_kib_peak_ingest=mem_ingest.footprint_peak,
        footprint_kib_peak_total=mem_final.footprint_peak, device=device,
        embed_ms_first_length=_timing(first_ms) if first_ms else {},
        embed_ms_repeat_length=_timing(repeat_ms) if repeat_ms else {},
        distinct_lengths=len(engine.seen_lengths), pad_to=pad_to, buckets=list(buckets),
        cache_limit_mib=cache_limit_mib, mlx_memory_mib=engine.mlx_memory_mib(),
        padded_tokens=engine.padded_tokens,
        disk_bytes=db_path.stat().st_size, vector_bytes=int(vectors.nbytes),
        query_ms=_timing(q_ms), query_embed_ms=_timing(q_embed), query_cpu_ms=_timing(q_cpu),
        gpu=("MLX plugin EP (unified memory: GPU buffers count in footprint_kib_*)" if device == "mlx"
             else "CoreML EP (ANE usage in neural_kib_peak)" if device == "coreml"
             else "not used: CPU provider"),
        coreml_nodes=engine.coreml_nodes, coreml_partitions=engine.coreml_partitions,
        compute_units=compute_units if device == "coreml" else None,
        bucket_load_s=dict(engine.bucket_load_s), bucket_first_run_ms=dict(engine.bucket_first_run_ms),
        sessions_live_peak=engine.sessions.peak_live if device == "coreml" else 0,
        evictions=engine.sessions.evictions if device == "coreml" else 0,
        neural_kib_peak=mem_final.neural_footprint_peak,
        cache_disk_kib=_dir_size_kib(engine.coreml_cache_root) if device == "coreml" else 0,
        compiler_cpu_s=compiler_cpu_s, energy_nj=mem_final.energy_nj, loadavg=os.getloadavg()[0],
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
