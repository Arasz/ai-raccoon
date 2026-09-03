# the P1 user families pushed this single vendored module past 1000 lines; one file per
# ADR-0009, so the line budget yields (same arrangement as badger_lib.py)
# pylint: disable=too-many-lines
"""SQLite runtime store for ai-badger (ADR-0024).

One stdlib-only module, vendored verbatim per ADR-0009: project runtime state lives in
``<project>/.ai-badger/task-tracking/tracking.db``, user-level state in ``~/.ai-badger/ai-badger.db``,
and the audit sink in its own DB file. Roots resolve from the environment at call time, never at
import (``AI_BADGER_TRACKING_ROOT``, ``AI_BADGER_USER_ROOT``, ``AI_BADGER_DEBUG_DIR``). This module
imports nothing from the engine and nothing outside the standard library.
"""
from __future__ import annotations

import argparse
import contextlib
import json
import os
import re
import sqlite3
import sys
import time
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any, Callable, Iterator, NamedTuple, Optional

try:
    import fcntl
except ImportError:  # pragma: no cover - Windows has no fcntl; locking degrades to a no-op
    fcntl = None  # type: ignore[assignment]

SCHEMA_VERSION = 2

#: Minimum seconds between two prunes of the same log table (D9/D30): the open-time prune
#: is throttled by the per-table ``pruned_at`` meta stamp so a burst of opens prunes once.
_PRUNE_THROTTLE_SECONDS = 3600

#: The message bus's delivery window (R3/R8, D5): a session's FIRST delivery — session
#: start or any cursor-less live read — sees only messages sent within the last 30 minutes
#: (inclusive: exactly 30 minutes old counts as inside); everything older is gated off.
_GATE_WINDOW = timedelta(minutes=30)

#: Session-start delivery cap (R5): the first delivery injects the 16 oldest messages in
#: the window and drops the overflow — the cursor lands past the gated window, so the
#: dropped tail never reaches that session. Live reads after the first are uncapped (A5).
_START_CAP = 16

#: Bus retention (R6/R10, D10): messages and cursors live 4 days, pruned at user-store
#: open with the shared prune_expired pattern. Boundary matches the log-table rule:
#: ``DELETE WHERE ts < cutoff`` — a row exactly 4 days old survives until the window closes.
_BUS_MAX_AGE_DAYS = 4

#: The bus tables' DDL (D2): born in SQLite, no legacy source, so it lives in the upgrade
#: hook rather than the v1 base _DDL — a fresh DB replays it before stamping, a stamped-1
#: DB gets it from hook 1, and neither path can half-land it (rollback undoes DDL).
_BUS_DDL = (
    """
    CREATE TABLE IF NOT EXISTS messages (
        id              INTEGER PRIMARY KEY AUTOINCREMENT,
        ts              TEXT NOT NULL,          -- ISO-8601 UTC send time; feeds retention
        sender_session  TEXT NOT NULL,
        sender_project  TEXT NOT NULL,
        target_session  TEXT,                   -- NULL unless 1:1
        target_project  TEXT,                   -- NULL unless project broadcast
        content         TEXT NOT NULL           -- raw string, per owner ruling
    )
    """,
    "CREATE INDEX IF NOT EXISTS idx_messages_ts ON messages(ts)",
    "CREATE INDEX IF NOT EXISTS idx_messages_target_session ON messages(target_session, id)",
    "CREATE INDEX IF NOT EXISTS idx_messages_target_project ON messages(target_project, id)",
    """
    CREATE TABLE IF NOT EXISTS cursors (
        session_id  TEXT PRIMARY KEY,
        cursor_id   INTEGER NOT NULL,
        ts          TEXT NOT NULL             -- last-activity stamp; feeds TTL prune
    )
    """,
    "CREATE INDEX IF NOT EXISTS idx_cursors_ts ON cursors(ts)",
)


def _upgrade_v1_to_v2(conn: sqlite3.Connection) -> None:
    """Land the message-bus tables (D1): DDL-only, idempotent, rollback-safe."""
    for statement in _BUS_DDL:
        conn.execute(statement)


#: Deterministic test seams for the delivery path (plan review F5/§D): the two points a
#: test must be able to freeze to make the exactly-once race real instead of won
#: green-trivially by the microsecond default window. In-process tests register callbacks
#: here; a child process arms a block via ``AI_BADGER_TEST_HOLD="<seam>:<release-path>""
#: and its parent releases it by creating the path. Production never registers or sets
#: the env — the seams are inert unless a test arms them.
_TEST_HOLDS: dict[str, list[Callable[[], None]]] = {}
_TEST_HOLD_ENV = "AI_BADGER_TEST_HOLD"
#: D3/L2: the env hold parks a delivery only when this is ALSO set — a hold value that
#: leaks into an unconfigured environment (a forgotten export, a nested test run) must be
#: inert, not a deadlock. The in-process _TEST_HOLDS registry stays ungated: tests that
#: register callbacks are deliberately arming the seam.
_TEST_HOLD_ARMED_ENV = "AI_BADGER_TEST_HOLD_ARMED"


def _hold_at(seam: str) -> None:
    """Run every callback registered for *seam*, then honour the env-gated cross-process hold.

    The env hold needs the pair (D3/L2): ``AI_BADGER_TEST_HOLD`` alone is ignored — see
    ``_TEST_HOLD_ARMED_ENV``."""
    for blocker in tuple(_TEST_HOLDS.get(seam, ())):
        blocker()
    spec = os.environ.get(_TEST_HOLD_ENV)
    if spec and spec.startswith(f"{seam}:") and os.environ.get(_TEST_HOLD_ARMED_ENV):
        release = Path(spec.split(":", 1)[1])
        while not release.exists():
            time.sleep(0.005)


def _message_document(row) -> dict:
    """One messages row as the delivered document — the shape schemas/message.schema.json
    validates (F4): ``{sender: {sessionId, projectId}, content, timestamp}``."""
    return {"sender": {"sessionId": row[2], "projectId": row[3]},
            "content": row[4], "timestamp": row[1]}


#: On-open upgrade seam: hook for version N migrates a database stamped N to N+1 (D27).
#: Key 1 is the message bus (P1) — the first migration this store has ever registered.
UPGRADE_HOOKS: dict[int, Callable[[sqlite3.Connection], None]] = {1: _upgrade_v1_to_v2}

TRACKING_ROOT_ENV = "AI_BADGER_TRACKING_ROOT"
USER_ROOT_ENV = "AI_BADGER_USER_ROOT"
DEBUG_DIR_ENV = "AI_BADGER_DEBUG_DIR"

_TABLE_NAME = re.compile(r"[a-z_][a-z0-9_]*\Z")

# The default home snapshots at import, before anything redirects $HOME for a session (the same
# pattern conftest's REAL_HOME uses): $HOME is session-wide state, never one of the three
# call-time env roots below. With no env override the store lands under the real home.
_DEFAULT_HOME = Path.home()

_DDL = (
    """
    CREATE TABLE IF NOT EXISTS meta (
        key   TEXT PRIMARY KEY,
        value TEXT NOT NULL
    )
    """,
    """
    CREATE TABLE IF NOT EXISTS tasks (
        id                INTEGER PRIMARY KEY AUTOINCREMENT,
        task_id           TEXT,
        session_id        TEXT NOT NULL,
        title             TEXT,
        cwd               TEXT,
        branch            TEXT,
        transcript_path   TEXT,
        resume_command    TEXT,
        started_at        TEXT,
        finished_at       TEXT,
        state             TEXT NOT NULL DEFAULT 'STARTED',
        resume_attempts   TEXT NOT NULL DEFAULT '[]',
        tracking_source   TEXT,
        state_json_updated        INTEGER NOT NULL DEFAULT 0,
        state_json_reminder_sent  INTEGER NOT NULL DEFAULT 0,
        compaction_reminder_sent  INTEGER NOT NULL DEFAULT 0
    )
    """,
    # Defense in depth for the one-active-task-per-session rule (D14); the FINISHED-terminal
    # and attach-refusal checks stay application-level (P0.3).
    """
    CREATE UNIQUE INDEX IF NOT EXISTS uq_tasks_active_session
        ON tasks(session_id) WHERE state <> 'FINISHED'
    """,
    # subagents is a JSON column per the P0.6a ruling (D1/D15); task_id NOT NULL because a
    # TEXT PK otherwise admits distinct NULLs and the natural-key dedup would never dedupe
    # them (P0.6a finding 4).
    """
    CREATE TABLE IF NOT EXISTS token_usage (
        task_id     TEXT NOT NULL PRIMARY KEY,
        session_id  TEXT,
        subagents   TEXT NOT NULL DEFAULT '[]',
        checkpoints TEXT NOT NULL DEFAULT '{}',
        usage       TEXT NOT NULL DEFAULT '{}',
        grade       TEXT,
        graded_at   TEXT,
        tracking_source TEXT
    )
    """,
    """
    CREATE TABLE IF NOT EXISTS sessions (
        session_id      TEXT PRIMARY KEY,
        transcript_path TEXT,
        cwd             TEXT,
        pid             INTEGER,
        recorded_at     TEXT
    )
    """,
    """
    CREATE TABLE IF NOT EXISTS statusline (
        key        TEXT PRIMARY KEY,
        value      TEXT NOT NULL,
        updated_at TEXT NOT NULL
    )
    """,
    """
    CREATE TABLE IF NOT EXISTS marker_state (
        key        TEXT PRIMARY KEY,
        value      TEXT NOT NULL,
        updated_at TEXT NOT NULL
    )
    """,
    # P1 user families (ADR-0024): the three KV tables share the statusline shape; the two
    # append-log tables follow the ts-index convention — every log table carries an index on
    # its ts column at creation (D17c), so the 60-day prune's range query stays indexed:
    # awm_decisions and searches here, hook_audit with its DDL in P2.1.
    """
    CREATE TABLE IF NOT EXISTS awm_state (
        key        TEXT PRIMARY KEY,
        value      TEXT NOT NULL,
        updated_at TEXT NOT NULL
    )
    """,
    """
    CREATE TABLE IF NOT EXISTS commit_reminder (
        key        TEXT PRIMARY KEY,
        value      TEXT NOT NULL,
        updated_at TEXT NOT NULL
    )
    """,
    """
    CREATE TABLE IF NOT EXISTS pending_feedback (
        key        TEXT PRIMARY KEY,
        value      TEXT NOT NULL,
        updated_at TEXT NOT NULL
    )
    """,
    """
    CREATE TABLE IF NOT EXISTS awm_decisions (
        id      INTEGER PRIMARY KEY AUTOINCREMENT,
        ts      TEXT NOT NULL,
        payload TEXT NOT NULL
    )
    """,
    "CREATE INDEX IF NOT EXISTS idx_awm_decisions_ts ON awm_decisions(ts)",
    """
    CREATE TABLE IF NOT EXISTS searches (
        id      INTEGER PRIMARY KEY AUTOINCREMENT,
        ts      TEXT NOT NULL,
        payload TEXT NOT NULL
    )
    """,
    "CREATE INDEX IF NOT EXISTS idx_searches_ts ON searches(ts)",
    # P2.0b session-store families (D10): one table per legacy surface. memory_first carries
    # the denial count as a real defaulted column — it is filtered data (gate MAX_DENIALS),
    # not payload; blast_radius_denials likewise. The two append-log tables follow the
    # ts-index convention (D17c) — hook_audit's idx lands with its DDL, P2.3 prunes it.
    """
    CREATE TABLE IF NOT EXISTS memory_first (
        session_id TEXT PRIMARY KEY,
        payload    TEXT NOT NULL,
        denials    INTEGER NOT NULL DEFAULT 0,
        updated_at TEXT NOT NULL
    )
    """,
    """
    CREATE TABLE IF NOT EXISTS semantica_nudge (
        session_id TEXT PRIMARY KEY,
        payload    TEXT NOT NULL,
        updated_at TEXT NOT NULL
    )
    """,
    """
    CREATE TABLE IF NOT EXISTS dispatch_lanes (
        lane_id    TEXT PRIMARY KEY,
        entries    TEXT NOT NULL,
        updated_at TEXT NOT NULL
    )
    """,
    """
    CREATE TABLE IF NOT EXISTS dirty_sweeps (
        key        TEXT PRIMARY KEY,
        value      TEXT NOT NULL,
        updated_at TEXT NOT NULL
    )
    """,
    """
    CREATE TABLE IF NOT EXISTS blast_radius_denials (
        key        TEXT PRIMARY KEY,
        denials    INTEGER NOT NULL,
        updated_at TEXT NOT NULL
    )
    """,
    """
    CREATE TABLE IF NOT EXISTS hook_audit (
        id      INTEGER PRIMARY KEY AUTOINCREMENT,
        ts      TEXT NOT NULL,
        payload TEXT NOT NULL
    )
    """,
    "CREATE INDEX IF NOT EXISTS idx_hook_audit_ts ON hook_audit(ts)",
    """
    CREATE TABLE IF NOT EXISTS hook_state (
        key        TEXT PRIMARY KEY,
        value      TEXT NOT NULL,
        updated_at TEXT NOT NULL
    )
    """,
)


