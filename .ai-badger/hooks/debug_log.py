"""Append-only debug audit log for ai-badger's own hooks.

Off unless switched on by the `call-behaviorist` skill. Self-contained on purpose: hooks run
from four deployment shapes and import nothing from the framework.

P2.2 storage: audit records live in the store's ``hook_audit`` family and the enable state in
``hook_state`` (KV row ``debug``); the legacy ``audit.jsonl``/``state.json`` files beside this
module's debug directory remain the import seam and the fallback sink when the store is
unavailable. Fail-open throughout: a store error never breaks the hook being observed (D31).
"""
from __future__ import annotations

import importlib.util
import json
import os
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path

DEBUG_ENV = "AI_BADGER_DEBUG"
DEBUG_DIR_ENV = "AI_BADGER_DEBUG_DIR"
# Drops the query field only, at the point of writing — a redacted record must never have
# contained the text, so this is checked inside log_event itself, never as a post-process.
REDACT_ENV = "AI_BADGER_DEBUG_REDACT"
PROJECT_DIR_ENV = "CLAUDE_PROJECT_DIR"
SCOPE_USER = "user"
SCOPE_PROJECT = "project"

# Recorded when no VERSION file and no scaffold manifest sit above the code that ran.
# Not a version: analysis must never read it as one copy disagreeing with another.
VERSION_UNKNOWN = "unknown"

# The store module: vendored beside this file in every deployment shape, engine/ canonical in
# tests. The scaffolder ships the pair together (adjust_hooks' PROJECT_HOOKS/USER_PLUGINS put
# badger_store.py beside every debug_log copy it delivers); when the sibling is nonetheless
# absent or unreadable — an older or partial shape — the loader degrades to legacy file mode
# (the old jsonl/state.json sink, still fully functional) instead of breaking this module's
# import: a missing store must never take a hook down with it (D31).
def _load_store_module():
    try:
        import badger_store
        return badger_store
    except ImportError:
        pass
    sibling = Path(__file__).resolve().parent / "badger_store.py"
    spec = importlib.util.spec_from_file_location("badger_store", sibling)
    if spec is None or spec.loader is None:
        return None
    module = importlib.util.module_from_spec(spec)
    sys.modules.setdefault("badger_store", module)
    try:
        spec.loader.exec_module(module)
    except OSError:
        sys.modules.pop("badger_store", None)
        return None
    return module


_store_module = _load_store_module()

# The whole sink moves with $AI_BADGER_DEBUG_DIR (D21); the module global is the same contract
# at call time, so tests can redirect the sink by patching DEBUG_DIR instead of the env.
AUDIT_DB_NAME = "audit.db"



def debug_dir() -> Path:
    """Where state and records live: `$AI_BADGER_DEBUG_DIR`, else `~/.ai-badger/debug`.

    The override exists so a test run redirects the sink instead of writing a real audit log.
    """
    override = os.environ.get(DEBUG_DIR_ENV)
    return Path(override) if override else Path.home() / ".ai-badger" / "debug"


DEBUG_DIR = debug_dir()
STATE_FILE = DEBUG_DIR / "state.json"
AUDIT_FILE = DEBUG_DIR / "audit.jsonl"


def audit_db() -> Path:
    """The audit sink's own DB file, under DEBUG_DIR.

    `$AI_BADGER_DEBUG_DIR` moves the whole sink (D21) by shaping `debug_dir()` at import;
    deriving from the DEBUG_DIR global (not re-reading the env) keeps a redirected sink —
    tests patching the global, a suite-wide conftest override — redirecting the DB too.
    """
    return DEBUG_DIR / AUDIT_DB_NAME


# An unbounded audit log on someone's disk is its own defect.
MAX_AUDIT_LINES = 5000
# Records must stay under PIPE_BUF (4096) so concurrent O_APPEND writes cannot interleave.
# Single-letter keys are part of that budget, not cosmetics.
MAX_FIELD_CHARS = 200
# The query is the one field a fixture can be built from, so it gets a larger share than the
# rest: at 200 it kept a prefix, not the question (#219). Bounded by the record budget below.
MAX_QUERY_CHARS = 2000
# The budget itself, enforced rather than assumed. Every field is capped, but enough capped
# fields still overflow PIPE_BUF, and a record that overflows is one a concurrent writer can
# interleave with — corrupting both lines. `_fit` shrinks the longest field until it fits.
PIPE_BUF_BYTES = 4096

