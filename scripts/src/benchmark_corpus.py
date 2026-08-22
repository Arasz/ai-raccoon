"""Real-world retrieval corpus for the ai-raccoon embedding benchmark.

Built from THIS repository's own public documentation (ai-raccoon#455, following the ADR-0090
precedent set for tests/AiRaccoon.Tests/Resources/docs-memory.db): docs/ + .ai-badger/, selected
by scripts/src/corpus_config.py — imported here, not re-listed, so one glob list drives both
fixtures (derive-or-delete-the-list). Extracts title + a short verbatim body excerpt per doc, and
emits two C# files:
  - Corpus/RealWorldCorpus.cs  (documents)
  - Corpus/RealWorldQueries.cs (queries with honest ground-truth judgments)

Regenerate: python3 scripts/generate-benchmark-corpus.py
Reproducible from a fresh clone — no private path, no second checkout, no pinned foreign commit.
Override the source root with AIRACCOON_BENCHMARK_CORPUS_ROOT; unset, it auto-detects the
checkout this script lives in.

Query ground truth:
  (a) doc-derived: query restates a doc's heading -> that doc is relevant
  (b) topic clusters: keyword groups -> docs whose title/body actually covers the topic
      (verified by scanning body text)
  (c) every RelevantDocId is validated to exist in the document set.
"""

from __future__ import annotations

import os
import re
import sys
from pathlib import Path
from typing import Dict, List, Optional, Tuple

sys.path.insert(0, str(Path(__file__).resolve().parent))
from corpus_config import select  # noqa: E402

ROOT_ENV = "AIRACCOON_BENCHMARK_CORPUS_ROOT"
MAX_BODY_CHARS = 420  # ~2-4 sentences

# Topic clusters verified against this repository's own docs (ai-raccoon#455): keyword groups,
# each checked by grep to actually appear in the title+body excerpt of at least one selected
# document before being kept here. A topic with no matching document simply yields no cluster
# query (build_queries drops empty relevance sets) rather than an invented one.
TOPICS: Dict[str, List[str]] = {
    "mcp": ["mcp tool", "model context protocol", " mcp "],
    "tdd": ["tdd", "test-driven", "failing test"],
    "validation": ["fluentvalidation"],
    "vector-search": ["vec0", "fts5", "reciprocal rank fusion", "rrf fusion", "ndcg"],
    "embedding-model": ["onnx", "gguf", "embedding model", "pooling"],
    "clean-architecture": ["clean architecture", "clean-architecture"],
    "identity": ["guidv7", "uuid version 7"],
    "logging": ["loggermessage"],
    "workspace": ["workspace sandbox", "workspace consolidat"],
    "promotion": ["promotion tier", "promote to shared"],
    "watch": ["file watcher", "watch pipeline"],
    "security": ["key vault", "hand-rolled crypto", "no hardcoded secret"],
    "state-machine": ["state machine", "state transition"],
    "guard-clauses": ["guard clause"],
    "pre-push-gate": ["pre-push gate", "pre-push hook"],
}

CLUSTER_QUESTIONS: Dict[str, str] = {
    "mcp": "How does this project expose tools to AI assistants over the Model Context Protocol?",
    "tdd": "What rule governs when production code may be written relative to tests?",
    "validation": "Which library keeps domain validation rules colocated with the models?",
    "vector-search": "How does the fused retriever combine keyword search and vector search results?",
    "embedding-model": "What embedding model format and pooling strategy does the project use?",
    "clean-architecture": "What must the domain layer stay free of?",
    "identity": "How does the project mint identifiers that are both unique and sortable by time?",
    "logging": "What convention wraps a high-performance logging call?",
    "workspace": "How does an in-progress workspace stay isolated from committed project memory?",
    "promotion": "How does content move from a workspace into the shared promotion tier?",
    "watch": "How does the file watcher decide when to re-ingest a changed path?",
    "security": "How are secrets and credentials kept out of tracked files?",
    "state-machine": "How are a domain object's state transitions constrained to the declared ones?",
    "guard-clauses": "What replaces a hand-rolled null check for argument validation?",
    "pre-push-gate": "What does the pre-push gate check before a commit reaches origin?",
}


