"""Tests for the E2 query-authoring support in retrieval_tuning.code_corpus
(docs/work/2026-09-23-code-retrieval-eval-plan.md sec E2) and for
scripts/retrieval_tuning/assemble_code_queries.py.

RED-first, phased leaf-to-root:
  1. split_identifier / comment_lines — pure, no dependencies.
  2. validate_queries — depends on both, exercised against a small but
     internally-consistent manifest+entries fixture built on tmp_path (same
     shape as test_code_corpus_manifest.py's build_valid_corpus).
  3. assign_split — independent, tiny seed dicts.
  4. assemble_code_queries.py's CLI, loaded like compare_code_eval.py (no
     package __init__ under scripts/retrieval_tuning/).
  5. compare_code_eval's rule 3 (per-language floor) proven able to fire for
     C# specifically, per the plan's "Rule 3 is proven able to go red for
     C#" requirement.
"""

from __future__ import annotations

import importlib.util
import json
import sys
from pathlib import Path

import pytest

from retrieval_tuning.code_corpus import (
    CODE_CORPUS_DIR,
    SELF_FAMILY,
    assign_split,
    comment_lines,
    split_identifier,
    validate_queries,
)

REPO_ROOT = Path(__file__).resolve().parents[2]


# ---------------------------------------------------------------------------
# split_identifier
# ---------------------------------------------------------------------------

class TestSplitIdentifier:
    def test_pascal_case_splits_on_word_boundaries(self):
        assert split_identifier("WatchOverlapResolver") == ["watch", "overlap", "resolver"]

    def test_acronym_aware_camel_case_with_trailing_digit(self):
        assert split_identifier("HTTPServer2") == ["http", "server", "2"]

    def test_acronym_in_the_middle_of_a_camel_case_name(self):
        assert split_identifier("parseHTTPRequest") == ["parse", "http", "request"]

    def test_snake_case(self):
        assert split_identifier("watch_overlap_resolver") == ["watch", "overlap", "resolver"]

    def test_kebab_case(self):
        assert split_identifier("watch-overlap-resolver") == ["watch", "overlap", "resolver"]

    def test_dotted_path(self):
        assert split_identifier("Watch.OverlapResolver") == ["watch", "overlap", "resolver"]

    def test_digit_then_letter_boundary(self):
        assert split_identifier("2Fast") == ["2", "fast"]

    def test_property_intersection_underscore_acronym_and_digit_together(self):
        # underscore boundary + acronym-aware camel case + digit boundary, all at once.
        assert split_identifier("parse_HTTPRequest2Body") == [
            "parse", "http", "request", "2", "body",
        ]

    def test_output_is_always_lowercase(self):
        assert all(tok == tok.lower() for tok in split_identifier("XMLHttpRequest"))

    def test_empty_name_returns_empty_list(self):
        assert split_identifier("") == []

    def test_single_lowercase_word_is_unsplit(self):
        assert split_identifier("handle") == ["handle"]


# ---------------------------------------------------------------------------
# comment_lines
# ---------------------------------------------------------------------------

