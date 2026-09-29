"""Store-backed plan service: canonical payloads, atomic CAS, frozen root precedence.

The one adapter between the plan model and the ``plans`` row: ``save_plan``/``update_step``
serialise a ``TaskPlan`` to the canonical payload the row stores, every mutation is one
read-validate-mutate-CAS write, and the raw payload's ``schema_version`` is gated before
pydantic ever sees the document. Root resolution mirrors ``tracker_lib.resolve_project_root``
(``CLAUDE_PROJECT_DIR`` -> the cwd marker walk -> the script-dir walk stopping at ``$HOME``),
collapses a linked worktree to its checkout, and pins ``AI_BADGER_TRACKING_ROOT`` before the
vendored store opens -- at call time, never at import.
"""
from __future__ import annotations

import contextlib
import importlib.util
import json
import os
import sqlite3
import subprocess
import sys
from collections.abc import Callable, Iterator, Mapping
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Optional

SCRIPT_DIR = Path(__file__).resolve().parent
TRACKING_ROOT_ENV = "AI_BADGER_TRACKING_ROOT"


def _load_path(name: str, path: Path) -> Any:
    """One module loaded from *path* under *name*, registered in ``sys.modules``."""
    spec = importlib.util.spec_from_file_location(name, path)
    if spec is None or spec.loader is None:  # pragma: no cover - the files are vendored
        raise ImportError(f"cannot load {name} at {path}")
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    try:
        spec.loader.exec_module(module)
    except BaseException:
        sys.modules.pop(name, None)
        raise
    return module


def _load_sibling(name: str) -> Any:
    """A module beside this file, under its plain name, cached in ``sys.modules``.

    A cached entry is reused only when it is *this* file: a stale module someone else
    registered under the bare name must be replaced, never served as the sibling.
    """
    path = SCRIPT_DIR / f"{name}.py"
    cached = sys.modules.get(name)
    if cached is not None and Path(getattr(cached, "__file__", "")).resolve() == path:
        return cached
    return _load_path(name, path)


def _load_badger_store() -> Any:
    """The vendored store beside this file; a stale bare-name copy is never used.

    Unlike the generic sibling, the bare ``badger_store`` name is bound by other modules at
    import time (hooks, trackers). A stale copy cached under it is therefore not displaced
    here: rebinding it would make their patches miss the copy this service talks to, so the
    sibling is loaded privately instead (the R4-F8 full-suite fallout).
    """
    path = SCRIPT_DIR / "badger_store.py"
    cached = sys.modules.get("badger_store")
    if cached is not None and Path(getattr(cached, "__file__", "")).resolve() == path:
        return cached
    try:
        import badger_store  # pylint: disable=import-outside-toplevel,redefined-outer-name
        if Path(getattr(badger_store, "__file__", "")).resolve() == path:
            return badger_store
    except ImportError:
        pass
    if "badger_store" in sys.modules:
        return _load_path("task_plan_store._badger_store", path)
    return _load_sibling("badger_store")


badger_store = _load_badger_store()
model = _load_sibling("task_plan_model")
SUPPORTED_SCHEMA_VERSION = model.SUPPORTED_SCHEMA_VERSION

# --------------------------------------------------------------------------- errors


class PlanStoreError(RuntimeError):
    """A structured plan-service refusal; ``code`` comes from the frozen closed set.

    ``as_error()`` is the payload the MCP/CLI transports lift into their error envelope;
    ``retryable`` is true only for a transient store condition (a held write lock).
    """

    code = "store-error"
    retryable = False

    def __init__(self, message: str, **details: Any) -> None:
        super().__init__(message)
        self.details = details

    def as_error(self) -> dict:
        return {"code": self.code, "message": str(self), "retryable": self.retryable,
                **self.details}


class PlanNotFound(PlanStoreError):
    """The plan or step a caller named does not exist."""

    code = "not-found"

    def __init__(self, what: str) -> None:
        super().__init__(f"{what} does not exist", what=what)


class PlanConflictError(PlanStoreError):
    """The CAS write lost: the row is not at the revision the caller read."""

    code = "conflict"

    def __init__(self, task_id: str, current_revision: Optional[int],
                 expected_revision: Optional[int]) -> None:
        super().__init__(
            f"plan {task_id!r} is at revision {current_revision}; the write expected "
            f"revision {expected_revision} -- re-read the plan and retry",
            task_id=task_id, expected_revision=expected_revision,
            current_revision=current_revision)
        self.task_id = task_id
        self.current_revision = current_revision
        self.expected_revision = expected_revision


