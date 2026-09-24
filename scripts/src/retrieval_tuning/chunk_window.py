"""Chunk size vs granite's 128-token local attention window: recall and resource cost per arm.

Runs the bundled engine's own fp16 ONNX graph and tokenizer.json (pinned by SHA-256 in its
manifest) on the CPU, one row per session run with half the cores, as the product does. The
chunkers are ports of MarkdownChunker and CodeChunker. Sub-fence re-fencing is not ported: an
oversized fence falls back to per-line units. Search is brute-force cosine plus an FTS5 leg
fused by RRF (k=60), an approximation of the product's hybrid search, not a copy of it.
"""

from __future__ import annotations

import json
import random
import re
import resource
import sqlite3
import statistics
import time
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Callable, Sequence

import numpy as np

from retrieval_tuning import scoring

TokenCount = Callable[[str], int]

OVERLAY_TOKENS = 48
SPECIAL_TOKEN_RESERVATION = 2
LOCAL_ATTENTION = 128
CODE_EXCLUDED_LANGUAGES = frozenset({"CSS", "HTML", "SQL"})
LEG_DEPTH = 50
RRF_K = 60
POSITION_BUCKETS = ((0, 64), (64, 128), (128, 256), (256, 512), (512, 1 << 30))


@dataclass(frozen=True)
class MarkdownChunk:
    text: str
    sections: tuple[str, ...]


@dataclass(frozen=True)
class CodeChunk:
    text: str
    line_start: int
    line_end: int


@dataclass
class _Unit:
    lines: list[str]
    tokens: int
    heading_level: int = 0
    source_header: bool = False

    @property
    def is_heading(self) -> bool:
        return self.heading_level > 0

    @property
    def is_blank(self) -> bool:
        return len(self.lines) == 1 and not self.lines[0].strip()

    @property
    def is_contentful(self) -> bool:
        return not self.is_blank and not self.is_heading

    @property
    def is_section_opener(self) -> bool:
        return self.is_heading and self.heading_level <= 2 and not self.source_header


def slug(heading: str) -> str:
    """The eval sets' `file#anchor` form of a heading: lowercase words joined by hyphens."""
    return "-".join(re.findall(r"[a-z0-9]+", heading.lower()))


def distinct_in_order(items: Sequence[str]) -> list[str]:
    seen: set[str] = set()
    return [x for x in items if not (x in seen or seen.add(x))]


def _split_lines(text: str) -> list[str]:
    text = text.replace("\r\n", "\n").replace("\r", "\n")
    lines = text.splitlines(keepends=True)
    return lines


def _largest_prefix(text: str, max_tokens: int, count: TokenCount) -> int:
    lo, hi = 0, len(text)
    while lo < hi:
        mid = lo + (hi - lo + 1) // 2
        if count(text[:mid]) <= max_tokens:
            lo = mid
        else:
            hi = mid - 1
    return lo


def _heading_level(line: str) -> int:
    trimmed = line.lstrip()
    level = len(trimmed) - len(trimmed.lstrip("#"))
    if 1 <= level <= 6 and level < len(trimmed) and trimmed[level] == " " and trimmed[level + 1:].strip():
        return level
    return 0


def _add_unit_or_split(units: list[_Unit], text: str, max_tokens: int, count: TokenCount) -> None:
    n = count(text)
    if n <= max_tokens:
        units.append(_Unit([text], n))
        return
    remaining = text
    while remaining:
        head_len = max(1, _largest_prefix(remaining, max_tokens, count))
        head = remaining[:head_len]
        units.append(_Unit([head], count(head)))
        remaining = remaining[head_len:]


def _is_fence(line: str) -> bool:
    trimmed = line.lstrip()
    return trimmed.startswith("```") or trimmed.startswith("~~~")


def _markdown_units(text: str, max_tokens: int, count: TokenCount) -> list[_Unit]:
    units: list[_Unit] = []
    fence: list[str] | None = None
    for line in _split_lines(text):
        if fence is None:
            if _is_fence(line):
                fence = [line]
            else:
                _add_unit_or_split(units, line, max_tokens, count)
            continue
        fence.append(line)
        if _is_fence(line):
            _flush_fence(units, fence, max_tokens, count)
            fence = None
    if fence is not None:
        _flush_fence(units, fence, max_tokens, count)
    for unit in units:
        if len(unit.lines) == 1:
            unit.heading_level = _heading_level(unit.lines[0])
            unit.source_header = unit.lines[0].lstrip().lower().startswith("## source:")
    return units


def _flush_fence(units: list[_Unit], fence: list[str], max_tokens: int, count: TokenCount) -> None:
    exact = count("".join(fence))
    if exact <= max_tokens:
        units.append(_Unit(fence, exact))
        return
    for line in fence:
        _add_unit_or_split(units, line, max_tokens, count)


