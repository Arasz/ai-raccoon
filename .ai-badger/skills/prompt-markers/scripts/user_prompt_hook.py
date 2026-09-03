#!/usr/bin/env python3
"""UserPromptSubmit hook: detect a leading prompt marker and inject behavior context.

Standalone, stdlib-only, project-agnostic. Detects a marker prefix (e.g. `h:`, `hint:`) at the
very start of the submitted prompt (case-insensitive), looks up its injected instruction text in
`markers-context.json` (resolved relative to this script, i.e. the skill directory next to
`scripts/`), and emits it via the hook's `additionalContext` field.

`additionalContext` is *appended*, never used to rewrite or prepend to the prompt: appending
preserves the prefix of the conversation so far, which keeps prompt caching effective (see
ADR-0017 "Prompt markers for agent context injection" in the originating project, or the
equivalent rationale wherever this hook is deployed). Prepending or replacing the prompt would
invalidate the cached prefix for this and every subsequent turn.

Silent (exit 0, no output) when: no marker matches, `markers-context.json` is missing/invalid, or
any internal error occurs — a broken hook must never block a prompt from going through.
"""
from __future__ import annotations

import importlib.util
import json
import os
import sqlite3
import sys
import traceback
from datetime import datetime, timezone
from pathlib import Path

# pylint: disable=no-member  # debug_log is an exec-populated shim; pylint cannot see its members
try:
    import debug_log  # pylint: disable=wrong-import-position
except ImportError:  # pragma: no cover - a missing logger must never break a hook
    debug_log = None

COMPONENT = "prompt_markers_hook"


# The hook payload, kept so every record can name the project it came from. An unattributed
# record pools into every project's analysis; see `call-behaviorist`.
_PAYLOAD: dict = {}


def _debug(event: str, **fields) -> None:
    """Record that this hook ran. Silent when debug is off or the logger is unavailable."""
    if debug_log is None:
        return
    project = fields.pop("project", None) or debug_log.resolve_project_root(_PAYLOAD)
    debug_log.log_event(COMPONENT, event, project=project, **fields)

SKILL_DIR = Path(__file__).resolve().parent.parent
MARKERS_CONTEXT_FILE = SKILL_DIR / "markers-context.json"

# Appended to a marker's inject text when the prompt used the importance token (`!` between
# alias and colon) and the marker defines no dedicated interrupt text. The pi-side
# session-signals extension aborts the running turn for `!`-markers; hosts without a
# mid-turn hook get this instruction instead — the model preempts what is in flight.
IMPORTANCE_SUFFIX = (
    "\n\nIMPORTANCE: interrupt-grade — the user marked this with ! to preempt current "
    "work. Break off what is running and handle this before anything else."
)

def _load_badger_store():
    """The store module: already-imported, importable, or the vendored copy beside this script.

    None when neither exists — persistence then degrades to silence, like a missing logger.
    """
    if "badger_store" in sys.modules:
        return sys.modules["badger_store"]
    try:
        import badger_store  # pylint: disable=import-outside-toplevel,redefined-outer-name
        return badger_store
    except ImportError:
        pass
    path = SKILL_DIR / "badger_store.py"
    if not path.exists():
        return None
    spec = importlib.util.spec_from_file_location("badger_store", path)
    module = importlib.util.module_from_spec(spec)
    sys.modules["badger_store"] = module
    spec.loader.exec_module(module)
    return module


badger_store = _load_badger_store()


def _open_marker_store(tracking_dir: Path):
    """Open the tracking store aimed at *tracking_dir*; None when unavailable or broken.

    The caller has already proven `.ai-badger` exists (the hook never creates tracking
    structure on its own): only then may the store create task-tracking/tracking.db beneath
    it. A broken store never blocks a prompt (D31) — hooks fail open.
    """
    if badger_store is None:
        return None
    # Aim the store at the directory the prompt named, at call time (D9) — the same
    # set-not-setdefault discipline as the task tracker's _sync_tracking_root.
    os.environ[badger_store.TRACKING_ROOT_ENV] = str(tracking_dir / "task-tracking")
    try:
        return badger_store.open_tracking()
    except (OSError, sqlite3.Error):
        return None


# Convention shared with the rest of an ai-badger-scaffolded project: a project-tracking
# directory at the repo root named ".ai-badger". Transformations are recorded there only if the
# project has actually adopted that convention (directory already exists) — this hook never
# creates project-tracking structure on its own.
TRACKING_DIR_NAME = ".ai-badger"

