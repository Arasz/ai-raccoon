"""P3 AC2 gate: behavior constants live in `data/**/*.json`, not in logic.

The forbidden-literal list is DERIVED from the JSON keys: walk every JSON file
under `data/`, flatten it to (key, value) leaves, and assert no logic file
assigns that literal to that key. A hand list rots the moment a new constant
lands in JSON; this one grows with the JSON by construction.

Scope of "logic file": every `.py` under scripts/retrieval_tuning and
scripts/src/retrieval_tuning, plus the refresh wrapper. Tests are not scanned
(they exist to pin values), nor are JSON files themselves. The gate's
fail-capability is proven on a synthetic module text in
`test_matcher_flags_a_synthetic_hardcode`.
"""

from __future__ import annotations

import ast
import json
import re
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
DATA = REPO / "data"


def _logic_files() -> list[Path]:
    roots = [REPO / "scripts" / "retrieval_tuning",
             REPO / "scripts" / "src" / "retrieval_tuning"]
    files = [p for root in roots for p in root.rglob("*.py")]
    files.append(REPO / "scripts" / "refresh-retrieval-corpora.py")
    return sorted(p for p in files if p.exists())


def _data_files() -> list[Path]:
    return sorted(p for p in DATA.rglob("*.json"))


def _leaves(node, key: str | None):
    """(key, value) for every scalar; list/dict containers are descended too.

    A container itself is yielded once (the "no literal collection assigned"
    check), then its items are walked under the same key so each frame string
    or grader field is pinned individually."""
    if isinstance(node, dict):
        if key is not None:
            yield key, node
        for child_key, child in node.items():
            yield from _leaves(child, child_key)
    elif isinstance(node, list):
        if key is not None:
            yield key, node
        for item in node:
            yield from _leaves(item, key)
    else:
        if key is not None:
            yield key, node


def _code_only(text: str) -> str:
    """Module source with docstrings blanked; a constant named in prose is not code."""
    tree = ast.parse(text)
    chars = list(text)

    def offset(lineno: int, col: int) -> int:
        return sum(len(line) + 1 for line in text.splitlines()[:lineno - 1]) + col

    for node in ast.walk(tree):
        if isinstance(node, (ast.Module, ast.ClassDef, ast.FunctionDef,
                             ast.AsyncFunctionDef)):
            body = node.body
            if body and isinstance(body[0], ast.Expr) \
                    and isinstance(body[0].value, ast.Constant) \
                    and isinstance(body[0].value.value, str):
                doc = body[0].value
                for i in range(offset(doc.lineno, doc.col_offset),
                               offset(doc.end_lineno, doc.end_col_offset)):
                    if chars[i] != "\n":
                        chars[i] = " "
    return "".join(chars)


def _value_patterns(value) -> list[str]:
    """Python-literal spellings of one JSON value (both quote styles for strings)."""
    if isinstance(value, bool):
        return ["True" if value else "False"]
    if value is None:
        return ["None"]
    if isinstance(value, (int, float)):
        return [re.escape(repr(value))]
    if isinstance(value, str):
        return [re.escape(repr(value)), re.escape(json.dumps(value))]
    if isinstance(value, list):
        return [r"\[", r"\("]  # a literal list/tuple assignment
    if isinstance(value, dict):
        return [r"\{"]
    return []


def hardcode_hits(key: str, value, text: str) -> list[str]:
    """Lines in `text` that hardcode `value` for `key` (pure; gate + RED proof).

    Matches `KEY = <literal>`, `KEY: <literal>`, and the dict forms
    `"KEY": <literal>` / `'KEY': <literal>` at line start, case-insensitively,
    so `SEED = 42` and `"rrfK": 60` are both caught."""
    key_alt = rf"(?:\"{re.escape(key)}\"|'{re.escape(key)}'|{re.escape(key)})"
    hits: list[str] = []
    for literal in _value_patterns(value):
        rx = re.compile(rf"(?im)^[ \t]*{key_alt}[ \t]*[:=][ \t]*{literal}")
        for match in rx.finditer(text):
            hits.append(match.group(0).strip())
    return hits


def test_matcher_flags_a_synthetic_hardcode():
    # Fail-capability: the matcher must flag the shapes a hand rewrite would use.
    assert hardcode_hits("EVAL_LIMIT", 8, "EVAL_LIMIT = 8\n")
    assert hardcode_hits("EVAL_LIMIT", 8, "    EVAL_LIMIT = 8  # comment\n")
    assert hardcode_hits("rrfK", 60, '        "rrfK": 60,\n')
    assert hardcode_hits("SEED", 42, "SEED = 42\n")
    # ... and must NOT flag the derived forms the refactor left behind.
    assert hardcode_hits("EVAL_LIMIT", 8, "EVAL_LIMIT = knobs[\"EVAL_LIMIT\"]\n") == []
    assert hardcode_hits("SEED", 42, "SEED = data[\"SEED\"]\n") == []
    assert hardcode_hits("PARAMS", {"rrfK": 60}, "PARAMS = dict(knobs[\"PARAMS\"])\n") == []
    assert hardcode_hits("FRAMES", ["a", "b"], "FRAMES = tuple(gen[\"FRAMES\"])\n") == []


def test_no_json_constant_is_hardcoded_in_a_logic_file():
    logic = {path: _code_only(path.read_text()) for path in _logic_files()}
    problems: list[str] = []
    for data_file in _data_files():
        payload = json.loads(data_file.read_text())
        for key, value in _leaves(payload, None):
            for path, text in logic.items():
                for hit in hardcode_hits(key, value, text):
                    problems.append(
                        f"{path.relative_to(REPO)}:{key} hardcodes {data_file.name}'s "
                        f"value ({hit})")
    assert problems == [], "behavior constants must come from data/**/*.json:\n" + \
        "\n".join(problems)


def test_every_json_top_level_key_is_referenced_by_a_logic_file():
    # A JSON file nobody reads is decoration, not a source of truth. Top-level
    # keys only: a container consumed wholesale (PARAMS, KNOB_DEFAULTS, TRIO)
    # proves its whole payload is read; the per-leaf hardcode scan above keeps
    # the individual leaves honest.
    logic_text = "\n".join(_code_only(path.read_text()) for path in _logic_files())
    missing: list[str] = []
    for data_file in _data_files():
        payload = json.loads(data_file.read_text())
        if not isinstance(payload, dict):
            continue
        for key in payload:
            if not re.search(rf"\b{re.escape(key)}\b", logic_text):
                missing.append(f"{data_file.name}: {key}")
    assert missing == [], f"JSON keys no logic file references: {missing}"


def test_settings_verbs_cover_exactly_the_defaults():
    from retrieval_tuning import repo_data

    assert set(repo_data.KNOBS["KNOB_VERBS"]) == set(repo_data.KNOBS["KNOB_DEFAULTS"])
    assert set(repo_data.KNOBS["PARAMS"]) >= {"rrfK", "ftsWeight", "vectorWeight"}
