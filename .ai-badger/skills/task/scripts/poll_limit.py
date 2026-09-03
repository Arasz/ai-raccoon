#!/usr/bin/env python3
"""Background usage-limit poller for the /task skill.

Starts as a daemon-friendly foreground process. It watches Claude availability;
when a previous limited state becomes available again, it resumes active /task
sessions discovered from task tracking data, falling back to Claude's user-level
transcript store (~/.claude/projects) when tracking is not yet populated.

Passing --auto-wm-on-reset additionally runs `/auto-wm away 4h` on that transition.
It is off by default: nothing may hand tool approval to the agent unattended.
"""
# pylint: disable=missing-function-docstring,missing-class-docstring,broad-exception-caught
# Ported verbatim from the originating job-search-ai-assistant repo's /task skill: kept in
# lockstep with that source rather than churned for local docstring/style rules.

from __future__ import annotations

import argparse
import json
import math
import os
import shutil
import subprocess
import sys
import time
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path

SCRIPT_DIR = Path(__file__).resolve().parent
sys.path.insert(0, str(SCRIPT_DIR))
import tracker_lib as lib

_CLAUDE_FALLBACKS = (Path.home() / ".local/bin/claude", Path("/usr/local/bin/claude"))
CLAUDE_BIN = shutil.which("claude") or next(
    (str(p) for p in _CLAUDE_FALLBACKS if p.is_file() and (p.stat().st_mode & 0o111)), "claude"
)

PROJECT_ROOT = lib.PROJECT_ROOT

# Share tracker_lib's own computed data dir rather than rebuilding it with a ".claude" literal:
# tracker_lib always writes task tracking under ".ai-badger/task-tracking/", so a hand-built
# ".claude/task-tracking/" here would silently point at a directory nothing else ever writes to.
LOG_FILE = lib.DATA_DIR / "poll_limit.log"
PID_FILE = lib.DATA_DIR / "poll_limit.pid"
DEFAULT_AVAILABLE_INTERVAL_SECONDS = 300
STATUSLINE_FRESH_SECONDS = 180
LIMIT_WAIT_SCHEDULE_SECONDS = [7200, 1800, 900, 300]
DEFAULT_RESUME_DELAY_SECONDS = 120
PROBE_MODEL = "claude-haiku-4-5-20251001"

# A watch with no stopping condition is a loop nobody can answer "what ends this?" for, and it
# outlives the session that started it — the rule owner-gate-review's SKILL.md already states.
# This poller had neither bound; ten instances were once found alive at once, six of them owned
# by worktrees that had been deleted. DEFAULT is generous because a usage limit can take hours
# to reset; MAX is the hard ceiling auto-wm already uses, so no caller can restore the old loop.
DEFAULT_MAX_HOURS = 12
MAX_MAX_HOURS = 12
IDLE_POLLS_BEFORE_EXIT = 3


@dataclass(frozen=True)
class TargetSession:
    session_id: str
    task_id: str = ""
    transcript_path: str = ""
    source: str = ""


@dataclass
class PollState:
    was_limited: bool | None = None
    limited_checks: int = 0
    # Off unless --auto-wm-on-reset was passed: a limit reset must never turn
    # auto-approval on with no human in the loop (F-12).
    auto_wm_on_reset: bool = False


def log(message: str) -> None:
    ts = datetime.now(timezone.utc).isoformat()
    msg = f"{ts} {message}"
    print(msg, flush=True)
    try:
        LOG_FILE.parent.mkdir(parents=True, exist_ok=True)
        with open(LOG_FILE, "a", encoding="utf-8") as fh:
            fh.write(msg + "\n")
    except Exception:
        pass


def _read_json(path: Path, default):
    try:
        return json.loads(path.read_text())
    except (OSError, json.JSONDecodeError):
        return default


def _pid_alive(pid: int) -> bool:
    try:
        os.kill(pid, 0)
        return True
    except OSError:
        return False


def already_running(pid_file: Path | None = None) -> bool:
    pid_file = pid_file or PID_FILE
    try:
        pid = int(pid_file.read_text().strip())
    except (OSError, ValueError):
        return False
    return pid != os.getpid() and _pid_alive(pid)