#: Where vendored copies of this module live or will land, repo-relative (D16). The running
#: module is the byte-equality reference; entries whose file is absent have not landed yet
#: (vendorin happens with the P0.5 re-scaffold and P2.2's mirror sync, which reuse this list).
VENDORED_PATHS: tuple[dict[str, str], ...] = (
    {"consumer": "hooks", "lands_in": "features/common/hooks/badger_store.py"},
    {"consumer": "task", "lands_in": "features/common/skills/task/scripts/badger_store.py"},
    {"consumer": "prompt-markers",
     "lands_in": "features/common/skills/prompt-markers/scripts/badger_store.py"},
    {"consumer": "welcome-ai-badger",
     "lands_in": "features/common/skills/welcome-ai-badger/scripts/badger_store.py"},
    {"consumer": "commit-reminder",
     "lands_in": "features/common/skills/commit-reminder/scripts/badger_store.py"},
    {"consumer": "test-economy",
     "lands_in": "features/common/skills/test-economy/scripts/badger_store.py"},
    {"consumer": "test-economy",
     "lands_in": "skills/test-economy/scripts/badger_store.py"},
    {"consumer": "mcp-index",
     "lands_in": "features/common/skills/mcp-index/scripts/badger_store.py"},
    {"consumer": "ai-raccoon-memory",
     "lands_in": "features/common/skills/ai-raccoon-memory/scripts/badger_store.py"},
    {"consumer": "ai-raccoon-memory",
     "lands_in": "skills/ai-raccoon-memory/scripts/badger_store.py"},
    {"consumer": "worktree-agent-isolation",
     "lands_in": "features/common/skills/worktree-agent-isolation/scripts/badger_store.py"},
    {"consumer": "worktree-agent-isolation",
     "lands_in": ".ai-badger/skills/worktree-agent-isolation/scripts/badger_store.py"},
    {"consumer": "auto-wm", "lands_in": "features/claude/skills/auto-wm/scripts/badger_store.py"},
    {"consumer": "auto-wm", "lands_in": "skills/auto-wm/scripts/badger_store.py"},
    {"consumer": "mcp-index", "lands_in": "skills/mcp-index/scripts/badger_store.py"},
    {"consumer": "call-behaviorist",
     "lands_in": "features/common/skills/call-behaviorist/scripts/badger_store.py"},
    {"consumer": "call-behaviorist",
     "lands_in": "skills/call-behaviorist/scripts/badger_store.py"},
    {"consumer": "send-message",
     "lands_in": "features/common/skills/send-message/scripts/badger_store.py"},
)


def vendored_copies_report(repo_root: Optional[Path] = None) -> list[str]:
    """Skew findings for landed vendored copies; empty means every landed copy is byte-identical.

    Copies not yet landed are named by the manifest but unchecked; a landed copy that differs
    from the running module is the failure the manifest exists to catch (D16).
    """
    root = repo_root if repo_root is not None else _default_badger_root().parent
    canonical = Path(__file__).resolve()
    try:
        expected = canonical.read_bytes()
    except OSError as exc:
        return [f"canonical {canonical} unreadable: {exc}"]
    findings = []
    for entry in VENDORED_PATHS:
        landed = root / entry["lands_in"]
        if not landed.exists():
            continue
        try:
            if landed.read_bytes() != expected:
                findings.append(f"{entry['lands_in']} differs from {canonical.name}")
        except OSError as exc:
            findings.append(f"{entry['lands_in']} unreadable: {exc}")
    return findings


def _now() -> str:
    """UTC ISO-8601 timestamp for row-level recency."""
    return datetime.now(timezone.utc).isoformat()


def _default_badger_root() -> Path:
    """The nearest existing ``.ai-badger`` directory above this module file (hook convention)."""
    for ancestor in Path(__file__).resolve().parents:
        if (ancestor / ".ai-badger").is_dir():
            return ancestor / ".ai-badger"
    return Path.home() / ".ai-badger"


def tracking_db_path() -> Path:
    """tracking.db — under ``AI_BADGER_TRACKING_ROOT`` when set, else the project root's."""
    env = os.environ.get(TRACKING_ROOT_ENV)
    if env:
        return Path(env) / "tracking.db"
    return _default_badger_root() / "task-tracking" / "tracking.db"


def user_db_path() -> Path:
    """ai-badger.db — under ``AI_BADGER_USER_ROOT`` when set, else the real home's .ai-badger."""
    env = os.environ.get(USER_ROOT_ENV)
    if env:
        return Path(env) / "ai-badger.db"
    return _DEFAULT_HOME / ".ai-badger" / "ai-badger.db"


def audit_db_path() -> Path:
    """The audit sink's own DB file — the ``AI_BADGER_DEBUG_DIR`` contract moves it whole (D21)."""
    env = os.environ.get(DEBUG_DIR_ENV)
    debug_dir = Path(env) if env else _DEFAULT_HOME / ".ai-badger" / "debug"
    return debug_dir / "audit.db"


def _ensure_root(db_path: Path) -> None:
    """Create the DB's parent 0700 when absent; an existing root keeps its own mode (D17).

    `exist_ok` is load-bearing: concurrent first-opens (a fan-out's hooks all opening the
    user store at once) race the mkdir, and a bare mkdir loses the race with FileExistsError.
    """
    if not db_path.parent.is_dir():
        db_path.parent.mkdir(parents=True, exist_ok=True)
        os.chmod(db_path.parent, 0o700)


def _assert_file_perms(db_path: Path) -> None:
    """Re-assert owner-only on the DB and its WAL sidecars (D17)."""
    for candidate in (db_path, Path(f"{db_path}-wal"), Path(f"{db_path}-shm")):
        if candidate.exists():
            os.chmod(candidate, 0o600)


def _precreate_db_file(db_path: Path) -> None:
    """Create an absent DB file at 0600 BEFORE sqlite3.connect runs (P0.6b carry 1).

    sqlite creates the file with the process umask, leaving a first-open window where a new
    DB exists world-readable until the end-of-open chmod; creating it here closes that window
    for both DBs, and the explicit chmod keeps the mode umask-independent.
    """
    if db_path.exists():
        return
    fd = os.open(str(db_path), os.O_CREAT | os.O_RDWR, 0o600)
    os.close(fd)
    os.chmod(db_path, 0o600)


def _create_schema(conn: sqlite3.Connection) -> None:
    for statement in _DDL:
        conn.execute(statement)


def _ensure_schema_version(conn: sqlite3.Connection, db_path: Path) -> None:
    """Stamp SCHEMA_VERSION on a fresh DB; dispatch upgrade hooks older; fail closed newer (D27)."""
    row = conn.execute("SELECT value FROM meta WHERE key = 'schema_version'").fetchone()
    if row is None:
        # A fresh DB carries only the v1 base DDL (created just before this runs), so it is
        # a version-1 database without a stamp yet: replay every upgrade hook inside one
        # transaction — the exact path a stamped-1 DB takes — then stamp it current (D1).
        conn.execute("BEGIN IMMEDIATE")
        try:
            for version in range(1, SCHEMA_VERSION):
                hook = UPGRADE_HOOKS.get(version)
                if hook is not None:
                    hook(conn)
            # INSERT OR REPLACE, not INSERT: two processes opening a never-stamped DB
            # concurrently both read None, serialize on BEGIN IMMEDIATE, and the loser's
            # plain INSERT would hit UNIQUE(meta.key) — IntegrityError out of the very
            # first open on a fresh machine (QA review M1). Replacing the winner's
            # identical stamp is the correct resolution.
            conn.execute(
                "INSERT OR REPLACE INTO meta(key, value) VALUES ('schema_version', ?)",
                (str(SCHEMA_VERSION),),
            )
            conn.commit()
        except BaseException:
            conn.rollback()
            raise
        return
    stored = int(row[0])
    if stored > SCHEMA_VERSION:
        # Name the database that actually failed, not whichever path tracking_db_path()
        # resolves to — the user DB must not be misnamed (P0.6a finding 6).
        raise sqlite3.OperationalError(
            f"store schema version {stored} is newer than this code knows ({SCHEMA_VERSION}); "
            f"refusing to write in an old shape — run den-refresh to upgrade ai-badger "
            f"({db_path})"
        )
    if stored < SCHEMA_VERSION:
        conn.execute("BEGIN IMMEDIATE")
        try:
            for version in range(stored, SCHEMA_VERSION):
                hook = UPGRADE_HOOKS.get(version)
                if hook is not None:
                    hook(conn)
            conn.execute(
                "UPDATE meta SET value = ? WHERE key = 'schema_version'", (str(SCHEMA_VERSION),)
            )
            conn.commit()
        except BaseException:
            conn.rollback()
            raise


class Family(NamedTuple):
    """One migrating store family: its table, its database, and its legacy JSON source.

    ``legacy_path`` is the family's legacy JSON source, absent (None) for a family born in
    SQLite — nothing to import, nothing to resurrect. ``legacy_kind`` selects the import
    shape: ``store`` is the born-in-SQLite kind with no source at all (messages, cursors);
    ``map`` is a top-level dict keyed like the KV table (marker-state.json); ``kvdoc`` is
    one whole JSON document stored as a single KV row named by ``row_key``
    (statusline-state.json / statusline-delegate.json); ``tasks`` and
    ``usage`` are ``{"tasks": [...]}`` row lists keyed on ``taskId`` (executed-tasks.json,
    token-usage.json); ``sessions`` is ``{"sessions": {id: info}}`` keyed on the session id
    (current-session.json); ``awm`` is the away-mode document whose per-project entries sit
    under ``projects`` (or the pre-#296 single-project shape) keyed by project path;
    ``jsonl`` is one JSON object per line written by the legacy appender (decisions.jsonl)
    with no natural key — imported with its ``ts_field`` (default "ts") normalised through
    iso_row_ts and deduped on exact (ts, payload) content so a re-import adds nothing. The
    file-set kinds
    (FILE_SET_KINDS) are many-file shapes — a directory's children, or a pattern at the user
    root — imported as ONE transaction with each file renamed afterward per the pinned
    per-file convention "<stem>.migrated<suffix>" (D10).
    """

    table: str
    db: str  # "tracking" or "user"
    legacy_path: Optional[Callable[[], Path]] = None  # None: born in SQLite, no source
    legacy_kind: str = "store"  # "store" | "map" | "kvdoc" | "tasks" | "usage" | ... (see above)
    row_key: str = ""  # kvdoc only: the KV row key this file's document becomes
    ts_field: str = "ts"  # jsonl only: the line field carrying the row's ts ("t" on audit)


#: Families with a legacy JSON source to lazy-migrate. marker_state's legacy dir is the
#: prompt-markers sibling of the tracking root (D5/D6; P1/P2 register their families here).
#: The task-family entries are NOT registered here: tracker_lib opens the store with its own
#: family set (its path constants are redirectable per test), built by _task_families() —
#: see tracker_lib. Consumers that never import tracker_lib use this default set.
FAMILIES: dict[str, Family] = {
    "marker_state": Family(
        table="marker_state",
        db="tracking",
        legacy_path=lambda: tracking_db_path().parent.parent / "prompt-markers"
        / "marker-state.json",
        legacy_kind="map",
    ),
}


def _user_root() -> Path:
    """The root .ai-badger user artifacts resolve against: USER_ROOT env, else the real home."""
    env = os.environ.get(USER_ROOT_ENV)
    return Path(env) if env else _DEFAULT_HOME / ".ai-badger"


