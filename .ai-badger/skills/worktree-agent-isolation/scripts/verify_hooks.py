#!/usr/bin/env python3
"""Gate for the two PreToolUse hooks in this directory: feeds each one hook payloads and
asserts the outcome. Lane liveness and worktree enumeration are stubbed, so it is
deterministic. Run `python3 verify_hooks.py` from here; non-zero exit means a defect.
"""
from __future__ import annotations

import importlib.util
import os
import subprocess
import sys
from datetime import datetime, timedelta, timezone
import tempfile
import time
from pathlib import Path
from typing import Any, Dict, List, Optional

HOOKS = Path(__file__).resolve().parent
# The command behind this guard: an agent told to "kill any build you have running" ran an
# unscoped pattern match and killed another lane's in-flight compile.
INCIDENT = "pkill -f build"

_failures: List[str] = []


def _load(name: str):
    spec = importlib.util.spec_from_file_location(name, HOOKS / f"{name}.py")
    if spec is None or spec.loader is None:
        raise RuntimeError(f"cannot load {name}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


guard = _load("blast_radius_kill_guard")
dirty = _load("cross_worktree_dirty_warning")


def probe(name: str, ok: bool, detail: str = "") -> None:
    print(f"{'PASS' if ok else 'FAIL'}  {name}{'' if ok else '  <- ' + detail}")
    if not ok:
        _failures.append(name)


# --------------------------------------------------------------------------- guard: parsing

ALLOW = [
    "grep -rn kill src/",
    "rg killall docs/",
    "git log --grep=pkill",
    "git commit -m 'guard against pkill'",
    "echo 'pkill is dangerous'",
    "# pkill -f build",
    "kill -s TERM 12345",
    "kill -- 12345",
    "kill %1",
    "kill 12345",
    "kill -9 12345",
    # One per STACK_HAZARDS row: the table must not swallow the ordinary command it sits next to.
    "dotnet build",
    "npm cache verify",
    "pip install -r requirements.txt",
    "cargo build --release",
    "rm -rf ./obj",
    "rm -rf ./node_modules",
    "ls -la",
]

DENY = [
    "killall5 -15",
    "bash -c 'kill $(pgrep -f build)'",
    "bash -lc 'pkill -f \"my build\"'",
    "sh -ec 'pkill node'",
    "/bin/bash -cx 'killall java'",
    "launchctl kickstart -k system/foo",
    INCIDENT,
    "killall python3",
    "/usr/bin/pkill -9 node",
    "sudo pkill dotnet",
    "FOO=1 pkill node",
    "ls && pkill node",
    "echo hi\npkill -f build",
    "kill $(pgrep -f build)",
    # One reap and one cache delete per STACK_HAZARDS row.
    "dotnet build-server shutdown",
    "rm -rf ~/.nuget/packages",
    "npm cache clean --force",
    "yarn cache clean",
    "pnpm store prune",
    "rm -rf ~/.npm",
    "pip cache purge",
    "uv cache clean",
    "rm -rf ~/.cache/pip",
    "sccache --stop-server",
    "rm -rf ~/.cargo/registry",
]


def check_classification() -> None:
    for command in ALLOW:
        reason = guard.find_hazard(command)
        probe(f"allow: {command!r}", reason is None, f"denied: {reason}")
    for command in DENY:
        reason = guard.find_hazard(command)
        probe(f"deny:  {command!r}", reason is not None, "allowed through")


def check_every_stack_has_a_probe() -> None:
    """Every row of STACK_HAZARDS must be exercised — a table nothing probes is decoration."""
    for stack in guard.STACK_HAZARDS:
        denied = [c for c in DENY if guard.find_hazard(c)
                  and stack in (guard.find_hazard(c) or "")]
        probe(f"table: {stack} has at least one DENY probe", bool(denied),
              "no DENY probe names this stack")


# ----------------------------------------------------------------------- guard: decisions

def _bash(command: str, session_id: str) -> Dict[str, Any]:
    return {"hook_event_name": "PreToolUse", "tool_name": "Bash",
            "tool_input": {"command": command}, "session_id": session_id}


def _denied(command: str, session_id: str) -> bool:
    decision = guard.decide(_bash(command, session_id))
    return decision.get("hookSpecificOutput", {}).get("permissionDecision") == "deny"


def check_lanes(marker_dir: Path) -> None:
    guard.MARKER_DIR = marker_dir
    original = guard._live_lane_pids  # noqa: SLF001 - stubbing the machine-state probe

    guard._live_lane_pids = lambda: [111, 222]  # noqa: SLF001
    probe("incident denied with 2 live lanes", _denied(INCIDENT, "s-lanes-2"))

    guard._live_lane_pids = lambda: [111]  # noqa: SLF001
    probe("incident allowed with 1 live lane", not _denied(INCIDENT, "s-lanes-1"))

    guard._live_lane_pids = lambda: []  # noqa: SLF001
    probe("incident allowed with 0 live lanes", not _denied(INCIDENT, "s-lanes-0"))

    guard._live_lane_pids = original  # noqa: SLF001
    guard.SOCK_DIR = marker_dir / "no-such-sock-dir"
    probe("incident allowed when the lane socket dir is absent",
          not _denied(INCIDENT, "s-nosocks"))
    guard.SOCK_DIR = Path("/tmp/cc-socks")


def check_valve(marker_dir: Path) -> None:
    guard.MARKER_DIR = marker_dir
    guard._live_lane_pids = lambda: [111, 222]  # noqa: SLF001

    outcomes = [_denied(INCIDENT, "s-valve") for _ in range(4)]
    probe("valve: same command denied 3x then allowed on the 4th",
          outcomes == [True, True, True, False], f"outcomes={outcomes}")

    harmless = ["grep -rn kill src/", "git log --grep=pkill",
                "git commit -m 'guard against pkill'"]
    before = [_denied(c, "s-harmless") for c in harmless]
    probe("valve: three harmless commands are never denied at all",
          before == [False, False, False], f"outcomes={before}")
    probe("valve: the incident is still denied after three harmless calls",
          _denied(INCIDENT, "s-harmless"))

    others = ["pkill alpha", "killall beta", "launchctl kickstart -k system/x"]
    denied_others = [_denied(c, "s-mixed") for c in others]
    probe("valve: three DIFFERENT hazards each denied",
          denied_others == [True, True, True], f"outcomes={denied_others}")
    probe("valve: the incident is still denied after three other hazards were denied",
          _denied(INCIDENT, "s-mixed"))


def check_pruning() -> None:
    """Row-level successor of the file-mtime prune: a stale row is dropped on write, the
    current (session, command) row survives it."""
    guard._live_lane_pids = lambda: [111, 222]  # noqa: SLF001
    store = guard.badger_store.open_user()
    stale_key = "ancient-session.deadbeef"
    old = datetime.now(timezone.utc) - timedelta(seconds=2 * guard.MARKER_TTL_SECONDS)
    store.conn.execute(
        "INSERT OR REPLACE INTO blast_radius_denials(key, denials, updated_at) "
        "VALUES (?, ?, ?)", (stale_key, 1, old.isoformat()))
    store.conn.commit()
    store.close()
    _denied(INCIDENT, "s-prune")
    store = guard.badger_store.open_user()
    try:
        pruned = store.conn.execute(
            "SELECT count(*) FROM blast_radius_denials WHERE key = ?",
            (stale_key,)).fetchone()[0]
        fresh_key = guard._denials_key("s-prune", INCIDENT)  # noqa: SLF001
        fresh = store.conn.execute(
            "SELECT count(*) FROM blast_radius_denials WHERE key = ?",
            (fresh_key,)).fetchone()[0]
    finally:
        store.close()
    probe("markers: a counter older than a day is pruned on write", pruned == 0,
          f"stale rows left: {pruned}")
    probe("markers: this call's counter survives the prune", fresh == 1,
          f"fresh rows: {fresh}")


# ------------------------------------------------------------------- dirty: fake worktrees

class FakeRepo:
    """A two-worktree repo on disk plus a stub `_run_git` that reports both as dirty."""

    def __init__(self, root: Path) -> None:
        self.main = root / "repo"
        self.lane = self.main / "wt" / "a"
        for path in (self.main, self.lane):
            (path / "src").mkdir(parents=True, exist_ok=True)
            (path / "src" / "shared.txt").write_text("-\n", encoding="utf-8")
        self.branches = {str(self.main): "main", str(self.lane): "lane-a"}
        self.calls: List[str] = []

    def listing(self) -> str:
        return "".join(
            f"worktree {path}\nHEAD 0\nbranch refs/heads/{branch}\n\n"
            for path, branch in self.branches.items())

    def toplevel_for(self, cwd: str) -> Optional[str]:
        matches = [p for p in self.branches if cwd == p or cwd.startswith(p + os.sep)]
        return max(matches, key=len) if matches else None

    def run_git(self, args: List[str], cwd: str, timeout: float) -> Optional[str]:
        self.calls.append(" ".join(args))
        if timeout <= 0:
            return None
        if args[0] == "rev-parse" and "--git-common-dir" in args:
            return f"{self.main}/.git\n"
        if args[0] == "rev-parse" and "--show-toplevel" in args:
            top = self.toplevel_for(cwd)
            return f"{top}\n" if top else None
        if args[:2] == ["worktree", "list"]:
            return self.listing()
        if args[:2] == ["status", "--porcelain"]:
            return " M src/shared.txt\n" if cwd in self.branches else None
        return None

    def sweeps(self) -> int:
        return sum(1 for c in self.calls if c.startswith("worktree list"))


def _edit(worktree: Path) -> Dict[str, Any]:
    return {"hook_event_name": "PreToolUse", "tool_name": "Edit",
            "tool_input": {"file_path": str(worktree / "src" / "shared.txt")},
            "cwd": str(worktree)}


def check_dirty_cache(root: Path) -> None:
    repo = FakeRepo(root)
    dirty._run_git = repo.run_git  # noqa: SLF001

    writer = dirty.check(_edit(repo.lane))
    probe("dirty: writer is told about the OTHER worktree",
          writer is not None and f"{repo.main} (branch main)" in writer, f"got {writer!r}")
    probe("dirty: writer is not warned about itself",
          writer is not None and "(branch lane-a)" not in writer, f"got {writer!r}")

    sweeps_after_writer = repo.sweeps()
    reader = dirty.check(_edit(repo.main))
    probe("dirty: reader sees the writer's dirty file through the shared cache",
          reader is not None and f"{repo.lane} (branch lane-a)" in reader, f"got {reader!r}")
    probe("dirty: reader is not warned about itself",
          reader is not None and f"{repo.main} (branch main)" not in reader, f"got {reader!r}")
    probe("dirty: the second call reused the cache instead of re-sweeping",
          repo.sweeps() == sweeps_after_writer == 1, f"sweeps={repo.calls}")


def check_dirty_budget(root: Path) -> None:
    repo = FakeRepo(root)
    dirty._run_git = repo.run_git  # noqa: SLF001
    original = dirty._SWEEP_BUDGET_SECONDS  # noqa: SLF001
    dirty._SWEEP_BUDGET_SECONDS = 0  # noqa: SLF001
    try:
        started = time.monotonic()
        result = dirty._sweep(repo.main)  # noqa: SLF001
        elapsed = time.monotonic() - started
        probe("dirty: an exhausted time budget returns immediately",
              result == {} and elapsed < 1.0, f"result={result} elapsed={elapsed:.2f}s")
    finally:
        dirty._SWEEP_BUDGET_SECONDS = original  # noqa: SLF001


def check_dirty_cache_shape(root: Path) -> None:
    """Row-level successor of the old file-shape probes: a corrupt or wrong-shaped
    sweep row must be rejected (fresh sweep wins), a well-shaped fresh row served."""
    dirty._run_git = lambda args, cwd, timeout: None  # noqa: SLF001 - a fresh sweep is {}
    store = dirty.badger_store.open_user()
    repo_root = root / "irrelevant-repo-root"
    key = dirty._sweep_key(repo_root)  # noqa: SLF001
    try:
        for label, content in (
                ("a bare string", '"injected text"'),
                ("a non-string branch", '{"src/shared.txt": [{"worktree": "x", "branch": 1}]}'),
                ("a non-list value", '{"src/shared.txt": "injected text"}'),
                ("broken json", "{not json"),
        ):
            store.conn.execute(
                "INSERT OR REPLACE INTO dirty_sweeps(key, value, updated_at) VALUES (?, ?, ?)",
                (key, content, datetime.now(timezone.utc).isoformat()))
            store.conn.commit()
            probe(f"dirty: cache row holding {label} is rejected",
                  dirty._sweep_cached(repo_root) == {}, "the row leaked into the result")
        shaped = {"src/shared.txt": [{"worktree": "x", "branch": "y"}]}
        store.kv_set("dirty_sweeps", key, shaped)
        probe("dirty: a fresh, well-shaped cache row is served",
              dirty._sweep_cached(repo_root) == shaped, "the row was ignored")
    finally:
        store.close()


# ------------------------------------------------------------------------------ fail-open

def check_lexer_fail_open() -> None:
    """Unparseable, deeply nested and oversized commands must ALLOW, never raise."""
    # A command that does not lex yields no tokens, so nothing is classified: it must allow.
    probe("lexer fail-open: unlexable command allows",
          guard.find_hazard("echo 'unterminated") is None)
    # Adversarial input must never raise — the verdict may be either way, but a raise blocks
    # the call.
    for command in ("pkill 'unterminated", "kill $(", "pkill \x00 x", "kill \\"):
        try:
            guard.find_hazard(command)
            probe(f"lexer does not raise on {command!r}", True)
        except Exception as exc:  # pylint: disable=broad-exception-caught
            probe(f"lexer does not raise on {command!r}", False, f"raised {exc!r}")
    nested = "bash -c '" * 10 + "pkill x" + "'" * 10
    try:
        guard.find_hazard(nested)
        probe("lexer depth cap: 10-deep nesting does not raise", True)
    except Exception as exc:  # pylint: disable=broad-exception-caught
        probe("lexer depth cap: 10-deep nesting does not raise", False, f"raised {exc!r}")
    try:
        big = "echo " + ("a" * 200_000)
        probe("lexer size cap: oversized command allows", guard.find_hazard(big) is None)
    except Exception as exc:  # pylint: disable=broad-exception-caught
        probe("lexer size cap: oversized command allows", False, f"raised {exc!r}")


def check_fail_open() -> None:
    cases = [
        ("blast_radius_kill_guard", "not json at all"),
        ("blast_radius_kill_guard", "{}"),
        ("blast_radius_kill_guard", '{"tool_name": "Bash"}'),
        ("blast_radius_kill_guard", '{"tool_name": "Bash", "tool_input": {}}'),
        ("blast_radius_kill_guard", "[1, 2, 3]"),
        ("cross_worktree_dirty_warning", "not json at all"),
        ("cross_worktree_dirty_warning", "{}"),
        ("cross_worktree_dirty_warning", '{"tool_name": "Edit"}'),
        ("cross_worktree_dirty_warning", "[1, 2, 3]"),
    ]
    for script, payload in cases:
        result = subprocess.run(
            [sys.executable, str(HOOKS / f"{script}.py")], input=payload,
            capture_output=True, text=True, timeout=30, check=False)
        ok = result.returncode == 0 and result.stdout.strip() == ""
        probe(f"fail-open: {script} on {payload!r}", ok,
              f"rc={result.returncode} stdout={result.stdout!r}")


# ----------------------------------------------------------------------------------- main

def main() -> int:
    with tempfile.TemporaryDirectory(prefix="verify-hooks-") as tmp:
        root = Path(tmp)
        # Every store-backed surface (dispatch denials, sweep cache) lands in the temp
        # user root: the probes must stay hermetic, not write the developer's own DB.
        os.environ[dirty.badger_store.USER_ROOT_ENV] = str(root / "user-root")
        check_classification()
        check_every_stack_has_a_probe()
        check_lanes(root / "markers")
        check_valve(root / "markers")
        check_pruning()
        check_dirty_cache(root / "case-a")
        check_dirty_budget(root / "case-b")
        check_dirty_cache_shape(root / "user-root")
        check_lexer_fail_open()
        check_fail_open()

    total = len(_failures)
    print(f"\n{total} failure(s)" if total else "\nall probes passed")
    return 1 if total else 0


if __name__ == "__main__":
    sys.exit(main())