def write_pid(pid_file: Path | None = None) -> None:
    pid_file = pid_file or PID_FILE
    pid_file.parent.mkdir(parents=True, exist_ok=True)
    pid_file.write_text(str(os.getpid()))


def _parse_iso_epoch(value: str) -> float | None:
    try:
        return datetime.fromisoformat(value).timestamp()
    except (TypeError, ValueError):
        return None


def statusline_state_age_seconds(state: dict) -> float | None:
    captured_at = state.get("capturedAt")
    captured_epoch = _parse_iso_epoch(captured_at)
    if captured_epoch is None:
        return None
    return time.time() - captured_epoch


def check_limit_from_statusline() -> tuple[bool, str] | None:
    """Use captured statusLine rate-limit metadata only when the capture is fresh."""
    state = lib.load_statusline_state()
    age_seconds = statusline_state_age_seconds(state)
    if age_seconds is None or age_seconds > STATUSLINE_FRESH_SECONDS:
        return None
    five_hour = (state.get("rateLimits") or {}).get("five_hour") or {}
    resets_at = five_hour.get("resets_at")
    used_percentage = five_hour.get("used_percentage")
    if resets_at is None:
        return None
    try:
        reset_epoch = float(resets_at)
    except (TypeError, ValueError):
        return None
    now_epoch = time.time()
    if reset_epoch <= now_epoch:
        return False, "statusline: fresh capture, five_hour reset time passed"
    wait_seconds = int(reset_epoch - now_epoch)
    if used_percentage is None or float(used_percentage) >= 99:
        return True, f"statusline: fresh capture, five_hour reset in {wait_seconds} seconds"
    # Reset is in the future but the window is not exhausted — not limited. (The previous code
    # mislabeled this case as "reset time passed", which was never true here.)
    return False, (f"statusline: fresh capture, five_hour not exhausted "
                   f"(used {used_percentage}%), reset in {wait_seconds} seconds")


def check_limit_with_probe() -> tuple[bool, str]:
    try:
        result = subprocess.run(
            [CLAUDE_BIN, "-p", "Reply with exactly: ok", "--model", PROBE_MODEL],
            capture_output=True,
            text=True,
            timeout=120,
            cwd=str(PROJECT_ROOT),
            check=False,
        )
    except (subprocess.SubprocessError, OSError) as exc:
        return False, str(exc)
    output = result.stdout + result.stderr
    if result.returncode == 0:
        return False, output
    return "limit" in output.lower(), output


def check_limit() -> tuple[bool, str]:
    """Return (is_limited, diagnostic_output).

    Falls back to the Claude probe when statusLine is stale.
    """
    statusline_result = check_limit_from_statusline()
    if statusline_result is not None:
        return statusline_result
    return check_limit_with_probe()


def discover_target_sessions(
    project_root: Path = PROJECT_ROOT, user_claude_dir: Path | None = None
) -> list[TargetSession]:
    sessions = _discover_task_sessions(project_root)
    if sessions:
        return sessions
    fallback_dir = user_claude_dir or (Path.home() / ".claude")
    return _discover_user_claude_sessions(project_root, fallback_dir)


def _discover_task_sessions(project_root: Path) -> list[TargetSession]:
    tasks_path = lib.compute_paths(project_root)["executed_tasks"]
    doc = _read_json(tasks_path, {"tasks": []})
    found: list[TargetSession] = []
    for entry in doc.get("tasks", []):
        if entry.get("state") == "FINISHED" or not entry.get("sessionId"):
            continue
        found.append(
            TargetSession(
                session_id=entry["sessionId"],
                task_id=entry.get("taskId", ""),
                transcript_path=entry.get("transcriptPath", ""),
                source="task-tracking",
            )
        )
    return found


def _discover_user_claude_sessions(
    project_root: Path, user_claude_dir: Path
) -> list[TargetSession]:
    projects_dir = user_claude_dir / "projects"
    if not projects_dir.exists():
        return []
    found: dict[str, TargetSession] = {}
    for transcript in projects_dir.rglob("*.jsonl"):
        session_id = _session_id_from_transcript(transcript, project_root)
        if session_id:
            found[session_id] = TargetSession(
                session_id=session_id,
                transcript_path=str(transcript),
                source="claude-projects",
            )
    return list(found.values())