KEY_TS = "t"
KEY_COMPONENT = "c"
KEY_EVENT = "e"
KEY_VERSION = "v"
KEY_PROJECT = "p"
KEY_SESSION = "s"
KEY_NAME = "n"

# Retrieval-telemetry payload keys (mcp-retrieval hit/gate/absent, and the tool-index check).
KEY_QUERY = "q"
KEY_TERMS = "g"
KEY_CANDIDATES = "d"
KEY_TOP = "o"
KEY_RETURNED = "r"
KEY_THRESHOLD = "h"
KEY_TOOL = "l"

# The legend `tail` and any reader needs to expand a record.
KEY_NAMES = {
    KEY_TS: "ts",
    KEY_COMPONENT: "component",
    KEY_EVENT: "event",
    KEY_VERSION: "version",
    KEY_PROJECT: "project",
    KEY_SESSION: "session",
    KEY_NAME: "name",
    KEY_QUERY: "query",
    KEY_TERMS: "terms",
    KEY_CANDIDATES: "candidates",
    KEY_TOP: "top",
    KEY_RETURNED: "returned",
    KEY_THRESHOLD: "threshold",
    KEY_TOOL: "tool",
}

STATE_ROW_KEY = "debug"  # hook_state holds the whole enable document under this key (D26)

# project dir -> name, resolved once per process: a config read on every hook event is a real
# cost for a facility that is meant to be nearly free.
_NAME_CACHE = {}


def now() -> datetime:
    return datetime.now(timezone.utc)


def iso(moment: datetime) -> str:
    return moment.isoformat(timespec="seconds")


def _manifest_version(directory) -> str:
    """The framework version recorded by a scaffold's manifest, or empty."""
    try:
        manifest = json.loads((directory / "manifest.json").read_text(encoding="utf-8"))
        version = manifest.get("frameworkVersion")
        return version if isinstance(version, str) and version else ""
    except (OSError, ValueError, AttributeError):
        return ""


def framework_version(start=None) -> str:
    """VERSION of the tree this file lives in — i.e. which copy of the code ran.

    Nearest ancestor wins: a scaffold carries no VERSION but records the version that
    materialised it in `.ai-badger/manifest.json`, and a host repo's own VERSION file
    sits further up than that manifest — so it can never be mistaken for the framework's.
    """
    here = Path(start or __file__).resolve()
    for anc in [here, *here.parents]:
        recorded = _manifest_version(anc)
        if recorded:
            return recorded
        version_file = anc / "VERSION"
        if version_file.is_file():
            try:
                text = version_file.read_text(encoding="utf-8").strip()
            except OSError:
                text = ""
            if text:
                return text
    return VERSION_UNKNOWN


def resolve_project_root(payload=None):
    """`$CLAUDE_PROJECT_DIR` first, else the payload's own `cwd`, else None — never a guess.

    Shared so every hook attributes its records the same way: an unattributed record
    pools into every project's analysis and skews all of them.
    """
    env_root = os.environ.get(PROJECT_DIR_ENV)
    if env_root:
        return env_root
    cwd = (payload or {}).get("cwd")
    return cwd if cwd else None


NAME_USER_SCOPE = "user"

# Where a user-scope install lives. A hook running from one of these belongs to no project,
# so naming a project would be a guess.
USER_INSTALL_ROOTS = (
    Path.home() / ".claude",
    Path.home() / ".hermes",
    Path.home() / ".ai-badger",
)


def is_user_scope() -> bool:
    """True when this copy of the logger is installed at user level, not inside a project."""
    here = Path(__file__).resolve()
    for root in USER_INSTALL_ROOTS:
        try:
            here.relative_to(root.resolve())
            return True
        except (ValueError, OSError):
            continue
    return False


def project_name(project):
    """The scaffolded project's name, `user` for a user-scope install, else None.

    Cached per process: a config read on every hook event is a real cost for a facility that
    is meant to be nearly free.
    """
    if is_user_scope():
        return NAME_USER_SCOPE
    if not project:
        return None
    if project in _NAME_CACHE:
        return _NAME_CACHE[project]
    name = None
    try:
        config = json.loads(
            (Path(project) / ".ai-badger" / "config.json").read_text(encoding="utf-8"))
        candidate = config.get("project", {}).get("name")
        name = candidate if isinstance(candidate, str) and candidate else None
    except (OSError, ValueError, AttributeError):
        name = None
    _NAME_CACHE[project] = name
    return name