class TestCommentLines:
    def test_python_hash_comments_and_docstring(self):
        text = (
            '"""Module summary line."""\n'
            "\n"
            "def f():\n"
            "    # inline comment\n"
            "    return 1\n"
        )
        lines = comment_lines(text, "Python")
        assert '"""Module summary line."""\n'.strip() in [l.strip() for l in lines]
        assert "# inline comment" in [l.strip() for l in lines]
        assert not any("return 1" in l for l in lines)

    def test_python_multiline_triple_quoted_docstring(self):
        text = (
            '"""\n'
            "Spans several lines.\n"
            "Second line of the docstring.\n"
            '"""\n'
            "x = 1\n"
        )
        lines = comment_lines(text, "Python")
        joined = "\n".join(lines)
        assert "Spans several lines." in joined
        assert "Second line of the docstring." in joined
        assert not any("x = 1" in l for l in lines)

    def test_c_style_line_and_doc_comments(self):
        text = (
            "// plain comment\n"
            "/// doc comment\n"
            "int x = 1;\n"
        )
        lines = [l.strip() for l in comment_lines(text, "C#")]
        assert "// plain comment" in lines
        assert "/// doc comment" in lines
        assert not any("int x = 1;" in l for l in lines)

    def test_c_style_multiline_block_comment(self):
        text = (
            "/* start\n"
            "   still inside\n"
            "   end */\n"
            "int y = 2;\n"
        )
        lines = comment_lines(text, "C++")
        assert len(lines) == 3
        assert not any("int y = 2;" in l for l in lines)

    def test_sql_double_dash_comment(self):
        text = (
            "-- explain the query below\n"
            "SELECT 1;\n"
        )
        lines = [l.strip() for l in comment_lines(text, "SQL")]
        assert "-- explain the query below" in lines
        assert not any("SELECT 1;" in l for l in lines)

    def test_html_comment_spanning_lines(self):
        text = (
            "<div>\n"
            "<!-- start of the notice\n"
            "     still the notice -->\n"
            "<p>hi</p>\n"
        )
        lines = comment_lines(text, "HTML")
        assert len(lines) == 2
        assert not any("<p>hi</p>" in l for l in lines)

    def test_hash_is_not_treated_as_a_comment_outside_python(self):
        # A CSS id selector must not be mistaken for a Python-style comment.
        text = "#navbar {\n  color: red;\n}\n"
        assert comment_lines(text, "CSS") == []


# ---------------------------------------------------------------------------
# validate_queries fixture
# ---------------------------------------------------------------------------

WATCH_PY = '''"""Resolve overlapping watch windows."""


class WatchOverlapResolver:
    """Finds where two watch windows overlap."""

    def resolve(self, a, b):
        # find the shared window between the two ranges
        start = max(a.start, b.start)
        end = min(a.end, b.end)
        return (start, end) if start < end else None
'''

BIG_PY = '''"""Coordinates deferred cleanup tasks."""


class DeferredCleanupQueue:
    """Runs cleanup callbacks once their deadline passes."""

    def __init__(self):
        self._items = []

    def schedule(self, deadline, callback):
        # keep the queue ordered by deadline so drain() can pop the earliest first
        self._items.append((deadline, callback))
        self._items.sort(key=lambda pair: pair[0])

    def drain(self, now):
        ready = [item for item in self._items if item[0] <= now]
        self._items = [item for item in self._items if item[0] > now]
        for deadline, callback in ready:
            callback()
'''

TEST_WATCH_PY = '''"""Tests for WatchOverlapResolver."""


def test_resolve_returns_none_when_disjoint():
    resolver = WatchOverlapResolver()
    assert resolver.resolve(Range(0, 1), Range(2, 3)) is None
'''

TEST_BIG_PY = '''"""Tests for DeferredCleanupQueue."""


def test_drain_runs_only_ready_callbacks():
    queue = DeferredCleanupQueue()
    queue.schedule(1, lambda: None)
    queue.drain(2)
'''

BETA_PY = '''"""Formats duration strings for display."""


def format_duration(seconds):
    # convert raw seconds into a compact human readable string
    minutes, secs = divmod(int(seconds), 60)
    return f"{minutes}m{secs}s"
'''

GAMMA_PY = '''"""Normalizes whitespace in log lines."""


def collapse_whitespace(line):
    # squeeze consecutive spaces and tabs down to a single space
    return " ".join(line.split())
'''

DELTA_PY = '''"""Builds a simple retry schedule."""


def next_delay(attempt):
    # double the wait time for every additional attempt, capped at a minute
    return min(2 ** attempt, 60)
'''

HELD_OUT_FAMILIES = ["alpha", "beta", "gamma"]


def _write(root: Path, vendored_path: str, text: str) -> None:
    path = root / vendored_path
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8")


def _row(family, path, language, *, size_band=None, role="src", pair_of=None):
    row = {
        "family": family,
        "path": path,
        "vendoredPath": f"{CODE_CORPUS_DIR}/files/{family}/{path}",
        "language": language,
        "sizeBand": size_band,
        "role": role,
    }
    if pair_of is not None:
        row["pairOf"] = pair_of
    return row