def repo_root() -> str:
    """Resolve the corpus source root.

    AIRACCOON_BENCHMARK_CORPUS_ROOT overrides; otherwise this walks up from this file to find
    the checkout it lives in (AiRaccoon.slnx). No absolute path is hardcoded — the corpus is
    this repository's own public docs (ADR-0090), reproducible from a fresh clone with no
    configuration.
    """
    env_root = os.environ.get(ROOT_ENV, "")
    if env_root:
        return env_root
    here = Path(__file__).resolve()
    for candidate in (here, *here.parents):
        if (candidate / "AiRaccoon.slnx").is_file():
            return str(candidate)
    raise RuntimeError(
        f"Could not locate AiRaccoon.slnx by walking up from {here}; "
        f"set {ROOT_ENV} explicitly."
    )


def read(path: str) -> str:
    try:
        with open(path, encoding="utf-8", errors="replace") as f:
            return f.read()
    except OSError:
        return ""


def strip_md(text: str) -> str:
    text = re.sub(r"^#{1,6}\s*", "", text, flags=re.M)  # headings
    text = re.sub(r"`([^`]*)`", r"\1", text)             # inline code
    text = re.sub(r"\[([^\]]*)\]\([^)]*\)", r"\1", text)  # links
    text = re.sub(r"^\s*[-*]\s*", "", text, flags=re.M)  # list bullets
    text = re.sub(r"[|>]", " ", text)                     # tables/blockquotes
    text = re.sub(r"\s+", " ", text).strip()
    return text


def first_heading(md: str, default: str) -> str:
    m = re.search(r"^#\s+(.+)$", md, flags=re.M)
    return strip_md(m.group(1))[:100] if m else default


def body_excerpt(md: str) -> str:
    """First meaningful paragraph(s) after the first heading, up to MAX_BODY_CHARS."""
    text = strip_md(md)
    # drop the leading title (first sentence equals the heading usually)
    sentences = re.split(r"(?<=[.!?])\s+", text)
    body = ""
    for s in sentences:
        if len(s.strip()) < 25:  # skip heading-like fragments
            continue
        body = (body + " " + s).strip()
        if len(body) >= MAX_BODY_CHARS:
            break
    return body[:MAX_BODY_CHARS] or text[:MAX_BODY_CHARS]


def safe_id(name: str) -> str:
    return re.sub(r"[^a-z0-9-]", "-", name.lower()).strip("-")[:70]


# Tokens a corpus document must not carry (composed from fragments so this generator's own
# source never contains them literally — see CorpusFixtureGuardTests.
# BenchmarkCorpusArtefacts_CarryNoPrivateRepoContent, ai-raccoon#455). Some of this repository's
# own ADRs (0047/0049/0050/0090) legitimately discuss the removed private corpus by name as
# historical fact; their excerpts are excluded from THIS corpus rather than from the
# corpus_config selection itself, which still ingests them into docs-memory.db — a different
# fixture with its own separate private-bank-absence guard (ADR-0090).
_FORBIDDEN_TOKENS = (
    "js" + "aa",
    "job-search-ai-" + "assistant",
    "arasz" + "kiewicz",
)


def _carries_forbidden_content(text: str) -> bool:
    lower = text.lower()
    return any(token in lower for token in _FORBIDDEN_TOKENS)


def csharp_literal(text: str) -> str:
    # raw string literal with safe delimiter; bodies contain quotes/apostrophes
    quotes = text.count('"')
    delim = '"' * (3 if quotes < 500 else 4)
    return f'{delim}\n{text}\n{delim}'


def collect_docs(root: Optional[str] = None, relative_paths: Optional[List[str]] = None) -> List[dict]:
    """Extract corpus documents from `root` (default: repo_root()).

    `relative_paths` overrides the corpus_config selection — used by tests to exercise the
    extraction logic against a small fixed fixture without needing a git-tracked temp repo;
    production callers leave it unset so the real ADR-0090 selection (git-tracked files only)
    drives what gets committed.
    """
    if root is None:
        root = repo_root()
    if relative_paths is None:
        relative_paths = select(Path(root))

    docs = []
    seen: Dict[str, bool] = {}
    for rel in relative_paths:
        path = os.path.join(root, rel)
        md = read(path)
        if len(md) < 60:
            continue
        stem = rel[:-3] if rel.endswith(".md") else rel
        title = first_heading(md, os.path.splitext(os.path.basename(rel))[0])
        body = body_excerpt(md)
        if not body:
            continue
        if _carries_forbidden_content(title) or _carries_forbidden_content(body):
            continue
        doc_id = safe_id(stem)
        if doc_id in seen:
            continue
        seen[doc_id] = True
        lower = (title + " " + body).lower()
        topics = sorted({label for label, keywords in TOPICS.items() if any(k in lower for k in keywords)})
        docs.append({"id": doc_id, "title": title, "body": body, "source": rel, "topics": topics})

    docs.sort(key=lambda d: d["id"])
    return docs


