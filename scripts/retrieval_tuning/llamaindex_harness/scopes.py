"""Shared scope/bucket predicates (P1; pure + stdlib-only).

One VALID_SCOPES table, one corpus-only scope mapping, one bucket resolver.
Imported by ingest (heavy), retrieve (heavy), evaluate (must stay stdlib-only
for the CI scripts-harness lane) and slice_copy (stdlib-only) — so this module
imports nothing beyond the standard library. P3 relocates it into src/
unchanged in behavior; P3 must not redefine it.

Bank ground truth (read, not inferred):
- MemoryTools.cs: memory_search accepts scope all/project/shared only;
  anything else is refused as invalid-params.
- SearchContexts.cs: scope=project reads the project context PLUS every
  custom-label context, so corpus scope=custom maps to bank scope=project.
"""

from __future__ import annotations

import json
import sqlite3
from pathlib import Path

# Bank-accepted scopes (MemoryTools.cs). Corpus-only scopes map via
# SCOPE_FALLBACK, never by widening the bank surface here.
VALID_SCOPES = ("project", "shared", "all")

# Corpus-only scopes -> bank-accepted scopes. custom->project because bank
# scope=project already covers custom labels (SearchContexts.cs).
SCOPE_FALLBACK = {"custom": "project"}


def normalize_scope(scope: str) -> str:
    """Map a corpus scope onto a bank-accepted scope (custom->project).

    Unknown scopes pass through untouched so their own gates still fail loud
    (ingest._scope_predicate raises; the bank refuses with invalid-params).
    """
    return SCOPE_FALLBACK.get(scope, scope)


def load_corpus(corpus) -> tuple[dict | None, list[dict]]:
    """A corpus path (or loaded JSON) -> (header-or-None, query/anchor entries).

    Header-shaped dicts ({header, queries}) carry the bucket derivation +
    exclusion manifest; bare lists (subset corpora) carry forced anchors only.
    """
    if isinstance(corpus, (str, Path)):
        corpus = json.loads(Path(corpus).read_text())
    if isinstance(corpus, list):
        return None, list(corpus)
    if isinstance(corpus, dict) and isinstance(corpus.get("queries"), list):
        header = corpus.get("header")
        return (dict(header) if isinstance(header, dict) else None,
                list(corpus["queries"]))
    raise ValueError(
        "corpus must be a header-shaped {header, queries} mapping or a bare "
        f"entry list, got {type(corpus).__name__}")


def _copy_projects(copy_path: str) -> set[str]:
    conn = sqlite3.connect(f"file:{Path(copy_path).resolve()}?mode=ro", uri=True)
    conn.execute("PRAGMA query_only=ON")
    try:
        return {r[0] for r in
                conn.execute("SELECT DISTINCT project_id FROM entries "
                             "WHERE project_id IS NOT NULL")}
    finally:
        conn.close()


def resolve_buckets(copy_path: str | None, corpus,
                    explicit: str | None) -> tuple[tuple[str, ...], list[dict]]:
    """(buckets, excludedProjects). Precedence: explicit --buckets >
    corpus-header derivation > fail loud, never a frozen fallback.

    copy_path validates the resolved buckets against the copy (an explicit
    typo fails here, not as a silent-empty ingest); None skips validation
    (pure coverage checks). corpus is a header-shaped path/mapping or None.
    The excluded manifest is ALWAYS the header's excludedProjects verbatim —
    closed to seed-equality, never extended ad hoc.
    """
    excluded: list[dict] = []
    header: dict | None = None
    if corpus is not None:
        header, _ = load_corpus(corpus)
        if isinstance(header, dict):
            excluded = list(header.get("excludedProjects") or [])
    if explicit:
        buckets = tuple(sorted({b.strip() for b in explicit.split(",") if b.strip()}))
        if not buckets:
            raise ValueError("resolve_buckets: --buckets is blank")
    elif header is not None and isinstance(header.get("projects"), dict):
        buckets = tuple(sorted(header["projects"]))
    else:
        raise ValueError(
            "resolve_buckets: cannot resolve ingest buckets — pass --buckets "
            "or a header-shaped --corpus; refusing the frozen fallback")
    if copy_path is not None:
        present = _copy_projects(copy_path)
        unknown = [b for b in buckets if b not in present]
        if unknown:
            raise ValueError(
                f"resolve_buckets: buckets absent from copy {copy_path}: {unknown}")
    return buckets, excluded