def _entry(entry_id, category, query, *, family=None, source=None, lines=None,
           language=None, size_band=None, negative=False):
    return {
        "id": entry_id,
        "category": category,
        "query": query,
        "kind": "code",
        "language": language,
        "sizeBand": size_band,
        "family": family,
        "expectedSource": source,
        "expectedLines": lines,
        "negativeTest": negative,
        "searchLimit": 10,
        "targetProjectId": "code-eval",
        "targetScope": "project",
        "relevanceGrade": 3,
        "expectedHash": None,
        "difficulty": "medium",
    }


def _negatives(n: int = 20) -> list[dict]:
    return [
        _entry(f"neg-{i:03d}", "negative",
               f"functionality that does not exist anywhere in the corpus #{i}",
               negative=True)
        for i in range(n)
    ]


def build_valid_fixture(tmp_path: Path) -> tuple[list[dict], dict, Path]:
    """alpha/beta/gamma held out (alpha has 2 src/test pairs), delta tuning."""
    root = tmp_path
    _write(root, f"{CODE_CORPUS_DIR}/files/alpha/src/watch.py", WATCH_PY)
    _write(root, f"{CODE_CORPUS_DIR}/files/alpha/src/big.py", BIG_PY)
    _write(root, f"{CODE_CORPUS_DIR}/files/alpha/tests/test_watch.py", TEST_WATCH_PY)
    _write(root, f"{CODE_CORPUS_DIR}/files/alpha/tests/test_big.py", TEST_BIG_PY)
    _write(root, f"{CODE_CORPUS_DIR}/files/beta/src/thing.py", BETA_PY)
    _write(root, f"{CODE_CORPUS_DIR}/files/gamma/src/thing.py", GAMMA_PY)
    _write(root, f"{CODE_CORPUS_DIR}/files/delta/src/thing.py", DELTA_PY)

    files = [
        _row("alpha", "src/watch.py", "Python", size_band="small"),
        _row("alpha", "src/big.py", "Python", size_band="large"),
        _row("alpha", "tests/test_watch.py", "Python", role="test", pair_of="src/watch.py"),
        _row("alpha", "tests/test_big.py", "Python", role="test", pair_of="src/big.py"),
        _row("beta", "src/thing.py", "Python", size_band="small"),
        _row("gamma", "src/thing.py", "Python", size_band="small"),
        _row("delta", "src/thing.py", "Python", size_band="small"),
    ]
    manifest = {"languages": ["Python"], "heldOutFamilies": HELD_OUT_FAMILIES, "files": files}

    entries = [
        _entry("alpha-001", "behaviour-nl",
               "where the overlap between two active timers gets computed",
               family="alpha", source="alpha/src/watch.py", lines=[7, 11],
               language="Python", size_band="small"),
        _entry("alpha-002", "identifier-fragment", "overlap resolver",
               family="alpha", source="alpha/src/watch.py", lines=[4, 11],
               language="Python", size_band="small"),
        _entry("alpha-003", "behaviour-nl",
               "how tasks that are already due get run and removed from the pending list",
               family="alpha", source="alpha/src/big.py", lines=[15, 19],
               language="Python", size_band="large"),
        _entry("alpha-004", "identifier-fragment", "cleanup queue",
               family="alpha", source="alpha/src/big.py", lines=[4, 19],
               language="Python", size_band="large"),
        _entry("alpha-005", "path-context",
               "the maintenance pass in the deferred queue that fires overdue callbacks",
               family="alpha", source="alpha/src/big.py", lines=[15, 19],
               language="Python", size_band="large"),
        _entry("alpha-006", "distractor",
               "checks that non overlapping windows return no result",
               family="alpha", source="alpha/src/watch.py", lines=[7, 11],
               language="Python", size_band="small"),
        _entry("alpha-007", "distractor",
               "verifying the resolver behavior for two disjoint ranges",
               family="alpha", source="alpha/src/watch.py", lines=[7, 11],
               language="Python", size_band="small"),
        _entry("alpha-008", "test-intent",
               "where is the overlap resolver's disjoint range behavior verified",
               family="alpha", source="alpha/tests/test_watch.py", lines=[4, 5],
               language="Python", size_band=None),
        _entry("alpha-009", "distractor",
               "confirms only callbacks whose deadline has arrived actually fire",
               family="alpha", source="alpha/src/big.py", lines=[15, 19],
               language="Python", size_band="large"),
        _entry("alpha-010", "distractor",
               "exercise scheduling one item and draining it once due",
               family="alpha", source="alpha/src/big.py", lines=[15, 19],
               language="Python", size_band="large"),
        _entry("alpha-011", "test-intent",
               "where the deferred cleanup queue's drain behavior is verified",
               family="alpha", source="alpha/tests/test_big.py", lines=[4, 6],
               language="Python", size_band=None),
        _entry("beta-001", "behaviour-nl",
               "turning a raw count of elapsed seconds into a short mm ss label",
               family="beta", source="beta/src/thing.py", lines=[4, 6],
               language="Python", size_band="small"),
        _entry("beta-002", "identifier-fragment", "duration format",
               family="beta", source="beta/src/thing.py", lines=[4, 6],
               language="Python", size_band="small"),
        _entry("gamma-001", "behaviour-nl",
               "squishing repeated blank characters in a line down to one space",
               family="gamma", source="gamma/src/thing.py", lines=[4, 6],
               language="Python", size_band="small"),
        _entry("gamma-002", "identifier-fragment", "whitespace collapse",
               family="gamma", source="gamma/src/thing.py", lines=[4, 6],
               language="Python", size_band="small"),
        _entry("delta-001", "behaviour-nl",
               "how long to wait before trying again after a failed attempt, doubling each time",
               family="delta", source="delta/src/thing.py", lines=[4, 6],
               language="Python", size_band="small"),
        _entry("delta-002", "identifier-fragment", "delay next",
               family="delta", source="delta/src/thing.py", lines=[4, 6],
               language="Python", size_band="small"),
    ] + _negatives(20)

    return entries, manifest, root


