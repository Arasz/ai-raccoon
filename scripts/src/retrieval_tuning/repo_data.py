"""The one reader for repo-root `data/**` behavior constants (P3 AC2).

Logic modules import the knobs they use; the literal values live only in the
JSON files. `scripts/tests/test_no_hardcoded_knobs.py` derives its forbidden-
literal list from these key names, so a constant re-hardcoded in a logic file
fails the gate.

Layout:
- `data/knobs.json`    — frozen PARAMS, settings defaults + CLI verbs, eval
  limit, model name/revision, bm25 weights, batch sizes.
- `data/buckets.json`  — slice defaults (bucket ids, per-bucket cap).
- `data/graders.json`  — grader trios + sampling seeds.
- `data/corpora/*`    — generator seeds, frames, allowlists, limits.
"""

from __future__ import annotations

import json
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[3]
DATA_DIR = REPO_ROOT / "data"


def load_data(name: str) -> dict:
    """Load one `data/<name>` JSON file (fails loud when absent or malformed)."""
    return json.loads((DATA_DIR / name).read_text())


def load_corpora() -> dict[str, dict]:
    """Every `data/corpora/*.json`, keyed by file stem."""
    return {path.stem: json.loads(path.read_text())
            for path in sorted((DATA_DIR / "corpora").glob("*.json"))}


KNOBS = load_data("knobs.json")
BUCKETS = load_data("buckets.json")
GRADERS = load_data("graders.json")
CORPORA = load_corpora()
