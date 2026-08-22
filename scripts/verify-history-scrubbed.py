#!/usr/bin/env python3
"""Fails if any of #414's private-prose paths still have committed history on the remote.

Clones the remote fresh into a temp dir (or checks a given local repo with --repo) and runs
`git log --all -- <path>` for every tracked path. Any path with a nonzero commit count means
the history rewrite (S6b) has not happened yet, or did not cover that path.
See docs/work/2026-08-22-414-s6b-history-rewrite-runbook.md.
"""

import argparse
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

DEFAULT_REMOTE = "https://github.com/Arasz/ai-raccoon.git"

# The #414 private-prose paths this gate protects. Extend at the CLI with --path, not by
# hand-editing this tuple for one-off checks.
PATHS = (
    "tests/AiRaccoon.Tests/Resources/jsaa-memory.db",
    "benchmarks/AiRaccoon.Benchmarks/Corpus/RealWorldCorpus.cs",
    "tests/AiRaccoon.Tests/Unit/Retrieval/assets/reference-topk.json",
)


def clone_fresh(remote: str, dest: Path) -> None:
    subprocess.run(["git", "clone", "--quiet", remote, str(dest)], check=True)


def commit_count(repo: Path, path: str) -> int:
    result = subprocess.run(
        ["git", "-C", str(repo), "log", "--all", "--oneline", "--", path],
        check=True, capture_output=True, text=True,
    )
    return len([line for line in result.stdout.splitlines() if line.strip()])


def check_paths(repo: Path, paths: list[str]) -> dict[str, int]:
    return {path: commit_count(repo, path) for path in paths}


def dirty_paths(results: dict[str, int]) -> list[str]:
    return [path for path, count in results.items() if count > 0]


def report(results: dict[str, int]) -> str:
    return "\n".join(f"{path}: {count} commit(s)" for path, count in results.items())


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--remote", default=DEFAULT_REMOTE, help="remote to clone (default: %(default)s)")
    parser.add_argument("--path", action="append", default=[], help="extra path to check, beyond PATHS")
    parser.add_argument("--repo", default=None, help="check this local repo instead of cloning --remote")
    args = parser.parse_args(argv)

    paths = list(PATHS) + list(args.path)
    tmp_dir = None
    try:
        if args.repo:
            repo = Path(args.repo)
        else:
            tmp_dir = tempfile.mkdtemp(prefix="ai-raccoon-verify-history.")
            repo = Path(tmp_dir)
            clone_fresh(args.remote, repo)

        results = check_paths(repo, paths)
        print(report(results))

        dirty = dirty_paths(results)
        if dirty:
            print(f"FAIL: history still reachable for: {', '.join(dirty)}", file=sys.stderr)
            return 1

        print("PASS: no history reachable for any tracked path")
        return 0
    finally:
        if tmp_dir:
            shutil.rmtree(tmp_dir, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