class SchemaVersionUnsupported(PlanStoreError):
    """The raw document's ``schema_version`` is newer, or older with no upgrade hook."""

    code = "schema-version-unsupported"
    remediation = "upgrade ai-badger, or re-create the plan with this version"

    def __init__(self, found: Any) -> None:
        super().__init__(
            f"plan schema_version {found!r} is not supported (supported: "
            f"{SUPPORTED_SCHEMA_VERSION}); {self.remediation}",
            found=found)
        self.found = found

    def as_error(self) -> dict:
        return {"code": self.code, "found": self.found,
                "supported": [SUPPORTED_SCHEMA_VERSION], "remediation": self.remediation}


class StoreUnavailable(PlanStoreError):
    """The tracking store cannot serve the plans table; ``retryable`` marks a held lock."""

    def __init__(self, message: str, *, retryable: bool = False) -> None:
        super().__init__(message)
        self.retryable = retryable


# --------------------------------------------------------------------------- root resolution

# badger_lib.GIT_LOCATION_ENV, repeated because this ships into projects with no framework
# checkout to import it from. tests/test_git_invocation.py pins every standalone copy.
_GIT_LOCATION_ENV = ("GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE",
                     "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES",
                     "GIT_PREFIX", "GIT_NAMESPACE", "GIT_CEILING_DIRECTORIES")


def git_env(env: Optional[dict] = None) -> dict:
    """``env`` (default ``os.environ``) minus every variable pinning git to another repo."""
    out = dict(os.environ if env is None else env)
    for name in _GIT_LOCATION_ENV:
        out.pop(name, None)
    return out


def _is_inside(candidate: Path, parent: Path) -> bool:
    try:
        candidate.relative_to(parent)
        return True
    except ValueError:
        return False


def _git_worktree_facts(project: Path) -> tuple:
    """(toplevel, main checkout) for *project*, or (None, None) when git cannot say."""
    try:
        result = subprocess.run(
            ["git", "-C", str(project), "worktree", "list", "--porcelain"],
            capture_output=True, text=True, check=False, timeout=10, env=git_env(),
        )
    except (OSError, subprocess.SubprocessError):
        return None, None
    if result.returncode != 0:
        return None, None
    worktrees = [Path(line[len("worktree "):]).resolve()
                 for line in result.stdout.splitlines() if line.startswith("worktree ")]
    if not worktrees:
        return None, None
    main_checkout = worktrees[0]
    resolved = project.resolve()
    candidates = [wt for wt in worktrees if wt == resolved or _is_inside(resolved, wt)]
    toplevel = max(candidates, key=lambda path: len(str(path))) if candidates else None
    return toplevel, main_checkout


def collapse_worktree(project: Path) -> Path:
    """The checkout a linked worktree belongs to, or *project* unchanged."""
    toplevel, checkout = _git_worktree_facts(project)
    if checkout is None or toplevel != project.resolve() or checkout == project.resolve():
        return project
    if not (checkout / ".ai-badger" / "config.json").is_file():
        return project
    return checkout


def project_above(start: Path, stop: Optional[Path] = None) -> Optional[Path]:
    """The nearest ancestor of *start* holding `.ai-badger/config.json`, collapsed.

    ``stop`` is an ancestor the walk refuses to adopt or pass: ``$HOME`` for a walk from
    the plugin cache, where every project's marker is one home directory away.
    """
    for ancestor in (start, *start.parents):
        if stop is not None and ancestor == stop:
            return None
        if (ancestor / ".ai-badger" / "config.json").is_file():
            return collapse_worktree(ancestor)
    return None


def resolve_project_root(env: Optional[dict] = None, cwd: Optional[Path] = None,
                         script_dir: Optional[Path] = None) -> Path:
    """Resolve the ai-badger project root, in the frozen precedence order:

    1. ``CLAUDE_PROJECT_DIR``, when set and pointing at an existing directory.
    2. The marker walk up from ``cwd`` (linked worktrees collapsed to their checkout).
    3. The marker walk up from ``script_dir``, stopping at ``$HOME``.
    4. Fallback: ``script_dir.parents[3]``, for a copy under no marker reached from no
       project (the plugin cache with no session context).
    """
    env = os.environ if env is None else env
    env_dir = env.get("CLAUDE_PROJECT_DIR")
    if env_dir and Path(env_dir).is_dir():
        return Path(env_dir)

    from_cwd = project_above(Path.cwd() if cwd is None else Path(cwd))
    if from_cwd is not None:
        return from_cwd

    script = SCRIPT_DIR if script_dir is None else Path(script_dir)
    from_script = project_above(script, stop=Path.home())
    if from_script is not None:
        return from_script

    return script.parents[3]  # .claude/skills/task/scripts -> repo root