#: The user-DB families (P1.1): schema and legacy paths land here; each family's import wiring
#: ("deferred" -> a real kind) lands with the lane that rewires its writer, so no store open
#: imports or renames a source its writer still owns (D5/D6). awm_state flipped to its real
#: kind with P1.2a's awm rewiring, awm_decisions to "jsonl" with P1.2b's decision rewiring,
#: commit_reminder/pending_feedback with P1.3's rewiring, searches to "recent" with P1.4's
#: memory-grade rewiring. Until a family flips it has neither dual-read nor lazy import.
USER_FAMILIES: dict[str, Family] = {
    "awm_state": Family(
        table="awm_state",
        db="user",
        # ~/.claude/awm is not a .ai-badger artifact: it follows the real home, never
        # AI_BADGER_USER_ROOT (the snapshot also keeps the suite's $HOME redirect from moving it).
        legacy_path=lambda: _DEFAULT_HOME / ".claude" / "awm" / "state.json",
        legacy_kind="awm",
    ),
    "awm_decisions": Family(
        table="awm_decisions",
        db="user",
        legacy_path=lambda: _DEFAULT_HOME / ".claude" / "awm" / "decisions.jsonl",
        legacy_kind="jsonl",  # flipped from "deferred" by P1.2b's decision-log rewiring
    ),
    "commit_reminder": Family(
        table="commit_reminder",
        db="user",
        legacy_path=lambda: _user_root() / "commit-reminder" / "state.json",
        legacy_kind="map",  # flipped from "deferred" by P1.3's commit-reminder rewiring
    ),
    "commit_reminder_pending": Family(
        table="commit_reminder",
        db="user",
        legacy_path=lambda: _user_root() / "commit-reminder" / "pending.json",
        legacy_kind="kvdoc",  # flipped from "deferred" by P1.3: the stash doc is one row
        row_key="pending",
    ),
    "pending_feedback": Family(
        table="pending_feedback",
        db="user",
        legacy_path=lambda: _user_root() / "pending-feedback.json",
        legacy_kind="kvdoc",  # flipped from "deferred" by P1.3's grounded-feedback rewiring
        row_key="pending",  # the kvdoc row key its import lands under
    ),
    "searches": Family(
        table="searches",
        db="user",
        legacy_path=lambda: _user_root() / "memory-grade" / "searches.json",
        legacy_kind="recent",  # flipped from "deferred" by P1.4's memory-grade rewiring
    ),
    # P2.0b session-store families (D10): the seven session-scoped surfaces. The file-set
    # kinds (FILE_SET_KINDS) import a whole legacy set in one transaction and rename per
    # file ("<stem>.migrated<suffix>"); the legacy DIRECTORY itself never moves (D5). The
    # natural keys: session id / session id / lane id / legacy hash verbatim (D4, all
    # worktrees of a repo share one record) / filename stem (session AND project).
    "memory_first": Family(
        table="memory_first",
        db="user",
        legacy_path=lambda: _user_root() / "memory-first",
        legacy_kind="markers",  # empty <uuid> presence markers + <uuid>.denials sidecars
    ),
    "semantica_nudge": Family(
        table="semantica_nudge",
        db="user",
        legacy_path=lambda: _user_root() / "semantica-nudge",
        legacy_kind="nudges",  # flat empty <uuid> files: presence means the nudge was shown
    ),
    "dispatch_lanes": Family(
        table="dispatch_lanes",
        db="user",
        legacy_path=lambda: _user_root() / "dispatch-lanes",
        legacy_kind="lanes",  # one non-JSON file per lane: "<epoch-float> <tool_use_id>" lines
    ),
    "dirty_sweeps": Family(
        table="dirty_sweeps",
        db="user",
        legacy_path=lambda: _user_root() / "dirty-sweep-*.json",
        legacy_kind="kv_glob",  # the filename's legacy hash is the key verbatim (D4)
    ),
    "blast_radius_denials": Family(
        table="blast_radius_denials",
        db="user",
        legacy_path=lambda: _user_root() / "blast-radius-guard",
        legacy_kind="stem_denials",  # <session>.<project-hash>.denials: the stem is the key
    ),
    "hook_audit": Family(
        table="hook_audit",
        db="user",
        legacy_path=lambda: _user_root() / "debug" / "audit.jsonl",
        legacy_kind="jsonl",
        ts_field="t",  # audit lines carry their timestamp in "t", not "ts"
    ),
    "hook_state": Family(
        table="hook_state",
        db="user",
        legacy_path=lambda: _user_root() / "debug" / "state.json",
        legacy_kind="kvdoc",  # one whole state document (D26), the pending-feedback pattern
        row_key="debug",
    ),
    # Message-bus families (P1, D2): born in SQLite — no legacy_path, no import wiring.
    # Their DDL arrives through UPGRADE_HOOKS[1], not the v1 base _DDL.
    "messages": Family(table="messages", db="user", legacy_kind="store"),
    "cursors": Family(table="cursors", db="user", legacy_kind="store"),
}

# -- task-family shapes: legacy entry key <-> row column -----------------------------
# Direct text columns; read back only when the column is non-NULL, so an entry never grows
# keys its writer did not set (stop_hook pins "reminder flag not in entry" until sent).
_TASK_TEXT_COLUMNS = {
    "taskId": "task_id", "title": "title", "sessionId": "session_id",
    "cwd": "cwd", "branch": "branch", "transcriptPath": "transcript_path",
    "resumeCommand": "resume_command", "startedAt": "started_at",
    "finishedAt": "finished_at", "state": "state", "trackingSource": "tracking_source",
}
_TASK_JSON_COLUMNS = {"resumeAttempts": "resume_attempts"}
_TASK_FLAG_COLUMNS = {
    "stateJsonUpdated": "state_json_updated",
    "stateJsonReminderSent": "state_json_reminder_sent",
    "compactionReminderSent": "compaction_reminder_sent",
}
_USAGE_TEXT_COLUMNS = {
    "sessionId": "session_id", "trackingSource": "tracking_source", "gradedAt": "graded_at",
}
_USAGE_JSON_COLUMNS = {"subagents": "subagents", "checkpoints": "checkpoints",
                       "usage": "usage"}
_SESSION_INFO_COLUMNS = {
    "transcriptPath": "transcript_path", "cwd": "cwd", "pid": "pid",
    "recordedAt": "recorded_at",
}

# Legacy residue with no live writer, intentionally dropped on migration (P0.6a finding 10):
# ``risk`` (executed-tasks) and ``note`` (token-usage). Recorded here so a future
# archaeologist does not re-add them.


def _dump(value, default):
    return json.dumps(value if value is not None else default)


def task_row_values(entry: dict) -> dict:
    """One executed-tasks entry as a tasks-row value dict (the single encode for import+writes)."""
    values = {column: entry.get(key) for key, column in _TASK_TEXT_COLUMNS.items()}
    values["state"] = values["state"] or "STARTED"
    values["resume_attempts"] = _dump(entry.get("resumeAttempts"), [])
    for key, column in _TASK_FLAG_COLUMNS.items():
        values[column] = 1 if entry.get(key) else 0
    return values


def task_entry(row: dict) -> dict:
    """One tasks row back as an executed-tasks entry (the single decode for reads)."""
    entry = {key: row[column] for key, column in _TASK_TEXT_COLUMNS.items()
             if row.get(column) is not None}
    entry["resumeAttempts"] = json.loads(row.get("resume_attempts") or "[]")
    for key, column in _TASK_FLAG_COLUMNS.items():
        if row.get(column):
            entry[key] = True
    return entry


def usage_row_values(entry: dict) -> dict:
    """One token-usage entry as a token_usage-row value dict."""
    values = {column: entry.get(key) for key, column in _USAGE_TEXT_COLUMNS.items()}
    values["task_id"] = entry.get("taskId")
    values["grade"] = None if entry.get("grade") is None else json.dumps(entry["grade"])
    for key, column in _USAGE_JSON_COLUMNS.items():
        values[column] = _dump(entry.get(key), [] if key == "subagents" else {})
    return values


def usage_entry(row: dict) -> dict:
    """One token_usage row back as a token-usage entry."""
    entry = {key: row[column] for key, column in _USAGE_TEXT_COLUMNS.items()
             if row.get(column) is not None}
    entry["taskId"] = row["task_id"]
    if row.get("grade") is not None:
        entry["grade"] = json.loads(row["grade"])
    for key, column in _USAGE_JSON_COLUMNS.items():
        entry[key] = json.loads(row.get(column) or ("[]" if key == "subagents" else "{}"))
    return entry


def session_row_values(session_id: str, info) -> dict:
    """One current-session entry as a sessions-row value dict."""
    values = {"session_id": session_id}
    for key, column in _SESSION_INFO_COLUMNS.items():
        values[column] = info.get(key) if isinstance(info, dict) else None
    return values


def session_info(row: dict) -> dict:
    """One sessions row back as a current-session info dict."""
    return {key: row[column] for key, column in _SESSION_INFO_COLUMNS.items()
            if row.get(column) is not None}



#: The file-set kinds (P2.0b, D10): families whose legacy source is many files — a legacy
#: directory's children (``markers``/``nudges``/``lanes``/``stem_denials``) or a pattern at
#: the user root (``kv_glob``). See Store._migrate_file_set for the import contract.
FILE_SET_KINDS = frozenset({"markers", "nudges", "lanes", "kv_glob", "stem_denials"})


def _file_set_paths(family: Family) -> list[Path]:
    """A file-set family's not-yet-migrated legacy files, sorted for deterministic resume.

    Directory kinds enumerate the legacy directory's files; the ``kv_glob`` kind expands
    its pattern under the parent (the dirty-sweep files live at the user root, D4).
    Migrated names ("<stem>.migrated<suffix>"), the lock file, and subdirectories never
    re-import. Module-level so the doctor's read-only scan can enumerate without a store.
    """
    path = family.legacy_path()
    if family.legacy_kind == "kv_glob":
        candidates = path.parent.glob(path.name)
    elif path.is_dir():
        candidates = path.iterdir()
    else:
        return []
    return sorted(
        candidate for candidate in candidates
        if candidate.is_file() and candidate.name != ".write.lock"
        and ".migrated" not in candidate.name
    )


def _sweep_key(name: str) -> str:
    """A dirty-sweep filename's legacy hash verbatim: dirty-sweep-<hash>.json -> <hash> (D4)."""
    return name[len("dirty-sweep-"):-len(".json")]


def _awm_projects(data: dict) -> dict:
    """An away-mode state document's per-project entries, keyed by project path.

    Both on-disk shapes: the #296 per-project form ({"projects": {path: entry}}) and the
    pre-#296 single-project form whose top level IS the entry (it names its own "project").
    """
    projects = data.get("projects")
    if isinstance(projects, dict):
        return dict(projects)
    if data.get("project"):
        return {data["project"]: data}
    return {}


def _stamp_key(table: str) -> str:
    return f"migrated_at.{table}"


def _check_table_name(table: str) -> None:
    """Refuse anything that is not a plain identifier before it reaches an f-string SQL slot."""
    if not _TABLE_NAME.match(table):
        raise ValueError(f"not a store table name: {table!r}")


#: Observers of committed store writes, invoked with the path that was written (D24). The
#: suite's write-attribution marking registers here so the conftest leak-guards keep working
#: once task state moves from JSON files into this store. Never raises into the writer.
WRITE_OBSERVERS: list[Callable[[Path], None]] = []


def notify_write(path: Path) -> None:
    """Tell every observer one store write committed at *path*; a broken observer is ignored."""
    for observer in WRITE_OBSERVERS:
        try:
            observer(path)
        except Exception:  # pylint: disable=broad-exception-caught
            pass  # a diagnostic does not get to fail the write it observes



@contextlib.contextmanager
def _legacy_lock(lock_path: Path) -> Iterator[None]:
    """Hold the legacy writers' ``.write.lock`` flock so import never races a legacy write (D5b)."""
    if fcntl is None:  # pragma: no cover - Windows: no legacy flock convention to honour
        yield
        return
    lock_path.parent.mkdir(parents=True, exist_ok=True)
    fd = os.open(str(lock_path), os.O_CREAT | os.O_RDWR, 0o600)
    try:
        fcntl.flock(fd, fcntl.LOCK_EX)  # blocking: wait out the legacy writer
        try:
            yield
        finally:
            fcntl.flock(fd, fcntl.LOCK_UN)
    finally:
        os.close(fd)


