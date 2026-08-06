#!/usr/bin/env python3
"""Regenerates the real-world retrieval corpus for the ai-raccoon embedding benchmark.

Thin CLI wrapper over scripts/src/benchmark_corpus.py (see docs/plans/scripts-refactor.md §5 P6).

Reads real markdown docs from the user's three repos (read-only), extracts
title + a short verbatim body per doc, and emits two C# files:
  - Corpus/RealWorldCorpus.cs  (documents)
  - Corpus/RealWorldQueries.cs (queries with honest ground-truth judgments)
"""

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent / "src"))

from benchmark_corpus import build_queries, collect_docs, emit_cs  # noqa: E402


def main() -> None:
    docs = collect_docs()
    queries = build_queries(docs)
    nd, nq = emit_cs(docs, queries)
    # validation
    ids = {d["id"] for d in docs}
    bad = [q for q in queries if any(r not in ids for r in q[2])]
    empty = [q for q in queries if not q[2]]
    print(f"docs: {nd}, queries: {nq}")
    print(f"queries with unknown relevant ids: {len(bad)}")
    print(f"queries with empty relevance: {len(empty)}")
    total_tokens = sum(len(d["body"].split()) for d in docs) * 1.3
    print(f"~{int(total_tokens)} doc tokens")


if __name__ == "__main__":
    main()