# Marker state lives in the store's marker_state KV table (tracking.db): the "history" row
# carries the audit trail and the "feedbackStreak" row the consolidated-restart counter —
# the row-count equivalent of the legacy prompt-markers/marker-state.json document, whose
# "history" list this row still is, still capped at MAX_HISTORY entries. The first store
# write lazy-migrates a legacy file to rows + marker-state.migrated.json (D6).
MAX_HISTORY = 100


def now_iso() -> str:
    """Return the current UTC time as a second-precision ISO 8601 string."""
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


def load_markers_context() -> dict:
    """Load markers-context.json (marker definitions + their injected instruction text)."""
    with MARKERS_CONTEXT_FILE.open() as fh:
        return json.load(fh)


def _prefix_candidates(prefix: str) -> list[str]:
    """The spellings one catalog prefix matches: itself, and its interrupt variant with a
    `!` inserted before the trailing colon (`f:` also matches `f!:`). The importance token
    is grammar, not catalog: every marker can carry it without enumerating variants in
    markers-context.json."""
    if prefix.endswith(":"):
        return [prefix, prefix[:-1] + "!:"]
    return [prefix]


def match_marker(prompt: str, markers: list[dict]) -> tuple[dict, str, bool] | None:
    """Return the (marker, matched prefix, bang) whose prefix leads `prompt`, or None.

    `bang` is True when the prompt used the importance token (`f!:`). A single-letter
    prefix must be followed by whitespace or end of prompt, so a Windows path
    (`H:\\Projects\\foo.py …`) is not read as `h:` (F-21); the guard counts only the
    alias letters, so its `!`-variant (`h!:`) is guarded identically.
    """
    prompt_trimmed = prompt.strip().lower()
    for marker in markers:
        for prefix in marker.get("prefixes", []):
            for candidate in _prefix_candidates(prefix.lower()):
                if not prompt_trimmed.startswith(candidate):
                    continue
                rest = prompt_trimmed[len(candidate):]
                alias_len = len(candidate.rstrip(":").rstrip("!"))
                if alias_len == 1 and rest and not rest[0].isspace():
                    continue
                return marker, candidate, candidate.endswith("!:")
    return None


def find_tracking_dir(start: Path) -> Path | None:
    """Walk up from `start` looking for an existing `.ai-badger` directory."""
    for candidate in (start, *start.parents):
        maybe = candidate / TRACKING_DIR_NAME
        if maybe.is_dir():
            return maybe
    return None


def record_transformation(
    cwd: str, prompt: str, marker_id: str, prefix: str, injected: str, bang: bool = False
) -> None:
    """Best-effort audit trail. Skips silently if the project has no tracking dir.

    Appends to the ``history`` row of the marker_state table, capped at MAX_HISTORY
    entries; the first write lazy-migrates the legacy marker-state.json (D6). Whole
    prompts land in the store verbatim — the DB is owner-only (security I5), enforced
    by the store on every write.
    """
    tracking_dir = find_tracking_dir(Path(cwd) if cwd else Path.cwd())
    if tracking_dir is None:
        return
    store = _open_marker_store(tracking_dir)
    if store is None:
        return
    try:
        history = store.kv_get("marker_state", "history", [])
        history.append({
            "timestamp": now_iso(),
            "originalPrompt": prompt,
            "matchedPrefix": prefix,
            "markerId": marker_id,
            "bang": bang,
            "injectedContext": injected,
        })
        store.kv_set("marker_state", "history", history[-MAX_HISTORY:])
    except (OSError, sqlite3.Error):
        pass  # best-effort: the audit trail never blocks the marker's injection
    finally:
        store.close()


def count_trailing_feedback(state: dict) -> int:
    """Count the consecutive feedback-turn streak in marker history.

    The streak counts consecutive *user turns* that were feedback markers:
    every recorded entry advances a turn, and a non-feedback turn (a marker of
    another kind) resets it.  Prompts with no marker never reach this file, so
    `feedbackStreak` — maintained by main() on every recorded turn — is what
    carries the count; this function reads it.
    """
    return state.get("feedbackStreak", 0)


RESTART_THRESHOLD = 2
RESTART_ADVISORY = (
    "CONSOLIDATED RESTART ADVISORY: This session has had {count} consecutive "
    "feedback turns. The thread has likely drifted. Restart with a single merged "
    "prompt that includes all accepted constraints, the failing evidence, and the "
    "original objective — instead of layering another correction on this stale thread."
)


