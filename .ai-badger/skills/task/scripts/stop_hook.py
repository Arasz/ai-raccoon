#!/usr/bin/env python3
"""Token checkpoints for tracked tasks, plus end-of-task enforcement on `Stop`.

Wired on two events (features/common/hooks/hooks-manifest.json):

`Stop` fires at the end of every assistant turn — not at session end. It refreshes the
`latest` checkpoint for any unfinished task tied to this session, promotes STARTED ->
IN_PROGRESS, and carries the framework's only channel back into the model: a Stop hook's
stdout reaches the model as `{"decision": "block", "reason": ...}` and by no other means, so
an "advisory" variant would be a silent deletion rather than a gentler feature. Three guards
keep it from becoming a nag — `stop_hook_active`, the per-task one-shot reminder flags, and
a per-session block budget.

`SessionEnd` fires once when the session itself ends and reaches the model on no channel at
all. It is here purely to flush the checkpoint for a final turn `Stop` never saw (an
interrupted turn skips `Stop` entirely), and it never blocks.
"""
# pylint: disable=missing-function-docstring
# Ported from the originating job-search-ai-assistant repo's /task skill; kept close to that
# source rather than churned for local docstring style rules.

import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import tracker_lib as lib

try:
    import debug_log  # pylint: disable=wrong-import-position
except ImportError:  # pragma: no cover - a missing logger must never break a hook
    debug_log = None

COMPONENT = "stop_hook"

# The event whose output the model never sees. Matched by name, and anything unrecognised is
# treated as a turn stop: losing the block channel silently is the worse of the two failures.
SESSION_END_EVENT = "SessionEnd"

# Claude Code stops honouring a Stop hook's block after eight in one session; a ninth costs a
# turn and changes nothing. The hook counts its own so it never spends a reminder's single
# shot on a block the platform will ignore.
MAX_BLOCKS_PER_SESSION = 8
BLOCK_COUNTS_KEY = "stopBlocks"


# The hook payload, kept so every record can name the project it came from. An unattributed
# record pools into every project's analysis; see `call-behaviorist`.
_PAYLOAD: dict = {}


def _debug(event: str, **fields) -> None:
    """Record that this hook ran. Silent when debug is off or the logger is unavailable."""
    if debug_log is None:
        return
    project = fields.pop("project", None) or debug_log.resolve_project_root(_PAYLOAD)
    debug_log.log_event(COMPONENT, event, project=project, **fields)


def field(payload: dict, *names, default=""):
    """First non-empty value among *names*, else *default*.

    Hook payloads are not a stable vocabulary — hosts and releases disagree on
    `session_id`/`sessionId`, `transcript_path`/`transcriptPath` — and indexing one spelling
    turns this hook into a silent no-op, which is the failure mode #141 exists to close.
    """
    for name in names:
        value = payload.get(name)
        if value not in (None, "", {}, []):
            return value
    return default


def is_empty_checkpoint(checkpoint) -> bool:
    """True when a checkpoint carries no measurement at all.

    `assistantMessages` is deliberately not consulted: the degenerate records downstream read
    `contextTokens: 0, assistantMessages: 3` with an all-zero `cumulative`, and a message count
    with no tokens behind it measures nothing.
    """
    if not isinstance(checkpoint, dict):
        return True
    if checkpoint.get("contextTokens"):
        return False
    return not any((checkpoint.get("cumulative") or {}).values())


def replaces(existing, candidate) -> bool:
    """Whether *candidate* may be written over *existing*.

    A missing or unreadable transcript parses to all zeros, indistinguishable from a session
    that spent nothing — so an empty checkpoint is refused over a populated one. Overwriting
    real numbers with zeros is worse than not checkpointing at all (#141).
    """
    if is_empty_checkpoint(existing):
        return True
    return not is_empty_checkpoint(candidate)


def blocks_spent(tasks: dict, session_id: str) -> int:
    """Blocks this session has already emitted; a missing or corrupt count reads as none."""
    counts = tasks.get(BLOCK_COUNTS_KEY)
    if not isinstance(counts, dict):
        return 0
    value = counts.get(session_id, 0)
    if isinstance(value, bool) or not isinstance(value, int) or value < 0:
        return 0
    return value


def checkpoint_tasks(entries: list, usage: dict, transcript: str) -> bool:
    """Refresh `latest` for every unfinished entry; returns whether *usage* changed.

    The transcript is parsed at most once per call, and not at all when no entry needs it:
    `parse_transcript_usage` reads the whole JSONL and this runs on every turn.
    """
    checkpoint = None
    dirty = False
    for entry in entries:
        if entry.get("state") not in (lib.STATE_STARTED, lib.STATE_IN_PROGRESS):
            continue
        if checkpoint is None:
            checkpoint = lib.make_checkpoint(transcript)
        usage_entry = lib.find_entry(usage, entry["taskId"])
        if usage_entry is None:
            continue
        checkpoints = usage_entry.setdefault("checkpoints", {})
        if replaces(checkpoints.get("latest"), checkpoint):
            checkpoints["latest"] = checkpoint
            dirty = True
    return dirty


