#!/usr/bin/env python3
"""PreToolUse hook: warn (never block) when the file an edit targets is also dirty in another
worktree of this repo. The worktree sweep is cached unfiltered and shared across sessions;
the calling worktree is excluded at read time. Fails open on every error.
Verified by verify_hooks.py in this directory.
"""
from __future__ import annotations

import hashlib
import importlib.util
import json
import os
import sqlite3
import subprocess
import sys
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, List, Optional

# badger_lib.GIT_LOCATION_ENV, repeated because this ships into projects that have no framework
# checkout to import it from: never let an inherited GIT_* var point our git calls at a different
# repository. tests/test_git_invocation.py pins every copy against the original.
GIT_LOCATION_ENV = ("GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE",
                    "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES",
                    "GIT_PREFIX", "GIT_NAMESPACE", "GIT_CEILING_DIRECTORIES")

_GIT_TIMEOUT_SECONDS = 3
_SWEEP_BUDGET_SECONDS = 6
_CACHE_TTL_SECONDS = 10
_EDIT_TOOLS = ("Edit", "Write", "MultiEdit", "NotebookEdit")


def _load_badger_store():
    """The store module: already-imported, importable, or the vendored copy beside this script."""
    if "badger_store" in sys.modules:
        return sys.modules["badger_store"]
    try:
        import badger_store  # pylint: disable=import-outside-toplevel,redefined-outer-name
        return badger_store
    except ImportError:
        pass
    try:
        path = Path(__file__).resolve().parent / "badger_store.py"
        spec = importlib.util.spec_from_file_location("badger_store", path)
        module = importlib.util.module_from_spec(spec)
        sys.modules["badger_store"] = module
        spec.loader.exec_module(module)
        return module
    except (OSError, ValueError):
        return None


badger_store = _load_badger_store()


def _sweep_key(repo_root: Path) -> str:
    """The sweep's store key: sha1 of the MAIN checkout root, 16 hex chars — the legacy
    cache filename's hash verbatim (D4), so every worktree of a repo shares one row."""
    return hashlib.sha1(str(repo_root).encode("utf-8")).hexdigest()[:16]


def git_env(env: Optional[Dict[str, str]] = None) -> Dict[str, str]:
    """`env` (default `os.environ`) minus every variable that pins git to another repository."""
    out = dict(os.environ if env is None else env)
    for name in GIT_LOCATION_ENV:
        out.pop(name, None)
    return out


def _run_git(args: List[str], cwd: str, timeout: float) -> Optional[str]:
    """stdout of a git invocation, or None on any failure (missing git, timeout, non-repo)."""
    if timeout <= 0:
        return None
    try:
        result = subprocess.run(
            ["git", *args], cwd=cwd, capture_output=True, text=True,
            timeout=timeout, env=git_env(), check=False,
        )
    except (OSError, subprocess.SubprocessError):
        return None
    return result.stdout if result.returncode == 0 else None


def _edited_path(payload: Dict[str, Any]) -> Optional[str]:
    """The file an edit-shaped tool call targets, mirroring generated_file_guard.py."""
    if not isinstance(payload, dict):
        return None
    if (payload.get("tool_name") or payload.get("toolName")) not in _EDIT_TOOLS:
        return None
    tool_input = payload.get("tool_input") or payload.get("toolInput") or {}
    if not isinstance(tool_input, dict):
        return None
    path = tool_input.get("file_path") or tool_input.get("notebook_path")
    return path if isinstance(path, str) and path.strip() else None


def _main_repo_root(cwd: str) -> Optional[Path]:
    """The main checkout's root, whether *cwd* is itself the main checkout or a linked
    worktree — `--git-common-dir` always points at the shared `.git`."""
    common_dir = _run_git(["rev-parse", "--path-format=absolute", "--git-common-dir"],
                          cwd, _GIT_TIMEOUT_SECONDS)
    if not common_dir:
        return None
    return Path(common_dir.strip()).parent


def _toplevel(cwd: str) -> Optional[Path]:
    top = _run_git(["rev-parse", "--show-toplevel"], cwd, _GIT_TIMEOUT_SECONDS)
    return Path(top.strip()) if top else None


def _parse_worktree_porcelain(output: str) -> List[Dict[str, str]]:
    """`git worktree list --porcelain` output -> [{"path": ..., "branch": ...}, ...]."""
    worktrees: List[Dict[str, str]] = []
    current: Dict[str, str] = {}
    for line in output.splitlines():
        if not line.strip():
            if current.get("path"):
                worktrees.append(current)
            current = {}
            continue
        if line.startswith("worktree "):
            current["path"] = line[len("worktree "):].strip()
        elif line.startswith("branch "):
            ref = line[len("branch "):].strip()
            head = "refs/heads/"
            current["branch"] = ref[len(head):] if ref.startswith(head) else ref
        elif line == "detached":
            current.setdefault("branch", "(detached)")
        elif line == "bare":
            current.setdefault("branch", "(bare)")
    if current.get("path"):
        worktrees.append(current)
    return worktrees


