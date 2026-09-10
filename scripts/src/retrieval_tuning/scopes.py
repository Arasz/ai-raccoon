"""The ONE scope/bucket builder (P3; pure + stdlib-only).

One VALID_SCOPES table + default-scope table, one corpus-only scope mapping,
one corpus loader, one bucket resolver, and the SQL/Chroma emitters every
caller shares:

- per-query predicates (FTS SQL for project/shared/all; Chroma `where`),
- ingest-rule set-membership (the project buckets + global shared tier),
- the slice keep-rule (shared tier whole + first N committed rows per bucket).

Imported by the harness (ingest/retrieve/evaluate/slice_copy) and by
`retrieval_tuning.corpus`; therefore it imports nothing beyond the standard
library. The harness module `llamaindex_harness.scopes` is a re-export shim —
this module is the one home.

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

# The corpus-level view: what a corpus file may carry. Derived from the one
# table — never a second hand-maintained set.
CORPUS_SCOPES = frozenset(VALID_SCOPES) | frozenset(SCOPE_FALLBACK)

# Default-scope table. The harness/eval path defaults a scoped query to
# project; ScratchServer.search (the eval-set path) defaults to all. They are
# different contracts and are named here once.
DEFAULT_QUERY_SCOPE = "project"
DEFAULT_SERVER_SCOPE = "all"


def normalize_scope(scope: str) -> str:
    """Map a corpus scope onto a bank-accepted scope (custom->project).

    Unknown scopes pass through untouched so their own gates still fail loud
    (the FTS predicate raises; the bank refuses with invalid-params).
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


# -- SQL emitters -----------------------------------------------------------
# The clause builders below are the single source for every scope predicate;
# callers differ only in the table alias they pass as `prefix`.

def committed_scope_clause(prefix: str = "") -> str:
    """The project/custom half: a bank project search covers custom labels."""
    return f"{prefix}scope IN ('project','custom')"


def shared_scope_clause(prefix: str = "") -> str:
    """The global shared tier half."""
    return f"{prefix}scope = 'shared'"


def ingest_predicate(buckets, prefix: str = "") -> tuple[str, tuple]:
    """The ingest-rule set membership: resolved project buckets + global shared.

    Used by ingest.load_rows and ingest.verify_store id-set parity, so a store
    can never be verified under a different rule than it was built with.
    """
    bucket_list = tuple(buckets)
    if not bucket_list:
        raise ValueError("ingest_predicate: empty buckets")
    placeholders = ",".join("?" for _ in bucket_list)
    return (f"({prefix}project_id IN ({placeholders})"
            f" AND {committed_scope_clause(prefix)})"
            f" OR {shared_scope_clause(prefix)}",
            bucket_list)


def scope_predicate(project_id: str, scope: str, prefix: str = "") -> tuple[str, tuple]:
    """Per-query scope predicate: the bank's three accepted scopes.

    Corpus custom -> bank project (SearchContexts.cs: scope=project covers
    custom labels); an unknown scope raises after normalization."""
    scope = normalize_scope(scope)
    if scope not in VALID_SCOPES:
        raise ValueError(f"unknown scope {scope!r}")
    project_half = f"{prefix}project_id = ? AND {committed_scope_clause(prefix)}"
    if scope == "project":
        return project_half, (project_id,)
    if scope == "shared":
        return shared_scope_clause(prefix), ()
    return (f"(({project_half}) OR {shared_scope_clause(prefix)})", (project_id,))


def slice_keep_clauses(buckets, cap_per_bucket: int) -> tuple[list[str], list]:
    """The slice keep-rule: the shared tier whole + first cap rows per bucket.

    Returns (clauses, params) so slice_copy can build the identical DELETE.
    Every clause is built from the shared emitters — the rule has one home."""
    clauses = [shared_scope_clause()]
    params: list = []
    for bucket in buckets:
        clauses.append(
            "id IN (SELECT id FROM entries WHERE project_id = ?"
            f" AND {committed_scope_clause()} ORDER BY id LIMIT ?)")
        params.extend([bucket, cap_per_bucket])
    return clauses, params


def chroma_where(project_id: str, scope: str) -> dict:
    """Chroma `where` for a per-query scope: the SQL predicate's twin.

    Corpus custom -> bank project (SearchContexts.cs: scope=project covers
    custom labels); without the map, custom fell through to the all-scope
    $or and over-searched the vector leg. Unknown scopes fall through to the
    all-branch, mirroring the 12a72dfb behaviour (the FTS leg raise is the
    fail-loud gate)."""
    project_half = {"project_id": {"$eq": project_id}}
    scope_half = {"scope": {"$in": ["project", "custom"]}}
    shared_half = {"scope": {"$eq": "shared"}}
    scope = normalize_scope(scope)
    if scope == "project":
        return {"$and": [project_half, scope_half]}
    if scope == "shared":
        return shared_half
    return {"$or": [{"$and": [project_half, scope_half]}, shared_half]}


# -- bucket resolution ------------------------------------------------------

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
