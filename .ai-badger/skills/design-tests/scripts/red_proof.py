#!/usr/bin/env python3
"""Turn "this test would fail" into evidence: mutate one line, run the check, revert, print both.

`design-tests` Stage 5 calls this whenever production code already exists, so RED cannot come
from a missing implementation — the RED has to be manufactured, watched, and reverted. This is
the one script in the skill that edits tracked source (a deliberate choice, recorded in the 0.132.0 changelog), so it carries a
durable journal: the original content is written to `.design-tests/red-proof.journal.json` and
fsynced *before* the file is touched — if the journal cannot be written, the run refuses and the
file is never mutated — the file is reverted in a `finally` so a normal failure still restores
it, and on start an existing journal is restored from and the run refuses to proceed — so a
process killed mid-proof (e.g. `SIGKILL`, which no `finally` can catch) leaves recoverable
evidence instead of a silently mutated production file, and the next invocation heals it rather
than piling a second mutation on top.

Python 3 stdlib only (ADR-0005) — this script ships to every scaffolded consumer project.

Usage:
  red_proof.py --file F --line N --replace "old" --with "new" --run "<test command>" [--root DIR]

Preconditions (refused with a message, exit 4, before any file is touched):
  - a journal already exists at <repo>/.design-tests/red-proof.journal.json — restored from and
    left in place; delete it once you have looked at what it restored, then re-run (exit 3)
  - F resolves outside the repo root, or under .git
  - `git status --porcelain -- F` is non-empty (F must be clean before this tool touches it)
  - N is out of range for F, or `--replace` does not occur on line N of F

Exit codes:
  0  went red, then green after revert — the check demonstrably catches this mutation
  1  stayed green — the mutation did not redden the check; this is the finding
  2  red for the wrong reason (the run looks like a build break) or did not return to green
     after the revert
  3  a stale journal was found and restored on start; nothing else ran
  4  a precondition was refused before any mutation was attempted

The mutated file is restored in every case except an uncaught kill signal, which no user-space
`finally` can intercept — that is exactly the case the journal exists for.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import List, Optional

JOURNAL_RELPATH = Path(".design-tests") / "red-proof.journal.json"

BUILD_BREAK_MARKERS = (
    "error CS", "error TS", "error NETSDK", "MSBUILD", "SyntaxError",
    "Cannot find module", "Build FAILED", "error MSB",
)


# --------------------------------------------------------------------------- pure-ish helpers


def _mutated_text(original: str, path: Path, line: int, replace: str, with_: str) -> str:
    """Pure: `original` with `replace` swapped for `with_` once, on 1-indexed `line`. `path` is
    used only in error messages. Raises ValueError if the target cannot be found."""
    lines = original.splitlines(keepends=True)
    if line < 1 or line > len(lines):
        raise ValueError(f"line {line} is out of range ({path} has {len(lines)} lines)")
    target = lines[line - 1]
    if replace not in target:
        raise ValueError(f"{replace!r} not found on line {line} of {path}: {target!r}")
    lines[line - 1] = target.replace(replace, with_, 1)
    return "".join(lines)


def apply_mutation(path: Path, line: int, replace: str, with_: str) -> str:
    """Replace `replace` with `with_` once, on 1-indexed `line` of `path`. Returns the original
    file text and mutates the file in place. Raises ValueError if the target cannot be found."""
    original = path.read_text(encoding="utf-8")
    path.write_text(_mutated_text(original, path, line, replace, with_), encoding="utf-8")
    return original


def _write_mutated_file(path: Path, text: str) -> None:
    """The one call in `main` that actually mutates the target — always after the journal for
    that mutation has already been written and fsynced (see `write_journal`)."""
    path.write_text(text, encoding="utf-8")


def journal_path(repo_root: Path) -> Path:
    return repo_root / JOURNAL_RELPATH


def write_journal(repo_root: Path, target: Path, original_content: str) -> None:
    """Write the pre-mutation journal and fsync it before returning, so a crash immediately
    after this call still leaves the journal durable on disk — never after `target` is mutated
    (W2-03: call this before any write to `target`, never after)."""
    jp = journal_path(repo_root)
    jp.parent.mkdir(parents=True, exist_ok=True)
    payload = {
        "path": str(target.resolve()),
        "original_content": original_content,
        "sha256": hashlib.sha256(original_content.encode("utf-8")).hexdigest(),
        "written_at": datetime.now(timezone.utc).isoformat(),
    }
    with open(jp, "w", encoding="utf-8") as f:
        f.write(json.dumps(payload, indent=2))
        f.flush()
        os.fsync(f.fileno())


def read_journal(repo_root: Path) -> Optional[dict]:
    jp = journal_path(repo_root)
    if not jp.is_file():
        return None
    return json.loads(jp.read_text(encoding="utf-8"))


def restore_from_journal(journal: dict) -> None:
    Path(journal["path"]).write_text(journal["original_content"], encoding="utf-8")


GIT_LOCATION_ENV = ("GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE",
                    "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES",
                    "GIT_PREFIX", "GIT_NAMESPACE", "GIT_CEILING_DIRECTORIES")


def git_env(env=None) -> dict:
    """`env` (default `os.environ`) minus every variable that pins git to another repository."""
    out = dict(os.environ if env is None else env)
    for name in GIT_LOCATION_ENV:
        out.pop(name, None)
    return out


def delete_journal(repo_root: Path) -> None:
    jp = journal_path(repo_root)
    if jp.is_file():
        jp.unlink()


def find_repo_root(start: Path) -> Path:
    """The nearest git root above `start` (a file or a directory)."""
    cwd = start if start.is_dir() else start.parent
    proc = subprocess.run(["git", "rev-parse", "--show-toplevel"], cwd=cwd,
                           capture_output=True, text=True, check=False, env=git_env())
    if proc.returncode == 0 and proc.stdout.strip():
        return Path(proc.stdout.strip())
    raise RuntimeError(f"no git repository found above {start}")


def validate_target(repo_root: Path, target: Path) -> Optional[str]:
    """None when `target` is an existing file inside `repo_root` and outside `.git`."""
    resolved = target.resolve()
    root_resolved = repo_root.resolve()
    try:
        rel = resolved.relative_to(root_resolved)
    except ValueError:
        return f"{target} is outside the repo root {repo_root}"
    if rel.parts and rel.parts[0] == ".git":
        return f"{target} is under .git — refusing"
    if not resolved.is_file():
        return f"{target} does not exist"
    return None


def git_is_dirty(repo_root: Path, target: Path) -> bool:
    rel = str(target.resolve().relative_to(repo_root.resolve()))
    proc = subprocess.run(["git", "status", "--porcelain", "--", rel], cwd=repo_root,
                           capture_output=True, text=True, check=False, env=git_env())
    return bool(proc.stdout.strip())


def looks_like_build_break(result: subprocess.CompletedProcess) -> bool:
    combined = (result.stdout or "") + (result.stderr or "")
    return any(marker in combined for marker in BUILD_BREAK_MARKERS)


def run_command(cmd: str, cwd: Path) -> subprocess.CompletedProcess:
    return subprocess.run(cmd, shell=True, cwd=cwd, capture_output=True, text=True, check=False)


def _print_run(label: str, result: subprocess.CompletedProcess) -> None:
    print(f"--- {label} run (exit {result.returncode}) ---")
    if result.stdout:
        print(result.stdout.rstrip())
    if result.stderr:
        print(result.stderr.rstrip())


# --------------------------------------------------------------------------- CLI


def main(argv: Optional[List[str]] = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                  formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--file", required=True)
    ap.add_argument("--line", required=True, type=int)
    ap.add_argument("--replace", required=True)
    ap.add_argument("--with", dest="with_", required=True)
    ap.add_argument("--run", required=True, help="the scoped test command to run")
    ap.add_argument("--root", help="repo root (default: auto-detected from --file)")
    args = ap.parse_args(argv)

    target = Path(args.file)
    start = target if target.is_absolute() else Path.cwd() / target
    try:
        repo_root = Path(args.root).resolve() if args.root else find_repo_root(start)
    except RuntimeError as exc:
        print(f"ERROR: {exc}")
        return 4

    stale = read_journal(repo_root)
    if stale is not None:
        restore_from_journal(stale)
        print(f"RESTORED a stale journal at {journal_path(repo_root)} — a previous run of this "
              f"tool did not complete. {stale['path']} has been restored to its original "
              f"content. Delete the journal and re-run.")
        return 3

    resolved_target = start.resolve()
    err = validate_target(repo_root, resolved_target)
    if err:
        print(f"ERROR: {err}")
        return 4
    if git_is_dirty(repo_root, resolved_target):
        print(f"ERROR: {resolved_target} has uncommitted changes (git status --porcelain is "
              f"non-empty) — red_proof.py refuses to mutate a dirty target. Commit or stash "
              f"first.")
        return 4

    try:
        original = resolved_target.read_text(encoding="utf-8")
    except OSError as exc:
        print(f"ERROR: {exc}")
        return 4

    try:
        mutated_text = _mutated_text(original, resolved_target, args.line, args.replace, args.with_)
    except ValueError as exc:
        print(f"ERROR: {exc}")
        return 4

    # Journal first, mutation second (W2-03): if the journal cannot be written, the target is
    # never touched, so there is nothing to restore.
    try:
        write_journal(repo_root, resolved_target, original)
    except OSError as exc:
        print(f"ERROR: could not write the red-proof journal at {journal_path(repo_root)} — "
              f"refusing to mutate {resolved_target} without one: {exc}")
        return 4

    status = "reddened"
    try:
        _write_mutated_file(resolved_target, mutated_text)
        red = run_command(args.run, repo_root)
        _print_run("mutated", red)
        if red.returncode == 0:
            status = "stayed-green"
        elif looks_like_build_break(red):
            status = "build-break"
    finally:
        resolved_target.write_text(original, encoding="utf-8")

    if status == "stayed-green":
        print("STAYED GREEN: the mutation did not redden the check. This is the finding.")
        delete_journal(repo_root)
        return 1
    if status == "build-break":
        print("RED FOR THE WRONG REASON: the mutated run looks like a build break, not a "
              "check failure.")
        delete_journal(repo_root)
        return 2

    green = run_command(args.run, repo_root)
    _print_run("reverted", green)
    delete_journal(repo_root)
    if green.returncode == 0:
        print("WENT RED THEN GREEN.")
        return 0
    print("RED AFTER REVERT: the target did not return to green once the original content was "
          "restored. Investigate before trusting this proof.")
    return 2


if __name__ == "__main__":
    sys.exit(main())