def _session_id_from_transcript(path: Path, project_root: Path) -> str:
    try:
        lines = path.read_text(errors="ignore").splitlines()
    except OSError:
        return ""
    for line in reversed(lines[-50:]):
        try:
            record = json.loads(line)
        except json.JSONDecodeError:
            continue
        cwd = record.get("cwd") or record.get("workspace") or record.get("projectPath")
        if cwd and Path(cwd) != project_root:
            continue
        sid = record.get("sessionId") or record.get("session_id")
        if sid:
            return str(sid)
    return path.stem if lines else ""


def run_auto_wm() -> bool:
    log("Running one-shot claude session with /auto-wm away 4h...")
    try:
        result = subprocess.run(
            [CLAUDE_BIN, "-p", "/auto-wm away 4h"],
            capture_output=True,
            text=True,
            timeout=120,
            cwd=str(PROJECT_ROOT),
            check=False,
        )
    except (subprocess.SubprocessError, OSError) as exc:
        log(f"Failed to run auto-wm: {exc}")
        return False
    log(f"auto-wm exited {result.returncode}")
    if result.returncode != 0:
        log(f"auto-wm error: {result.stderr.strip()[:300]}")
    return result.returncode == 0


def resume_session(target: TargetSession) -> bool:
    prompt = "Continue from where this Claude Code session left off."
    if target.task_id:
        tracker = SCRIPT_DIR / "task_tracker.py"
        prompt = (
            f"Run `python3 {tracker} reattach {target.task_id}` first, then continue "
            "the /task workflow."
        )
    task_suffix = f" task {target.task_id}" if target.task_id else ""
    log(f"Resuming session {target.session_id} ({target.source}{task_suffix})...")
    try:
        lib.spawn_detached([CLAUDE_BIN, "--resume", target.session_id, "-p", prompt,
                            "--permission-mode", "acceptEdits"], cwd=PROJECT_ROOT)
        return True
    except (OSError, subprocess.SubprocessError) as exc:
        log(f"Failed to resume {target.session_id}: {exc}")
        return False


def next_limit_wait_seconds(limited_checks: int) -> int:
    index = max(0, limited_checks - 1)
    if index >= len(LIMIT_WAIT_SCHEDULE_SECONDS):
        return LIMIT_WAIT_SCHEDULE_SECONDS[-1]
    return LIMIT_WAIT_SCHEDULE_SECONDS[index]


def poll_once(
    state: PollState,
    limit_checker=check_limit,
    session_discoverer=discover_target_sessions,
    auto_wm_runner=run_auto_wm,
    session_resumer=resume_session,
    sleep_between_resumes=time.sleep,
    resume_delay_seconds: int = DEFAULT_RESUME_DELAY_SECONDS,
) -> int:
    limited, output = limit_checker()
    if state.was_limited is True and limited is False:
        log("Limit reset detected!")
        if state.auto_wm_on_reset:
            auto_wm_runner()
        sessions = session_discoverer()
        if sessions:
            log(f"Found {len(sessions)} sessions to resume: {[s.session_id for s in sessions]}")
            for index, target in enumerate(sessions):
                if index:
                    log(f"Waiting {resume_delay_seconds} seconds before next resume...")
                    sleep_between_resumes(resume_delay_seconds)
                session_resumer(target)
        else:
            log("No active task or Claude project sessions found to resume.")
    state.was_limited = limited
    if limited:
        state.limited_checks += 1
        wait_seconds = next_limit_wait_seconds(state.limited_checks)
        detail = (output or "").strip()[:200]
        log(f"Status: Limited. Next check in {wait_seconds} seconds. {detail}")
        return wait_seconds
    state.limited_checks = 0
    return DEFAULT_AVAILABLE_INTERVAL_SECONDS


