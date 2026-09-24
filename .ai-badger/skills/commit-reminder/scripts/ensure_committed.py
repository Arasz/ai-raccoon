#!/usr/bin/env python3
"""Report which projects have been told to commit repeatedly and have not.

The read side of `commit_reminder_hook`'s state. A PostToolUse hook can only add context to the
agent that triggered it and has no channel to that agent's parent, so the hook records and this
reports — a parent runs it to find a subagent that is about to lose work while there is still
time to take over, commit, or kill it.

Reports; never gates *known* state. Exit status is 0 whether or not work is at risk, so nothing
that runs this fails because of what it found — but 1 when the state itself could not be read at
all (the store is broken): "nothing is at risk" and "this report is unreliable" must never look
the same (L9-3). The state read is strict (`commit_reminder.load_state(strict=True)`) for
exactly this reason — the one reader of this module that must fail closed rather than silently
reading a broken store as empty. A project whose own `git status` fails or times out stays in
the report (exit 0) but is listed with `"uncommitted": "unknown"` rather than being dropped as
if the work were already committed.

Usage: ensure_committed.py [--quiet]
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import commit_reminder  # pylint: disable=wrong-import-position


def at_risk_report(strict: bool = False) -> dict:
    """Every project at or past the escalation bar that still has uncommitted work.

    The entry only clears on a later hook run in that project, so a finished or deleted
    worktree would stay "at risk" forever. `git status` is the live answer; a vanished
    worktree reports nothing uncommitted and drops out for the same reason. A project whose
    `git status` fails or times out is neither: it is listed as `"uncommitted": "unknown"`
    rather than silently dropped as if it were clean (L9-3) — a check that could not run must
    never look like a check that passed.
    """
    entries = []
    for root, entry in commit_reminder.at_risk_entries(strict=strict).items():
        status = commit_reminder.git_status(root)
        if status is commit_reminder.GIT_UNKNOWN:
            uncommitted = "unknown"
        elif not status:
            continue  # work has since been committed; no longer at risk
        else:
            uncommitted = len(status)
        entries.append({
            "project": root,
            "unanswered": entry.get("fires", 0),
            "session": entry.get("session", ""),
            "since": entry.get("since", ""),
            "uncommitted": uncommitted,
        })
    entries.sort(key=lambda e: (-e["unanswered"], e["project"]))
    return {"atRisk": entries, "escalateAfter": commit_reminder.ESCALATE_AFTER}


def format_report(report: dict) -> str:
    """A human-readable summary naming the action, not just the state."""
    at_risk = report["atRisk"]
    if not at_risk:
        return "No project has unanswered commit commands. Nothing is at risk."

    lines = [f"{len(at_risk)} project(s) told to commit "
             f"{report['escalateAfter']}+ times with no commit since:"]
    for entry in at_risk:
        where = entry["project"]
        who = f" session {entry['session']}" if entry["session"] else ""
        since = f" since {entry['since']}" if entry["since"] else ""
        lines.append(f"  {where}{who} — {entry['unanswered']} unanswered{since}")
    lines.append("Take over the work, commit it yourself, or stop the agent — "
                 "an agent that ends here leaves the work unrecoverable.")
    return "\n".join(lines)


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description="Report projects at risk of losing work.")
    parser.add_argument("--quiet", action="store_true",
                        help="print JSON only, without the human-readable summary")
    args = parser.parse_args(argv)

    # A parent runs this to find out whether it is about to lose work. Crashing on malformed
    # state would be a worse failure than the one being reported — but a state that could not
    # be read at all must not silently read as "nothing is at risk" either (L9-3), so this is
    # the one place `load_state(strict=True)` is asked for and its failure is never rendered
    # through `format_report` (which always reads an empty `atRisk` as "nothing is at risk").
    try:
        report = at_risk_report(strict=True)
    except Exception:  # pylint: disable=broad-except
        report = {"atRisk": [], "escalateAfter": commit_reminder.ESCALATE_AFTER,
                  "error": "state unreadable"}
        if not args.quiet:
            print("[ai-badger] state unreadable — this is NOT the same as nothing being at "
                  "risk; the store could not be read. Check it directly.", file=sys.stderr)
        print(json.dumps(report, indent=2))
        return 1

    if not args.quiet:
        print(format_report(report), file=sys.stderr)
    print(json.dumps(report, indent=2))
    return 0


if __name__ == "__main__":  # pragma: no cover - entry point
    sys.exit(main())