class Store:  # pylint: disable=too-many-public-methods  # one accessor per store surface
    """One open SQLite store: the raw connection plus KV accessors and lazy family migration.

    ``families`` selects which legacy JSON sources this store knows about; the default is the
    module's FAMILIES registry, and tracker_lib passes its own (redirectable) task-family set.
    """

    def __init__(self, conn: sqlite3.Connection, db_path: Path, kind: str,
                 families: Optional[dict] = None) -> None:
        self.conn = conn
        self.db_path = db_path
        self.kind = kind
        self.families = FAMILIES if families is None else families
        # Per-family containment (M2): families whose legacy file resurrected, recorded at
        # open by _check_resurrections — {family name: resurrected legacy path}. Keyed by
        # FAMILY, not table: two families share table "statusline", and containing one
        # must leave its delegate sibling working.
        self._contained: dict[str, Path] = {}

    def contained_families(self) -> dict[str, str]:
        """The families recorded unavailable at open: {family name: resurrected path}.

        The read-only view the doctor and callers report on; empty means every family is
        live. A contained family refuses on access until repaired or re-named.
        """
        return {name: str(path) for name, path in self._contained.items()}

    def _contained_error(self, name: str) -> sqlite3.OperationalError:
        """The refusal for a contained family — the resurrection error, upgrade pointer
        included: the condition surfaces on every access, never silently (D5c)."""
        return sqlite3.OperationalError(
            f"legacy {self._contained[name]} reappeared after its migration (a stale "
            f"surface is writing behind the store); restore the *.migrated.json name, "
            f"den-refresh the stale surface, or run badger_store.py doctor --repair — "
            f"the store refuses to diverge")

    def _refuse_contained_table(self, table: str) -> None:
        """Refuse a whole-table access while any of its families is contained (D5c):
        the merged view the table's accessors serve cannot be completed without the
        contained family's file."""
        for name, family in self.families.items():
            if family.table == table and name in self._contained:
                raise self._contained_error(name)

    def _refuse_contained_kv(self, table: str, key: str) -> None:
        """Refuse a keyed access when a contained family owns *key* (M2, per family):
        a kvdoc family owns exactly its row_key, so its table siblings keep working; any
        other kind's file could hold any key, so it refuses the whole table."""
        for name, family in self.families.items():
            if family.table != table or name not in self._contained:
                continue
            if family.legacy_kind != "kvdoc" or key == family.row_key:
                raise self._contained_error(name)

    def _family_name(self, family: Family) -> Optional[str]:
        """The registry name a family object was registered under, if it still is."""
        for name, candidate in self.families.items():
            if candidate == family:
                return name
        return None

    def close(self) -> None:
        """Close the connection; sidecars disappear with the last open WAL connection."""
        with contextlib.suppress(sqlite3.Error):
            self.conn.close()

    # -- reads (fail open, D31) --------------------------------------------------------

    def kv_get(self, table: str, key: str, default: Any = None) -> Any:
        """The value for *key*: the DB row when present, else the legacy row, else *default*.

        A contained family owning *key* refuses before anything is served (D5c).
        """
        _check_table_name(table)
        self._refuse_contained_kv(table, key)
        try:
            row = self.conn.execute(
                f"SELECT value FROM {table} WHERE key = ?", (key,)
            ).fetchone()
        except sqlite3.Error:
            return default  # D31: a broken store never blocks a caller
        if row is not None:
            return self._decode(row[0], default)
        legacy = self._legacy_rows(table)  # raises on resurrection: never diverge (D5c)
        return legacy.get(key, default)

    def kv_all(self, table: str) -> dict:
        """Every key of *table*: DB rows merged with legacy-only rows (per-key LWW, D5a).

        Refuses while any family of the table is contained — a merged view cannot be
        completed without the contained family's file (D5c).
        """
        _check_table_name(table)
        self._refuse_contained_table(table)
        try:
            rows = {
                key: self._decode(value, None)
                for key, value in self.conn.execute(f"SELECT key, value FROM {table}")
            }
        except sqlite3.Error:
            return {}  # D31: a broken store never blocks a caller
        for key, value in self._legacy_rows(table).items():
            rows.setdefault(key, value)
        return {key: value for key, value in rows.items() if value is not None}

    @staticmethod
    def _decode(raw: str, default: Any) -> Any:
        try:
            return json.loads(raw)
        except (TypeError, ValueError):
            return default  # a corrupt row reads as absence, never a crash (D31)

    def _migration_stamp(self, table: str) -> Optional[float]:
        """When this family's import committed, or None before the first migration."""
        try:
            row = self.conn.execute(
                "SELECT value FROM meta WHERE key = ?", (_stamp_key(table),)
            ).fetchone()
        except sqlite3.Error:
            return None
        if row is None:
            return None
        try:
            return float(row[0])
        except (TypeError, ValueError):
            return None

    def _families_for_table(self, table: str) -> list:
        return [family for family in self.families.values() if family.table == table]

    def _named_families_for_table(self, table: str) -> list:
        """(registry name, family) pairs for *table* — the name keys containment state."""
        return [(name, family) for name, family in self.families.items()
                if family.table == table]

    def _raise_on_resurrection(self, path: Path, table: str) -> None:
        """A legacy file newer than its migration stamp fails closed: never diverge (D5c)."""
        stamp = self._migration_stamp(table)
        if stamp is not None and path.stat().st_mtime > stamp:
            raise sqlite3.OperationalError(
                f"legacy {path} reappeared after its migration (a stale surface is writing "
                f"behind the store); restore the *.migrated.json name, den-refresh the "
                f"stale surface, or run badger_store.py doctor --repair — the store "
                f"refuses to diverge"
            )

    def _legacy_rows(self, table: str) -> dict:
        """Legacy KV rows still mergeable for *table*; a resurrected legacy file fails closed."""
        merged: dict = {}
        for family in self._families_for_table(table):
            if family.legacy_kind == "kv_glob":
                if self._family_name(family) in self._contained:
                    # contained kv_glob: the legacy read refuses, it never merges (M2)
                    raise self._contained_error(self._family_name(family))
                self._raise_on_family_resurrection(family)
                for path in self._file_set_files(family):
                    try:
                        doc = json.loads(path.read_text(encoding="utf-8"))
                    except (OSError, ValueError):
                        continue  # unreadable legacy file: the DB rows stay authoritative (D31)
                    merged[_sweep_key(path.name)] = doc
                continue
            if family.legacy_kind not in ("map", "kvdoc", "awm"):
                continue
            path = family.legacy_path()
            if not path.exists():
                continue
            if self._family_name(family) in self._contained:
                continue  # contained: skipped here, its accessors refuse on access (M2)
            self._raise_on_resurrection(path, family.table)
            try:
                data = json.loads(path.read_text(encoding="utf-8"))
            except (OSError, ValueError):
                continue  # unreadable legacy file: the DB rows stay authoritative (D31)
            if not isinstance(data, dict):
                continue
            if family.legacy_kind == "kvdoc":
                merged[family.row_key] = data
            elif family.legacy_kind == "awm":
                merged.update(_awm_projects(data))
            else:
                merged.update(data)
        return merged

    def _check_resurrections(self) -> None:
        """Open-time gate (D5c), recorded per family (M2): a legacy file newer than its
        migration stamp CONTAINS that family instead of aborting the store — the store
        opens, contained_families() names them, and every accessor of a contained family
        refuses with the resurrection error until it is repaired."""
        for name, family in self.families.items():
            if family.db != self.kind:
                continue
            if family.legacy_path is None:
                continue  # born in SQLite: no legacy source to resurrect (D2)
            if family.legacy_kind in FILE_SET_KINDS:
                stamp = self._migration_stamp(family.table)
                if stamp is None:
                    continue
                for path in self._file_set_files(family):
                    if path.stat().st_mtime > stamp:
                        self._contained[name] = path
                        break
                continue
            path = family.legacy_path()
            if not path.exists():
                continue
            stamp = self._migration_stamp(family.table)
            if stamp is not None and path.stat().st_mtime > stamp:
                self._contained[name] = path

    def _file_set_files(self, family: Family) -> list[Path]:
        """A file-set family's not-yet-migrated legacy files, sorted for deterministic resume."""
        return _file_set_paths(family)

    def _raise_on_family_resurrection(self, family: Family) -> None:
        """A file-set family's original file newer than its migration stamp fails closed (D5c)."""
        stamp = self._migration_stamp(family.table)
        if stamp is None:
            return
        for path in self._file_set_files(family):
            if path.stat().st_mtime > stamp:
                raise sqlite3.OperationalError(
                    f"legacy {path} reappeared after its migration (a stale surface is writing "
                    f"behind the store); den-refresh the stale surface or run "
                    f"badger_store.py doctor --repair — the store refuses to diverge"
                )

    # -- task-family rows (caller-managed transactions; see tracking_transaction there) ---

    def _row_map(self, table: str, key_column: str) -> dict:
        """Every row of *table* keyed on its natural-key column, in insertion order."""
        columns = [row[1] for row in self.conn.execute(f"PRAGMA table_info({table})")]
        if key_column not in columns:
            raise sqlite3.OperationalError(f"table {table} has no column {key_column}")
        return {
            record[key_column]: record
            for record in (dict(zip(columns, row))
                           for row in self.conn.execute(f"SELECT * FROM {table} ORDER BY rowid"))
            if record.get(key_column) is not None
        }

    def _family_entries(self, family: Family) -> list:
        """A row-kind family's legacy entries, verbatim; a resurrected file fails closed."""
        path = family.legacy_path()
        if not path.exists():
            return []
        self._raise_on_resurrection(path, family.table)
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            return []  # unreadable legacy file: the DB rows stay authoritative (D31)
        if not isinstance(data, dict):
            return []
        entries = data.get("tasks")
        return [entry for entry in entries if isinstance(entry, dict)] \
            if isinstance(entries, list) else []

    def tasks_all(self) -> list:
        """Every task entry: DB rows merged with legacy-only entries, DB wins per key (D5a).

        Refuses while the tasks family is contained (D5c) — the legacy side of the merge
        cannot be proven absent.
        """
        _check_table_name("tasks")
        self._refuse_contained_table("tasks")
        try:
            merged = {task_id: task_entry(row)
                      for task_id, row in self._row_map("tasks", "task_id").items()}
        except sqlite3.Error:
            merged = {}  # D31: a broken store never blocks a reader
        for family in self._families_for_table("tasks"):
            for entry in self._family_entries(family):
                task_id = entry.get("taskId")
                if task_id is not None:
                    merged.setdefault(task_id, entry)
        return list(merged.values())

    def stop_blocks(self) -> dict:
        """The per-session stop-hook block budget (meta bookkeeping; no ruled column carries it)."""
        value = self.meta_get("stopBlocks", {})
        return value if isinstance(value, dict) else {}

    def usage_all(self) -> list:
        """Every token-usage entry: DB rows merged with legacy-only entries (D5a).

        Refuses while the token_usage family is contained, like tasks_all (D5c).
        """
        _check_table_name("token_usage")
        self._refuse_contained_table("token_usage")
        try:
            merged = {task_id: usage_entry(row)
                      for task_id, row in self._row_map("token_usage", "task_id").items()}
        except sqlite3.Error:
            merged = {}
        for family in self._families_for_table("token_usage"):
            for entry in self._family_entries(family):
                task_id = entry.get("taskId")
                if task_id is not None:
                    merged.setdefault(task_id, entry)
        return list(merged.values())

    def sessions_map(self) -> dict:
        """Every known session as {sessionId: info}: DB rows merged with legacy rows (D5a).

        Refuses while the sessions family is contained, like tasks_all (D5c).
        """
        _check_table_name("sessions")
        self._refuse_contained_table("sessions")
        try:
            merged = {session_id: session_info(row)
                      for session_id, row in self._row_map("sessions", "session_id").items()}
        except sqlite3.Error:
            merged = {}
        for family in self._families_for_table("sessions"):
            path = family.legacy_path()
            if not path.exists():
                continue
            self._raise_on_resurrection(path, family.table)
            try:
                data = json.loads(path.read_text(encoding="utf-8"))
            except (OSError, ValueError):
                continue  # unreadable legacy file: the DB rows stay authoritative (D31)
            sessions = data.get("sessions") if isinstance(data, dict) else None
            if not isinstance(sessions, dict):
                continue
            for session_id, info in sessions.items():
                if isinstance(info, dict):
                    merged.setdefault(session_id, info)
        return merged

    def task_upsert(self, entry: dict) -> None:
        """Insert or explicitly UPDATE one tasks row keyed on task_id.

        Never INSERT OR REPLACE: against tasks it silently deletes the session's other ACTIVE
        row (P0.6a MUST-2, scratch-verified), bypassing the exit-2 attach contract.
        Refuses while the tasks family is contained — a write behind a resurrected file
        would deepen the divergence the containment exists to surface (D5c).
        """
        self._refuse_contained_table("tasks")
        values = task_row_values(entry)
        task_id = values["task_id"]
        if not task_id:
            raise ValueError("task entry without taskId")
        columns = list(values)
        placeholders = ", ".join("?" for _ in columns)
        assignments = ", ".join(f"{column} = ?" for column in columns)
        existing = self.conn.execute(
            "SELECT id FROM tasks WHERE task_id = ?", (task_id,)
        ).fetchone()
        if existing:
            self.conn.execute(
                f"UPDATE tasks SET {assignments} WHERE id = ?",
                (*values.values(), existing[0]),
            )
        else:
            self.conn.execute(
                f"INSERT INTO tasks({', '.join(columns)}) VALUES ({placeholders})",
                tuple(values.values()),
            )

    def usage_upsert(self, entry: dict) -> None:
        """Insert or explicitly UPDATE one token_usage row keyed on its primary key.

        Refuses while the token_usage family is contained, like task_upsert (D5c).
        """
        self._refuse_contained_table("token_usage")
        values = usage_row_values(entry)
        if not values["task_id"]:
            raise ValueError("usage entry without taskId")
        columns = list(values)
        assignments = ", ".join(f"{column} = ?" for column in columns)
        existing = self.conn.execute(
            "SELECT task_id FROM token_usage WHERE task_id = ?", (values["task_id"],)
        ).fetchone()
        if existing:
            self.conn.execute(
                f"UPDATE token_usage SET {assignments} WHERE task_id = ?",
                (*values.values(), values["task_id"]),
            )
        else:
            self.conn.execute(
                f"INSERT INTO token_usage({', '.join(columns)}) "
                f"VALUES ({', '.join('?' for _ in columns)})",
                tuple(values.values()),
            )

    def session_upsert(self, session_id: str, info: dict) -> None:
        """Insert or explicitly UPDATE one sessions row keyed on the session id.

        Refuses while the sessions family is contained, like task_upsert (D5c).
        """
        self._refuse_contained_table("sessions")
        values = session_row_values(session_id, info)
        columns = list(values)
        assignments = ", ".join(f"{column} = ?" for column in columns)
        existing = self.conn.execute(
            "SELECT session_id FROM sessions WHERE session_id = ?", (session_id,)
        ).fetchone()
        if existing:
            self.conn.execute(
                f"UPDATE sessions SET {assignments} WHERE session_id = ?",
                (*values.values(), session_id),
            )
        else:
            self.conn.execute(
                f"INSERT INTO sessions({', '.join(columns)}) "
                f"VALUES ({', '.join('?' for _ in columns)})",
                tuple(values.values()),
            )

    def session_delete(self, session_id: str) -> None:
        """Drop one session row (the current-session prune of dead pids)."""
        self.conn.execute("DELETE FROM sessions WHERE session_id = ?", (session_id,))

    def meta_get(self, key: str, default=None):
        """One meta row as JSON, or *default* when absent or unparsable."""
        try:
            row = self.conn.execute("SELECT value FROM meta WHERE key = ?", (key,)).fetchone()
        except sqlite3.Error:
            return default
        if row is None:
            return default
        try:
            return json.loads(row[0])
        except (TypeError, ValueError):
            return default

    def meta_set(self, key: str, value) -> None:
        """Upsert one meta row as JSON (caller-managed transaction)."""
        self.conn.execute(
            "INSERT INTO meta(key, value) VALUES (?, ?) "
            "ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            (key, json.dumps(value)),
        )

    # -- retention seam (the open-time caller and full prune UX land with P2.3; D9/D17c) --

    def prune_expired(self, table: str, *, max_age_days: int = 60) -> int:
        """Delete log rows whose ``ts`` predates the age cutoff; return the deleted row count.

        The per-table ``pruned_at.<table>`` meta stamp throttles: a second call inside the
        window deletes nothing (returns 0) even when rows expired since — the next window
        catches them. Stamp check, DELETE, and stamp rewrite share one BEGIN IMMEDIATE, so
        the throttle has no check-then-act window; every sqlite failure fails open as 0 (D9).
        """
        _check_table_name(table)
        stamp_key = f"pruned_at.{table}"
        try:
            row = self.conn.execute(
                "SELECT value FROM meta WHERE key = ?", (stamp_key,)
            ).fetchone()
        except sqlite3.Error:
            return 0
        if row is not None:
            try:
                last = float(json.loads(row[0]))
            except (TypeError, ValueError):
                last = None
            if last is not None and time.time() - last < _PRUNE_THROTTLE_SECONDS:
                return 0
        try:
            cutoff = (datetime.now(timezone.utc) - timedelta(days=max_age_days)).isoformat()
            self.conn.execute("BEGIN IMMEDIATE")
            try:
                cursor = self.conn.execute(f"DELETE FROM {table} WHERE ts < ?", (cutoff,))
                pruned = cursor.rowcount + self._sweep_unparseable_ts(table)
                self.conn.execute(
                    "INSERT OR REPLACE INTO meta(key, value) VALUES (?, ?)",
                    (stamp_key, json.dumps(time.time())),
                )
                self.conn.commit()
            except BaseException:
                self.conn.rollback()
                raise
        except sqlite3.Error:
            return 0  # a broken store never blocks a caller on maintenance (D31)
        return pruned

    def _sweep_unparseable_ts(self, table: str) -> int:
        """Delete rows whose ts never parses; return the count. Caller holds the write txn.

        A row that never parses can never satisfy any future cutoff, so string comparison
        alone leaves it immortal (D36). The scan is hour-throttled like the prune itself
        and the tables it touches are retention-bounded, so the full pass stays cheap.
        """
        rows = self.conn.execute(f"SELECT rowid, ts FROM {table}").fetchall()
        dead = [row[0] for row in rows if not _parseable_ts(row[1])]
        for rowid in dead:
            self.conn.execute(f"DELETE FROM {table} WHERE rowid = ?", (rowid,))
        return len(dead)

    # -- writes (may raise) ------------------------------------------------------------

    def kv_set(self, table: str, key: str, value: Any) -> None:
        """Write *value* under *key*; the first write lazy-migrates the family (D6).

        A contained family owning *key* refuses before anything is written (D5c).
        """
        _check_table_name(table)
        self._refuse_contained_kv(table, key)
        self.migrate(table)
        self.conn.execute("BEGIN IMMEDIATE")
        try:
            self.conn.execute(
                f"INSERT OR REPLACE INTO {table}(key, value, updated_at) VALUES (?, ?, ?)",
                (key, json.dumps(value), _now()),
            )
            self.conn.commit()
        except BaseException:
            self.conn.rollback()
            raise
        _assert_file_perms(self.db_path)
        notify_write(self.db_path)

    def kv_delete(self, table: str, key: str) -> None:
        """Drop one KV row; the first write lazy-migrates the family (D6), like kv_set."""
        _check_table_name(table)
        self._refuse_contained_kv(table, key)
        self.migrate(table)
        self.conn.execute("BEGIN IMMEDIATE")
        try:
            self.conn.execute(f"DELETE FROM {table} WHERE key = ?", (key,))
            self.conn.commit()
        except BaseException:
            self.conn.rollback()
            raise
        _assert_file_perms(self.db_path)
        notify_write(self.db_path)

    def kv_update(self, table: str, key: str, fn, default: Any = None) -> Any:
        """Atomic read-modify-write of one KV row: fn(current) -> new value, one transaction.

        The SELECT and the write share one BEGIN IMMEDIATE, so concurrent updaters of the
        same row serialize on the write lock instead of racing a read-then-write gap —
        the shape pop-style stash consumers need. Returns fn's result. A contained family
        owning *key* refuses before anything is written (D5c).
        """
        _check_table_name(table)
        self._refuse_contained_kv(table, key)
        self.migrate(table)
        self.conn.execute("BEGIN IMMEDIATE")
        try:
            row = self.conn.execute(
                f"SELECT value FROM {table} WHERE key = ?", (key,)
            ).fetchone()
            current = default if row is None else self._decode(row[0], default)
            updated = fn(current)
            self.conn.execute(
                f"INSERT OR REPLACE INTO {table}(key, value, updated_at) VALUES (?, ?, ?)",
                (key, json.dumps(updated), _now()),
            )
            self.conn.commit()
        except BaseException:
            self.conn.rollback()
            raise
        _assert_file_perms(self.db_path)
        notify_write(self.db_path)
        return updated

    def log_rows(self, table: str) -> list:
        """Every (ts, payload) row of a log table in append order; [] on any failure (D31).

        The read side of log_append: consumers merge these rows with their legacy sources
        instead of querying the schema directly. Payloads stay encoded — a payload that
        never decodes is the caller's skip, not a store crash.
        """
        _check_table_name(table)
        try:
            return self.conn.execute(
                f"SELECT ts, payload FROM {table} ORDER BY id").fetchall()
        except sqlite3.Error:
            return []

    def log_rows_since(self, table: str, cutoff_ts: str) -> list:
        """(ts, payload) rows with ``ts >= cutoff_ts``, append order; [] on any failure.

        The windowed read beside log_rows: a consumer answering a short recency window
        must not read and decode the whole retention-bounded table (the memory-grade hook
        measured linearly, 59 ms at 20k rows) — the ts-index DDL convention (D17c) makes
        the cutoff a seek. Comparisons are the prune's ISO string comparison, so the
        cutoff must carry the isoformat shape the writes store (iso_row_ts output).
        """
        _check_table_name(table)
        try:
            return self.conn.execute(
                f"SELECT ts, payload FROM {table} WHERE ts >= ? ORDER BY id",
                (cutoff_ts,)).fetchall()
        except sqlite3.Error:
            return []

    def log_append(self, table: str, ts: str, payload: dict) -> None:
        """Append one log row (ts, payload JSON); the first write lazy-migrates the family (D6).

        Refuses while any family of the table is contained: the append would silently
        deepen the divergence the resurrected file carries (D5c, reviewer M1).
        """
        _check_table_name(table)
        self._refuse_contained_table(table)
        self.migrate(table)
        self.conn.execute("BEGIN IMMEDIATE")
        try:
            self.conn.execute(
                f"INSERT INTO {table}(ts, payload) VALUES (?, ?)",
                (ts, json.dumps(payload)),
            )
            self.conn.commit()
        except BaseException:
            self.conn.rollback()
            raise
        _assert_file_perms(self.db_path)
        notify_write(self.db_path)

    def migrate(self, table: str) -> None:
        """Import every legacy source for *table* — COMMIT first, rename after (D6).

        Idempotent: re-import after a crash between COMMIT and rename adds no duplicate rows,
        and a legacy file whose mtime postdates the migration stamp fails closed (D5c) instead
        of diverging. Row-kind families carry a post-import count check because INSERT OR
        IGNORE silently drops rows violating NOT NULL/CHECK (P0.6a finding 3).
        Contained families are SKIPPED, not imported: their file is quarantined by
        containment and the refusing accessors surface it, so a transaction's other
        tables proceed (M2) — a direct write accessor to the contained table still
        refuses before reaching here.
        """
        for name, family in self._named_families_for_table(table):
            if name in self._contained:
                continue
            self._migrate_family(family)

    def _migrate_family(self, family: Family, force: bool = False) -> None:
        """Import one family's legacy doc; *force* (doctor repair) re-imports despite
        containment and a newer mtime — the import itself stays idempotent."""
        if family.legacy_kind in ("deferred", "store"):
            return  # deferred: wiring lands with the writer lane; store: born in SQLite (D2)
        if family.legacy_kind in FILE_SET_KINDS:
            self._migrate_file_set(family, force=force)
            return
        name = self._family_name(family)
        if not force and name is not None and name in self._contained:
            raise self._contained_error(name)  # defence in depth: migrate skips these
        path = family.legacy_path()
        if not path.exists():
            return
        if not force:
            self._raise_on_resurrection(path, family.table)
        with _legacy_lock(path.parent / ".write.lock"):
            if not path.exists():  # re-check under the lock: another writer migrated it
                return
            self.conn.execute("BEGIN IMMEDIATE")
            try:
                expected = self._import_legacy(family, path)
                missing = [key for key in expected if not self._row_exists(family.table, key)]
                if missing:
                    raise sqlite3.IntegrityError(
                        f"legacy import for {family.table} would drop {len(missing)} row(s) "
                        f"violating the schema (first: {missing[0]!r}) — fix or remove "
                        f"{path}; the store refuses to migrate with silent drops"
                    )
                self.conn.execute(
                    "INSERT OR REPLACE INTO meta(key, value) VALUES (?, ?)",
                    (_stamp_key(family.table), str(time.time())),
                )
                self.conn.commit()
            except BaseException:
                self.conn.rollback()
                raise
            # Only after COMMIT: a crash before the rename leaves both artifacts, and the
            # next write re-runs this idempotent import (D6).
            os.replace(path, path.with_name(f"{path.stem}.migrated{path.suffix}"))
        _assert_file_perms(self.db_path)
        # D17: the rename preserves the legacy file's mode — a foreign-written legacy file
        # could carry a laxer one. The migrated file holds the same data as the DB it fed,
        # so it inherits the DB's (just re-asserted) mode.
        migrated = path.with_name(f"{path.stem}.migrated{path.suffix}")
        os.chmod(migrated, os.stat(self.db_path).st_mode & 0o777)
        notify_write(self.db_path)
        notify_write(migrated)

    def _row_exists(self, table: str, key) -> bool:
        if isinstance(key, tuple):  # jsonl log rows: the (ts, payload) content key
            row = self.conn.execute(
                f"SELECT 1 FROM {table} WHERE ts = ? AND payload = ?", key
            ).fetchone()
            return row is not None
        key_column = {
            "tasks": "task_id", "token_usage": "task_id", "sessions": "session_id",
            "memory_first": "session_id", "semantica_nudge": "session_id",
            "dispatch_lanes": "lane_id",
        }.get(table, "key")
        row = self.conn.execute(
            f"SELECT 1 FROM {table} WHERE {key_column} = ?", (key,)
        ).fetchone()
        return row is not None

    def _import_legacy(self, family: Family, path: Path) -> list:
        """Insert every legacy row with OR IGNORE on the natural key; return the expected keys.

        The expected-keys list is what the post-import count check verifies: a row dropped by
        OR IGNORE (NOT NULL/CHECK violation) shows up as missing and fails the migration loudly
        instead of silently (P0.6a finding 3). Unreadable or shape-less files import nothing and
        rename anyway — quarantine, matching the map-kind behavior this module shipped with.
        """
        if family.legacy_kind == "jsonl":
            return self._import_jsonl(family, path)
        if family.legacy_kind == "recent":
            return self._import_recent(family, path)
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            return []
        if not isinstance(data, dict):
            return []
        stamp = _now()
        if family.legacy_kind == "kvdoc":
            self.conn.execute(
                f"INSERT OR IGNORE INTO {family.table}(key, value, updated_at) VALUES (?, ?, ?)",
                (family.row_key, json.dumps(data), stamp),
            )
            return [family.row_key]
        if family.legacy_kind == "map":
            for key, value in data.items():
                self.conn.execute(
                    f"INSERT OR IGNORE INTO {family.table}(key, value, updated_at) "
                    f"VALUES (?, ?, ?)",
                    (key, json.dumps(value), stamp),
                )
            return list(data)
        if family.legacy_kind == "awm":
            projects = _awm_projects(data)
            for project, entry in projects.items():
                self.conn.execute(
                    f"INSERT OR IGNORE INTO {family.table}(key, value, updated_at) "
                    f"VALUES (?, ?, ?)",
                    (project, json.dumps(entry), stamp),
                )
            return list(projects)
        if family.legacy_kind == "tasks":
            entries = data.get("tasks") if isinstance(data.get("tasks"), list) else []
            blocks = data.get("stopBlocks")
            if isinstance(blocks, dict) and blocks:
                # Doc-level residue of executed-tasks.json: the per-session stop-hook block
                # budget carries no ruled column, so it lives in meta (DO NOTHING: a re-import
                # must never clobber the newer DB state with legacy counts).
                self.conn.execute(
                    "INSERT INTO meta(key, value) VALUES ('stopBlocks', ?) "
                    "ON CONFLICT(key) DO NOTHING",
                    (json.dumps(blocks),),
                )
            for entry in entries:
                if not isinstance(entry, dict) or entry.get("taskId") is None:
                    continue  # unreachable by find_entry; not expected by the count check
                values = task_row_values(entry)
                self.conn.execute(
                    f"INSERT OR IGNORE INTO tasks({', '.join(values)}) "
                    f"VALUES ({', '.join('?' for _ in values)})",
                    tuple(values.values()),
                )
            return [entry["taskId"] for entry in entries
                    if isinstance(entry, dict) and entry.get("taskId") is not None]
        if family.legacy_kind == "usage":
            entries = data.get("tasks") if isinstance(data.get("tasks"), list) else []
            for entry in entries:
                if not isinstance(entry, dict) or entry.get("taskId") is None:
                    continue
                values = usage_row_values(entry)
                self.conn.execute(
                    f"INSERT OR IGNORE INTO token_usage({', '.join(values)}) "
                    f"VALUES ({', '.join('?' for _ in values)})",
                    tuple(values.values()),
                )
            return [entry["taskId"] for entry in entries
                    if isinstance(entry, dict) and entry.get("taskId") is not None]
        if family.legacy_kind == "sessions":
            sessions = data.get("sessions") if isinstance(data.get("sessions"), dict) else {}
            for session_id, info in sessions.items():
                values = session_row_values(session_id, info)
                self.conn.execute(
                    f"INSERT OR IGNORE INTO sessions({', '.join(values)}) "
                    f"VALUES ({', '.join('?' for _ in values)})",
                    tuple(values.values()),
                )
            return list(sessions)
        raise ValueError(f"unsupported family legacy kind: {family.legacy_kind!r}")

    def _import_jsonl(self, family: Family, path: Path) -> list:
        """One JSON object per line, no natural key: exact (ts, payload) content is the key.

        The ts comes from the line's ``ts_field`` ("t" on audit lines), normalised through
        iso_row_ts like the recent kind: the prune's sweep parses with datetime.fromisoformat,
        which on the declared floor (3.10) rejects a "Z" suffix and 9-digit fractional
        seconds, so a verbatim legacy ts would be swept as an ordinary expiry on the next
        prune (join-review finding) — every imported row must be sweep-parseable (D36). The
        verbatim LINE is stored as the payload — not a re-serialization. A line whose ts
        needs the now-fallback loses re-import identity (two imports disagree on now); the
        common case is parseable-ts lines, whose (ts, payload) key keeps the import
        idempotent (D6). A torn or non-object line imports nothing and quarantines with the
        file rename, like the doc-kind families.
        """
        expected: list = []
        try:
            lines = path.read_text(encoding="utf-8").splitlines()
        except OSError:
            return []
        for line in lines:
            if not line.strip():
                continue
            try:
                entry = json.loads(line)
            except ValueError:
                continue
            if not isinstance(entry, dict):
                continue
            ts = iso_row_ts(entry.get(family.ts_field))
            exists = self.conn.execute(
                f"SELECT 1 FROM {family.table} WHERE ts = ? AND payload = ?", (ts, line)
            ).fetchone()
            if exists is None:
                self.conn.execute(
                    f"INSERT INTO {family.table}(ts, payload) VALUES (?, ?)", (ts, line)
                )
            expected.append((ts, line))
        return expected

    def _import_recent(self, family: Family, path: Path) -> list:
        """A wrapper document's entry list as log rows: payload verbatim, row ts converted.

        The memory-grade stash (searches.json) holds {"recent": [{correlationId,
        sourceFiles, ts: <epoch>}, ...]}: each entry becomes one row — the entry document
        verbatim as the payload (its consumer does float window arithmetic on the embedded
        ts), and the entry's own ts field converted by iso_row_ts for the row's ts column,
        which the 60-day prune must parse (G0-Q2/D36). Idempotent like the jsonl kind:
        exact (ts, payload) content is the key, so a crash between COMMIT and rename
        re-imports without duplicates (D6). Non-dict entries import nothing and
        quarantine with the rename.
        """
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            return []
        entries = data.get("recent") if isinstance(data, dict) else None
        if not isinstance(entries, list):
            return []
        expected: list = []
        for entry in entries:
            if not isinstance(entry, dict):
                continue
            payload = json.dumps(entry)
            ts = iso_row_ts(entry.get(family.ts_field))
            exists = self.conn.execute(
                f"SELECT 1 FROM {family.table} WHERE ts = ? AND payload = ?", (ts, payload)
            ).fetchone()
            if exists is None:
                self.conn.execute(
                    f"INSERT INTO {family.table}(ts, payload) VALUES (?, ?)", (ts, payload)
                )
            expected.append((ts, payload))
        return expected

    # -- file-set families (P2.0b, D10): one transaction for the set, rename per file -------

    def _migrate_file_set(self, family: Family, force: bool = False) -> None:
        """Import a multi-file family: COMMIT once for the whole set, then rename per file.

        Resumable (D6/D10): a crash between COMMIT and the renames leaves rows imported and
        original files present; the next import re-reads them and OR IGNORE on the natural
        key adds nothing. Renames follow the pinned per-file convention —
        "<stem>.migrated<suffix>" in place — and the legacy DIRECTORY itself is never renamed
        or removed (D5): a stale surface may keep writing new files there, and after the
        import no original filename remains, so nothing can resurrect. Contained families
        refuse (a re-import would bless files newer than their stamp) unless *force* —
        the doctor repair's explicit idempotent re-import.
        """
        name = self._family_name(family)
        if not force and name is not None and name in self._contained:
            raise self._contained_error(name)
        with _legacy_lock(family.legacy_path().parent / ".write.lock"):
            files = self._file_set_files(family)  # re-checked under the lock
            if not files:
                return
            if not force:
                self._raise_on_family_resurrection(family)
            self.conn.execute("BEGIN IMMEDIATE")
            try:
                expected = self._import_file_set(family, files)
                missing = [key for key in expected if not self._row_exists(family.table, key)]
                if missing:
                    raise sqlite3.IntegrityError(
                        f"legacy import for {family.table} would drop {len(missing)} row(s) "
                        f"violating the schema (first: {missing[0]!r}) — fix or remove the "
                        f"legacy files under {family.legacy_path()}; the store refuses to "
                        f"migrate with silent drops"
                    )
                self.conn.execute(
                    "INSERT OR REPLACE INTO meta(key, value) VALUES (?, ?)",
                    (_stamp_key(family.table), str(time.time())),
                )
                self.conn.commit()
            except BaseException:
                self.conn.rollback()
                raise
            for path in files:  # only after COMMIT: a crash here resumes without duplicates
                migrated = path.with_name(f"{path.stem}.migrated{path.suffix}")
                os.replace(path, migrated)
                # D17: the rename preserves the legacy file's mode — a foreign-written legacy
                # file could carry a laxer one; the migrated file inherits the DB's mode.
                os.chmod(migrated, os.stat(self.db_path).st_mode & 0o777)
        _assert_file_perms(self.db_path)
        notify_write(self.db_path)
        for path in files:
            notify_write(path.with_name(f"{path.stem}.migrated{path.suffix}"))

    def _import_file_set(self, family: Family, files: list[Path]) -> list:
        """Insert every file's rows with OR IGNORE on the natural key; return the expected keys.

        The expected-keys list feeds the post-import count check shared with the doc-kind
        migration (P0.6a finding 3): a row dropped by OR IGNORE shows up as missing and fails
        the migration loudly instead of silently.
        """
        if family.legacy_kind == "markers":
            return self._import_markers(files)
        if family.legacy_kind == "nudges":
            return self._import_nudges(files)
        if family.legacy_kind == "lanes":
            return self._import_lanes(files)
        if family.legacy_kind == "kv_glob":
            return self._import_sweeps(files)
        if family.legacy_kind == "stem_denials":
            return self._import_stem_denials(files)
        raise ValueError(f"unsupported file-set kind: {family.legacy_kind!r}")

    def _import_markers(self, files: list[Path]) -> list:
        """memory-first shape: empty <uuid> presence markers plus <uuid>.denials sidecars.

        One row per session — consulted in payload, the count in the denials column; a
        denials-only session (no marker) imports with consulted=false.
        """
        sessions: dict[str, dict] = {}
        for path in files:
            if path.name.endswith(".denials"):
                session = path.name[: -len(".denials")]
                entry = sessions.setdefault(session, {"consulted": False, "denials": 0})
                entry["denials"] = int(path.read_text(encoding="utf-8").strip())
            else:
                entry = sessions.setdefault(path.name, {"consulted": False, "denials": 0})
                entry["consulted"] = True
        stamp = _now()
        for session, entry in sessions.items():
            self.conn.execute(
                "INSERT OR IGNORE INTO memory_first(session_id, payload, denials, updated_at) "
                "VALUES (?, ?, ?, ?)",
                (session, json.dumps({"consulted": entry["consulted"]}),
                 entry["denials"], stamp),
            )
        return list(sessions)

    def _import_nudges(self, files: list[Path]) -> list:
        """semantica-nudge shape: flat empty <uuid> files — one row per session, payload
        {"shown": true} (the file's presence is the whole fact)."""
        stamp = _now()
        for path in files:
            self.conn.execute(
                "INSERT OR IGNORE INTO semantica_nudge(session_id, payload, updated_at) "
                "VALUES (?, ?, ?)",
                (path.name, json.dumps({"shown": True}), stamp),
            )
        return [path.name for path in files]

    def _import_lanes(self, files: list[Path]) -> list:
        """dispatch-lanes shape: one non-JSON file per lane, lines of
        "<epoch-float> <tool_use_id>" — the whole lane's lines become the entries JSON."""
        stamp = _now()
        expected = []
        for path in files:
            entries = []
            for line in path.read_text(encoding="utf-8").splitlines():
                line = line.strip()
                if not line:
                    continue
                ts, _, tool_use_id = line.partition(" ")
                entries.append({"ts": ts, "tool_use_id": tool_use_id})
            self.conn.execute(
                "INSERT OR IGNORE INTO dispatch_lanes(lane_id, entries, updated_at) "
                "VALUES (?, ?, ?)",
                (path.name, json.dumps(entries), stamp),
            )
            expected.append(path.name)
        return expected

    def _import_sweeps(self, files: list[Path]) -> list:
        """dirty-sweep shape: dirty-sweep-<hash>.json documents at the user root — the hash
        is the natural key verbatim (D4), all worktrees of a repo sharing one record."""
        stamp = _now()
        expected = []
        for path in files:
            try:
                doc = json.loads(path.read_text(encoding="utf-8"))
            except (OSError, ValueError):
                continue  # unreadable sweep file: quarantines with the rename (D31)
            self.conn.execute(
                "INSERT OR IGNORE INTO dirty_sweeps(key, value, updated_at) VALUES (?, ?, ?)",
                (_sweep_key(path.name), json.dumps(doc), stamp),
            )
            expected.append(_sweep_key(path.name))
        return expected

    def _import_stem_denials(self, files: list[Path]) -> list:
        """blast-radius-guard shape: <session>.<project-hash>.denials integers — the filename
        stem (session AND project) is the key, the count the denials column."""
        stamp = _now()
        expected = []
        for path in files:
            self.conn.execute(
                "INSERT OR IGNORE INTO blast_radius_denials(key, denials, updated_at) "
                "VALUES (?, ?, ?)",
                (path.stem, int(path.read_text(encoding="utf-8").strip()), stamp),
            )
            expected.append(path.stem)
        return expected

    # -- message bus (P1): send, deliver, cursor lifecycle --------------------------------

    def send_message(self, *, sender_session: str, sender_project: str, content: str,
                     target_session: Optional[str] = None,
                     target_project: Optional[str] = None) -> int:
        """Store one bus message and return its row id.

        Sender identity is REQUIRED at send (R10): an empty session or project refuses
        with the missing-identity error and writes nothing. Addressing normalises at
        write (D3): a given target_session makes the row 1:1 with target_project stored
        NULL, a target_project alone is a project broadcast, neither is a machine
        broadcast — every read predicate stays single-shape. Content is a raw string.
        """
        if not sender_session:
            raise ValueError("send refused: missing sender identity (sessionId)")
        if not sender_project:
            raise ValueError("send refused: missing sender identity (projectId)")
        if not isinstance(content, str):
            raise ValueError("message content must be a raw string")
        stored_session = target_session or None
        stored_project = None if stored_session else (target_project or None)
        self.conn.execute("BEGIN IMMEDIATE")
        try:
            cursor = self.conn.execute(
                "INSERT INTO messages(ts, sender_session, sender_project, target_session, "
                "target_project, content) VALUES (?, ?, ?, ?, ?, ?)",
                (_now(), sender_session, sender_project, stored_session, stored_project,
                 content),
            )
            row_id = cursor.lastrowid
            self.conn.commit()
        except BaseException:
            self.conn.rollback()
            raise
        _assert_file_perms(self.db_path)
        notify_write(self.db_path)
        return row_id

    def deliver_for_session(self, session_id: str,
                            project_id: Optional[str] = None) -> tuple:
        """Deliver the messages addressed to *session_id* and advance its cursor — atomically.

        Returns ``(messages, summary)`` (C2): *messages* is the delivered document list
        exactly as before; *summary* is ``{"addressed": n, "broadcast": m}`` — the wake
        classification computed INSIDE this transaction from the delivered rows only,
        never re-derived by consumers. It counts the DELIVERED batch: post-gate,
        post-cap, post-R2 — gated-off rows, the sender's own rows and a not-run D7 leg
        (no *project_id* → project/broadcast shapes never enter the batch) count 0
        (QA-10/QA-11). 1:1 rows (target_session = the session) and project rows count as
        addressed; both-targets-NULL rows count as broadcast.

        The read and the cursor upsert share one BEGIN IMMEDIATE, so two hooks racing on
        one unread message serialize: exactly one injects it, both finish at the same
        cursor (R3). A session with no cursor row gates history to the last 30 minutes
        (inclusive) and caps at the first 16 oldest, cursor landing past the gated
        window — the WHOLE window when the project leg ran; only the 1:1 leg's window
        content when it ran alone (L1/R1a) — so the overflow is never revisited
        (R4/R5, D5) — in history AND live mode, since
        this is the one delivery path. Later reads are pure ``id > cursor`` and uncapped
        (A5: live broadcast volume is bounded by agent-paced sends, not by a cap).
        With no *project_id* only the 1:1 leg runs — the caller's unresolved-project
        fail-open contract (D7). A cursor above every row id (a replaced/restored DB
        whose cursors row survived — C9) reads as cursor-less for that one read, so the
        gate, cap and leg-scoped landing re-apply. Every returned document is
        ``{sender: {sessionId, projectId}, content, timestamp}`` in chronological order.
        """
        if not session_id:
            raise ValueError("delivery requires a session id")
        _hold_at("deliver.entry")
        self.conn.execute("BEGIN IMMEDIATE")
        try:
            row = self.conn.execute(
                "SELECT cursor_id FROM cursors WHERE session_id = ?", (session_id,)
            ).fetchone()
            if row is not None and row[0] > self.conn.execute(
                    "SELECT COALESCE(MAX(id), 0) FROM messages").fetchone()[0]:
                # C9 (CR-S1/QA-2): the cursor sits above every row id — a replaced or
                # restored DB whose cursors row survived (AUTOINCREMENT forbids rowid
                # reuse through this store's own writes; the strict > leaves the healthy
                # caught-up state cursor == MAX(id) alone). Treat the session as
                # cursor-less FOR THIS READ: the gate, the cap and the leg-scoped landing
                # re-apply below, and the upsert lands a sane cursor — never a plain
                # reset onto the live-read path.
                row = None
            if row is None:
                cutoff = (datetime.now(timezone.utc) - _GATE_WINDOW).isoformat()
                rows = self._read_addressed(session_id, project_id, after_id=0,
                                            since_ts=cutoff)
                delivered = rows[:_START_CAP]
                messages = [_message_document(r) for r in delivered]
                summary = self._delivery_summary(delivered, session_id)
                if project_id:
                    # All three legs ran: land past the WHOLE gated window, not past
                    # the last delivered row — the dropped overflow (and everything
                    # older) must never be revisited (R5, L1/R1c guard).
                    next_cursor = self.conn.execute(
                        "SELECT COALESCE(MAX(id), 0) FROM messages").fetchone()[0]
                else:
                    # D7 fail-open ran the 1:1 leg only: land past what THAT leg
                    # delivered within the window (L1/R1a) — a global landing would
                    # sweep in-window project/broadcast mail before any later session
                    # whose project resolves could read it. The leg's own overflow past
                    # the cap is still never revisited (its max covers it), and older
                    # leg rows cannot sit above the cursor (ids grow with ts).
                    next_cursor = self.conn.execute(
                        "SELECT COALESCE(MAX(id), 0) FROM messages "
                        "WHERE target_session = ? AND sender_session <> ? AND ts >= ?",
                        (session_id, session_id, cutoff)).fetchone()[0]
            else:
                rows = self._read_addressed(session_id, project_id,
                                            after_id=row[0], since_ts=None)
                messages = [_message_document(r) for r in rows]
                summary = self._delivery_summary(rows, session_id)
                next_cursor = rows[-1][0] if rows else row[0]
            _hold_at("deliver.after_read")
            self.conn.execute(
                "INSERT INTO cursors(session_id, cursor_id, ts) VALUES (?, ?, ?) "
                "ON CONFLICT(session_id) DO UPDATE SET cursor_id = excluded.cursor_id, "
                "ts = excluded.ts",
                (session_id, next_cursor, _now()),
            )
            self.conn.commit()
        except BaseException:
            self.conn.rollback()
            raise
        _assert_file_perms(self.db_path)
        notify_write(self.db_path)
        return messages, summary

    @staticmethod
    def _delivery_summary(rows, session_id: str) -> dict:
        """C2's wake classification, computed from the delivered rows only. A 1:1 row
        (target_session = the session) and a project row (target_session NULL, the
        project leg) count as addressed; a both-targets-NULL row counts as broadcast.
        The rows are the DELIVERED batch — post-gate, post-cap, post-R2 — so gated-off
        rows, the sender's own rows and a not-run D7 leg count 0 (QA-10/QA-11)."""
        addressed = 0
        broadcast = 0
        for row in rows:
            if row[5] == session_id:  # the 1:1 leg
                addressed += 1
            elif row[5] is None and row[6] is None:  # the broadcast shape
                broadcast += 1
            else:  # the project leg: target_session NULL, target_project = project_id
                addressed += 1
        return {"addressed": addressed, "broadcast": broadcast}

    def _read_addressed(self, session_id: str, project_id: Optional[str], *,
                        after_id: int, since_ts: Optional[str]) -> list:
        """The three D3 shapes (1:1, project, broadcast) minus the sender's own rows (R2),
        past *after_id*, optionally gated to *since_ts*, oldest first.

        One OR-shaped query: the planner serves it as a MULTI-INDEX OR — each branch is a
        seek on idx_messages_target_session / idx_messages_target_project / the PK range
        (D6, verified by the EXPLAIN gate test). Without a project_id the project and
        broadcast branches drop out (deliver 1:1 only, D7). The target columns ride the
        SELECT (appended — positions 0–4 are the document fields) so C2's summary can
        classify the delivered batch inside the txn (QA-11); the D6 EXPLAIN gate
        re-covers the widened query plan.
        """
        shapes = ["target_session = ?"]
        params: list = [session_id]
        if project_id:
            shapes.append("(target_session IS NULL AND target_project = ?)")
            shapes.append("(target_session IS NULL AND target_project IS NULL)")
            params.append(project_id)
        clauses = [f"({' OR '.join(shapes)})", "id > ?", "sender_session <> ?"]
        params.extend((after_id, session_id))
        if since_ts is not None:
            clauses.append("ts >= ?")
            params.append(since_ts)
        return self.conn.execute(
            f"SELECT id, ts, sender_session, sender_project, content, target_session, "
            f"target_project FROM messages WHERE {' AND '.join(clauses)} ORDER BY id ASC",
            tuple(params),
        ).fetchall()

    def delete_cursor(self, session_id: str) -> bool:
        """Remove one session's cursor row — the close-event cleanup (R6); True if it existed."""
        self.conn.execute("BEGIN IMMEDIATE")
        try:
            cursor = self.conn.execute(
                "DELETE FROM cursors WHERE session_id = ?", (session_id,))
            deleted = cursor.rowcount
            self.conn.commit()
        except BaseException:
            self.conn.rollback()
            raise
        _assert_file_perms(self.db_path)
        notify_write(self.db_path)
        return deleted > 0