class TestValidateQueriesBaseline:
    def test_valid_fixture_has_no_problems(self, tmp_path):
        entries, manifest, root = build_valid_fixture(tmp_path)
        assert validate_queries(entries, manifest, root) == []


class TestRequiredFieldsAndEnums:
    def test_missing_id_is_reported(self, tmp_path):
        entries, manifest, root = build_valid_fixture(tmp_path)
        del entries[0]["id"]
        problems = validate_queries(entries, manifest, root)
        assert any("id" in p for p in problems)

    def test_invalid_category_is_rejected(self, tmp_path):
        entries, manifest, root = build_valid_fixture(tmp_path)
        entries[0]["category"] = "vibes"
        problems = validate_queries(entries, manifest, root)
        assert any("alpha-001" in p and "category" in p for p in problems)

    def test_duplicate_id_is_rejected(self, tmp_path):
        entries, manifest, root = build_valid_fixture(tmp_path)
        entries[1]["id"] = entries[0]["id"]
        problems = validate_queries(entries, manifest, root)
        assert any("duplicate" in p.lower() for p in problems)

    def test_kind_must_be_code(self, tmp_path):
        entries, manifest, root = build_valid_fixture(tmp_path)
        entries[0]["kind"] = "memory"
        problems = validate_queries(entries, manifest, root)
        assert any("kind" in p for p in problems)

    def test_negative_test_must_be_a_bool(self, tmp_path):
        entries, manifest, root = build_valid_fixture(tmp_path)
        entries[0]["negativeTest"] = "false"
        problems = validate_queries(entries, manifest, root)
        assert any("negativeTest" in p for p in problems)


class TestExpectedSourceResolution:
    def test_expected_source_with_no_manifest_row_is_rejected(self, tmp_path):
        entries, manifest, root = build_valid_fixture(tmp_path)
        entries[0]["expectedSource"] = "alpha/src/does-not-exist.py"
        problems = validate_queries(entries, manifest, root)
        assert any("does-not-exist.py" in p for p in problems)

    def test_expected_source_resolving_twice_is_rejected(self, tmp_path):
        entries, manifest, root = build_valid_fixture(tmp_path)
        manifest["files"].append(dict(manifest["files"][0]))  # duplicate row
        problems = validate_queries(entries, manifest, root)
        assert any("watch.py" in p and ("2" in p or "exactly 1" in p) for p in problems)


