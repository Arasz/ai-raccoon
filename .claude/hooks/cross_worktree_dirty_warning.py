#!/usr/bin/env python3
"""PreToolUse hook: warn (never block) when the file an edit targets is also dirty in another
worktree of this repo. The worktree sweep is cached unfiltered and shared across sessions;
the calling worktree is excluded at read time. Fails open on every error.
"""
from __future__ import annotations

import hashlib
import json
import os
import subprocess
import sys
import tempfile
import time
from pathlib import Path
from typing import Any, Dict, List, Optional

# Same rationale as memory_first_gate.py: never let an inherited GIT_* var point our git
# calls at a different repository than the one the hook was invoked for.
_GIT_LOCATION_ENV = ("GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE",
                     "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES",
                     "GIT_PREFIX", "GIT_NAMESPACE", "GIT_CEILING_DIRECTORIES")

_GIT_TIMEOUT_SECONDS = 3
_SWEEP_BUDGET_SECONDS = 6
_CACHE_TTL_SECONDS = 10
_EDIT_TOOLS = ("Edit", "Write", "MultiEdit", "NotebookEdit")


def _cache_root() -> Optional[Path]:
    """The cache directory, or None when this process has no home directory. Deliberately
    not the world-writable temp dir: the cached text is interpolated into a model's context."""
    try:
        return Path.home() / ".ai-badger" / "dirty-sweep"
    except (RuntimeError, OSError):
        return None


CACHE_DIR: Optional[Path] = _cache_root()


def _git_env() -> Dict[str, str]:
    out = dict(os.environ)
    for name in _GIT_LOCATION_ENV:
        out.pop(name, None)
    return out


def _run_git(args: List[str], cwd: str, timeout: float) -> Optional[str]:
    """stdout of a git invocation, or None on any failure (missing git, timeout, non-repo)."""
    if timeout <= 0:
        return None
    try:
        result = subprocess.run(
            ["git", *args], cwd=cwd, capture_output=True, text=True,
            timeout=timeout, env=_git_env(), check=False,
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


def _cache_path(repo_root: Path) -> Optional[Path]:
    if CACHE_DIR is None:
        return None
    key = hashlib.sha1(str(repo_root).encode("utf-8")).hexdigest()[:16]
    return CACHE_DIR / f"dirty-sweep-{key}.json"


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
            current["branch"] = ref.removeprefix("refs/heads/")
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


def _read_cache(cache_file: Path) -> Optional[Dict[str, List[Dict[str, str]]]]:
    try:
        if time.time() - cache_file.stat().st_mtime > _CACHE_TTL_SECONDS:
            return None
        data = json.loads(cache_file.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None
    return data if _valid_sweep(data) else None


def _write_cache(cache_file: Path, data: Dict[str, List[Dict[str, str]]]) -> None:
    """Atomic replace so a concurrent reader never sees a half-written sweep."""
    try:
        cache_file.parent.mkdir(parents=True, exist_ok=True)
        handle, temp_name = tempfile.mkstemp(dir=str(cache_file.parent), suffix=".tmp")
        try:
            with os.fdopen(handle, "w", encoding="utf-8") as stream:
                json.dump(data, stream)
            os.replace(temp_name, cache_file)
        except OSError:
            os.unlink(temp_name)
    except OSError:
        pass  # a cache write failure only costs the next call a fresh sweep


def _sweep_cached(repo_root: Path) -> Dict[str, List[Dict[str, str]]]:
    cache_file = _cache_path(repo_root)
    if cache_file is None:
        return _sweep(repo_root)
    cached = _read_cache(cache_file)
    if cached is not None:
        return cached
    data = _sweep(repo_root)
    _write_cache(cache_file, data)
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
