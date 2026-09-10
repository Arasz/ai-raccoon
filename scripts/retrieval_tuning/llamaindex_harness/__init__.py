"""LlamaIndex + Chroma fusion-retrieval harness (import-safe; see ingest.py)."""

import sys
from pathlib import Path

# scripts/src carries retrieval_tuning (the one scope builder, repo_data, the
# scratch/server helpers). Bootstrap it here so `python3 -m
# llamaindex_harness.evaluate <...>` works from scripts/retrieval_tuning too,
# not only under pytest's pythonpath.
_SRC = Path(__file__).resolve().parents[2] / "src"
if str(_SRC) not in sys.path:
    sys.path.insert(0, str(_SRC))