class TestExpectedLines:
    def test_end_line_past_file_length_is_rejected(self, tmp_path):
        entries, manifest, root = build_valid_fixture(tmp_path)
        entries[0]["expectedLines"] = [7, 999]
        problems = validate_queries(entries, manifest, root)
        assert any("alpha-001" in p for p in problems)

    def test_start_after_end_is_rejected(self, tmp_path):
        entries, manifest, root = build_valid_fixture(tmp_path)
        entries[0]["expectedLines"] = [9, 7]
        problems = validate_queries(entries, manifest, root)
        assert any("alpha-001" in p for p in problems)


class TestNegativeQueryShape:
    def test_negative_with_a_source_is_rejected(self, tmp_path):
        entries, manifest, root = build_valid_fixture(tmp_path)
        entries[-1]["expectedSource"] = "alpha/src/watch.py"
        problems = validate_queries(entries, manifest, root)
        assert any(entries[-1]["id"] in p for p in problems)

    def test_negative_with_negative_test_false_is_rejected(self, tmp_path):
        entries, manifest, root = build_valid_fixture(tmp_path)
        entries[-1]["negativeTest"] = False
        problems = validate_queries(entries, manifest, root)
        assert any(entries[-1]["id"] in p for p in problems)

    def test_non_negative_with_negative_test_true_is_rejected(self, tmp_path):
        entries, manifest, root = build_valid_fixture(tmp_path)
        entries[0]["negativeTest"] = True
        problems = validate_queries(entries, manifest, root)
        assert any("alpha-001" in p for p in problems)

    def test_fewer_than_twenty_negatives_is_rejected(self, tmp_path):
        entries, manifest, root = build_valid_fixture(tmp_path)
        entries = [e for e in entries if e["id"] != "neg-000"]
        problems = validate_queries(entries, manifest, root)
        assert any("negative" in p.lower() and "20" in p for p in problems)


class TestCategoryCountsPerFile:
    def test_a_src_file_missing_identifier_fragment_is_rejected(self, tmp_path):
        entries, manifest, root = build_valid_fixture(tmp_path)
        entries = [e for e in entries if e["id"] != "alpha-002"]
        problems = validate_queries(entries, manifest, root)
        assert any("watch.py" in p and "identifier-fragment" in p for p in problems)

    def test_a_large_file_missing_path_context_is_rejected(self, tmp_path):
        entries, manifest, root = build_valid_fixture(tmp_path)
        entries = [e for e in entries if e["id"] != "alpha-005"]
        problems = validate_queries(entries, manifest, root)
        assert any("big.py" in p and "path-context" in p for p in problems)

    def test_a_pair_with_only_one_distractor_is_rejected(self, tmp_path):
        entries, manifest, root = build_valid_fixture(tmp_path)
        entries = [e for e in entries if e["id"] != "alpha-007"]
        problems = validate_queries(entries, manifest, root)
        assert any("distractor" in p for p in problems)

    def test_a_pair_missing_test_intent_is_rejected(self, tmp_path):
        entries, manifest, root = build_valid_fixture(tmp_path)
        entries = [e for e in entries if e["id"] != "alpha-008"]
        problems = validate_queries(entries, manifest, root)
        assert any("test-intent" in p for p in problems)


class TestLeakGate:
    def test_query_that_restates_a_comment_line_is_rejected(self, tmp_path):
        entries, manifest, root = build_valid_fixture(tmp_path)
        entries[0]["query"] = "the shared window between two ranges"
        problems = validate_queries(entries, manifest, root)
        assert any("leak" in p.lower() for p in problems)

    def test_a_paraphrased_query_is_not_flagged(self, tmp_path):
        entries, manifest, root = build_valid_fixture(tmp_path)
        assert not any("leak" in p.lower() for p in validate_queries(entries, manifest, root))


