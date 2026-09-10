#!/usr/bin/env python3
"""Thin wrapper — regeneration logic lives in
scripts/src/retrieval_tuning/refresh_corpora.py (P3 AC3).

Exit codes: 0 clean, 1 anchor drift, 2 snapshot mismatch, 3 smoke regression.
"""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent / "src"))

from retrieval_tuning.refresh_corpora import main  # noqa: E402

if __name__ == "__main__":
    sys.exit(main())