# -- project identity (P2): the cwd → projectId resolver (D4) -------------------------

#: The explicit project id (the resolver contract's "explicit wins" rule, A3): set and
#: non-blank it IS the answer at send and delivery alike — one entry point serves both
#: sides so a conversation's addresses resolve identically — and the registry is never
#: consulted. Blank reads as unset, mirroring the contract's IsNullOrWhiteSpace.
PROJECT_ID_ENV = "AI_BADGER_PROJECT_ID"


def _nearest_project_id_file(cwd: Optional[str]) -> Optional[Path]:
    """The nearest parent ``.ai-badger/project-id`` file above *cwd*, if any.

    The nearest ``.ai-badger`` directory wins and stops the walk: a project with a
    parent scaffold but no local project-id is treated as id-absent, not as a parent
    fallback. Blank values and missing directories are treated as unset.
    """
    if cwd is None or not str(cwd).strip():
        return None
    start = Path(_real_path(cwd))
    if start.is_file():
        start = start.parent
    for ancestor in (start, *start.parents):
        aib_dir = ancestor / ".ai-badger"
        if not aib_dir.exists():
            continue
        if not aib_dir.is_dir():
            continue
        project_id_file = aib_dir / "project-id"
        if project_id_file.is_file():
            return project_id_file
        return None
    return None


