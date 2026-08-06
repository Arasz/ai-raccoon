#!/usr/bin/env python3
"""Ingest job-search-ai-assistant documentation into AiRaccoon's memory.db.

Thin CLI wrapper over scripts/src/pipeline.py (see docs/plans/scripts-refactor.md §5 P5).

Walks the JSAA project tree, chunks files by content type, writes chunks to
AiRaccoon via its MCP HTTP transport, and exports a structured-path → SHA256
hash map for the test runner's isExpectedSource matching.

Usage:
  python3 scripts/ingest-jsaa-docs.py --dry-run       # enumerate only
  python3 scripts/ingest-jsaa-docs.py --chunk-only     # enumerate + chunk, no writes
  python3 scripts/ingest-jsaa-docs.py --ingest-only    # full ingest, no spot-checks
  python3 scripts/ingest-jsaa-docs.py --verify         # full ingest + spot-checks
  python3 scripts/ingest-jsaa-docs.py --reset          # delete all contexts then re-ingest

Exclusion list (plan C §0/§3 Wave 0 — the canonical corpus must be curated):
  docs/work/, docs/state.json, docs/now.md, .ai-badger/state.json,
  .remember/now.md, docs/brand/, and any other non-ADR, non-invariant
  content — see EXCLUDE_GLOBS in src/jsaa_config.py. Ingest is pinned to
  JSAA_PINNED_COMMIT; the script aborts if the jsaa tree HEAD does not match
  the pin.
"""

from __future__ import annotations

import argparse
import asyncio
import logging
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent / "src"))

from jsaa_config import PROJECT_ID  # noqa: E402  # re-exported: C# tests reference PROJECT_ID in this file
from pipeline import run_pipeline  # noqa: E402


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Ingest job-search-ai-assistant documentation into AiRaccoon memory."
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Enumerate files only, no chunking or MCP writes.",
    )
    parser.add_argument(
        "--chunk-only",
        action="store_true",
        help="Enumerate + chunk + hash map, no MCP writes.",
    )
    parser.add_argument(
        "--ingest-only",
        action="store_true",
        help="Full ingest without spot-checks.",
    )
    parser.add_argument(
        "--verify",
        action="store_true",
        help="Full ingest with verification spot-checks.",
    )
    parser.add_argument(
        "--reset",
        action="store_true",
        help="Delete all contexts before re-ingesting (requires full access mode).",
    )

    args = parser.parse_args()

    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s [%(levelname)s] %(message)s",
        datefmt="%H:%M:%S",
    )

    # Determine mode from flags (default: --ingest-only behaviour)
    dry_run = args.dry_run
    chunk_only = args.chunk_only
    ingest_only = args.ingest_only
    verify = args.verify
    reset = args.reset

    # If no flag passed, default to ingest-only
    if not any([dry_run, chunk_only, ingest_only, verify, reset]):
        ingest_only = True

    # --verify implies ingest
    if verify:
        ingest_only = True

    asyncio.run(run_pipeline(
        dry_run=dry_run,
        chunk_only=chunk_only,
        ingest_only=ingest_only,
        verify=verify,
        reset=reset,
    ))


if __name__ == "__main__":
    main()