def _contexts(units: list[_Unit]) -> list[str]:
    """The leaf heading in force before each unit (levels 1-2, as HeadingPathParser keeps)."""
    stack: list[tuple[int, str]] = []
    contexts: list[str] = []
    for unit in units:
        contexts.append(stack[-1][1] if stack else "")
        if unit.is_section_opener:
            level = unit.heading_level
            while stack and stack[-1][0] >= level:
                stack.pop()
            stack.append((level, unit.lines[0].lstrip()[level:].strip()))
    return contexts


def _joined(units: list[_Unit]) -> str:
    return "".join(line for unit in units for line in unit.lines)


def _defer_open_section(chunk: list[_Unit], new_count: int, units: list[_Unit], cursor: int) -> int:
    new_start = len(chunk) - new_count
    opener = next((i for i in range(len(chunk) - 1, new_start, -1) if chunk[i].is_section_opener), -1)
    if opener < 0:
        return 0
    has_own_content = any(u.is_contentful for u in chunk[opener + 1:])
    idx = cursor
    while idx < len(units) and units[idx].is_blank:
        idx += 1
    continues = idx < len(units) and not units[idx].is_section_opener
    if has_own_content and not continues:
        return 0
    if not any(u.is_contentful for u in chunk[new_start:opener]):
        return 0
    removed = len(chunk) - opener
    del chunk[opener:]
    return removed


def chunk_markdown(text: str, max_tokens: int, count: TokenCount,
                   overlay_tokens: int = OVERLAY_TOKENS) -> list[MarkdownChunk]:
    """MarkdownChunker.ChunkWithHeadings: greedy line units, tail overlay, cut sections deferred."""
    overlay_tokens = min(overlay_tokens, max(0, max_tokens - 1))
    units = _markdown_units(text, max_tokens, count)
    contexts = _contexts(units)
    chunks: list[MarkdownChunk] = []
    previous: list[_Unit] | None = None
    cursor = 0
    while cursor < len(units):
        overlay: list[_Unit] = []
        used = 0
        for unit in reversed(previous or []):
            if used + unit.tokens > overlay_tokens:
                break
            overlay.insert(0, unit)
            used += unit.tokens
        chunk = list(overlay)
        tokens = used
        new_count = 0
        c = cursor
        while c < len(units):
            if new_count > 0 and tokens + units[c].tokens > max_tokens:
                break
            chunk.append(units[c])
            tokens += units[c].tokens
            new_count += 1
            c += 1
        while count(_joined(chunk)) > max_tokens:
            if len(chunk) > new_count:
                chunk.pop(0)
                continue
            if new_count <= 1:
                break
            chunk.pop()
            new_count -= 1
            c -= 1
        while (deferred := _defer_open_section(chunk, new_count, units, c)) > 0:
            new_count -= deferred
            c -= deferred
        body = _joined(chunk)
        if body.strip():
            new_start = len(chunk) - new_count
            leaves = [contexts[cursor + i] for i, u in enumerate(chunk[new_start:]) if u.is_contentful]
            sections = tuple(distinct_in_order([slug(leaf) for leaf in leaves if leaf]))
            chunks.append(MarkdownChunk(body, sections))
        previous = chunk
        cursor = c
    return chunks


def _brace_delta(text: str) -> int:
    return text.count("{") - text.count("}")


def chunk_code(text: str, max_tokens: int, count: TokenCount) -> list[CodeChunk]:
    """CodeChunker.Chunk: blank-line blocks, last brace-balanced boundary within budget, no overlay."""
    lines = _split_lines(text)
    if not lines or all(not line.strip() for line in lines):
        return []
    units: list[tuple[str, int, int, int, int]] = []
    balance = 0

    def add_line(line: str, number: int) -> None:
        nonlocal balance
        n = count(line)
        if n <= max_tokens:
            balance += _brace_delta(line)
            units.append((line, n, number, number, balance))
            return
        remaining = line
        while remaining:
            head = remaining[:max(1, _largest_prefix(remaining, max_tokens, count))]
            balance += _brace_delta(head)
            units.append((head, count(head), number, number, balance))
            remaining = remaining[len(head):]

    def add_block(block: list[str], start: int, end: int) -> None:
        nonlocal balance
        joined = "".join(block)
        n = count(joined)
        if n <= max_tokens:
            balance += _brace_delta(joined)
            units.append((joined, n, start, end, balance))
            return
        for i, line in enumerate(block):
            add_line(line, start + i)

    block: list[str] = []
    block_start = 1
    for i, line in enumerate(lines):
        block.append(line)
        at_transition = (not line.strip() and i + 1 < len(lines) and lines[i + 1].strip()
                         and any(x.strip() for x in block))
        if at_transition:
            add_block(block, block_start, i + 1)
            block = []
            block_start = i + 2
    if block:
        add_block(block, block_start, len(lines))

    chunks: list[CodeChunk] = []
    cursor = 0
    while cursor < len(units):
        tokens, reach, c = 0, cursor, cursor
        while c < len(units):
            if c > cursor and tokens + units[c][1] > max_tokens:
                break
            tokens += units[c][1]
            reach = c
            c += 1
        end = next((k for k in range(reach, cursor - 1, -1) if units[k][4] == 0), reach)
        while end > cursor and count("".join(u[0] for u in units[cursor:end + 1])) > max_tokens:
            end -= 1
        chunks.append(CodeChunk("".join(u[0] for u in units[cursor:end + 1]), units[cursor][2], units[end][3]))
        cursor = end + 1
    return chunks