class TestCircularityGuard:
    def test_identifier_fragment_equal_to_the_full_split_is_rejected(self, tmp_path):
        entries, manifest, root = build_valid_fixture(tmp_path)
        entries[1]["query"] = "watch overlap resolver"  # full split of WatchOverlapResolver
        problems = validate_queries(entries, manifest, root)
        assert any("circular" in p.lower() or "full split" in p.lower() for p in problems)

    def test_a_partial_fragment_is_not_flagged(self, tmp_path):
        entries, manifest, root = build_valid_fixture(tmp_path)
        problems = validate_queries(entries, manifest, root)
        assert not any("circular" in p.lower() for p in problems)

    def test_property_intersection_leaky_and_circular_at_once(self, tmp_path):
        """A single bad query can trip both gates at once; both must be reported.

        A standalone fixture (not build_valid_fixture): its docstring names the
        identifier fragment directly, so the exact ordered split of
        `OverlapResolver` ("overlap resolver") is both circular (equals the
        identifier's full split) AND leaky (>0.4 jaccard with the docstring).
        """
        root = tmp_path
        text = (
            '"""The overlap resolver."""\n'
            "\n"
            "class OverlapResolver:\n"
            "    def resolve(self, a, b):\n"
            "        return min(a, b)\n"
        )
        _write(root, f"{CODE_CORPUS_DIR}/files/zeta/src/thing.py", text)
        manifest = {
            "languages": ["Python"],
            "heldOutFamilies": [],
            "files": [_row("zeta", "src/thing.py", "Python", size_band="small")],
        }
        entry = _entry(
            "zeta-001", "identifier-fragment", "overlap resolver",
            family="zeta", source="zeta/src/thing.py", lines=[3, 5],
            language="Python", size_band="small",
        )

        problems = validate_queries([entry], manifest, root)

        assert any("leak" in p.lower() for p in problems)
        assert any("circular" in p.lower() or "full split" in p.lower() for p in problems)


class TestHeldOutCoverage:
    def test_fewer_than_three_held_out_families_present_is_rejected(self, tmp_path):
        entries, manifest, root = build_valid_fixture(tmp_path)
        entries = [e for e in entries if e.get("family") != "gamma"]
        # gamma still needs its manifest-required queries dropped too, or the
        # per-file category checks above would fire instead of (or alongside)
        # the coverage check; both firing is fine, we only assert coverage does.
        problems = validate_queries(entries, manifest, root)
        assert any("held-out" in p.lower() and ("3" in p or "coverage" in p.lower()) for p in problems)

    def test_a_held_out_family_with_pairs_needs_at_least_two_covered(self, tmp_path):
        entries, manifest, root = build_valid_fixture(tmp_path)
        entries = [e for e in entries if e["id"] != "alpha-011"]  # drop test-intent for pair 2
        problems = validate_queries(entries, manifest, root)
        assert any("alpha" in p and "pair" in p.lower() for p in problems)

    def test_baseline_already_covers_two_pairs_for_the_held_out_family_with_pairs(self, tmp_path):
        entries, manifest, root = build_valid_fixture(tmp_path)
        problems = validate_queries(entries, manifest, root)
        assert not any("pair" in p.lower() and "alpha" in p for p in problems)


# ---------------------------------------------------------------------------
# assign_split
# ---------------------------------------------------------------------------

SEED = {
    "heldOutFamilies": ["nlohmann-json", "zod", "gin", "html5-boilerplate"],
    "selfHeldOut": ["src/AiRaccoon.Infrastructure/Sqlite/MemorySql.cs", "scripts/src/retrieval_tuning/scoring.py"],
}