def _read_project_id_file(cwd: Optional[str]) -> Optional[str]:
    """The .ai-badger-scoped project id above *cwd*, or ``None`` when unset."""
    project_id_file = _nearest_project_id_file(cwd)
    if project_id_file is None:
        return None
    try:
        value = project_id_file.read_text(encoding="utf-8").strip()
    except OSError:
        return None
    return value or None


def _real_path(path: str) -> str:
    """The path as the walk compares it: absolute, symlink-resolved, trimmed. Tails that
    do not exist keep their literal spelling, so a not-yet-created directory resolves."""
    return os.path.realpath(os.path.abspath(path))


def resolve_project_id(cwd: Optional[str]) -> Optional[str]:
    """Resolve a working directory to the current project's id (R8; D4; ADR-0025).

    The explicit override (``AI_BADGER_PROJECT_ID``) wins unconditionally — when set and
    non-blank it is returned before anything else is read, at send and delivery alike.
    Otherwise the nearest ancestor ``.ai-badger/project-id`` file wins (minted at
    scaffold time, backfilled by den-refresh): a cwd with no ``.ai-badger`` in its
    ancestry — or one whose id file is absent or blank — resolves to None, and the
    caller owns the fail-open (D7/D8). There is no ambiguity to refuse: a single
    upward walk has one nearest directory, never a guess.
    """
    override = os.environ.get(PROJECT_ID_ENV)
    if override and override.strip():
        return override.strip()
    return _read_project_id_file(cwd)


