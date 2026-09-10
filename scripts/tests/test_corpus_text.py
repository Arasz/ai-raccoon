"""B corpus-quality gates (T1–T7): the markup-aware topic repair module.

`retrieval_tuning.corpus_text.repair_topic` turns a raw chunk value into a
markup-free prose topic for the project-corpus generator. T1–T5 pin exact
expected strings on fixtures (never predicate-negativity only); T6 keeps the
module stdlib-only so the CI lane stays dependency-free; T7 keeps the repair
independent of the report's debris predicate (no shared code, no
self-certifying metric).
"""

import ast
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from retrieval_tuning import corpus_text  # noqa: E402

MODULE_PATH = Path(__file__).resolve().parents[1] / "src" / "retrieval_tuning" / "corpus_text.py"


def test_module_carries_the_pinned_constants():
    assert corpus_text.TOPIC_MAX_CHARS == 100
    assert corpus_text.TOPIC_DERIVATION == "markup-aware-v1"


def test_repairs_json_key_clause_into_prose():
    value = (
        '{\n"name": "retrieval",\n"intent": "Close the loop on feedback"\n}\n'
        "Close the loop on feedback. The handler retries the write twice."
    )
    topic = corpus_text.repair_topic(value)
    assert topic == "The handler retries the write twice"
    assert not any(ch in topic for ch in '{}"')


def test_repairs_markdown_table_row():
    value = "| cache | Stores project memory for agents |\n| --- | --- |\n"
    topic = corpus_text.repair_topic(value)
    assert topic == "Stores project memory for agents"
    assert "|" not in topic


def test_repairs_fenced_listing_and_unwraps_links():
    value = (
        '```json\n{"key": "value"}\n```\n'
        "The memory guide explains project scoping. "
        "It covers [shared rows](https://example.com/shared)."
    )
    topic = corpus_text.repair_topic(value)
    assert topic == "It covers shared rows"
    assert "```" not in topic
    assert "https://" not in topic
    assert "{" not in topic


def test_strips_scoped_package_at_sign():
    value = (
        "The [@scope/pkg](https://example.com/pkg) adapter handles retries. "
        "It backs off exponentially."
    )
    topic = corpus_text.repair_topic(value)
    assert topic == "The scope/pkg adapter handles retries"
    assert "@" not in topic


def test_topic_is_never_empty_and_capped():
    assert corpus_text.repair_topic('{"a": 1}\n', fallback="my fallback topic") == (
        "my fallback topic"
    )
    assert corpus_text.repair_topic("```\n~~~\n") == "this note"
    assert corpus_text.repair_topic("") == "this note"
    long_value = (
        "The retrieval subsystem records every project observation with full "
        "provenance metadata and bucket counts for later analysis work. Short tail."
    )
    topic = corpus_text.repair_topic(long_value)
    assert topic == (
        "The retrieval subsystem records every project observation with full "
        "provenance metadata and bucket"
    )
    assert len(topic) <= corpus_text.TOPIC_MAX_CHARS


def test_module_imports_are_stdlib_only():
    tree = ast.parse(MODULE_PATH.read_text(encoding="utf-8"))
    roots = set()
    for node in ast.walk(tree):
        if isinstance(node, ast.Import):
            roots.update(alias.name.split(".")[0] for alias in node.names)
        elif isinstance(node, ast.ImportFrom):
            if node.module:
                roots.add(node.module.split(".")[0])
    stdlib = set(sys.stdlib_module_names)
    assert roots <= stdlib, f"non-stdlib imports in corpus_text: {sorted(roots - stdlib)}"


def test_does_not_reference_debris_signature():
    source = MODULE_PATH.read_text(encoding="utf-8")
    for symbol in ("debris_query", "DEBRIS_SIGNATURE"):
        assert symbol not in source, (
            f"corpus_text must not reference {symbol}: the repair stays "
            "independent of the report's measurement predicate"
        )
