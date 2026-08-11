#!/usr/bin/env python3
"""Auto-grade memory_search results using Prometheus 7B via LM Studio.

Reads ungraded rows from the search_quality table, sends each to Prometheus
with a rubric-based prompt, parses the 1-5 grade, and writes it back.

Usage:
    python3 prometheus_grade.py [--limit N] [--dry-run] [--db PATH]

Requires LM Studio running at http://localhost:1234 with prometheus-7b-v2.0 loaded.
See docs/plans/2026-08-11-search-quality-metric-plan.md (Phase 2: LLM judge).
"""
from __future__ import annotations

import argparse
import json
import sqlite3
import sys
import urllib.request
from pathlib import Path

LM_STUDIO_URL = "http://localhost:1234/v1/chat/completions"
MODEL = "prometheus-7b-v2.0"

RUBRIC = """You are grading the usefulness of memory search results for an AI agent.

The agent searched its memory bank with a query and received the top results.
Grade how useful these results are for answering the agent's information need.

[Score 1]: Results are irrelevant or noise
[Score 2]: Results are weakly relevant; the agent would need to search again
[Score 3]: Results are somewhat relevant but incomplete or partially off-topic
[Score 4]: Results are highly relevant and mostly answer the query
[Score 5]: Results directly answer the query (decisive hit)"""

PROMPT_TEMPLATE = """###Task Description
An AI agent performed a memory search. Grade the usefulness of the search results.

###Query
{query}

###Search Results
{results}

###Score Rubric
{rubric}

###Result
Score:"""


def _find_db(custom_path: str | None = None) -> Path:
    if custom_path:
        p = Path(custom_path)
        if p.exists():
            return p
        print(f"prometheus_grade: db not found at {p}", file=sys.stderr)
        sys.exit(1)
    default = Path.home() / ".ai-raccoon" / "memory.db"
    if default.exists():
        return default
    print("prometheus_grade: memory.db not found", file=sys.stderr)
    sys.exit(1)


def _get_ungraded(db_path: Path, limit: int) -> list[dict]:
    conn = sqlite3.connect(str(db_path))
    conn.row_factory = sqlite3.Row
    try:
        rows = conn.execute(
            """
            SELECT correlation_id, query, top_source_files, result_count
            FROM search_quality
            WHERE usefulness_grade IS NULL
            ORDER BY created_at DESC
            LIMIT ?
            """,
            (limit,),
        ).fetchall()
        return [dict(r) for r in rows]
    finally:
        conn.close()


def _call_prometheus(query: str, results_summary: str, result_count: int) -> tuple[int, str] | None:
    """Call Prometheus and return (grade, explanation) or None."""
    prompt = PROMPT_TEMPLATE.format(
        rubric=RUBRIC,
        query=query,
        results=results_summary,
    )
    payload = json.dumps({
        "model": MODEL,
        "messages": [{"role": "user", "content": prompt}],
        "temperature": 0.0,
        "max_tokens": 128,
    }).encode()

    req = urllib.request.Request(
        LM_STUDIO_URL,
        data=payload,
        headers={"Content-Type": "application/json"},
    )
    try:
        with urllib.request.urlopen(req, timeout=120) as resp:
            data = json.loads(resp.read())
    except Exception as exc:
        print(f"prometheus_grade: API error: {exc}", file=sys.stderr)
        return None

    try:
        content = data["choices"][0]["message"]["content"].strip()
    except (KeyError, IndexError):
        return None

    # Extract the first digit from the response
    grade = None
    for ch in content:
        if ch in "12345":
            grade = int(ch)
            break
    if grade is None:
        return None
    return grade, content


def _write_grade(db_path: Path, correlation_id: str, grade: int, explanation: str | None = None) -> bool:
    conn = sqlite3.connect(str(db_path))
    try:
        cur = conn.execute(
            "UPDATE search_quality SET usefulness_grade = ?, grade_note = ? WHERE correlation_id = ? AND usefulness_grade IS NULL",
            (grade, explanation, correlation_id),
        )
        conn.commit()
        return cur.rowcount > 0
    finally:
        conn.close()


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Auto-grade memory_search results with Prometheus")
    parser.add_argument("--limit", type=int, default=10, help="Max rows to grade per run")
    parser.add_argument("--dry-run", action="store_true", help="Print prompts without grading")
    parser.add_argument("--db", type=str, default=None, help="Path to memory.db")
    args = parser.parse_args(argv)

    db_path = _find_db(args.db)
    ungraded = _get_ungraded(db_path, args.limit)

    if not ungraded:
        print("prometheus_grade: no ungraded rows")
        return 0

    print(f"prometheus_grade: {len(ungraded)} ungraded rows")

    graded = 0
    for row in ungraded:
        cid = row["correlation_id"]
        query = row["query"] or ""
        top_files = row["top_source_files"] or "[]"
        result_count = row["result_count"] or 0

        # Build a summary of results from the stored JSON
        try:
            files = json.loads(top_files) if isinstance(top_files, str) else top_files
        except (ValueError, TypeError):
            files = []
        results_summary = "\n".join(f"- {f}" for f in files) if files else "(no source files recorded)"

        if args.dry_run:
            print(f"\n--- {cid} ---")
            print(f"Query: {query}")
            print(f"Results:\n{results_summary}")
            continue

        result = _call_prometheus(query, results_summary, result_count)
        if result is None:
            print(f"  {cid}: could not parse grade", file=sys.stderr)
            continue

        grade, explanation = result
        ok = _write_grade(db_path, cid, grade, explanation)
        if ok:
            graded += 1
            print(f"  {cid}: grade={grade} note={explanation[:80]}...")
        else:
            print(f"  {cid}: row already graded or missing", file=sys.stderr)

    print(f"prometheus_grade: graded {graded}/{len(ungraded)}")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        sys.exit(130)
