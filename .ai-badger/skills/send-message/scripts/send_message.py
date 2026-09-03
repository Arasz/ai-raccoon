#!/usr/bin/env python3
"""send-message: send one message through the machine-wide user-DB message bus.

The target decides the shape: ``--session-id`` is a 1:1 send, ``--project-id`` a
project broadcast, neither a machine broadcast — both given, the session wins and the
project half is dropped (normalised at write, so every read predicate stays
single-shape). Sender identity is REQUIRED on both halves: explicit
``--sender-session``/``--sender-project``, or derived — the session half via the
claude_session_source pattern (harness session env var, then pid ancestry, then unique
cwd against the sessions store), the project half via the store's cwd resolver
(``AI_BADGER_PROJECT_ID`` override, then the nearest ``.ai-badger/project-id`` walk,
ADR-0025). A send that cannot
establish identity refuses with a clean message and non-zero exit, writing nothing
(D7: expected errors are refusals, never tracebacks). A ``--project-id`` target is
validated before anything is written too: it must resolve on this machine — the
sender's own resolution, the ``AI_BADGER_PROJECT_ID`` override, or a project-id file
within the machine scan's depth-4 budget — or the send refuses, because a row no
receiver can resolve must not be written (the msg-10 shape: stored, then undeliverable
forever).
"""
from __future__ import annotations

import argparse
import os
import subprocess
import sys
from pathlib import Path
from typing import Optional

_SCRIPTS_DIR = Path(__file__).resolve().parent
if str(_SCRIPTS_DIR) not in sys.path:
    sys.path.insert(0, str(_SCRIPTS_DIR))
try:
    import badger_store  # pylint: disable=wrong-import-position  # noqa: E402
except ImportError:  # a partial deployment (the vendored copy never landed)
    badger_store = None  # type: ignore[assignment]  # main() refuses cleanly on it

#: The harness session env vars the derivation's env leg reads, one per registered
#: session source (claude, pi, hermes — features/<agent>/adjustments/*_session_source.py).
#: Each harness exports its LIVE session id to the tool subprocesses it spawns, and that
#: is the same id the consumer's delivery will carry — recording it is what makes the
#: store's R2 exclusion bite on the agent's own broadcasts (an agent with a stale
#: task-tracked id in the sessions store must not resolve to that id instead).
SESSION_ENVS = ("CLAUDE_CODE_SESSION_ID", "PI_SESSION_ID", "HERMES_SESSION_ID")

_PID_ANCESTRY_DEPTH = 12

#: The machine scan's budget: at most this many directory levels below the scan root
#: are visited. A scaffolded project deeper than the budget is invisible to the scan —
#: the named residual; the refusal lists what the scan DID find and the escape hatch is
#: the minted-id contract, not a bypass flag.
_WALK_DEPTH = 4

#: Directory names the machine scan never descends into — system caches and dependency
#: trees that hold tens of thousands of entries and never scaffold a project. Matched
#: lowercase so macOS spellings cannot sneak past.
_PRUNED_DIRS = frozenset({
    "library", "node_modules", ".git", ".cache", ".trash", ".npm", ".cargo",
    ".rustup", ".gradle", ".m2", ".venv", "venv", "__pycache__", ".pytest_cache",
    ".mypy_cache", ".tox", ".ruff_cache",
})


def _refused(reason: str) -> int:
    """Every expected failure speaks in this one voice: stderr, non-zero, no row."""
    print(f"send refused: {reason}", file=sys.stderr)
    return 1


def _own_pid_ancestry(max_depth: int = _PID_ANCESTRY_DEPTH) -> list[int]:
    """PIDs of this process and its ancestors, nearest first (the tracker_lib pattern)."""
    chain: list[int] = []
    pid = os.getpid()
    for _ in range(max_depth):
        chain.append(pid)
        result = subprocess.run(["ps", "-o", "ppid=", "-p", str(pid)],
                                capture_output=True, text=True, check=False)
        ppid_text = result.stdout.strip()
        if not ppid_text:
            break
        try:
            ppid = int(ppid_text)
        except ValueError:
            break
        if ppid <= 1 or ppid == pid:
            break
        pid = ppid
    return chain