def advance_feedback_streak(cwd: str, is_feedback: bool) -> int:
    """Advance the per-project feedback streak by one user turn.

    A feedback turn increments the streak; any other marker resets it to 0.
    Returns the new streak.  Best-effort: silently returns 0 when no tracking
    dir exists or the store is unavailable; the streak is the ``feedbackStreak``
    row of the marker_state table.
    """
    tracking_dir = find_tracking_dir(Path(cwd) if cwd else Path.cwd())
    if tracking_dir is None:
        return 0
    store = _open_marker_store(tracking_dir)
    if store is None:
        return 0
    try:
        prior = store.kv_get("marker_state", "feedbackStreak", 0)
    except (OSError, sqlite3.Error):
        prior = 0
    streak = prior + 1 if is_feedback else 0
    try:
        store.kv_set("marker_state", "feedbackStreak", streak)
    except (OSError, sqlite3.Error):
        pass
    finally:
        store.close()
    return streak


def main() -> int:
    """Read the hook payload from stdin and emit additionalContext if a marker matched."""
    payload = json.load(sys.stdin)
    _PAYLOAD.update(payload)
    prompt = payload.get("prompt", "")
    if not prompt:
        _debug("skip", reason="no_prompt")
        return 0

    config = load_markers_context()
    matched = match_marker(prompt, config.get("markers", []))
    if matched is None:
        # No marker: still a user turn — reset the feedback streak so an
        # interleaved normal prompt breaks a would-be restart advisory.
        cwd = payload.get("cwd", "")
        if find_tracking_dir(Path(cwd) if cwd else Path.cwd()) is not None:
            advance_feedback_streak(cwd, is_feedback=False)
        _debug("skip", reason="no_match")
        return 0

    marker, prefix, bang = matched
    injected = marker["inject"]
    if bang:
        # A dedicated interrupt text (the important marker's emergency instruction) wins;
        # otherwise the marker's meaning carries the generic preemption suffix.
        injected = marker.get("injectInterrupt") or injected + IMPORTANCE_SUFFIX

    cwd = payload.get("cwd", "")
    record_transformation(cwd, prompt, marker["id"], prefix, injected, bang)

    # Consolidated restart: track consecutive *user turns* that were feedback.
    # Every recorded turn advances the streak; non-feedback markers reset it.
    if marker["id"] == "feedback":
        streak = advance_feedback_streak(cwd, is_feedback=True)
        if streak >= RESTART_THRESHOLD:
            injected += "\n\n" + RESTART_ADVISORY.format(count=streak)
            _debug("restart_advisory", count=streak)
    else:
        advance_feedback_streak(cwd, is_feedback=False)

    _debug("fire", marker=marker["id"], prefix=prefix, bang=bang)

    print(json.dumps({
        "hookSpecificOutput": {
            "hookEventName": "UserPromptSubmit",
            "additionalContext": injected,
        }
    }))
    return 0


HOOK_ERRORS_FILE = Path.home() / ".ai-badger" / "hook-errors.log"
MAX_ERROR_LOG_BYTES = 1_000_000


def record_hook_failure(where):
    """Leave one content-free line behind before a hook swallows an exception.

    Type and location only: an exception message can quote scanned input.
    """
    exc_type, _, tb = sys.exc_info()
    frame = traceback.extract_tb(tb)[-1] if tb else None
    at = f"{Path(frame.filename).name}:{frame.lineno}" if frame else "unknown"
    name = exc_type.__name__ if exc_type else "Unknown"
    print(f"[ai-badger] {where} hook failed: {name} at {at}", file=sys.stderr)
    try:
        HOOK_ERRORS_FILE.parent.mkdir(parents=True, exist_ok=True)
        if HOOK_ERRORS_FILE.exists() and HOOK_ERRORS_FILE.stat().st_size > MAX_ERROR_LOG_BYTES:
            HOOK_ERRORS_FILE.unlink()
        with HOOK_ERRORS_FILE.open("a", encoding="utf-8") as fh:
            fh.write(f"{datetime.now(timezone.utc).isoformat()} {where} {name} at {at}\n")
    except OSError:
        pass


def guarded_main():
    """Run main(): a hook never breaks the session, but never fails invisibly either."""
    try:
        return main() or 0
    except Exception:  # pylint: disable=broad-exception-caught
        record_hook_failure("user_prompt_hook")
        return 0


if __name__ == "__main__":
    sys.exit(guarded_main())
