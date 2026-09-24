"""Chunker ports and scoring helpers of the chunk-size vs attention-window eval.

The ports must keep the product chunkers' contracts (docs/adr/0036, 0048): no chunk over
budget, markdown carries a bounded tail overlay, a cut section's heading moves to the chunk
that holds its content, and code chunks tile the file's lines with no overlap.
"""

from __future__ import annotations

from retrieval_tuning.chunk_ports import (
    chunk_code,
    chunk_markdown,
    distinct_in_order,
    slug,
)


def words(text: str) -> int:
    return len(text.split())


def test_slug_matches_eval_set_anchor_shape() -> None:
    assert slug("Alternatives rejected") == "alternatives-rejected"
    assert slug("Decision 2 — the kill switch") == "decision-2-the-kill-switch"


def test_markdown_chunks_never_exceed_budget() -> None:
    text = "\n".join(f"line {i} has five words" for i in range(200)) + "\n"

    chunks = chunk_markdown(text, 20, words)

    assert len(chunks) > 1
    assert all(words(c.text) <= 20 for c in chunks)


def test_markdown_chunk_carries_tail_overlay_of_previous() -> None:
    text = "".join(f"w{i} a b c d\n" for i in range(40))

    chunks = chunk_markdown(text, 20, words, overlay_tokens=10)

    previous_tail = chunks[0].text.splitlines()[-2:]
    assert chunks[1].text.splitlines()[:2] == previous_tail


def test_markdown_records_sections_the_chunk_holds() -> None:
    text = "# T\n\n## Context\n\nalpha beta\n\n## Decision\n\ngamma delta\n"

    chunks = chunk_markdown(text, 100, words)

    assert chunks[0].sections == ("context", "decision")


def test_markdown_defers_heading_of_a_cut_section() -> None:
    body = "".join(f"x{i} y z\n" for i in range(6))
    text = "## Context\n" + body + "## Decision\n" + body

    chunks = chunk_markdown(text, 24, words, overlay_tokens=0)

    assert chunks[0].sections == ("context",)
    assert "## Decision" not in chunks[0].text
    assert chunks[1].text.startswith("## Decision")


def test_code_chunks_tile_lines_without_overlap() -> None:
    text = "".join(f"stmt {i} a b;\n" + ("\n" if i % 3 == 2 else "") for i in range(60))

    chunks = chunk_code(text, 25, words)

    assert all(words(c.text) <= 25 for c in chunks)
    assert chunks[0].line_start == 1
    assert chunks[-1].line_end == len(text.splitlines())
    assert all(b.line_start == a.line_end + 1 for a, b in zip(chunks, chunks[1:]))


def test_code_prefers_a_brace_balanced_boundary() -> None:
    # Greedy packing reaches the open "g {" block (4 + 3 <= 8); the balanced boundary is "}".
    text = "f {\n a\n}\n\ng {\n b\n\nc\n}\n"

    chunks = chunk_code(text, 8, words)

    assert chunks[0].text == "f {\n a\n}\n\n"


def test_distinct_in_order_keeps_first_occurrence() -> None:
    assert distinct_in_order(["a", "b", "a", "c", "b"]) == ["a", "b", "c"]