def _parseable_ts(ts) -> bool:
    """True for a ts the prune's string comparison can be trusted on: non-empty ISO-8601."""
    if not isinstance(ts, str) or not ts:
        return False
    try:
        datetime.fromisoformat(ts)
    except ValueError:
        return False
    return True


def iso_row_ts(raw_ts) -> str:
    """A log entry's own ts as the ISO-8601 row ts the prune's comparison trusts (D36).

    Epoch floats (the memory-grade stash's native ts) convert to UTC ISO; parseable ISO
    strings pass through; anything else becomes now — a NOT NULL ts column and the
    unparseable-ts sweep both need a value that parses. The entry document itself stays
    verbatim in the payload, ts field included.
    """
    if isinstance(raw_ts, (int, float)) and not isinstance(raw_ts, bool):
        try:
            return datetime.fromtimestamp(raw_ts, timezone.utc).isoformat()
        except (OverflowError, OSError, ValueError):
            return _now()  # an out-of-range epoch is a value problem, not a crash
    if _parseable_ts(raw_ts):
        return raw_ts
    return _now()


#: The retention scope (owner decision #2 + G0-Q2): every log table and the DB it lives
#: in. The on-write callers name their table; the CLI reports the whole scope.
LOG_TABLES: dict[str, tuple[str, ...]] = {
    "user": ("awm_decisions", "searches", "messages", "cursors"),
    "audit": ("hook_audit",),
}