def tracking_root(env: Optional[dict] = None, cwd: Optional[Path] = None,
                  script_dir: Optional[Path] = None) -> Path:
    """``AI_BADGER_TRACKING_ROOT`` when already set, else the resolved project's tracking dir."""
    env = os.environ if env is None else env
    explicit = env.get(TRACKING_ROOT_ENV)
    if explicit:
        return Path(explicit)
    return resolve_project_root(env, cwd, script_dir) / ".ai-badger" / "task-tracking"


# --------------------------------------------------------------------------- store opening


def _store_unavailable(exc: BaseException) -> StoreUnavailable:
    """Map a raw store failure to the actionable refusal the transports report."""
    text = str(exc)
    locked = "database is locked" in text.lower()
    if locked:
        remediation = "retry once the other writer releases the store"
    elif "no such table" in text.lower():
        remediation = ("run den-refresh to upgrade ai-badger, or run the task pipeline once "
                       "to create the plans table")
    else:
        remediation = ("run den-refresh to upgrade ai-badger, or run the task pipeline once "
                       "to create the tracking store")
    return StoreUnavailable(f"{text}; {remediation}", retryable=locked)


def open_plan_store(env: Optional[dict] = None, cwd: Optional[Path] = None,
                    script_dir: Optional[Path] = None):
    """Open the tracking store for plans, rooted by the frozen precedence at call time.

    The resolved root is pinned into ``AI_BADGER_TRACKING_ROOT`` before the vendored store
    reads it; an explicit value already set wins over every resolution step.
    """
    root = tracking_root(env=env, cwd=cwd, script_dir=script_dir)
    os.environ[TRACKING_ROOT_ENV] = str(root)
    try:
        return badger_store.open_tracking()
    except (sqlite3.Error, OSError) as exc:
        raise _store_unavailable(exc) from exc


@contextlib.contextmanager
def _plan_transaction(env: Optional[dict] = None, cwd: Optional[Path] = None,
                      script_dir: Optional[Path] = None) -> Iterator[Any]:
    """One BEGIN IMMEDIATE write transaction on a fresh store: commit or roll back."""
    store = open_plan_store(env=env, cwd=cwd, script_dir=script_dir)
    try:
        try:
            store.conn.execute("BEGIN IMMEDIATE")
        except sqlite3.Error as exc:
            raise _store_unavailable(exc) from exc
        try:
            yield store
            store.conn.commit()
        except BaseException:
            store.conn.rollback()
            raise
    finally:
        store.close()


# --------------------------------------------------------------------------- payload / version gate


def canonical_payload(plan) -> str:
    """The plan as the canonical JSON stored in ``plans.payload`` (aliases, no None padding)."""
    return plan.model_dump_json(by_alias=True, exclude_none=True)


def _is_version(value: Any) -> bool:
    return isinstance(value, int) and not isinstance(value, bool)


def _upgrade(document: Mapping) -> Mapping:
    """Apply the pre-pydantic version gate, refusing any version with no upgrade hook."""
    version = document.get("schema_version")
    if not _is_version(version):
        return document  # the model reports the malformed version
    while version < SUPPORTED_SCHEMA_VERSION:
        hook = model.PLAN_UPGRADES.get(version)
        if hook is None:
            raise SchemaVersionUnsupported(version)
        upgraded = hook(document)
        next_version = upgraded.get("schema_version") if isinstance(upgraded, Mapping) else None
        if not _is_version(next_version) or next_version <= version:
            raise SchemaVersionUnsupported(version)
        document, version = upgraded, next_version
    if version > SUPPORTED_SCHEMA_VERSION:
        raise SchemaVersionUnsupported(version)
    return document


def decode_plan(payload: str):
    """A raw stored payload through the version gate and then the model, in that order.

    The ``schema_version`` is read off the raw JSON before pydantic sees the document:
    newer than supported refuses, older upgrades only through a registered ``PLAN_UPGRADES``
    hook, and a version with no hook refuses identically -- never silent coercion.
    """
    try:
        document = json.loads(payload)
    except (TypeError, ValueError) as exc:
        raise StoreUnavailable(f"plans payload is not JSON: {exc}") from exc
    if isinstance(document, Mapping):
        document = _upgrade(document)
    return model.TaskPlan.model_validate(document)


