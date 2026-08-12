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
import re
import sqlite3
import sys
import urllib.request
from pathlib import Path

LM_STUDIO_URL = "http://localhost:1234/v1/chat/completions"
MODEL = "m-prometheus-14b"

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
You MUST respond with ONLY a single digit (1-5) on the first line, followed by a brief explanation.

###Query
{query}

###Search Results
{results}

###Score Rubric
{rubric}

###Result
Grade (1-5):"""


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


def _read_snippet_from_db(db_path: Path, source_file: str, max_chars: int = 300) -> str:
    """Read a snippet from memory.db entries table for a given source_file."""
    try:
        conn = sqlite3.connect(str(db_path))
        conn.row_factory = sqlite3.Row
        try:
            row = conn.execute(
                "SELECT value FROM entries WHERE source_file = ? LIMIT 1",
                (source_file,),
            ).fetchone()
            if row and row["value"]:
                text = row["value"][:max_chars]
                return text.strip() + ("..." if len(row["value"]) >= max_chars else "")
            return f"(no entry found for: {source_file})"
        finally:
            conn.close()
    except Exception:
        return f"(db read failed for: {source_file})"


def _read_snippet(file_path: str, db_path: Path | None = None, max_chars: int = 300) -> str:
    """Read a short snippet — from disk for files, from DB for transcripts."""
    try:
        p = Path(file_path).expanduser()
        if p.exists():
            text = p.read_text(encoding="utf-8", errors="replace")[:max_chars]
            return text.strip() + ("..." if len(text) >= max_chars else "")
    except Exception:
        pass
    # Fall back to memory.db for transcripts and non-file entries
    if db_path and file_path:
        return _read_snippet_from_db(db_path, file_path, max_chars)
    return f"(file not found: {file_path})"


def _build_results_summary(top_files: list[str], db_path: Path | None = None) -> str:
    """Build results summary with content snippets for each file."""
    if not top_files:
        return "(no source files recorded)"
    parts = []
    for f in top_files[:5]:  # Limit to 5 files to stay within context
        snippet = _read_snippet(f, db_path)
        parts.append(f"- [{f}]: {snippet}")
    return "\n".join(parts)


def _parse_grade(content: str) -> int | None:
    """Extract the 1-5 grade from a Prometheus response.

    The model states the grade on the first line, or in a trailing
    "grade/score is N" sentence when the response runs longer.
    """
    first_line = content.split("\n")[0].strip()
    for ch in first_line:
        if ch in "12345":
            return int(ch)
    matches = re.findall(r"(?:grade|score)[^\d]{0,25}([1-5])", content)
    if matches:
        return int(matches[-1])
    return None


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
        "max_tokens": 512,
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

    grade = _parse_grade(content)
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

        # Build a summary of results with content snippets
        try:
            files = json.loads(top_files) if isinstance(top_files, str) else top_files
        except (ValueError, TypeError):
            files = []
        results_summary = _build_results_summary(files, db_path)

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