def _families():
    """The two families this module owns, pointed at the legacy seams beside DEBUG_DIR."""
    return {
        "hook_audit": _store_module.Family(
            table="hook_audit", db="user",
            legacy_path=lambda: AUDIT_FILE, legacy_kind="jsonl", ts_field=KEY_TS,
        ),
        "hook_state": _store_module.Family(
            table="hook_state", db="user",
            legacy_path=lambda: STATE_FILE, legacy_kind="kvdoc", row_key=STATE_ROW_KEY,
        ),
    }


def _store():
    """The audit store over its own DB, or None when unavailable (legacy file mode, D31)."""
    if _store_module is None:
        return None
    try:
        # _open is the module's single store constructor; the audit sink gets its own DB
        # file and only the two families this module writes (a narrower view than open_user).
        store = _store_module._open(audit_db(), "user", _families())  # pylint: disable=protected-access
        _own_only(audit_db().parent)  # a pre-existing parent may be group-readable
        return store
    except Exception:  # pylint: disable=broad-except
        return None


def _valid_state(state) -> bool:
    """A stored state document enables logging only while unexpired."""
    if not isinstance(state, dict) or not state.get("enabled"):
        return False
    expires_at = state.get("expires_at")
    if expires_at:
        try:
            if now() >= datetime.fromisoformat(expires_at):
                return False
        except ValueError:
            return False
    return True


