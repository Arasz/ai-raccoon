#!/usr/bin/env python3
"""Every-30-min cron: resume unfinished task sessions after a usage-limit stall.

Logic:
- A task counts as STALLED when its state is not FINISHED and its transcript
  has not changed for STALE_MINUTES — i.e. the session died mid-task, which is
  what hitting a usage limit looks like from the outside. An actively running
  session keeps writing its transcript, so we never touch healthy work.
- If nothing is stalled: do nothing (we haven't hit a limit, or nothing is tracked).
- If something is stalled: probe with a one-shot cheap Haiku call. If the probe
  fails (limit still in force), do nothing and try again in 30 min. If it
  succeeds, resume every stalled task via `claude --resume <sessionId>` with a
  continuation prompt, sequentially.
- At most one resume attempt per task per RETRY_COOLDOWN_MINUTES, and a lock
  file prevents overlapping cron runs.

Project-agnostic: every path (the tracker script, the lock/log files) is resolved via
tracker_lib relative to this script's own location, never an absolute path baked in at
install time — so `install-cron` in task_tracker.py can point cron at wherever this skill
happens to be deployed for a given project.

Usage: resume_cron.py run [--dry-run]
"""
# pylint: disable=missing-function-docstring
# Ported verbatim from the originating job-search-ai-assistant repo's /task skill: kept in
# lockstep with that source rather than churned for local docstring style rules.

from __future__ import annotations

import argparse
import fcntl
import shutil
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import tracker_lib as lib

STALE_MINUTES = 25
RETRY_COOLDOWN_MINUTES = 45
PROBE_MODEL = "claude-haiku-4-5-20251001"
PROBE_TIMEOUT_S = 120
RESUME_TIMEOUT_S = 3600
CRON_LOCK = lib.DATA_DIR / ".cron.lock"

# cron runs with a minimal PATH that usually excludes the CLI's actual install
# location (e.g. ~/.local/bin), so `subprocess.run(["claude", ...])` fails with
# FileNotFoundError even though the binary exists — resolve it once, up front.
_CLAUDE_FALLBACKS = (Path.home() / ".local/bin/claude", Path("/usr/local/bin/claude"))
CLAUDE_BIN = shutil.which("claude") or next(
    (str(p) for p in _CLAUDE_FALLBACKS if p.is_file() and (p.stat().st_mode & 0o111)), "claude"
)


def log(message: str) -> None:
    print(f"{lib.now_iso()} {message}", flush=True)


def minutes_since(dt: datetime) -> float:
    return (datetime.now(timezone.utc) - dt).total_seconds() / 60


def transcript_stale(entry: dict) -> bool:
    path = Path(entry.get("transcriptPath") or "")
    if not path.exists():
        # No transcript to watch; fall back to startedAt so we don't resume brand-new tasks.
        started = entry.get("startedAt")
        return bool(started) and minutes_since(lib.parse_iso(started)) > STALE_MINUTES
    mtime = datetime.fromtimestamp(path.stat().st_mtime, tz=timezone.utc)
    return minutes_since(mtime) > STALE_MINUTES


def recently_attempted(entry: dict) -> bool:
    attempts = entry.get("resumeAttempts") or []
    if not attempts:
        return False
    last = lib.parse_iso(attempts[-1]["at"])
    return minutes_since(last) < RETRY_COOLDOWN_MINUTES


def usage_limit_lifted() -> bool:
    """One-shot cheap call. Success => we can spend tokens again."""
    try:
        result = subprocess.run(
            [CLAUDE_BIN, "-p", "Reply with exactly: ok", "--model", PROBE_MODEL],
            capture_output=True,
            text=True,
            timeout=PROBE_TIMEOUT_S,
            cwd=str(lib.PROJECT_ROOT),
            check=False,
        )
    except (subprocess.TimeoutExpired, FileNotFoundError) as exc:
        log(f"probe failed to run: {exc}")
        return False
    if result.returncode != 0:
        tail = (result.stderr or result.stdout).strip()[:200]
        log(f"probe exited {result.returncode}: {tail}")
        return False
    return True


def resume_task(entry: dict, dry_run: bool) -> None:
    task_id = entry["taskId"]
    tracker = lib.SCRIPT_DIR / "task_tracker.py"
    prompt = (
        f"You were interrupted (likely a usage limit) while working on task {task_id} via the "
        f"/task skill. First run `python3 {tracker} reattach {task_id}` so tracking follows this "
        "session, then review where the transcript left off and continue the /task workflow. "
        "When the task is complete, follow the skill's finish protocol (update "
        f".ai-badger/state.json, then `python3 {tracker} finish {task_id}`)."
    )
    cmd = [
        CLAUDE_BIN,
        "--resume",
        entry["sessionId"],
        "-p",
        prompt,
        "--permission-mode",
        "acceptEdits",
    ]
    log(f"resuming {task_id} (session {entry['sessionId']}){' [dry-run]' if dry_run else ''}")
    if dry_run:
        return
    try:
        result = subprocess.run(
            cmd, capture_output=True, text=True, timeout=RESUME_TIMEOUT_S,
            cwd=str(lib.PROJECT_ROOT), check=False,
        )
        tail = (result.stdout or result.stderr).strip()[-300:]
        log(f"resume {task_id} exited {result.returncode}; tail: {tail}")
    except subprocess.TimeoutExpired:
        log(f"resume {task_id} still running after {RESUME_TIMEOUT_S}s; "
            "leaving it to the next cron cycle")


def run(dry_run: bool) -> int:
    lib.ensure_data_dir()
    # Held open (not `with`) for the process lifetime: releasing it early via `with` would
    # drop the exclusive lock before the run actually completes.
    lock_fh = open(CRON_LOCK, "w", encoding="utf-8")  # pylint: disable=consider-using-with
    try:
        fcntl.flock(lock_fh, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BlockingIOError:
        log("another cron run is active; skipping")
        return 0

    tasks = lib.load_tasks()
    stalled = [
        t
        for t in tasks["tasks"]
        if t.get("state") != lib.STATE_FINISHED
        and t.get("sessionId")
        and transcript_stale(t)
        and not recently_attempted(t)
    ]
    if not stalled:
        return 0  # nothing to do — no limit hit, or work is progressing

    log(f"stalled tasks: {[t['taskId'] for t in stalled]}")
    if not dry_run and not usage_limit_lifted():
        log("usage limit still in force; will retry next cycle")
        return 0

    for entry in stalled:
        with lib.locked_store():
            fresh = lib.load_tasks()
            fresh_entry = lib.find_entry(fresh, entry["taskId"])
            if fresh_entry is None or fresh_entry.get("state") == lib.STATE_FINISHED:
                continue
            fresh_entry.setdefault("resumeAttempts", []).append(
                {"at": lib.now_iso(), "dryRun": dry_run}
            )
            fresh_entry["state"] = lib.STATE_IN_PROGRESS
            lib.save_json(lib.EXECUTED_TASKS, fresh)
        resume_task(entry, dry_run)
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    p_run = sub.add_parser("run")
    p_run.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()
    return run(args.dry_run)


if __name__ == "__main__":
    sys.exit(main())
