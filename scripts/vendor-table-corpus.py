#!/usr/bin/env python3
"""Vendor the table-bearing retrieval corpus into tests/AiRaccoon.Tests/Resources/TableCorpus.

Reads scripts/table-corpus-sources.json, extracts each listed file from its source repo at the
pinned commit (`git show <pin>:<path>`, so the live checkout is never touched or moved), and
writes it under <corpus>/<source-id>/<path>. Refuses to vendor a file with no markdown table —
a table-blind corpus is the defect ADR-0077 exists to record.

Unlike the jsaa corpus this produces source markdown, not a bank: the gate ingests these files
at test time through the production FileIngestor, so a chunking change re-chunks them.

Usage: python3 scripts/vendor-table-corpus.py [--check]
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from pathlib import Path

TABLE_SEPARATOR = re.compile(r"^\s*\|?[\s:-]*\|[\s:|-]*$", re.MULTILINE)
SECRETS = re.compile(
    r"(?i)(api[_-]?key|secret|password|token)\s*[:=]\s*['\"]?[A-Za-z0-9/+_-]{16,}"
)

REPO_ROOT = Path(__file__).resolve().parent.parent
MANIFEST = REPO_ROOT / "scripts" / "table-corpus-sources.json"
CORPUS = REPO_ROOT / "tests" / "AiRaccoon.Tests" / "Resources" / "TableCorpus"


def has_table(text: str) -> bool:
    """A markdown table needs a separator row with a header line directly above it."""
    lines = text.splitlines()
    return any(
        index > 0 and TABLE_SEPARATOR.match(line) and lines[index - 1].strip().startswith("|")
        for index, line in enumerate(lines)
    )


def read_pinned(root: Path, pin: str, relative: str) -> str:
    result = subprocess.run(
        ["git", "show", f"{pin}:{relative}"],
        cwd=root, capture_output=True, text=True, check=False,
    )
    if result.returncode != 0:
        raise SystemExit(f"{root.name}: cannot read {relative} at {pin[:8]}: {result.stderr.strip()}")
    return result.stdout


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true",
                        help="Verify the vendored copies match the pins; write nothing.")
    args = parser.parse_args()

    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    stale: list[str] = []
    vendored = 0

    for source in manifest["sources"]:
        root, pin, source_id = Path(source["root"]), source["pinnedCommit"], source["id"]
        if not args.check and not root.exists():
            raise SystemExit(f"source repo not found: {root} (needed to vendor '{source_id}')")

        for relative in source["files"]:
            target = CORPUS / source_id / relative
            if args.check:
                if not target.exists():
                    stale.append(f"{source_id}/{relative}: not vendored")
                    continue
                if root.exists() and target.read_text(encoding="utf-8") != read_pinned(root, pin, relative):
                    stale.append(f"{source_id}/{relative}: differs from {pin[:8]}")
                continue

            text = read_pinned(root, pin, relative)
            if not has_table(text):
                raise SystemExit(f"{source_id}/{relative} carries no markdown table — "
                                 "a table-blind corpus is the defect this corpus exists to remove")
            if SECRETS.search(text):
                raise SystemExit(f"{source_id}/{relative} looks like it carries a credential; not vendoring it")
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text(text, encoding="utf-8")
            vendored += 1

    if args.check:
        if stale:
            print("\n".join(stale), file=sys.stderr)
            raise SystemExit(f"{len(stale)} vendored file(s) drifted from their pin; "
                             "re-run scripts/vendor-table-corpus.py")
        print("table corpus matches its pins")
        return

    # Outside CORPUS: everything inside it is ingested, and a manifest listing the corpus's own
    # file paths would be indexed alongside the documents it describes.
    provenance = CORPUS.parent / "TableCorpus-SOURCES.md"
    lines = ["# Table corpus provenance", "",
             "Vendored by `scripts/vendor-table-corpus.py` from `scripts/table-corpus-sources.json`.",
             "Do not edit these files by hand — re-run the script instead.", ""]
    for source in manifest["sources"]:
        lines += [f"## {source['id']} @ `{source['pinnedCommit']}`", ""]
        lines += [f"- `{relative}`" for relative in source["files"]]
        lines += [""]
    provenance.write_text("\n".join(lines), encoding="utf-8")
    print(f"vendored {vendored} files into {CORPUS.relative_to(REPO_ROOT)}")


if __name__ == "__main__":
    main()