# --------------------------------------------------------------------------- reads / writes


def _read_row(store, task_id: str) -> dict:
    try:
        row = store.plan_row(task_id)
    except sqlite3.Error as exc:
        raise _store_unavailable(exc) from exc
    if row is None:
        raise PlanNotFound(f"plan {task_id!r}")
    return row


def load_plan(task_id: str, *, store=None):
    """The stored plan, or ``PlanNotFound``; the version gate runs before the model."""
    if store is not None:
        return decode_plan(_read_row(store, task_id)["payload"])
    active = open_plan_store()
    try:
        return decode_plan(_read_row(active, task_id)["payload"])
    finally:
        active.close()


def _now() -> str:
    """UTC ISO-8601 timestamp for the document's ``updated_at``."""
    return datetime.now(timezone.utc).isoformat()


def _stamp(plan, revision: int):
    """The plan re-validated with ``revision`` and ``updated_at`` server-stamped."""
    document = plan.model_dump(by_alias=True, exclude_none=True, mode="json",
                               warnings=False)
    document["revision"] = revision
    document["updated_at"] = _now()
    return model.TaskPlan.model_validate(document)


def _upsert(store, task_id: str, payload: str, expected_revision: Optional[int]) -> int:
    try:
        return store.plan_upsert(task_id, payload, expected_revision)
    except badger_store.PlanConflict as exc:
        raise PlanConflictError(task_id, exc.current_revision, expected_revision) from exc
    except sqlite3.Error as exc:
        raise _store_unavailable(exc) from exc


def _write_plan(store, plan, expected_revision: Optional[int]):
    """Stamp and CAS-write *plan* on an already-open store; return the stamped document."""
    if expected_revision is not None:
        _read_row(store, plan.task_id)  # CAS on an absent row is not-found, never an insert
    revision = 0 if expected_revision is None else expected_revision + 1
    stamped = _stamp(plan, revision)
    _upsert(store, plan.task_id, canonical_payload(stamped), expected_revision)
    return stamped


def save_plan(plan, *, expected_revision: Optional[int] = None, store=None):
    """Create at revision 0 or CAS-update a plan; return the stamped document.

    ``expected_revision=None`` is create-only: an existing row refuses with ``conflict``.
    A CAS on an absent row is ``not-found``. A caller-provided store is written directly
    (the caller owns its transaction); otherwise one BEGIN IMMEDIATE transaction wraps it.
    """
    if store is not None:
        return _write_plan(store, plan, expected_revision)
    with _plan_transaction() as active:
        return _write_plan(active, plan, expected_revision)


def update_step(task_id: str, step_id: str, mutate: Callable, *, expected_revision: int,
                store=None):
    """One atomic read-validate-mutate-CAS write of one step; return the stored document.

    ``mutate(step)`` returns the replacement step, or ``None`` for a no-op: a no-op writes
    nothing and returns the stored plan. A stale ``expected_revision`` (or a competing
    writer) raises ``PlanConflictError`` carrying the current revision.
    """
    if store is not None:
        return _mutate_step(store, task_id, step_id, mutate, expected_revision)
    with _plan_transaction() as active:
        return _mutate_step(active, task_id, step_id, mutate, expected_revision)


def _mutate_step(store, task_id: str, step_id: str, mutate: Callable, expected_revision: int):
    row = _read_row(store, task_id)
    if row["revision"] != expected_revision:
        raise PlanConflictError(task_id, row["revision"], expected_revision)
    plan = decode_plan(row["payload"])
    step = plan.workflow.steps.get(step_id)
    if step is None:
        raise PlanNotFound(f"step {step_id!r} in plan {task_id!r}")
    mutated = mutate(step)
    if mutated is None:
        return plan
    document = plan.model_dump(by_alias=True, exclude_none=True, mode="json",
                               warnings=False)
    document["workflow"]["steps"][step_id] = mutated.model_dump(
        by_alias=True, exclude_none=True, mode="json", warnings=False)
    document["revision"] = expected_revision + 1
    document["updated_at"] = _now()
    stamped = model.TaskPlan.model_validate(document)
    _upsert(store, task_id, canonical_payload(stamped), expected_revision)
    return stamped
