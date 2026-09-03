"""pi session source for the /task tracker (installed by the pi adjustment).

pi has no session database (no state.db, no SQLite store). The session source reads the
PI_SESSION_ID env var for identification and provides a resume command:
`pi -p --session {id}` — measured against pi 0.84.3, `--resume, -r` takes NO argument (it
opens an interactive selector), so a trailing id there is a separate, silently-ignored argv
token. `--session <path|id>` accepts a partial UUID.

Token usage is read from pi's own session JSONL, not from an API — pi exposes no per-session
token endpoint. Each session writes to
`~/.pi/agent/sessions/--<cwd-with-slashes-as-dashes>--/<ISO-timestamp>_<uuid>.jsonl`; one JSON
object per line, `type` field per line, and an assistant turn is a `"type": "message"` entry
carrying a `message.usage` object (`input`/`output`/`cacheRead`/`cacheWrite`/`totalTokens` —
pi's own field names, not Anthropic's `input_tokens`/`output_tokens`). The checkpoint sums
`message.usage` across every such entry in the session file matching PI_SESSION_ID. Any read
failure (missing dir, missing file, unreadable or non-JSON line) degrades to an all-zero
checkpoint rather than raising — a checkpoint reader that crashes is worse than one that
reports zero.

Delegation tokens are read from the subagent logs the delegation runner tees —
`~/.pi/agent/subagent-logs/<runId>.jsonl`, the R4 contract written by
pi-badger-integration's delegation-runner (a frozen cross-repo format: consumed read-only,
never rewritten). Each line is one JSON object: a `run` header, child pi `--mode json`
events (assistant turns end in a `message_end` whose `message` carries `model` and a
`usage` with `input`/`output`/cache fields), optional `tee-elided` byte-cap markers, blank
lines, and a settled marker: an `exit` line (any exitCode, `signal` when killed) **or** a
bare `agent_settled` line — a TUI-side abort (`settleAborted`) writes no `exit` line, so
requiring `exit` alone would refuse exactly the aborted-but-spent runs this reader exists
for; `spawnError` (child never ran) and markerless logs record nothing, as does a settled
log with no usage. `totalTokens` is the sum of `usage.input + usage.output` across
assistant `message_end` events — pi's own `usage.totalTokens` is cache-inclusive, and
input+output is what hermes's source records, so cache tokens are excluded for cross-source
parity. `at` is `exit.endedAt`, else the last assistant `message_end.timestamp`, else the
header `startedAt` — epoch milliseconds (pi's log timestamps), not an ISO string.

The pi adjustment (features/pi/adjustments/adjust_task.py) copies this module into
the scaffolded .ai-badger/skills/task/scripts/pi_session_source.py, where
tracker_lib's discovery import finds and asks it to register(lib).
"""
from __future__ import annotations

import json
import os
from pathlib import Path

PI_SESSION_ENV = "PI_SESSION_ID"
SESSIONS_DIR = Path.home() / ".pi" / "agent" / "sessions"
SUBAGENT_LOGS_DIR = Path.home() / ".pi" / "agent" / "subagent-logs"

_ZERO_CUMULATIVE = {
    "inputTokens": 0,
    "outputTokens": 0,
    "cacheReadTokens": 0,
    "cacheCreationTokens": 0,
}


def register(tracker_lib) -> None:
    """Register the pi session source with a tracker_lib module.

    Called by tracker_lib's guarded optional import. Wires the env var,
    checkpoint maker, resume command, and delegation reader.
    """
    tracker_lib.register_session_source(
        "pi",
        env_var=PI_SESSION_ENV,
        resolve=_resolve,
        checkpoint=lambda session: _checkpoint_for_cwd(session["sessionId"], os.getcwd()),
        resume=lambda session_id: f"pi -p --session {session_id}",
        delegation_usage=_delegation_usage,
    )