def derive_sender_session(explicit: Optional[str], sessions: dict) -> Optional[str]:
    """The claude_session_source pattern: env var, then pid ancestry, then unique cwd.

    An explicit argument short-circuits everything. The sessions map is {sessionId:
    info} as the tracking store serves it; the env leg reads the harness session env
    vars (first set wins — each carries the harness's live id); ancestry matches any
    process up the chain, and a cwd leg only fires when exactly one known session
    carries the cwd.
    """
    if explicit:
        return explicit
    for env_name in SESSION_ENVS:
        env_id = os.environ.get(env_name)
        if env_id:
            return env_id
    ancestry = set(_own_pid_ancestry())
    for session_id, info in sessions.items():
        if isinstance(info, dict) and info.get("pid") in ancestry:
            return session_id
    cwd = str(Path.cwd())
    cwd_matches = [sid for sid, info in sessions.items()
                   if isinstance(info, dict) and info.get("cwd") == cwd]
    if len(cwd_matches) == 1:
        return cwd_matches[0]
    return None


def resolve_sender_project(explicit: Optional[str], cwd: str) -> Optional[str]:
    """Sender project: the CLI arg, then the store resolver (env override, then the
    nearest .ai-badger/project-id walk — ADR-0025)."""
    if explicit:
        return explicit
    return badger_store.resolve_project_id(cwd)


def _scan_root() -> Path:
    """The machine view the scan approximates: the user root's parent.

    Production never sets ``AI_BADGER_USER_ROOT``, so this is the store's own home
    (``badger_store._DEFAULT_HOME``) — the same bounded walk that proved the F3
    undeliverable message. The suite's redirections compose instead of escaping: a
    redirected user root scans its own tmp tree, and without one the redirected $HOME
    IS the store's home — the real machine is never scanned under test.
    """
    env_root = os.environ.get(badger_store.USER_ROOT_ENV, "").strip() if badger_store else ""
    if env_root:
        return Path(env_root).parent
    return Path(badger_store._DEFAULT_HOME)


def _machine_project_ids(root: Path, max_depth: int = _WALK_DEPTH) -> list[str]:
    """Stripped contents of every ``.ai-badger/project-id`` within *max_depth* levels.

    The F3 method: each visited directory is checked for ``.ai-badger/project-id`` and
    the file read stripped; the walk never descends into ``.ai-badger`` itself (the
    file is read from its parent — a scope directory is not a project parent), into the
    pruned noise trees, or through a directory symlink; a directory that cannot be read
    is skipped. The scan approximates the receiver universe by construction (depth
    budget, symlink boundary) and reports exactly what it found.
    """
    found: set[str] = set()
    pending: list[tuple[Path, int]] = [(root, 0)]
    while pending:
        directory, depth = pending.pop()
        try:
            marker = directory / ".ai-badger" / "project-id"
            if marker.is_file():
                try:
                    value = marker.read_text(encoding="utf-8").strip()
                except OSError:
                    value = ""
                if value:
                    found.add(value)
            if depth >= max_depth:
                continue
            for entry in os.scandir(directory):
                name = entry.name.lower()
                if name == ".ai-badger" or name in _PRUNED_DIRS:
                    continue
                if entry.is_dir(follow_symlinks=False):
                    pending.append((Path(entry.path), depth + 1))
        except OSError:
            continue
    return sorted(found)