class TestAssignSplit:
    def test_external_held_out_family_is_held_out(self):
        entry = {"family": "gin", "expectedSource": "gin/routergroup.go"}
        assert assign_split(entry, SEED) == "held-out"

    def test_external_tuning_family_is_tuning(self):
        entry = {"family": "serde", "expectedSource": "serde/src/lib.rs"}
        assert assign_split(entry, SEED) == "tuning"

    def test_self_file_pinned_in_self_held_out_is_held_out(self):
        entry = {"family": SELF_FAMILY, "expectedSource": f"{SELF_FAMILY}/scripts/src/retrieval_tuning/scoring.py"}
        assert assign_split(entry, SEED) == "held-out"

    def test_self_file_not_pinned_is_tuning(self):
        entry = {"family": SELF_FAMILY, "expectedSource": f"{SELF_FAMILY}/scripts/src/retrieval_tuning/corpus.py"}
        assert assign_split(entry, SEED) == "tuning"

    def test_negative_query_with_no_family_defaults_to_tuning(self):
        entry = {"family": None, "expectedSource": None}
        assert assign_split(entry, SEED) == "tuning"


# ---------------------------------------------------------------------------
# assemble_code_queries.py (standalone script, no package __init__, loaded
# the same way test_code_eval_compare.py loads compare_code_eval.py)
# ---------------------------------------------------------------------------

ASSEMBLE_PATH = REPO_ROOT / "scripts" / "retrieval_tuning" / "assemble_code_queries.py"


def _load_assemble():
    spec = importlib.util.spec_from_file_location("assemble_code_queries", ASSEMBLE_PATH)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


assemble = _load_assemble()


def _assemble_fixture(tmp_path: Path):
    entries, manifest, root = build_valid_fixture(tmp_path)
    manifest_path = root / CODE_CORPUS_DIR / "MANIFEST.json"
    manifest_path.parent.mkdir(parents=True, exist_ok=True)
    manifest_path.write_text(json.dumps(manifest))

    queries_dir = root / CODE_CORPUS_DIR / "queries"
    queries_dir.mkdir(parents=True, exist_ok=True)
    by_family: dict[str, list[dict]] = {}
    for entry in entries:
        by_family.setdefault(entry.get("family") or "_negatives", []).append(entry)
    for family, rows in by_family.items():
        (queries_dir / f"{family}.json").write_text(json.dumps(rows))

    seed = {"heldOutFamilies": HELD_OUT_FAMILIES, "selfHeldOut": []}
    return root, manifest_path, queries_dir, seed


