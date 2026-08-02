#!/usr/bin/env python3
"""Report which projects have been told to commit repeatedly and have not.

The read side of `commit_reminder_hook`'s state. A PostToolUse hook can only add context to the
agent that triggered it and has no channel to that agent's parent, so the hook records and this
reports — a parent runs it to find a subagent that is about to lose work while there is still
time to take over, commit, or kill it.

Reports; never gates. Exit status is 0 even when work is at risk, so nothing that runs this
fails because of what it found.

Usage: ensure_committed.py [--quiet]
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import commit_reminder  # pylint: disable=wrong-import-position


def at_risk_report() -> dict:
    """Every project at or past the escalation bar that still has uncommitted work.

    The entry only clears on a later hook run in that project, so a finished or deleted
    worktree would stay "at risk" forever. `git status` is the live answer; a vanished
    worktree reports nothing uncommitted and drops out for the same reason.
    """
    entries = [
        {
            "project": root,
            "unanswered": entry.get("fires", 0),
            "session": entry.get("session", ""),
            "since": entry.get("since", ""),
        }
        for root, entry in commit_reminder.at_risk_entries().items()
        if commit_reminder.uncommitted_files(root)
    ]
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
    # state would be a worse failure than the one being reported, so nothing propagates.
    try:
        report = at_risk_report()
    except Exception:  # pylint: disable=broad-except
        report = {"atRisk": [], "escalateAfter": commit_reminder.ESCALATE_AFTER,
                  "error": "state unreadable"}
    if not args.quiet:
        print(format_report(report), file=sys.stderr)
    print(json.dumps(report, indent=2))
    return 0


if __name__ == "__main__":  # pragma: no cover - entry point
    sys.exit(main())