def _target_resolution(target: str, sender_cwd: str) -> tuple[bool, list[str]]:
    """Can a receiver on this machine resolve *target* as its project (ADR-0025)?

    Receivers match rows whose ``target_project`` equals their own resolver output, and
    the sender cannot enumerate receiver cwds, so the target must be producible by the
    same machinery here: the sender's own resolution (the resolver's env leg and cwd
    walk), or the stripped content of some project-id file the bounded machine scan
    finds. Returns (resolves, the ids the scan found) — the refusal lists the latter.
    """
    if badger_store.resolve_project_id(sender_cwd) == target:
        return True, []
    if os.environ.get(badger_store.PROJECT_ID_ENV, "").strip() == target:
        return True, []
    found = _machine_project_ids(_scan_root())
    return target in found, found


def _unresolvable_target_reason(target: str, found_ids: list[str]) -> str:
    """The refusal reason for a target no project-id file carries (the msg-10 shape)."""
    reason = (f"--project-id '{target}' does not resolve to any project on this machine "
              "— no .ai-badger/project-id carries it (ADR-0025)")
    if found_ids:
        reason += "; ids found on this machine: " + ", ".join(found_ids)
    return reason + "; use a minted id or omit --project-id for a machine broadcast"


def _load_sessions() -> dict:
    """The tracking store's current-session map for the derivation's fallback legs.

    A store that cannot open yields {} — derivation then falls through to refusal
    (D7). Swallowed here by design: a broken tracking store must cost the send its
    identity fallback, not the caller a traceback.
    """
    try:
        store = badger_store.open_tracking()
    except Exception:  # pylint: disable=broad-exception-caught  # noqa: BLE001
        return {}
    try:
        return store.sessions_map()
    finally:
        store.close()


def build_parser() -> argparse.ArgumentParser:
    """The CLI contract: --content always; targets optional; identity overridable."""
    parser = argparse.ArgumentParser(
        prog="send_message.py", description="Send one message-bus message.")
    parser.add_argument("--content", required=True,
                        help="message body, stored and delivered verbatim")
    parser.add_argument("--session-id", default=None,
                        help="target session id: 1:1 send (wins over --project-id)")
    parser.add_argument("--project-id", default=None,
                        help="target project id: project broadcast")
    parser.add_argument("--sender-session", default=None,
                        help="explicit sender sessionId, bypassing derivation")
    parser.add_argument("--sender-project", default=None,
                        help="explicit sender projectId, bypassing the cwd resolver")
    return parser


def main(argv: Optional[list] = None) -> int:
    """Resolve identity, store the message, report the row id; refusals exit 1."""
    args = build_parser().parse_args(argv)

    if badger_store is None:
        return _refused("the vendored store copy is missing from this skill's scripts/ "
                        "— re-run the sync or the scaffold to land it")

    sessions = {} if args.sender_session else _load_sessions()
    sender_session = derive_sender_session(args.sender_session, sessions)
    if not sender_session:
        return _refused("missing sender identity (sessionId) — pass --sender-session or "
                        f"set a harness session env var ({', '.join(SESSION_ENVS)})")

    sender_project = resolve_sender_project(args.sender_project, str(Path.cwd()))
    if not sender_project:
        return _refused("missing sender identity (projectId) — pass --sender-project, set "
                        f"{badger_store.PROJECT_ID_ENV}, or run inside a project carrying "
                        ".ai-badger/project-id")

    if args.project_id and not args.session_id:  # only when the project half is stored
        resolves, found_ids = _target_resolution(args.project_id, str(Path.cwd()))
        if not resolves:
            return _refused(_unresolvable_target_reason(args.project_id, found_ids))

    try:
        store = badger_store.open_user()
    except Exception as exc:  # pylint: disable=broad-exception-caught  # noqa: BLE001
        return _refused(f"the user store could not be opened: {exc}")
    try:
        row_id = store.send_message(
            sender_session=sender_session, sender_project=sender_project,
            content=args.content, target_session=args.session_id or None,
            target_project=args.project_id or None)
    except ValueError as exc:  # the store's own identity/content refusals
        return _refused(str(exc))
    finally:
        store.close()
    print(f"sent {row_id}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
