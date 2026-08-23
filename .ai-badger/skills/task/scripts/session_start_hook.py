#!/usr/bin/env python3
"""SessionStart hook: record the current session's id + transcript path.

task_tracker.py reads current-session.json so the model never has to know its
own session id. On resume, also surface any unfinished tracked tasks so the
model reattaches instead of starting from scratch. Also launches the
background usage-limit poller (poll_limit.py) so it is running for the
duration of the session.

Does NOT do Tier 1 drift detection (ADR-0001 decision 5): this script is a *scaffolded* copy
living in a consumer's own `.ai-badger/`, so `$CLAUDE_PLUGIN_ROOT` -- a per-plugin command-string
placeholder the CLI substitutes only for plugin-provided hooks -- is never set for it. Drift is
handled entirely by the plugin-provided `drift_notice_hook.py`, registered via
`hooks/hooks.json`; see #24.
"""
# pylint: disable=missing-function-docstring
# Ported from the originating job-search-ai-assistant repo's /task skill: kept in lockstep
# with that source rather than churned for local docstring style rules. One deliberate
# addition over the source: start_poll_limit_background() catches launch failures so a
# broken poller can never crash SessionStart itself.

import json
import subprocess
import sys

sys.path.insert(0, str(__import__("pathlib").Path(__file__).resolve().parent))
import tracker_lib as lib

try:
    import debug_log  # pylint: disable=wrong-import-position
except ImportError:  # pragma: no cover - a missing logger must never break a hook
    debug_log = None

COMPONENT = "session_start_hook"

# The hook payload, kept so every record can name the project it came from. An unattributed
# record pools into every project's analysis; see `call-behaviorist`.
_PAYLOAD: dict = {}


def _debug(event: str, **fields) -> None:
    """Record that this hook ran. Silent when debug is off or the logger is unavailable."""
    if debug_log is None:
        return
    project = fields.pop("project", None) or debug_log.resolve_project_root(_PAYLOAD)
    debug_log.log_event(COMPONENT, event, project=project, **fields)

def start_poll_limit_background() -> None:
    script = lib.SCRIPT_DIR / "poll_limit.py"
    try:
        lib.ensure_data_dir()
        lib.spawn_detached(["python3", str(script)],
                           log_path=lib.DATA_DIR / "poll_limit.log")
    except (OSError, subprocess.SubprocessError):
        pass


def main() -> int:
    try:
        payload = json.load(sys.stdin)
    except json.JSONDecodeError:
        return 0
    _PAYLOAD.update(payload)
    session_id = payload.get("session_id", "")
    transcript = payload.get("transcript_path", "")
    if session_id:
        lib.save_current_session(session_id, transcript, payload.get("cwd", ""))
    else:
        _debug("skip", reason="no_session_id")
    start_poll_limit_background()
    _debug("start", session=session_id, resume=payload.get("source") == "resume")

    notices = []
    if payload.get("source") == "resume":
        unfinished = [
            t["taskId"] for t in lib.load_tasks()["tasks"] if t.get("state") != lib.STATE_FINISHED
        ]
        if unfinished:
            notices.append(
                f"[task-skill] Unfinished tracked tasks: {', '.join(unfinished)}. "
                f"If you are continuing one of them, run "
                f"`python3 {lib.SCRIPT_DIR / 'task_tracker.py'} reattach <taskId>` "
                "so tracking follows this session, then resume the /task workflow."
            )

    if notices:
        print(json.dumps({
            "hookSpecificOutput": {
                "hookEventName": "SessionStart",
                "additionalContext": "\n".join(notices),
            }
        }))
    return 0


if __name__ == "__main__":
    sys.exit(main())