def _parse_status_paths(output: str) -> List[str]:
    """`git status --porcelain` output -> repo-relative paths it reports as dirty."""
    paths: List[str] = []
    for line in output.splitlines():
        if len(line) < 4:
            continue
        rest = line[3:]
        if " -> " in rest:
            rest = rest.split(" -> ", 1)[1]
        rest = rest.strip()
        if len(rest) >= 2 and rest[0] == '"' and rest[-1] == '"':
            rest = rest[1:-1]
        if rest:
            paths.append(rest)
    return paths


def _sweep(repo_root: Path) -> Dict[str, List[Dict[str, str]]]:
    """path -> [{"worktree": ..., "branch": ...}] across EVERY worktree, caller included, so
    the result is caller-independent and safe to share. Partial once the budget is spent."""
    deadline = time.monotonic() + _SWEEP_BUDGET_SECONDS
    if deadline - time.monotonic() <= 0:
        return {}
    listing = _run_git(["worktree", "list", "--porcelain"], str(repo_root),
                       min(_GIT_TIMEOUT_SECONDS, deadline - time.monotonic()))
    if not listing:
        return {}
    data: Dict[str, List[Dict[str, str]]] = {}
    for wt in _parse_worktree_porcelain(listing):
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            break  # budget spent — a partial sweep beats blowing the hook timeout
        wt_path = Path(wt["path"])
        if not wt_path.is_dir():
            continue  # prunable/stale entry — git worktree list can carry these
        status = _run_git(["status", "--porcelain"], str(wt_path),
                          min(_GIT_TIMEOUT_SECONDS, remaining))
        if status is None:
            continue
        for rel in _parse_status_paths(status):
            data.setdefault(rel, []).append(
                {"worktree": str(wt_path), "branch": wt.get("branch", "?")})
    return data


def _valid_sweep(data: Any) -> bool:
    """True only for the exact {path: [{"worktree": str, "branch": str}]} shape we write."""
    if not isinstance(data, dict):
        return False
    for key, hits in data.items():
        if not isinstance(key, str) or not isinstance(hits, list):
            return False
        for hit in hits:
            if not isinstance(hit, dict):
                return False
            if not isinstance(hit.get("worktree"), str) or not isinstance(hit.get("branch"), str):
                return False
    return True


def _sweep_cached(repo_root: Path) -> Dict[str, List[Dict[str, str]]]:
    """The shared sweep: the store row when fresh, else a fresh sweep written back. The
    row is keyed on the main repo root (D4); a miss or an unreadable row only costs a
    re-sweep, never a wrong answer."""
    key = _sweep_key(repo_root)
    if badger_store is None:
        return _sweep(repo_root)
    try:
        store = badger_store.open_user()
        if store is None:
            return _sweep(repo_root)
        try:
            row = store.conn.execute(
                "SELECT value, updated_at FROM dirty_sweeps WHERE key = ?", (key,)).fetchone()
            if row is not None:
                age = datetime.now(timezone.utc).timestamp() - \
                    datetime.fromisoformat(str(row[1])).timestamp()
                data = json.loads(row[0])
                if 0 <= age <= _CACHE_TTL_SECONDS and _valid_sweep(data):
                    return data
        finally:
            store.close()
    except (OSError, sqlite3.Error, ValueError, TypeError):
        pass  # a cache read failure only costs a fresh sweep
    data = _sweep(repo_root)
    try:
        store = badger_store.open_user()
        if store is not None:
            try:
                store.kv_set("dirty_sweeps", key, data)
            finally:
                store.close()
    except (OSError, sqlite3.Error, ValueError):
        pass
    return data


def _elsewhere(hits: List[Dict[str, str]], mine: Path) -> List[Dict[str, str]]:
    """The hits belonging to worktrees other than *mine* — self-exclusion at read time."""
    try:
        resolved = mine.resolve()
    except OSError:
        return hits
    out: List[Dict[str, str]] = []
    for hit in hits:
        try:
            if Path(hit["worktree"]).resolve() == resolved:
                continue
        except OSError:
            pass
        out.append(hit)
    return out


def check(payload: Dict[str, Any]) -> Optional[str]:
    """The warning message for this call, or None when nothing else has this file dirty."""
    path = _edited_path(payload)
    if path is None:
        return None
    cwd = str(payload.get("cwd") or os.getcwd())
    target = Path(path)
    if not target.is_absolute():
        target = Path(cwd) / target

    toplevel = _toplevel(str(target.parent) if target.parent.exists() else cwd)
    if toplevel is None:
        return None
    repo_root = _main_repo_root(str(toplevel))
    if repo_root is None:
        return None
    try:
        rel = target.resolve().relative_to(toplevel.resolve()).as_posix()
    except (OSError, ValueError):
        return None

    hits = _elsewhere(_sweep_cached(repo_root).get(rel, []), toplevel)
    if not hits:
        return None
    named = "; ".join(f"{h['worktree']} (branch {h['branch']})" for h in hits)
    return (f"cross-worktree dirty-file warning: {rel} is also modified in another "
            f"worktree — {named}. Another session may be editing this file too.")


def main() -> int:
    try:
        payload = json.load(sys.stdin)
    except (json.JSONDecodeError, OSError):
        return 0
    if not isinstance(payload, dict):
        return 0
    try:
        message = check(payload)
    except Exception:  # pylint: disable=broad-exception-caught
        return 0
    if message:
        print(json.dumps({"systemMessage": message}))
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception:  # pylint: disable=broad-exception-caught
        sys.exit(0)