# ---------------------------------------------------------------------------------------------
# Engine


def rss_kib() -> dict[str, int]:
    """Current (VmRSS) and peak (VmHWM) resident set of this process, in KiB."""
    out: dict[str, int] = {}
    for line in Path("/proc/self/status").read_text().splitlines():
        key, _, value = line.partition(":")
        if key in ("VmRSS", "VmHWM"):
            out[key] = int(value.split()[0])
    return out


class Engine:
    """The bundled granite fp16 graph, CPU provider, one row per run (OnnxEmbeddingGenerator.RunEachRow)."""

    def __init__(self, model_dir: Path, threads: int) -> None:
        import onnxruntime as ort
        from tokenizers import Tokenizer

        self.tokenizer = Tokenizer.from_file(str(model_dir / "tokenizer.json"))
        options = ort.SessionOptions()
        options.intra_op_num_threads = threads
        self.session = ort.InferenceSession(str(model_dir / "model_fp16.onnx"), options,
                                            providers=["CPUExecutionProvider"])
        self.window = json.loads((model_dir / "ai-raccoon.manifest.json").read_text())["contextWindowTokens"]

    def count(self, text: str) -> int:
        return len(self.tokenizer.encode(text, add_special_tokens=False).ids)

    def embed(self, text: str) -> tuple[np.ndarray, int, float, float]:
        """(unit vector, tokens incl. specials, wall ms, process CPU ms) for one row."""
        ids = self.tokenizer.encode(text, add_special_tokens=True).ids[: self.window]
        input_ids = np.asarray([ids], dtype=np.int64)
        wall, cpu = time.perf_counter(), time.process_time()
        out = self.session.run(["sentence_embedding"],
                               {"input_ids": input_ids, "attention_mask": np.ones_like(input_ids)})[0][0]
        wall_ms = (time.perf_counter() - wall) * 1000
        cpu_ms = (time.process_time() - cpu) * 1000
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
            work_dir: Path) -> ArmResult:
    """Chunk, embed and index one corpus at one budget, then run every query twice (second pass timed)."""
    documents, queries = (memory_corpus if corpus == "memory" else code_corpus)(repo)
    engine = Engine(model_dir, threads)
    rss_load = rss_kib()["VmRSS"]
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
    vectors = np.zeros((len(chunks), 384), dtype=np.float32)
    lengths, embed_ms, embed_cpu = [], [], []
    for i, chunk in enumerate(chunks):
        vectors[i], n, ms, cpu = engine.embed(chunk.text)
        lengths.append(n - SPECIAL_TOKEN_RESERVATION)
        embed_ms.append(ms)
        embed_cpu.append(cpu)
        db.execute("INSERT INTO chunks VALUES(?,?,?,?,?,?)",
                   (i, chunk.source, chunk.line_start, chunk.line_end, ",".join(chunk.sections), chunk.text))
        db.execute("INSERT INTO vectors VALUES(?,?)", (i, vectors[i].tobytes()))
    db.execute("INSERT INTO chunks_fts(chunks_fts) VALUES('rebuild')")
    db.commit()
    ingest_wall, ingest_cpu = time.perf_counter() - wall0, time.process_time() - cpu0
    rss_ingest = rss_kib()["VmHWM"]
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
        rss_kib_after_load=rss_load, rss_kib_peak_ingest=rss_ingest, rss_kib_peak_total=rss_kib()["VmHWM"],
        disk_bytes=db_path.stat().st_size, vector_bytes=int(vectors.nbytes),
        query_ms=_timing(q_ms), query_embed_ms=_timing(q_embed), query_cpu_ms=_timing(q_cpu),
        gpu="not measured: no GPU in this host; the Linux package runs ORT on the CPU (ADR-0108)",
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