def promote_started(entries: list) -> bool:
    """STARTED -> IN_PROGRESS: a first stop means real work happened. True when any moved."""
    moved = False
    for entry in entries:
        if entry.get("state") == lib.STATE_STARTED:
            entry["state"] = lib.STATE_IN_PROGRESS
            moved = True
    return moved


def enforcement_reasons(entry: dict):
    """Why a FINISHED task should block, and whether *entry* itself changed.

    Returns `(reasons, changed)`. Each reason spends its one-shot reminder flag as it is
    produced, so a caller that discards the reasons must still persist the entry.
    """
    reasons = []
    changed = False
    if not entry.get("stateJsonUpdated") and not entry.get("stateJsonReminderSent"):
        changed = True
        if lib.state_json_updated_since(entry["startedAt"]):
            entry["stateJsonUpdated"] = True
        else:
            entry["stateJsonReminderSent"] = True
            reasons.append(
                f"Task {entry['taskId']} finished but .ai-badger/state.json was not "
                "updated. Add what this task changed/learned to it now."
            )
    over = lib.over_budget_docs()
    if over and not entry.get("compactionReminderSent"):
        entry["compactionReminderSent"] = True
        changed = True
        listed = "; ".join(
            f"{Path(s['path']).name} ({s['chars']} chars / {s['lines']} lines)"
            for s in over
        )
        reasons.append(
            f"{len(over)} agent instruction file(s) over the size budget "
            f"({over[0]['maxChars']} chars / {over[0]['maxLines']} lines): {listed}. "
            "Compact them: drop anything derivable from code/git/docs — per-task "
            "state belongs in .ai-badger/state.json, not here. These files are "
            "generated, so edits to them are lost on the next re-scaffold: shorten "
            "the source in .ai-badger/, decline catalog items via exclude in "
            "config.json, or raise the budget there with "
            '"agentDocs": {"maxChars": N, "maxLines": N}.'
        )
    return reasons, changed


def enforce(tasks: dict, entries: list, session_id: str):
    """This stop's block reasons and whether the task ledger changed.

    Charges one block against the session budget, whatever the reason count: the platform
    counts blocks, not sentences. A spent budget returns before any one-shot flag is touched.
    """
    spent = blocks_spent(tasks, session_id)
    if spent >= MAX_BLOCKS_PER_SESSION:
        return [], False
    reasons = []
    dirty = False
    for entry in entries:
        if entry.get("state") != lib.STATE_FINISHED:
            continue
        entry_reasons, changed = enforcement_reasons(entry)
        reasons.extend(entry_reasons)
        dirty = dirty or changed
    if reasons:
        tasks.setdefault(BLOCK_COUNTS_KEY, {})[session_id] = spent + 1
        dirty = True
    return reasons, dirty


def main() -> int:
    try:
        payload = json.load(sys.stdin)
    except json.JSONDecodeError:
        return 0
    _PAYLOAD.update(payload)
    session_id = field(payload, "session_id", "sessionId")
    if not session_id:
        _debug("skip", reason="no_session_id")
        return 0

    transcript = field(payload, "transcript_path", "transcriptPath")
    session_end = field(payload, "hook_event_name", "hookEventName") == SESSION_END_EVENT
    resumed = field(payload, "stop_hook_active", "stopHookActive", default=False)

    block_reasons = []
    with lib.locked_store():
        tasks = lib.load_tasks()
        mine = [e for e in tasks["tasks"] if e.get("sessionId") == session_id]
        if not mine:
            _debug("skip", reason="no_tracked_task")
            return 0

        usage = lib.load_usage()
        usage_dirty = checkpoint_tasks(mine, usage, transcript)
        tasks_dirty = promote_started(mine)

        if not session_end and not resumed:
            block_reasons, enforcement_dirty = enforce(tasks, mine, session_id)
            tasks_dirty = tasks_dirty or enforcement_dirty

        if tasks_dirty:
            lib.save_json(lib.EXECUTED_TASKS, tasks)
        if usage_dirty:
            lib.save_json(lib.TOKEN_USAGE, usage)

    if block_reasons:
        _debug("block", reasons=len(block_reasons))
        print(json.dumps({"decision": "block", "reason": " ".join(block_reasons)}))
    else:
        _debug("checkpoint", session=session_id)
    return 0


if __name__ == "__main__":
    sys.exit(main())
