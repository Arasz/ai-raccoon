#!/usr/bin/env python3
"""E2 — assemble the code-eval query set (plan §E2).

Merges every author-produced `code-corpus/queries/<family>.json`, assigns
each entry's tuning/held-out split (`code_corpus.assign_split`, ADR-0056),
validates the merged set against `MANIFEST.json`
(`code_corpus.validate_queries`), and writes one sorted-by-id
`code-corpus/queries.json`.

`--check` validates the query files already on disk without writing.

Usage:
    python scripts/retrieval_tuning/assemble_code_queries.py
    python scripts/retrieval_tuning/assemble_code_queries.py --check

Exit code 0 = assembled/verified clean; 1 = no query files found, or any
entry fails validation.
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import Optional

REPO_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO_ROOT / "scripts" / "src"))
from retrieval_tuning import code_corpus, repo_data  # noqa: E402

CODE_CORPUS_ROOT = REPO_ROOT / code_corpus.CODE_CORPUS_DIR
QUERIES_DIR = CODE_CORPUS_ROOT / "queries"
MANIFEST_PATH = CODE_CORPUS_ROOT / "MANIFEST.json"
OUTPUT_PATH = CODE_CORPUS_ROOT / "queries.json"
REPO_ROOT_FOR_VALIDATION = REPO_ROOT


def _load_seed() -> dict:
    return repo_data.CORPORA["code-eval-corpus"]


def load_query_files(queries_dir: Path) -> list[dict]:
    """Every `queries/<family>.json`'s entries, family files in sorted order."""
    entries: list[dict] = []
    for path in sorted(queries_dir.glob("*.json")):
        payload = json.loads(path.read_text())
        if not isinstance(payload, list):
            raise ValueError(f"{path}: expected a JSON array of query entries")
        entries.extend(payload)
    return entries


def assemble(entries: list[dict], seed: dict) -> list[dict]:
    """Assign each entry's split; return the set sorted by id."""
    assigned = []
    for entry in entries:
        tagged = dict(entry)
        tagged["split"] = code_corpus.assign_split(entry, seed)
        assigned.append(tagged)
    return sorted(assigned, key=lambda e: e.get("id") or "")


def run(
    *, queries_dir: Path, manifest_path: Path, root: Path, seed: dict,
    write_to: Optional[Path],
) -> tuple[list[dict], list[str]]:
    """Load, assemble and validate; write to `write_to` when given (None = --check)."""
    if not queries_dir.is_dir() or not any(queries_dir.glob("*.json")):
        return [], [f"no query files: nothing under {queries_dir}"]

    entries = load_query_files(queries_dir)
    manifest = json.loads(manifest_path.read_text())
    assembled = assemble(entries, seed)

    problems = code_corpus.validate_queries(assembled, manifest, root)
    if problems:
        return assembled, problems

    if write_to is not None:
        write_to.parent.mkdir(parents=True, exist_ok=True)
        write_to.write_text(json.dumps(assembled, indent=2) + "\n")
    return assembled, []


def main(argv: Optional[list[str]] = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument(
        "--check", action="store_true",
        help="validate the query files already on disk without writing queries.json",
    )
    args = parser.parse_args(argv)

    seed = _load_seed()
    write_to = None if args.check else OUTPUT_PATH
    assembled, problems = run(
        queries_dir=QUERIES_DIR, manifest_path=MANIFEST_PATH,
        root=REPO_ROOT_FOR_VALIDATION, seed=seed, write_to=write_to,
    )

    if problems:
        print(f"FAIL: {len(problems)} problem(s):", file=sys.stderr)
        for problem in problems:
            print(f"  - {problem}", file=sys.stderr)
        return 1

    if args.check:
        print(f"OK: {len(assembled)} queries verified against the manifest.")
    else:
        print(f"OK: assembled {len(assembled)} queries -> {OUTPUT_PATH}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