def build_queries(docs: List[dict]) -> List[tuple]:
    by_id = {d["id"]: d for d in docs}
    queries = []  # (id, text, relevant_ids, judgment)

    def add_query(qid, text, relevant, judgment):
        relevant = [r for r in relevant if r in by_id]
        if relevant:
            queries.append((qid, text, relevant, judgment))

    # (a) doc-derived: restate a doc's heading as a question; relevant = the doc itself + other
    # docs verified (by keyword scan) to cover the same first topic. Sample every 3rd doc (by
    # id, already sorted) for the easy tier, matching the original derivation method.
    topic_docs: Dict[str, List[str]] = {}
    for d in docs:
        for t in d["topics"]:
            topic_docs.setdefault(t, []).append(d["id"])

    for d in docs[::3]:
        title = d["title"].strip(" ?.")
        question = f"What does the project decide or document about: {title}?"
        relevant = [d["id"]] + [x for x in topic_docs.get(d["topics"][0], []) if x != d["id"]] if d["topics"] else [d["id"]]
        add_query(f"doc-{d['id']}", question, relevant,
                  f"// judgment: query restates the decision/heading of {d['id']}; same-topic docs keyword-verified")

    # (b) topic clusters: every doc whose title/body was keyword-verified to cover the topic.
    for topic, question in CLUSTER_QUESTIONS.items():
        relevant = [d["id"] for d in docs if topic in d["topics"]]
        add_query(f"cluster-{topic}", question, relevant,
                  f"// judgment: all docs whose title/body covers '{topic}' were keyword-verified")

    return queries


def csharp_string(text: str) -> str:
    """Regular C# string literal (escaped) — used for titles, matching committed style."""
    return '"' + text.replace("\\", "\\\\").replace('"', '\\"') + '"'


def emit_cs(docs: List[dict], queries: List[tuple], out: Optional[str] = None) -> Tuple[int, int]:
    if out is None:
        out = os.path.join(repo_root(), "benchmarks", "AiRaccoon.Benchmarks", "Corpus")
    os.makedirs(out, exist_ok=True)

    # RealWorldCorpus.cs
    lines = [
        "// AUTO-GENERATED from this repository's own public docs (ai-raccoon#455, ADR-0090).",
        "// Regenerate with scripts/generate-benchmark-corpus.py.",
        "// Bodies are verbatim excerpts (2-4 sentences) from the source files.",
        "namespace AiRaccoon.Benchmarks.Corpus;",
        "",
        "/// <summary>Real-world retrieval corpus: this repository's own public documentation.</summary>",
        "public static class RealWorldCorpus",
        "{",
        "    public static IReadOnlyList<CorpusDocument> Documents { get; } =",
        "    [",
    ]
    for d in docs:
        lines.append(f'        new({csharp_string(d["id"])}, {csharp_string(d["title"])}, {csharp_literal(d["body"])}),')
    lines += [
        "    ];",
        "}",
        "",
    ]
    with open(os.path.join(out, "RealWorldCorpus.cs"), "w") as f:
        f.write("\n".join(lines))

    # RealWorldQueries.cs
    lines = [
        "// AUTO-GENERATED from this repository's own public docs; each query carries a",
        "// // judgment comment documenting how its relevance set was verified.",
        "using AiRaccoon.Benchmarks.Corpus;",
        "",
        "namespace AiRaccoon.Benchmarks.Corpus;",
        "",
        "/// <summary>Real-world queries with honest ground-truth relevance judgments.</summary>",
        "public static class RealWorldQueries",
        "{",
        "    public static IReadOnlyList<CorpusQuery> Queries { get; } =",
        "    [",
    ]
    for qid, text, relevant, judgment in queries:
        ids = ", ".join(f'"{r}"' for r in relevant)
        lines.append(f"        {judgment}")
        lines.append(f'        new({csharp_string(qid)}, {csharp_literal(text)}, [{ids}]),')
    lines += [
        "    ];",
        "}",
        "",
    ]
    with open(os.path.join(out, "RealWorldQueries.cs"), "w") as f:
        f.write("\n".join(lines))

    return len(docs), len(queries)
