"""Compatibility shim: the scope/bucket builder now lives in
`retrieval_tuning.scopes` (P3 AC1 — one home). This module only bootstraps
`scripts/src` onto sys.path and re-exports the same objects, so harness call
sites (`from . import scopes`) and the stdlib-only CI lane keep working.

No logic belongs here; the identity gates in
`scripts/tests/test_retrieval_tuning_scopes.py` pin that.
"""

from __future__ import annotations

import sys
from pathlib import Path

_SRC = Path(__file__).resolve().parents[2] / "src"
if str(_SRC) not in sys.path:
    sys.path.insert(0, str(_SRC))

from retrieval_tuning.scopes import (  # noqa: E402,F401
    CORPUS_SCOPES,
    DEFAULT_QUERY_SCOPE,
    DEFAULT_SERVER_SCOPE,
    SCOPE_FALLBACK,
    VALID_SCOPES,
    chroma_where,
    committed_scope_clause,
    ingest_predicate,
    load_corpus,
    normalize_scope,
    resolve_buckets,
    scope_predicate,
    shared_scope_clause,
    slice_keep_clauses,
)