def _state():
    """The recorded debug state, or None when absent/unreadable/expired.

    Store row when the audit DB already exists; the legacy state.json beside DEBUG_DIR is the
    import seam (read back until a state write migrates it) and the fallback otherwise. A
    disabled logger creates nothing: no DB, no directory.
    """
    if not audit_db().exists() and not STATE_FILE.exists():
        return None
    if audit_db().exists():
        store = _store()
        if store is not None:
            try:
                stored = store.kv_get("hook_state", STATE_ROW_KEY)
                if stored is not None:
                    return stored if _valid_state(stored) else None
            except Exception:  # pylint: disable=broad-except
                pass
            finally:
                store.close()
    try:
        state = json.loads(STATE_FILE.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None
    return state if _valid_state(state) else None


def enabled_for(project=None) -> bool:
    """True when this call should be recorded. The env override wins over stored state."""
    if os.environ.get(DEBUG_ENV):
        return True
    state = _state()
    if state is None:
        return False
    if state.get("scope", SCOPE_USER) != SCOPE_PROJECT:
        return True
    scoped = state.get("project")
    return bool(scoped) and project == scoped


def set_state(state: dict) -> None:
    """Record the enable document (behaviorist's on/off). Never raises (D31).

    Store row when the store is available; the legacy state.json file otherwise.
    """
    try:
        store = _store()
        if store is not None:
            try:
                store.kv_set("hook_state", STATE_ROW_KEY, state)
            finally:
                store.close()
            return
        DEBUG_DIR.mkdir(parents=True, exist_ok=True)
        _own_only(DEBUG_DIR)
        STATE_FILE.write_text(json.dumps(state), encoding="utf-8")
        _own_only(STATE_FILE)
    except Exception:  # pylint: disable=broad-except
        return


def _own_only(path: Path) -> None:
    """0600 for a file, 0700 for a directory: this log says where you work and what ran."""
    try:
        path.chmod(0o700 if path.is_dir() else 0o600)
    except OSError:
        pass


def _clip(value, limit=None) -> str:
    text = str(value).replace("\n", " ").replace("\r", " ")
    return text[:MAX_FIELD_CHARS if limit is None else limit]


def _encoded_len(record) -> int:
    return len(json.dumps(record, ensure_ascii=False).encode("utf-8")) + 1


def _fit(record):
    """Shrink the longest field until one serialised record plus its newline fits PIPE_BUF.

    Field caps alone do not bound the record: enough capped fields still overflow. Halving the
    largest each pass converges in a few steps and takes the space from whichever field is
    actually responsible, rather than from the query by default.
    """
    while _encoded_len(record) > PIPE_BUF_BYTES:
        key = max(record, key=lambda k: len(record[k]))
        if len(record[key]) <= 1:
            return record
        record[key] = record[key][:len(record[key]) // 2]
    return record


def _legacy_append(record) -> None:
    """One jsonl line at AUDIT_FILE with the PIPE_BUF interleaving bound and the cap."""
    DEBUG_DIR.mkdir(parents=True, exist_ok=True)
    _own_only(DEBUG_DIR)
    with AUDIT_FILE.open("a", encoding="utf-8") as handle:
        handle.write(json.dumps(record, ensure_ascii=False) + "\n")
    _own_only(AUDIT_FILE)
    _trim()


def _trim() -> None:
    """Keep the newest MAX_AUDIT_LINES records (legacy sink only; the DB needs no trim)."""
    try:
        lines = AUDIT_FILE.read_text(encoding="utf-8").splitlines(keepends=True)
    except OSError:
        return
    if len(lines) <= MAX_AUDIT_LINES:
        return
    try:
        AUDIT_FILE.write_text("".join(lines[-MAX_AUDIT_LINES:]), encoding="utf-8")
        _own_only(AUDIT_FILE)
    except OSError:
        pass


def read_records(limit=None):
    """Audit records, oldest first; the newest `limit` when given. Never raises (D31).

    Store rows when the store is available (the legacy jsonl imports on first write), the
    legacy file otherwise — the same view the behaviorist's verbs read.
    """
    try:
        store = _store()
        if store is not None:
            try:
                rows = store.conn.execute(
                    "SELECT payload FROM hook_audit ORDER BY id" + (
                        f" DESC LIMIT {int(limit)}" if limit else "")
                ).fetchall()
                records = [json.loads(row[0]) for row in rows]
                return records if not limit else records[::-1]
            finally:
                store.close()
        if not AUDIT_FILE.exists():
            return []
        lines = AUDIT_FILE.read_text(encoding="utf-8").splitlines()
        records = [json.loads(line) for line in lines if line.strip()]
        return records if limit is None else records[-int(limit):]
    except Exception:  # pylint: disable=broad-except
        return []


def count_records() -> int:
    """How many audit records exist. Never raises (D31)."""
    try:
        store = _store()
        if store is not None:
            try:
                return int(store.conn.execute("SELECT COUNT(*) FROM hook_audit").fetchone()[0])
            finally:
                store.close()
        return len(read_records())
    except Exception:  # pylint: disable=broad-except
        return 0


def clear_records() -> None:
    """Drop every audit record. Never raises (D31)."""
    try:
        store = _store()
        if store is not None:
            try:
                store.conn.execute("DELETE FROM hook_audit")
                store.conn.commit()
            finally:
                store.close()
            return
        if AUDIT_FILE.exists():
            AUDIT_FILE.write_text("", encoding="utf-8")
            _own_only(AUDIT_FILE)
    except Exception:  # pylint: disable=broad-except
        return


def log_event(component: str, event: str, project=None, session=None, **fields) -> None:
    """Record one line. Never raises: a debug facility must not break what it observes."""
    try:
        if not enabled_for(project):
            return
        record = {
            KEY_TS: iso(now()),
            KEY_COMPONENT: _clip(component),
            KEY_EVENT: _clip(event),
            KEY_VERSION: framework_version(),
        }
        if project:
            record[KEY_PROJECT] = _clip(project)
            name = project_name(project)
            if name:
                record[KEY_NAME] = _clip(name)
        if session:
            record[KEY_SESSION] = _clip(session)
        redact_query = bool(os.environ.get(REDACT_ENV))
        for key, value in fields.items():
            if redact_query and key == KEY_QUERY:
                continue
            if value is not None:
                record[key] = _clip(value, MAX_QUERY_CHARS if key == KEY_QUERY else None)
        _fit(record)

        store = _store()
        if store is not None:
            try:
                store.log_append("hook_audit", record[KEY_TS], record)
                # Retention (P2.3/D30): the write is the prune opportunity — throttled by the
                # store's pruned_at stamp, one transaction with the DELETE, fail-open (a
                # sqlite error returns 0; anything worse lands in the handler below).
                store.prune_expired("hook_audit", max_age_days=60)
            finally:
                store.close()
            return
        _legacy_append(record)
    except Exception:  # pylint: disable=broad-except
        return
