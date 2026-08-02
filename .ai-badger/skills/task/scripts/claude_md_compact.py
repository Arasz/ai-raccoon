#!/usr/bin/env python3
"""Check CLAUDE.md against its size budget. Exit 0 = within budget, 1 = over.

The compaction itself is editorial work the model does (dropping facts
derivable from code/git; per-task state belongs in .ai-badger/state.json, not
here) — this script only measures, so the trigger is deterministic and
hook-friendly. If the project has no CLAUDE.md, this reports 0 chars/lines
and is always within budget — nothing project-specific is assumed.

Usage: claude_md_compact.py [--max-chars N] [--max-lines N]
"""
# pylint: disable=missing-function-docstring
# Ported verbatim from the originating job-search-ai-assistant repo's /task skill: kept in
# lockstep with that source rather than churned for local docstring style rules.

import argparse
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import tracker_lib as lib


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--max-chars", type=int, default=None)
    parser.add_argument("--max-lines", type=int, default=None)
    args = parser.parse_args()

    stats = lib.claude_md_stats(args.max_chars, args.max_lines)
    print(json.dumps(stats, indent=2))
    return 1 if stats["overBudget"] else 0


if __name__ == "__main__":
    sys.exit(main())