def _open(db_path: Path, kind: str, families: Optional[dict] = None) -> Store:
    _ensure_root(db_path)
    _precreate_db_file(db_path)
    conn = sqlite3.connect(db_path, timeout=5.0, isolation_level=None)
    store = Store(conn, db_path, kind, families)
    try:
        conn.execute("PRAGMA busy_timeout = 5000")
        for attempt in range(4):
            # Parallel first opens race the WAL conversion: it takes a lock the
            # winner holds and returns BUSY without honouring busy_timeout, so
            # retry briefly and accept a database someone already converted.
            try:
                conn.execute("PRAGMA journal_mode = WAL")
                break
            except sqlite3.OperationalError:
                mode = conn.execute("PRAGMA journal_mode").fetchone()
                if mode and str(mode[0]).lower() == "wal":
                    break
                if attempt == 3:
                    raise
                time.sleep(0.05)
        conn.execute("PRAGMA synchronous = NORMAL")
        _create_schema(conn)
        _ensure_schema_version(conn, db_path)
        store._check_resurrections()  # pylint: disable=protected-access
    except BaseException:
        conn.close()
        raise
    _assert_file_perms(db_path)
    return store


def open_tracking(families: Optional[dict] = None) -> Store:
    """Open (creating when absent) the project tracking store."""
    return _open(tracking_db_path(), "tracking", families)


def open_user(families: Optional[dict] = None) -> Store:
    """Open (creating when absent) the user-level store; defaults to USER_FAMILIES.

    Every open prunes the bus tables to their 4-day retention (R6/R10, D10) — wired here,
    NOT in general _open: tracking stores have no bus retention to run. The prune is
    hour-throttled by its pruned_at stamps and fails open as 0 (D31), so a broken store
    still opens.
    """
    store = _open(user_db_path(), "user", USER_FAMILIES if families is None else families)
    store.prune_expired("messages", max_age_days=_BUS_MAX_AGE_DAYS)
    store.prune_expired("cursors", max_age_days=_BUS_MAX_AGE_DAYS)
    return store


# -- CLI: `prune --status`, the retention scope's read-only inspection surface ---------

def _render_stamp(conn: sqlite3.Connection, table: str) -> str:
    """The table's pruned_at stamp as a UTC timestamp, or '-' when absent/foreign."""
    try:
        row = conn.execute("SELECT value FROM meta WHERE key = ?",
                           (f"pruned_at.{table}",)).fetchone()
    except sqlite3.Error:
        return "-"
    if row is None:
        return "-"
    try:
        moment = float(json.loads(row[0]))
    except (TypeError, ValueError):
        return "-"
    return datetime.fromtimestamp(moment, timezone.utc).isoformat()


def prune_status_lines() -> list[str]:
    """Retention state of every log table (LOG_TABLES): rows, oldest ts, last prune stamp.

    Read-only throughout — a status verb must never create, migrate or write the store it
    reports on (mode=ro), and an absent DB is reported as such with zeroed tables.
    """
    lines: list[str] = []
    for db_kind, tables in LOG_TABLES.items():
        db_path = user_db_path() if db_kind == "user" else audit_db_path()
        header = f"db={db_kind} path={db_path}"
        conn = None
        if db_path.exists():
            try:
                conn = sqlite3.connect(f"{db_path.as_uri()}?mode=ro", uri=True)
            except sqlite3.Error:
                conn = None
        if conn is None:
            header += " status=no-database"
        lines.append(header)
        for table in tables:
            rows, oldest, stamp = 0, "-", "-"
            if conn is not None:
                try:
                    count, min_ts = conn.execute(
                        f"SELECT COUNT(*), MIN(ts) FROM {table}").fetchone()
                    rows, oldest = int(count), min_ts if min_ts is not None else "-"
                    stamp = _render_stamp(conn, table)
                except sqlite3.Error:
                    pass  # a DB predating the table reports as empty
            lines.append(f"  {table} rows={rows} oldest={oldest} last_prune={stamp}")
        if conn is not None:
            with contextlib.suppress(sqlite3.Error):
                conn.close()
    return lines


# -- CLI: `doctor`, the per-family containment surface (M2) ---------------------------

#: Kinds whose resurrected file may be NEWER than the DB: repair never merges them
#: (INSERT OR IGNORE would silently drop the newer legacy values — research-b); they are
#: reported with guidance instead, and the owner reconciles by hand.
DOCTOR_INSPECT_ONLY_KINDS = frozenset({"map", "kvdoc", "awm"})


def doctor_target(project: Optional[Path] = None) -> tuple[Path, dict]:
    """The DB path and family registry a doctor run reports on: a project root's tracking
    store with the FAMILIES registry when *project* is given, else the machine user root
    with USER_FAMILIES. (The task families belong to tracker_lib's own open gate.)

    The project's families are the FAMILIES registry re-rooted onto the project's own
    ``.ai-badger``: the legacy layout is root-relative, so each family's path relative to
    the current .ai-badger root is replayed under the project's root.
    """
    if project is not None:
        base = tracking_db_path().parent.parent  # the .ai-badger root the registry used
        rebased: dict = {}
        for name, family in FAMILIES.items():
            if family.legacy_path is None:
                rebased[name] = family
                continue
            try:
                rel = family.legacy_path().relative_to(base)
            except ValueError:
                rebased[name] = family  # not a root-relative layout: report it verbatim
                continue
            rebased[name] = Family(
                table=family.table, db=family.db,
                legacy_path=lambda rel=rel, root=project / ".ai-badger": root / rel,
                legacy_kind=family.legacy_kind, row_key=family.row_key,
                ts_field=family.ts_field)
        return project / ".ai-badger" / "task-tracking" / "tracking.db", rebased
    return user_db_path(), USER_FAMILIES


def _doctor_content_diff(conn: Optional[sqlite3.Connection], family: Family,
                         path: Path) -> dict:
    """A doc-kind family's file-vs-DB content diff, {} values when it cannot be computed.

    The incident shape (reviewer S3): a newer file beside DB rows it predates or diverges
    from — the report names what the file would add, change and remove, read-only.
    """
    if conn is None or family.legacy_kind not in ("map", "kvdoc", "awm"):
        return {"diff": None}
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {"diff": None}
    if not isinstance(data, dict):
        return {"diff": None}
    if family.legacy_kind == "kvdoc":
        file_map = {family.row_key: data}
    elif family.legacy_kind == "awm":
        file_map = _awm_projects(data)
    else:
        file_map = data
    try:
        db_map = {key: json.loads(value) for key, value in conn.execute(
            f"SELECT key, value FROM {family.table}")}
    except (sqlite3.Error, ValueError):
        return {"diff": None}
    return {"diff": {
        "added": sorted(set(file_map) - set(db_map)),
        "removed": sorted(set(db_map) - set(file_map)),
        "changed": sorted(key for key in set(file_map) & set(db_map)
                          if file_map[key] != db_map[key]),
    }}


def doctor_scan(db_path: Path, families: dict) -> list[dict]:
    """One read-only row per family with a legacy source: presence, stamp, mtime, state.

    Never creates, migrates or writes: the DB opens mode=ro when present, an absent DB is
    reported (state per family from the filesystem alone), nothing is provisioned.
    States: no-file, pre-migration (dual-read window), resurrected (contained),
    restored-legacy (inert until the next write re-imports it), migrated (normal).
    """
    conn = None
    if db_path.exists():
        try:
            conn = sqlite3.connect(f"{db_path.as_uri()}?mode=ro", uri=True)
        except sqlite3.Error:
            conn = None
    rows: list[dict] = []
    for name, family in families.items():
        if family.legacy_path is None or family.legacy_kind == "store":
            continue
        path = family.legacy_path()
        stamp = None
        if conn is not None:
            try:
                row = conn.execute("SELECT value FROM meta WHERE key = ?",
                                   (_stamp_key(family.table),)).fetchone()
                stamp = float(row[0]) if row is not None else None
            except (sqlite3.Error, TypeError, ValueError):
                stamp = None
        if family.legacy_kind in FILE_SET_KINDS:
            files = _file_set_paths(family)
            exists = bool(files)
            mtime = max((p.stat().st_mtime for p in files), default=None)
        else:
            exists = path.exists()
            mtime = path.stat().st_mtime if exists else None
        if not exists:
            state = "migrated" if stamp is not None else "no-file"
        elif stamp is None:
            state = "pre-migration"
        elif mtime is not None and mtime > stamp:
            state = "resurrected"
        else:
            state = "restored-legacy"
        row = {"family": name, "table": family.table, "kind": family.legacy_kind,
               "path": str(path), "file": exists, "stamp": stamp, "mtime": mtime,
               "state": state}
        if exists and family.legacy_kind not in FILE_SET_KINDS:
            row.update(_doctor_content_diff(conn, family, path))
        else:
            row["diff"] = None
        rows.append(row)
    if conn is not None:
        with contextlib.suppress(sqlite3.Error):
            conn.close()
    return rows


def _doctor_stamp_or_dash(stamp: Optional[float]) -> str:
    """A migration stamp as a UTC timestamp, or '-' when absent."""
    return datetime.fromtimestamp(stamp, timezone.utc).isoformat() if stamp else "-"


def doctor_status_lines(project: Optional[Path] = None) -> list[str]:
    """`doctor --status`: the read-only containment report, the prune --status pattern.

    Names every family with a legacy source — state, stamp, file mtime, and for the doc
    kinds a content diff against the DB rows (newer file vs stale rows, reviewer S3).
    """
    db_path, families = doctor_target(project)
    lines = [f"db={db_path}" + ("" if db_path.exists() else " status=no-database")]
    for row in doctor_scan(db_path, families):
        line = (f"  family={row['family']} table={row['table']} kind={row['kind']} "
                f"state={row['state']} stamp={_doctor_stamp_or_dash(row['stamp'])} "
                f"mtime={_doctor_stamp_or_dash(row['mtime'])}")
        diff = row.get("diff")
        if diff:
            line += (f" diff=+{diff['added']} ~{diff['changed']} -{diff['removed']}")
        lines.append(line)
    return lines


def doctor_repair_lines(project: Optional[Path] = None) -> list[str]:
    """`doctor --repair`: re-import additive kinds idempotently (then rename), inspect
    only for map/kvdoc/awm.

    Additive kinds dedupe on natural keys / exact content, so re-importing a newer file
    loses nothing and the rename re-quarantines it. The doc kinds may hold values NEWER
    than the DB; merging them is an owner decision, so repair prints guidance and leaves
    them byte-identical (research-b: OR IGNORE would silently drop the newer values).
    """
    db_path, families = doctor_target(project)
    kind = next(iter(families.values())).db
    lines = [f"db={db_path}"]
    resurrected = [row for row in doctor_scan(db_path, families)
                   if row["state"] == "resurrected"]
    if not resurrected:
        lines.append("  state=clean — nothing to repair")
        return lines
    store = _open(db_path, kind, families)
    try:
        for row in resurrected:
            name = row["family"]
            family = families[name]
            if family.legacy_kind in DOCTOR_INSPECT_ONLY_KINDS:
                lines.append(
                    f"  family={name} kind={family.legacy_kind} state=resurrected "
                    f"inspect-only: the file may be newer than the DB — compare the "
                    f"doctor --status diff and reconcile by hand, then restore the "
                    f"*.migrated.json name; the store never merges it for you")
                continue
            if family.legacy_kind in FILE_SET_KINDS:
                store._migrate_file_set(family, force=True)  # pylint: disable=protected-access
            else:
                store._migrate_family(family, force=True)  # pylint: disable=protected-access
            lines.append(
                f"  family={name} kind={family.legacy_kind} state=resurrected "
                f"action=re-imported idempotently and renamed (*.migrated)")
    finally:
        store.close()
    return lines


def main(argv: Optional[list] = None) -> int:
    """CLI entry point: `prune --status` (retention inspection, D30) and `doctor`
    (--status / --repair, the per-family containment surface, M2)."""
    parser = argparse.ArgumentParser(
        prog="badger_store", description="SQLite runtime store utilities (ADR-0024)")
    sub = parser.add_subparsers(dest="command", required=True)
    prune = sub.add_parser("prune", help="retention over the log tables")
    prune.add_argument("--status", action="store_true",
                       help="per-table row count, oldest row and last prune stamp")
    doctor = sub.add_parser("doctor", help="per-family containment: inspect and repair")
    doctor.add_argument("--status", action="store_true",
                        help="read-only per-family report: state, stamp, mtime, map diff")
    doctor.add_argument("--repair", action="store_true",
                        help="re-import additive kinds idempotently (then rename); "
                             "map/kvdoc/awm are inspect-only")
    doctor.add_argument("--user", action="store_true",
                        help="the machine user root (the default target)")
    doctor.add_argument("--project", type=Path, default=None, metavar="PATH",
                        help="a project root: scan its .ai-badger/task-tracking store")
    args = parser.parse_args(argv)
    if args.command == "prune":
        if not args.status:
            prune.error("nothing to do — only --status is implemented")
        for line in prune_status_lines():
            print(line)
        return 0
    if args.command == "doctor":
        if args.status == args.repair:
            doctor.error("nothing to do — exactly one of --status or --repair is required")
        if args.user and args.project is not None:
            doctor.error("--user and --project are mutually exclusive")
        lines = doctor_status_lines(project=args.project) if args.status \
            else doctor_repair_lines(project=args.project)
        for line in lines:
            print(line)
        return 0
    return 2  # unreachable: the subcommand is required


if __name__ == "__main__":
    sys.exit(main())