def _delegation_usage(delegation_id: str) -> dict | None:
    """Token record `{totalTokens, model, apiCalls, at}` for one delegation run, or None.

    Parses `SUBAGENT_LOGS_DIR/<id>.jsonl` (see module docstring for the log contract).
    Returns None when the id is not a bare filename component (path-traversal guard —
    real ids are `d-<n>`), the log is missing or unreadable, the run never settled (`exit`
    or `agent_settled` marker required; `spawnError` counts as never ran), or the settled
    total is 0 — the caller must refuse rather than record a fabricated number.
    `totalTokens` sums `usage.input + usage.output` across assistant `message_end` events;
    cache tokens are excluded for cross-source parity with hermes (pi's own
    `usage.totalTokens` is cache-inclusive).
    """
    if (not delegation_id or delegation_id in (".", "..")
            or "/" in delegation_id or "\\" in delegation_id):
        return None
    path = SUBAGENT_LOGS_DIR / f"{delegation_id}.jsonl"
    total = 0
    api_calls = 0
    model = None
    ended_at = None
    settled = False
    started_at = None
    last_assistant_ts = None
    try:
        lines = path.read_text(encoding="utf-8").splitlines()
    except (OSError, ValueError):
        return None
    for line in lines:
        line = line.strip()
        if not line:
            continue
        try:
            record = json.loads(line)
        except ValueError:
            continue
        if not isinstance(record, dict):  # same tolerance as _sum_usage: a valid-JSON non-object skips
            continue
        rtype = record.get("type")
        if rtype == "exit":
            settled = True
            ended_at = record.get("endedAt")
            continue
        if rtype == "agent_settled":
            settled = True
            continue
        if rtype == "run":
            started_at = record.get("startedAt")
            continue
        if rtype != "message_end":
            continue
        message = record.get("message") or {}
        if not isinstance(message, dict):
            continue
        if message.get("role") != "assistant":
            continue
        usage = message.get("usage")
        if not isinstance(usage, dict):
            continue
        total += (usage.get("input") or 0) + (usage.get("output") or 0)
        api_calls += 1
        last_assistant_ts = message.get("timestamp")
        if model is None:
            candidate = message.get("model")
            if isinstance(candidate, str) and candidate:
                model = candidate
    if not settled or not total:
        return None
    at = ended_at if ended_at is not None else last_assistant_ts
    return {"totalTokens": total, "model": model, "apiCalls": api_calls,
            "at": at if at is not None else started_at}


def _resolve() -> dict:
    """Identify the invoking pi session: PI_SESSION_ID env var."""
    sid = os.environ.get(PI_SESSION_ENV)
    if sid:
        return {"sessionId": sid, "transcriptPath": None}
    return {}


def _project_session_dir(cwd: str) -> Path:
    """The session directory pi writes for `cwd`: `--<cwd-with-slashes-as-dashes>--`."""
    return SESSIONS_DIR / ("--" + cwd.strip("/").replace("/", "-") + "--")


def _find_session_file(session_id: str, cwd: str) -> Path | None:
    """Locate the session JSONL whose filename's uuid suffix matches `session_id`.

    Filenames are `<ISO-timestamp>_<uuid>.jsonl`; the uuid is the text after the last
    underscore. An exact uuid match wins; otherwise the first uuid that *starts with*
    session_id is used — a partial/prefix match, mirroring how pi's own `--session <path|id>`
    accepts a partial UUID. Any directory-listing failure (missing dir, permission error)
    yields no match rather than raising.
    """
    if not session_id:
        return None
    project_dir = _project_session_dir(cwd)
    try:
        candidates = sorted(project_dir.glob("*.jsonl"))
    except OSError:
        return None

    prefix_match: Path | None = None
    for candidate in candidates:
        _, _, uuid_part = candidate.stem.rpartition("_")
        if not uuid_part:
            continue
        if uuid_part == session_id:
            return candidate
        if prefix_match is None and uuid_part.startswith(session_id):
            prefix_match = candidate
    return prefix_match


def _sum_usage(path: Path) -> dict:
    """Sum `message.usage` across every `type: message` line in `path` that carries one.

    A line that is not valid JSON, or whose `usage` is missing or not an object, is skipped
    individually — it does not abort the rest of the file.
    """
    cumulative = dict(_ZERO_CUMULATIVE)
    messages = 0
    try:
        lines = path.read_text(encoding="utf-8").splitlines()
    except (OSError, ValueError):
        return {"cumulative": cumulative, "assistantMessages": 0}

    for line in lines:
        line = line.strip()
        if not line:
            continue
        try:
            record = json.loads(line)
        except ValueError:
            continue
        if not isinstance(record, dict) or record.get("type") != "message":
            continue
        usage = (record.get("message") or {}).get("usage")
        if not isinstance(usage, dict):
            continue
        messages += 1
        cumulative["inputTokens"] += usage.get("input") or 0
        cumulative["outputTokens"] += usage.get("output") or 0
        cumulative["cacheReadTokens"] += usage.get("cacheRead") or 0
        cumulative["cacheCreationTokens"] += usage.get("cacheWrite") or 0

    return {"cumulative": cumulative, "assistantMessages": messages}


def _checkpoint_for_cwd(session_id: str, cwd: str) -> dict:
    """Checkpoint built from the real session JSONL, zeroed on any read failure."""
    path = _find_session_file(session_id, cwd)
    if path is None:
        return _zeroed_checkpoint(session_id)

    summary = _sum_usage(path)
    return {
        "timestamp": "",
        "contextTokens": 0,
        "assistantMessages": summary["assistantMessages"],
        "byModel": {},
        "cumulative": summary["cumulative"],
    }


def _zeroed_checkpoint(session_id: str) -> dict:  # pylint: disable=unused-argument
    """Checkpoint shape with all zeros — the tolerant fallback when no session file is found."""
    return {
        "timestamp": "",
        "contextTokens": 0,
        "assistantMessages": 0,
        "byModel": {},
        "cumulative": dict(_ZERO_CUMULATIVE),
    }