def bounded_max_hours(hours: float) -> float:
    """Clamp a requested cap to MAX_MAX_HOURS, refusing values that are not a duration.

    Both ends matter. Above the ceiling is the unbounded loop this whole change removes.
    At or below zero the deadline is already past, so the poller exits before its first poll
    while reporting that it reached a cap — and argparse accepts `0`, `-5`, `nan` and `inf`
    as floats without comment. `nan` is the worst of them: every `clock() < deadline`
    comparison is false, so it looks exactly like an expired cap.
    """
    if not math.isfinite(hours):
        raise ValueError(f"--max-hours must be a finite number of hours, got {hours!r}")
    if hours <= 0:
        raise ValueError(f"--max-hours must be greater than zero, got {hours!r}")
    return min(hours, MAX_MAX_HOURS)


def _has_unfinished_task(project_root: Path | None = None) -> bool:
    """Whether task tracking still holds a task this poller could resume.

    Resolves PROJECT_ROOT at call time: a module constant bound as a default argument freezes
    at import, which is invisible until something reassigns it.
    """
    return bool(_discover_task_sessions(project_root or PROJECT_ROOT))


def remove_pid(pid_file: Path | None = None) -> None:
    """Drop our pid file so the next poller need not decide whether a stale pid is alive.

    Resolves PID_FILE at call time like its siblings: a module constant bound as a default
    argument freezes at import, and for a function that *unlinks* something that means
    deleting a file at a path nobody is using any more.
    """
    pid_file = pid_file or PID_FILE
    try:
        pid_file.unlink()
    except OSError:
        pass


def run_forever(
    interval_seconds: int | None = None,
    auto_wm_on_reset: bool = False,
    max_hours: float = DEFAULT_MAX_HOURS,
    clock=time.monotonic,
    sleeper=time.sleep,
) -> int:
    """Poll until the cap expires or no unfinished task is left to resume."""
    if already_running(PID_FILE):
        log("poll_limit.py is already running; exiting")
        return 0
    write_pid(PID_FILE)
    max_hours = bounded_max_hours(max_hours)
    deadline = clock() + max_hours * 3600
    log(f"Starting Claude limit poller (dynamic interval: 2h, 30m, 15m, then 5m "
        f"while limited; 5m otherwise). Stops at the {max_hours}h cap, or once no "
        f"unfinished task remains.")
    state = PollState(auto_wm_on_reset=auto_wm_on_reset)
    idle_polls = 0
    try:
        while clock() < deadline:
            try:
                wait_seconds = poll_once(state)
            except Exception as exc:  # noqa: BLE001 - a daemon must outlive transient poll errors
                log(f"poll_once error (continuing): {exc!r}")
                wait_seconds = DEFAULT_AVAILABLE_INTERVAL_SECONDS

            idle_polls = 0 if _has_unfinished_task() else idle_polls + 1
            if idle_polls >= IDLE_POLLS_BEFORE_EXIT:
                log(f"No unfinished task for {idle_polls} polls; nothing left to resume. Exiting.")
                return 0

            sleeper(interval_seconds if interval_seconds is not None else wait_seconds)
        log(f"Reached the {max_hours}h cap; exiting. Re-run /task to start a new poller.")
        return 0
    finally:
        remove_pid(PID_FILE)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--interval-seconds", type=int, default=None)
    parser.add_argument("--once", action="store_true")
    parser.add_argument(
        "--max-hours", type=float, default=DEFAULT_MAX_HOURS,
        help=f"Wall-clock cap before the poller exits (default {DEFAULT_MAX_HOURS}h, "
             f"hard ceiling {MAX_MAX_HOURS}h). It also exits once no unfinished task remains.")
    parser.add_argument(
        "--auto-wm-on-reset", action="store_true",
        help="On a limit reset, also run `/auto-wm away 4h`. Off by default: this hands "
             "tool approval to the agent, so it must be asked for explicitly.")
    args = parser.parse_args()
    if args.once:
        state = PollState(was_limited=True, auto_wm_on_reset=args.auto_wm_on_reset)
        poll_once(state)
        return 0
    try:
        return run_forever(args.interval_seconds, args.auto_wm_on_reset, args.max_hours)
    except ValueError as exc:
        # A mistyped --max-hours is a usage error, not a crash: exit 2, the code argparse
        # itself uses, with the message rather than a stack trace.
        print(str(exc), file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
