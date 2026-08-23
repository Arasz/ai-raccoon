#!/usr/bin/env python3
"""Open a draft PR to the ai-badger framework repo with generalized contributions.

The agent first writes the generalized feature files into the ai-badger CHECKOUT (under the
right {stack}/{feature}/ paths) and regenerates index.json. This script does the mechanical
git+PR work: branch, commit, push, `gh pr create --draft`. No LLM.

Every declared path is scanned for credential-shaped literals first; a finding refuses the
PR. Only declared paths are staged — an unrelated dirty file in the checkout never rides along
(security I4).

Usage:
  open_pr.py --checkout <ai-badger checkout> --branch feed/<slug> \
             --title "..." --body-file <path> --path <rel> [--path <rel> ...] \
             [--repo Arasz/ai-badger] [--dry-run]

--dry-run prints the git/gh commands without executing (used for logic-tests).
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
from pathlib import Path
from typing import List


def _bootstrap_lib() -> Path:
    """Put the framework's engine/ and tooling/ on sys.path and return its root.

    One predicate, shared with badger_lib.is_framework_root: schemas/ + features/ +
    engine/badger_lib.py. Ordered inputs: --root, an ancestor walk, $AI_BADGER, the root
    recorded in a .ai-badger/manifest.json above this file, then ~/.ai-badger/framework
    (ADR-0009). Duplicated verbatim in every entry point because locating badger_lib is
    what it is for.
    """

    def is_root(path):
        return ((path / "schemas").is_dir() and (path / "features").is_dir()
                and (path / "engine" / "badger_lib.py").is_file())

    def argv_root():
        # sys.argv is ours only when this file is the program being run; these modules are
        # also imported into hosts whose own --root means something else entirely.
        try:
            if not sys.argv or Path(sys.argv[0]).resolve() != Path(__file__).resolve():
                return None
        except (OSError, ValueError):
            return None
        argv = sys.argv[1:]
        for i, arg in enumerate(argv):
            if arg == "--root" and i + 1 < len(argv):
                return argv[i + 1]
            if arg.startswith("--root="):
                return arg.split("=", 1)[1]
        return None

    def checked(value, source):
        root = Path(value).expanduser()
        if not is_root(root):
            raise RuntimeError(
                f"{source} is {root}, which is not an ai-badger framework root "
                f"(no schemas/ + features/ + engine/badger_lib.py)"
            )
        return root

    def manifests(start):
        # Above this file only. A working directory belongs to whatever repo the user
        # opened, and no repo may steer the sys.path of a hook that runs on session start.
        for anc in [start, *start.parents]:
            manifest = (anc / "manifest.json" if anc.name == ".ai-badger"
                        else anc / ".ai-badger" / "manifest.json")
            if not manifest.is_file():
                continue
            try:
                data = json.loads(manifest.read_text(encoding="utf-8"))
            except (OSError, ValueError):
                continue
            if isinstance(data, dict):
                yield manifest, data

    def recorded(start):
        for manifest, data in manifests(start):
            value = data.get("frameworkRoot")
            if not value:
                continue
            candidate = Path(value).expanduser()
            if not candidate.is_absolute():
                candidate = manifest.parent.parent / candidate
            if is_root(candidate):
                return candidate.resolve()
        return None

    def warn_on_cache_skew(root, start):
        # The cache is last in the order and never updated in place, so its engine can be
        # many releases behind the caller. Say so; never break a session over it.
        if root.resolve() != cache.resolve():
            return
        try:
            have = (cache / "VERSION").read_text(encoding="utf-8").strip()
        except OSError:
            return
        want = next((d.get("frameworkVersion") for _, d in manifests(start)
                     if d.get("frameworkVersion")), None)
        if have and want and have != want:
            print(f"ai-badger: {cache} is version {have}, but this project was scaffolded "
                  f"by {want}. The cache is never updated in place — remove it, or pass "
                  f"--root <framework checkout>.", file=sys.stderr)

    here = Path(__file__).resolve()
    cache = Path.home() / ".ai-badger" / "framework"
    value = argv_root()
    if value:
        root = checked(value, "--root")
    else:
        root = next((anc for anc in [here, *here.parents] if is_root(anc)), None)
        if root is None and os.environ.get("AI_BADGER"):
            root = checked(os.environ["AI_BADGER"], "$AI_BADGER")
        root = root or recorded(here) or (cache if is_root(cache) else None)
    if root is None:
        raise RuntimeError(
            f"could not locate the ai-badger framework: none above {here.parent}, no "
            f"$AI_BADGER, no frameworkRoot in a .ai-badger/manifest.json above it, and no "
            f"cache at {cache} — pass --root <framework> or clone "
            f"https://github.com/Arasz/ai-badger"
        )
    warn_on_cache_skew(root, here)
    sys.path.insert(0, str(root / "tooling"))
    sys.path.insert(0, str(root / "engine"))
    return root.resolve()


FRAMEWORK_ROOT = _bootstrap_lib()
import unsafe_literals as ul  # pylint: disable=wrong-import-position


def run(cmd: List[str], cwd: Path, dry: bool) -> int:
    """Print `cmd`, then execute it in `cwd` unless `dry` is set; return its exit code."""
    printable = " ".join(cmd)
    if dry:
        print(f"    $ {printable}")
        return 0
    print(f"    $ {printable}")
    proc = subprocess.run(cmd, cwd=str(cwd), check=False)
    return proc.returncode


def main(argv=None) -> int:
    """CLI entry point: branch, commit, push, and open a draft PR for --checkout."""
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--checkout", required=True)
    ap.add_argument("--branch", required=True)
    ap.add_argument("--title", required=True)
    ap.add_argument("--body-file", required=True)
    ap.add_argument("--repo", default="Arasz/ai-badger")
    ap.add_argument("--path", action="append", dest="paths", required=True, metavar="REL",
                    help="Checkout-relative path to contribute. Repeatable. Required: only "
                         "declared paths are staged, so nothing else in the tree can ride "
                         "along.")
    ap.add_argument("--dry-run", action="store_true")
    args = ap.parse_args(argv)

    checkout = Path(args.checkout).resolve()
    dry = args.dry_run

    findings = ul.scan_paths(checkout, args.paths)
    if findings:
        print("refusing to open a PR — content that looks like a credential was found:")
        for finding in findings:
            print(f"    - {finding['file']}: {finding['pattern']}")
        print("Remove it (or replace it with an obviously fake value) and re-run. This is a "
              "guard, not proof: it checks known literal shapes, nothing more.")
        return 1

    steps = [
        ["git", "checkout", "-b", args.branch],
        ["git", "add", "--", *args.paths],
        ["git", "commit", "-m", args.title],
        ["git", "push", "-u", "origin", args.branch],
        ["gh", "pr", "create", "--draft", "--repo", args.repo,
         "--title", args.title, "--body-file", args.body_file],
    ]
    print(f"opening draft PR to {args.repo} from {checkout} (dry-run={dry}):")
    for step in steps:
        rc = run(step, checkout, dry)
        if rc != 0 and not dry:
            print(f"step failed ({rc}); aborting.")
            return rc
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
