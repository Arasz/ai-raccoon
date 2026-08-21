"""Load/validate corpus JSON per plan §5.3, with anchors resolved against the memory-db copy.

Validation is read-only against the copy (sqlite3 ?mode=ro); the live bank is
never opened here — only the copy path a caller passes in.
"""

from __future__ import annotations

import json
import re
import sqlite3
from pathlib import Path
from typing import Optional

from .scoring import anchor_of, source_file_matches

VALID_SCOPES = {"project", "shared", "all"}
TEST_GRADES = {"good", "could-be-improved", "just-wrong"}
_HEX_RE = re.compile(r"^[0-9a-fA-F]{6,}$")
_WS_RE = re.compile(r"\s+")


def load_corpus(path) -> list[dict]:
    """Read a corpus file: a JSON list of entries, or {'queries': [...]}."""
    raw = json.loads(Path(path).read_text())
    if isinstance(raw, list):
        return raw
    if isinstance(raw, dict) and isinstance(raw.get("queries"), list):
        return raw["queries"]
    raise ValueError(f"{path}: corpus JSON must be a list of entries or {{'queries': [...]}}")


def validate_entries(
    entries: list[dict],
    db_path: Optional[str] = None,
    require_grade: bool = False,
    expected_count: Optional[int] = None,
) -> list[str]:
    """Structural + anchor validation. Returns a list of problems (empty = valid)."""
    problems: list[str] = []
    if not entries:
        problems.append("corpus is empty")

    seen: set = set()
    for entry in entries:
        entry_id = entry.get("id")
        if entry_id is None:
            problems.append("entry missing 'id'")
            continue
        if entry_id in seen:
            problems.append(f"duplicate id '{entry_id}'")
        seen.add(entry_id)

        if not entry.get("query"):
            problems.append(f"{entry_id}: missing 'query'")
        limit = entry.get("searchLimit")
        if not isinstance(limit, int) or limit < 1:
            problems.append(f"{entry_id}: 'searchLimit' must be an int >= 1")
        scope = entry.get("targetScope")
        if scope is not None and scope not in VALID_SCOPES:
            problems.append(f"{entry_id}: invalid targetScope '{scope}' (expected {sorted(VALID_SCOPES)})")

        expected_hash = entry.get("expectedHash")
        expected_source = entry.get("expectedSource")
        if expected_hash is not None and (
            not isinstance(expected_hash, str) or not _HEX_RE.match(expected_hash)
        ):
            problems.append(f"{entry_id}: 'expectedHash' must be a hex string of >= 6 chars")

        non_file = bool(entry.get("nonFileTarget"))
        if non_file:
            if not expected_hash:
                problems.append(f"{entry_id}: non-file query requires 'expectedHash'")
            if expected_source:
                problems.append(f"{entry_id}: non-file query must not carry 'expectedSource'")
        elif not expected_hash and not expected_source:
            problems.append(f"{entry_id}: file-targeted query needs 'expectedSource' or 'expectedHash'")

        if require_grade:
            grade = entry.get("grade")
            if grade not in TEST_GRADES:
                problems.append(f"{entry_id}: test-set entry needs 'grade' in {sorted(TEST_GRADES)}")
            if not entry.get("gradeRationale"):
                problems.append(f"{entry_id}: test-set entry needs 'gradeRationale'")

    if expected_count is not None and len(entries) != expected_count:
        problems.append(f"expected {expected_count} entries, got {len(entries)}")

    if db_path is not None:
        problems.extend(resolve_anchors(entries, db_path))
    return problems


def resolve_anchors(entries: list[dict], db_path) -> list[str]:
    """Every expectedHash / expectedSource must resolve to a chunk in the copy (read-only)."""
    rows = _copy_rows(db_path)
    problems: list[str] = []
    for entry in entries:
        entry_id = entry.get("id")
        expected_hash = entry.get("expectedHash")
        if expected_hash and not any(
            row["hash"].lower().startswith(expected_hash.lower()) for row in rows
        ):
            problems.append(f"{entry_id}: expectedHash '{expected_hash}' does not resolve in the copy")

        expected_source = entry.get("expectedSource")
        if expected_source:
            matches = [
                row for row in rows
                if row["source_file"] and source_file_matches(row["source_file"], expected_source)
            ]
            if not matches:
                problems.append(
                    f"{entry_id}: expectedSource '{expected_source}' resolves to no chunk in the copy"
                )
                continue
            anchor = anchor_of(expected_source)
            if anchor:
                for row in matches:
                    heading = f"{row['heading_path'] or ''} > {row['section'] or ''}"
                    if any(seg.strip().lower() == anchor for seg in heading.split(">")):
                        break
                else:
                    problems.append(
                        f"{entry_id}: expectedSource anchor '#{anchor}' matches no chunk heading "
                        f"in the copy"
                    )
    return problems


def _copy_rows(db_path) -> list[dict]:
    """All (hash, source_file, heading_path, section) rows of the copy — read-only."""
    con = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True)
    try:
        return [
            {"hash": row[0] or "", "source_file": row[1], "heading_path": row[2], "section": row[3]}
            for row in con.execute(
                "SELECT hash, source_file, heading_path, section FROM entries"
            )
        ]
    finally:
        con.close()


def check_disjoint(test_entries: list[dict], eval_entries: list[dict]) -> list[str]:
    """Test-vs-eval honesty (plan §5.2): no shared query text, no shared target file."""
    problems: list[str] = []

    def norm_query(query) -> str:
        return _WS_RE.sub(" ", str(query).strip().casefold())

    test_queries = {norm_query(e.get("query")) for e in test_entries if e.get("query")}
    for entry in eval_entries:
        query = norm_query(entry.get("query", ""))
        if query and query in test_queries:
            problems.append(f"{entry.get('id')}: query text is shared with the test set")

    def file_key(entry) -> Optional[str]:
        source = entry.get("expectedSource")
        return source.split("#", 1)[0] if source else None

    test_files = {file_key(e) for e in test_entries if file_key(e)}
    for entry in eval_entries:
        key = file_key(entry)
        if key and key in test_files:
            problems.append(f"{entry.get('id')}: target file {key} is shared with the test set")
    return problems


def category_counts(entries: list[dict]) -> dict:
    """The plan's two report buckets: file-targeted vs non-file queries."""
    return {
        "file": sum(1 for e in entries if not e.get("nonFileTarget")),
        "non-file": sum(1 for e in entries if e.get("nonFileTarget")),
    }