class TestAssembleCodeQueries:
    def test_no_query_files_fails_clearly(self, tmp_path):
        root = tmp_path
        manifest_path = root / CODE_CORPUS_DIR / "MANIFEST.json"
        manifest_path.parent.mkdir(parents=True, exist_ok=True)
        manifest_path.write_text(json.dumps({"heldOutFamilies": [], "files": []}))
        queries_dir = root / CODE_CORPUS_DIR / "queries"

        assembled, problems = assemble.run(
            queries_dir=queries_dir, manifest_path=manifest_path, root=root,
            seed={"heldOutFamilies": [], "selfHeldOut": []}, write_to=None,
        )
        assert assembled == []
        assert any("no query files" in p.lower() for p in problems)

    def test_valid_queries_are_assembled_split_tagged_and_sorted(self, tmp_path):
        root, manifest_path, queries_dir, seed = _assemble_fixture(tmp_path)
        output_path = root / CODE_CORPUS_DIR / "queries.json"

        assembled, problems = assemble.run(
            queries_dir=queries_dir, manifest_path=manifest_path, root=root,
            seed=seed, write_to=output_path,
        )

        assert problems == []
        assert [e["id"] for e in assembled] == sorted(e["id"] for e in assembled)
        assert all("split" in e for e in assembled)
        alpha_entry = next(e for e in assembled if e["id"] == "alpha-001")
        assert alpha_entry["split"] == "held-out"
        delta_entry = next(e for e in assembled if e["id"] == "delta-001")
        assert delta_entry["split"] == "tuning"

        written = json.loads(output_path.read_text())
        assert [e["id"] for e in written] == [e["id"] for e in assembled]

    def test_check_flag_validates_without_writing(self, tmp_path):
        root, manifest_path, queries_dir, seed = _assemble_fixture(tmp_path)
        output_path = root / CODE_CORPUS_DIR / "queries.json"

        assembled, problems = assemble.run(
            queries_dir=queries_dir, manifest_path=manifest_path, root=root,
            seed=seed, write_to=None,
        )

        assert problems == []
        assert not output_path.exists()

    def test_invalid_queries_are_not_written(self, tmp_path):
        root, manifest_path, queries_dir, seed = _assemble_fixture(tmp_path)
        # Break one file: an unknown category.
        beta_path = queries_dir / "beta.json"
        rows = json.loads(beta_path.read_text())
        rows[0]["category"] = "vibes"
        beta_path.write_text(json.dumps(rows))
        output_path = root / CODE_CORPUS_DIR / "queries.json"

        assembled, problems = assemble.run(
            queries_dir=queries_dir, manifest_path=manifest_path, root=root,
            seed=seed, write_to=output_path,
        )

        assert problems != []
        assert not output_path.exists()

    def test_main_check_exits_nonzero_on_missing_query_files(self, tmp_path, monkeypatch, capsys):
        root = tmp_path
        manifest_path = root / CODE_CORPUS_DIR / "MANIFEST.json"
        manifest_path.parent.mkdir(parents=True, exist_ok=True)
        manifest_path.write_text(json.dumps({"heldOutFamilies": [], "files": []}))
        queries_dir = root / CODE_CORPUS_DIR / "queries"

        monkeypatch.setattr(assemble, "QUERIES_DIR", queries_dir)
        monkeypatch.setattr(assemble, "MANIFEST_PATH", manifest_path)
        monkeypatch.setattr(assemble, "REPO_ROOT_FOR_VALIDATION", root)
        monkeypatch.setattr(assemble, "_load_seed", lambda: {"heldOutFamilies": [], "selfHeldOut": []})

        exit_code = assemble.main(["--check"])
        assert exit_code == 1
        assert "no query files" in capsys.readouterr().err.lower()

    def test_main_writes_output_on_success(self, tmp_path, monkeypatch):
        root, manifest_path, queries_dir, seed = _assemble_fixture(tmp_path)
        output_path = root / CODE_CORPUS_DIR / "queries.json"

        monkeypatch.setattr(assemble, "QUERIES_DIR", queries_dir)
        monkeypatch.setattr(assemble, "MANIFEST_PATH", manifest_path)
        monkeypatch.setattr(assemble, "OUTPUT_PATH", output_path)
        monkeypatch.setattr(assemble, "REPO_ROOT_FOR_VALIDATION", root)
        monkeypatch.setattr(assemble, "_load_seed", lambda: seed)

        exit_code = assemble.main([])
        assert exit_code == 0
        assert output_path.exists()


# ---------------------------------------------------------------------------
# compare_code_eval rule 3: proven able to fire for C# specifically
# ---------------------------------------------------------------------------

COMPARE_PATH = REPO_ROOT / "scripts" / "retrieval_tuning" / "compare_code_eval.py"


def _load_compare():
    spec = importlib.util.spec_from_file_location("compare_code_eval", COMPARE_PATH)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


compare = _load_compare()


def _cs_row(entry_id, ndcg5, *, category, language):
    return {
        "entry_id": entry_id, "category": category, "ndcg5": ndcg5,
        "language": language, "split": "held-out",
    }


class TestRule3CanGoRedForCSharp:
    def test_a_csharp_only_regression_drops_the_arm(self):
        # A strong, clean gain on the target category (identifier-fragment,
        # Python) would otherwise KEEP; a C#-only regression past the -0.05
        # language floor (rule 3) must still force a DROP.
        good_baseline = [_cs_row(f"py{i}", 0.5, category="identifier-fragment", language="Python")
                          for i in range(20)]
        good_arm = [_cs_row(f"py{i}", 0.9, category="identifier-fragment", language="Python")
                    for i in range(20)]
        cs_baseline = [_cs_row(f"cs{i}", 0.6, category="path-context", language="C#")
                       for i in range(20)]
        cs_arm = [_cs_row(f"cs{i}", 0.3, category="path-context", language="C#")
                  for i in range(20)]  # -0.3, far past the -0.05 language floor

        result = compare.evaluate_keep_drop(
            good_baseline + cs_baseline, good_arm + cs_arm,
            target_category="identifier-fragment",
        )

        assert result["verdict"] == "DROP"
        assert any("C#" in reason for reason in result["reasons"])
