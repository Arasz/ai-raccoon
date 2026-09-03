#!/usr/bin/env python3
"""SessionStart hook: warn when this repo's git config has lost its remote plumbing.

A truncated `.git/config` can drop `remote.origin.fetch` while leaving the remote itself
intact: `git fetch origin` then reports success while `refs/remotes/origin/*` silently stops
moving (see docs/changelog -- measured, not theoretical). A separate, common second symptom is
`branch.<name>.merge` going missing, which breaks `git pull` outright. Both are cheap to detect
and cheap to repair, so this fires once per session rather than waiting for the confusing
downstream symptom to be debugged from scratch.

Ships standalone (no framework import, matching cross_worktree_dirty_warning.py in
worktree-agent-isolation/scripts/): GIT_LOCATION_ENV/git_env are repeated here rather than
imported from badger_lib, because this file ships into projects with no framework checkout to
import from. tests/test_git_invocation.py pins every standalone copy against the original.

Output convention copied from drift_notice_hook.py: read the SessionStart stdin payload, and
print `hookSpecificOutput.additionalContext` on stdout only when there is something to say.
Fails open on every error -- never breaks a session, never prints unconditionally. Exit code is
always 0.

The cwd resolution is deliberately NOT copied from drift_notice_hook.py: that hook wants the
project-level scaffold root, where CLAUDE_PROJECT_DIR is authoritative, but CLAUDE_PROJECT_DIR
names the MAIN checkout even inside a linked worktree session -- exactly the case this hook
must get right (see B4/H7 below). Matches the payload-cwd-only precedence in
worktree-agent-isolation/scripts/cross_worktree_dirty_warning.py instead.
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
from typing import Any, Dict, List, Optional

# Repeated from badger_lib.GIT_LOCATION_ENV -- see the module docstring above.
GIT_LOCATION_ENV = ("GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE",
                    "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES",
                    "GIT_PREFIX", "GIT_NAMESPACE", "GIT_CEILING_DIRECTORIES")

_GIT_TIMEOUT_SECONDS = 3

_FETCH_REPAIR = ("git config remote.origin.fetch '+refs/heads/*:refs/remotes/origin/*' && "
                 "git fetch origin")


def git_env(env: Optional[Dict[str, str]] = None) -> Dict[str, str]:
    """`env` (default `os.environ`) minus every variable that pins git to another repository."""
    out = dict(os.environ if env is None else env)
    for name in GIT_LOCATION_ENV:
        out.pop(name, None)
    return out


def _run_git(args: List[str], cwd: str) -> Optional[str]:
    """stdout of a git invocation, or None on any failure (missing git, timeout, non-repo)."""
    try:
        result = subprocess.run(
            ["git", *args], cwd=cwd, capture_output=True, text=True,
            timeout=_GIT_TIMEOUT_SECONDS, env=git_env(), check=False,
        )
    except (OSError, subprocess.SubprocessError):
        return None
    return result.stdout.strip() if result.returncode == 0 else None


def _repo_dir(cwd: str) -> Optional[str]:
    """The repo's shared `.git` directory, or None outside a repo (or with no git binary) --
    the gate that keeps every check below silent rather than wrong in that case."""
    return _run_git(["rev-parse", "--path-format=absolute", "--git-common-dir"], cwd)


def _config_get(repo_dir: str, key: str) -> Optional[str]:
    """A single config value via `git config`, never by parsing the config file ourselves."""
    return _run_git(["config", "--local", "--get", key], repo_dir)


def _current_branch(cwd: str) -> Optional[str]:
    """The checked-out branch at *cwd*, or None on detached HEAD (symbolic-ref fails) -- the
    caller skips the branch.<name>.merge check rather than crashing on a missing branch."""
    return _run_git(["symbolic-ref", "--short", "-q", "HEAD"], cwd)


def _remote_branch_exists(repo_dir: str, branch: str) -> bool:
    """True when refs/remotes/origin/<branch> exists -- the same probe the repair playbook's
    multi-branch loop uses. A branch with no remote counterpart has never been pushed, and
    its missing branch.<name>.merge is the normal state, not lost plumbing."""
    return _run_git(["rev-parse", "--verify", "-q", f"refs/remotes/origin/{branch}"],
                    repo_dir) is not None


def config_health_notice(cwd: str) -> Optional[str]:
    """One combined warning for a lost `remote.origin.fetch` and/or `branch.<name>.merge`, or
    None when the repo is healthy, has no remote at all, or *cwd* is not a git repo."""
    repo_dir = _repo_dir(cwd)
    if repo_dir is None:
        return None
    origin_url = _config_get(repo_dir, "remote.origin.url")
    if not origin_url:
        return None  # no remote at all -- a fresh local repo is not broken

    findings = []
    if not _config_get(repo_dir, "remote.origin.fetch"):
        findings.append(
            "remote.origin.fetch is unset: `git fetch origin` will report success while "
            f"refs/remotes/origin/* stays frozen. Repair: {_FETCH_REPAIR}"
        )

    branch = _current_branch(cwd)  # cwd, not repo_dir: HEAD is per-worktree, unlike config
    if branch and not _config_get(repo_dir, f"branch.{branch}.merge") \
            and _remote_branch_exists(repo_dir, branch):
        # Warn only when origin/<branch> exists: on an unpushed branch (`git switch -c`,
        # `git worktree add -b`) the missing tracking section is normal repo state, and the
        # old prescription failed anyway -- `--set-upstream-to=origin/<branch>` exits 128
        # when the remote branch does not exist.
        findings.append(
            f"branch.{branch}.merge is unset: `git pull` has no upstream. Repair: "
            f"git branch --set-upstream-to=origin/{branch}"
        )

    if not findings:
        return None
    return "git config health: " + " | ".join(findings)


def main() -> int:
    try:
        payload = json.load(sys.stdin)
    except (json.JSONDecodeError, OSError):
        return 0
    if not isinstance(payload, dict):
        return 0
    # payload["cwd"] only -- CLAUDE_PROJECT_DIR names the MAIN checkout even inside a
    # linked worktree session, which is exactly the case _current_branch below must get
    # right; os.getcwd() is a strictly better last resort than a value known wrong here.
    cwd = str(payload.get("cwd") or os.getcwd())
    try:
        notice = config_health_notice(str(cwd))
    except Exception:  # pylint: disable=broad-exception-caught
        return 0
    if notice:
        print(json.dumps({
            "hookSpecificOutput": {
                "hookEventName": "SessionStart",
                "additionalContext": notice,
            },
        }))
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception:  # pylint: disable=broad-exception-caught
        sys.exit(0)
