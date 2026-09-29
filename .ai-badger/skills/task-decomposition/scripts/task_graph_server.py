#!/usr/bin/env python3
# /// script
# requires-python = ">=3.10"
# dependencies = ["pydantic>=2.12,<3"]
# ///
# the frozen §2 tool surface is one interface with one protocol loop; splitting it would
# scatter the contract across modules, so the module budget yields (same as badger_store.py)
# pylint: disable=too-many-lines
"""task-graph MCP server: the 12 frozen plan tools over hand-rolled NDJSON stdio JSON-RPC.

One ``TaskPlan`` (DR6) is authored, decomposed and executed through twelve tools whose names,
inputs, outputs and closed error-code set are frozen in the plan's §2 interface freeze. The
protocol layer speaks JSON-RPC 2.0 over stdin/stdout — one JSON object per line, UTF-8,
diagnostics on stderr only — and implements exactly ``initialize``, ``notifications/initialized``
(no-op), ``ping``, ``tools/list`` and ``tools/call``; protocol failures are JSON-RPC errors
(parse −32700, invalid request −32600, method not found −32601, unknown tool −32602, internal
−32603) while tool-domain failures are ``isError:true`` results carrying the closed error
envelope, so nothing raises through the loop.

Every mutation is one ``BEGIN IMMEDIATE`` read→validate→mutate→CAS write over the S6 store
(``task_plan_store``); ``content_hash`` (DR5) makes create/replace/complete/ac_check/fail/skip
replays no-ops with ``changed:false``. After every write the server renders
``<tracking-root>/plans/<YYYY-MM-DD>-<taskId>.md`` (DR10) as the human fallback view, with one
``**S<N> …**`` heading per step in topological order and one checkbox per acceptance criterion.

Run through PEP 723: ``uv run --script task_graph_server.py``. ``--check`` validates the
checked-in ``schemas/task-plan.schema.json`` against this runtime's pydantic model and exits
0/1, so the prerequisite is a real check rather than a claim.

The ``completion`` slot is the only record v1 keeps: a forced override note or a failure
reason stays readable until the step advances and the next move overwrites it (no event log).
"""
from __future__ import annotations

import argparse
import importlib.util
import json
import sqlite3
import sys
from contextlib import contextmanager
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Callable, Dict, Iterator, List, Literal, Mapping, NamedTuple, Optional, Tuple

from pydantic import BaseModel, ConfigDict, Field, ValidationError

SCRIPT_DIR = Path(__file__).resolve().parent

SUPPORTED_PROTOCOL_VERSIONS = ("2024-11-05", "2025-03-26", "2025-06-18")
LATEST_PROTOCOL_VERSION = SUPPORTED_PROTOCOL_VERSIONS[-1]
SCHEMA_URL = "https://github.com/Arasz/ai-badger/schemas/task-plan.schema.json"
SCHEMA_RELPATHS = ("schemas/task-plan.schema.json", ".ai-badger/schemas/task-plan.schema.json")

JSONRPC_PARSE_ERROR = -32700
JSONRPC_INVALID_REQUEST = -32600
JSONRPC_METHOD_NOT_FOUND = -32601
JSONRPC_INVALID_PARAMS = -32602
JSONRPC_INTERNAL_ERROR = -32603

#: The frozen §2 error-code → HTTP-analogue status map (the only codes this server emits).
ERROR_STATUS: Dict[str, int] = {
    "invalid-arguments": 422, "not-found": 404, "already-exists": 409, "conflict": 409,
    "invalid-transition": 409, "dependencies-incomplete": 409, "criteria-unmet": 422,
    "plan-in-progress": 409, "schema-version-unsupported": 422, "prerequisite-missing": 500,
    "config-error": 500, "store-error": 500,
}
ERROR_TITLES: Dict[str, str] = {
    "invalid-arguments": "Invalid arguments", "not-found": "Not found",
    "already-exists": "Already exists", "conflict": "Revision conflict",
    "invalid-transition": "Invalid transition",
    "dependencies-incomplete": "Dependencies incomplete",
    "criteria-unmet": "Acceptance criteria unmet", "plan-in-progress": "Plan in progress",
    "schema-version-unsupported": "Schema version unsupported",
    "prerequisite-missing": "Prerequisite missing", "config-error": "Configuration error",
    "store-error": "Store error",
}


# ------------------------------------------------------------------ sibling module loading


def _load_sibling(name: str):
    """A module beside this file, under its plain name, cached in ``sys.modules``.

    ``task_graph`` registers the bare ``task_plan_model`` when it loads, so the store and this
    module share one model object instead of three copies of the classes.
    """
    path = SCRIPT_DIR / f"{name}.py"
    cached = sys.modules.get(name)
    if cached is not None and Path(getattr(cached, "__file__", "")).resolve() == path:
        return cached
    spec = importlib.util.spec_from_file_location(name, path)
    if spec is None or spec.loader is None:  # pragma: no cover - the files ship beside us
        raise ImportError(f"cannot load {name} at {path}")
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    try:
        spec.loader.exec_module(module)
    except BaseException:
        sys.modules.pop(name, None)
        raise
    return module


graph = _load_sibling("task_graph")
model = _load_sibling("task_plan_model")
task_plan_store = _load_sibling("task_plan_store")
StepStatus = model.StepStatus


def _log(message: str) -> None:
    """Diagnostics go to stderr only; stdout is the protocol wire."""
    print(f"task-graph: {message}", file=sys.stderr, flush=True)


def _utc_now() -> str:
    """ISO-8601 UTC, seconds precision, ``Z`` suffix — the model's accepted timestamp shape."""
    return datetime.now(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")


def _now_dt() -> datetime:
    """An aware UTC now for the graph ops' own stamping."""
    return datetime.now(timezone.utc)


def _server_version() -> str:
    """The first ``VERSION`` file above this script, else ``"0"``."""
    for ancestor in (SCRIPT_DIR, *SCRIPT_DIR.parents):
        candidate = ancestor / "VERSION"
        if candidate.is_file():
            return candidate.read_text(encoding="utf-8").strip()
    return "0"


# ------------------------------------------------------------------------- argument models


class ToolInput(BaseModel):
    """Base for every tool argument model: unknown keys are refused, never dropped."""

    model_config = ConfigDict(extra="forbid", populate_by_name=True)


class EvidenceInput(ToolInput):
    """One piece of evidence a caller attaches; ``recorded_at`` defaults to server time."""

    kind: Literal["command", "test", "artifact", "review", "note"]
    summary: str = Field(min_length=1)
    ref: Optional[str] = None
    recorded_at: Optional[str] = None


class AcceptanceCriterionDraft(ToolInput):
    """One checkable acceptance criterion inside an authored step."""

    id: str = Field(min_length=1)
    statement: str = Field(min_length=1)
    check: Optional[str] = None


class StepDraft(ToolInput):
    """One authored step; runtime state (status/evidence) is the server's, never the caller's."""

    id: str = Field(pattern=model.STEP_ID_PATTERN)
    goal: str = Field(min_length=1)
    instructions: str = Field(min_length=1)
    effort: Literal["low", "medium", "high"]
    level: Optional[Literal["low", "medium", "high"]] = None
    model: Optional[str] = None
    persona: Optional[str] = None
    depends_on: List[str] = Field(default_factory=list)
    acceptance_criteria: List[AcceptanceCriterionDraft] = Field(default_factory=list)
    files: List[str] = Field(default_factory=list)
    resources: List[str] = Field(default_factory=list)


class PlanRef(ToolInput):
    """The only plan identity there is: ``task_id`` (``plan_id`` does not exist)."""

    task_id: str = Field(pattern=model.TASK_ID_PATTERN)


class PlanCreateInput(PlanRef):
    """`plan_create`: author a new plan at revision 0."""

    task_description_ref: str = Field(min_length=1)
    steps: List[StepDraft] = Field(min_length=1)
    task_context: str = ""
    research_ref: Optional[str] = None
    loop: Literal["low", "high"]
    source_refs: List[str] = Field(default_factory=list)


class PlanReplaceInput(PlanRef):
    """`plan_replace`: re-author the whole plan under an expected-revision CAS."""

    expected_revision: int = Field(ge=0)
    task_description_ref: str = Field(min_length=1)
    steps: List[StepDraft] = Field(min_length=1)
    task_context: str = ""
    research_ref: Optional[str] = None
    loop: Literal["low", "high"]
    source_refs: List[str] = Field(default_factory=list)


class PlanGetInput(PlanRef):
    """`plan_get`: one of four read projections, optionally guarded by an expected revision."""

    include: Literal["summary", "full", "steps", "state"] = "summary"
    revision: Optional[int] = Field(default=None, ge=0)


class PlanExportInput(PlanRef):
    """`plan_export`: the stored document verbatim."""


class StepGetInput(PlanRef):
    """`step_get`: one step plus its relations."""

    step_id: str = Field(min_length=1)


class StepStartInput(PlanRef):
    """`step_start`: advance pending/failed → in_progress, optionally forcing the dep guard."""

    step_id: str = Field(min_length=1)
    expected_revision: int = Field(ge=0)
    run_id: Optional[str] = None
    force: bool = False
    reason: Optional[str] = None


class StepCompleteInput(PlanRef):
    """`step_complete`: in_progress → complete with AC results and evidence."""

    step_id: str = Field(min_length=1)
    expected_revision: int = Field(ge=0)
    evidence: List[EvidenceInput] = Field(default_factory=list)
    criterion_results: Dict[str, Literal["passed", "failed"]] = Field(default_factory=dict)
    note: Optional[str] = None
    force: bool = False
    reason: Optional[str] = None


class StepFailInput(PlanRef):
    """`step_fail`: in_progress → failed with a reason."""

    step_id: str = Field(min_length=1)
    expected_revision: int = Field(ge=0)
    evidence: List[EvidenceInput]
    reason: str = Field(min_length=1)


class StepSkipInput(PlanRef):
    """`step_skip`: any non-terminal → skipped (DR7 wave-1 amendment)."""

    step_id: str = Field(min_length=1)
    expected_revision: int = Field(ge=0)
    reason: str = Field(min_length=1)
    evidence: List[EvidenceInput] = Field(default_factory=list)


class AcCheckInput(PlanRef):
    """`ac_check`: record one acceptance-criterion verdict."""

    step_id: str = Field(min_length=1)
    ac_id: str = Field(min_length=1)
    expected_revision: int = Field(ge=0)
    status: Literal["passed", "failed"]
    evidence: List[EvidenceInput] = Field(default_factory=list)
    note: Optional[str] = None


class StepsReadyInput(PlanRef):
    """`steps_ready`: the dispatch frontier and deferral waves (offline by contract)."""

    waves: bool = True


class ProgressChecklistInput(PlanRef):
    """`progress_checklist`: the derived status view, JSON or text."""

    format: Literal["json", "text"] = "json"


# --------------------------------------------------------------------------- domain errors


class ToolRefusal(Exception):
    """A tool-domain refusal; ``code`` is from the closed set and maps to a status."""

    def __init__(self, code: str, detail: str, details: Optional[dict] = None,
                 *, retryable: bool = False) -> None:
        super().__init__(detail)
        self.code = code
        self.detail = detail
        self.details = dict(details) if details else None
        self.retryable = retryable


def _refuse(code: str, detail: str, details: Optional[dict] = None,
            *, retryable: bool = False) -> ToolRefusal:
    """Construct a refusal (kept a function so guard clauses read as intent)."""
    return ToolRefusal(code, detail, details, retryable=retryable)


def _not_found(task_id: str, *, step_id: Optional[str] = None) -> ToolRefusal:
    what = f"step {step_id!r} in plan {task_id!r}" if step_id else f"plan {task_id!r}"
    return _refuse("not-found", f"{what} does not exist",
                   {"task_id": task_id, **({"step_id": step_id} if step_id else {})})


def _conflict(task_id: str, current_revision: Optional[int],
              expected_revision: int) -> ToolRefusal:
    return _refuse(
        "conflict",
        f"plan {task_id!r} is at revision {current_revision}; the write expected "
        f"revision {expected_revision} -- re-read the plan and retry",
        {"task_id": task_id, "current_revision": current_revision,
         "expected_revision": expected_revision})


def _findings(exc: ValidationError) -> List[dict]:
    """Pydantic errors as structured findings: loc as a dotted path, type, message."""
    return [{"loc": ".".join(str(part) for part in error["loc"]), "type": error["type"],
             "msg": error["msg"]} for error in exc.errors()]


def _invalid_arguments(detail: str, exc: Optional[ValidationError] = None) -> ToolRefusal:
    details = {"findings": _findings(exc)} if exc is not None else None
    return _refuse("invalid-arguments", detail, details)


def _map_transition(refused: graph.TransitionRefused) -> ToolRefusal:
    """Map a frozen graph refusal onto the closed tool error set.

    ``already_complete`` is a graph-internal name for the complete→X refusal; the tool surface
    calls that ``invalid-transition`` with no allowed transitions. ``dependencies-incomplete``
    carries ``blocking[]`` on the wire instead of the graph's ``waiting_on``.
    """
    details = dict(refused.details)
    if refused.code == "already_complete":
        return _refuse("invalid-transition", str(refused),
                       {"step_id": details.get("step_id"), "status": "complete",
                        "allowed_transitions": list(graph.ALLOWED_TOOLS["complete"])})
    if refused.code == "dependencies-incomplete":
        details["blocking"] = details.pop("waiting_on", [])
    code = refused.code if refused.code in ERROR_STATUS else "invalid-transition"
    return _refuse(code, str(refused), details)


# ------------------------------------------------------------------------- store plumbing


def _unavailable(exc: BaseException) -> task_plan_store.StoreUnavailable:
    """A raw sqlite failure as the actionable store-error the envelope reports."""
    locked = "database is locked" in str(exc).lower()
    return task_plan_store.StoreUnavailable(str(exc), retryable=locked)


@contextmanager
def _write_transaction() -> Iterator[Any]:
    """One ``BEGIN IMMEDIATE`` read→validate→mutate→CAS transaction; commit or roll back."""
    store = task_plan_store.open_plan_store()
    try:
        try:
            store.conn.execute("BEGIN IMMEDIATE")
        except sqlite3.Error as exc:
            raise _unavailable(exc) from exc
        try:
            yield store
            store.conn.commit()
        except BaseException:
            store.conn.rollback()
            raise
    finally:
        store.close()


def _row(store, task_id: str) -> Optional[dict]:
    """The plans row, or None; a broken store raises rather than reading as absent."""
    try:
        return store.plan_row(task_id)
    except sqlite3.Error as exc:
        raise _unavailable(exc) from exc


def _plan_at_revision(store, task_id: str, expected_revision: int):
    """The decoded plan at *expected_revision*, refusing absent rows and stale revisions."""
    row = _row(store, task_id)
    if row is None:
        raise _not_found(task_id)
    if row["revision"] != expected_revision:
        raise _conflict(task_id, row["revision"], expected_revision)
    return task_plan_store.decode_plan(row["payload"])


def _read_plan(task_id: str) -> Tuple[Any, str]:
    """A read-only load: the decoded plan and the raw stored payload."""
    store = task_plan_store.open_plan_store()
    try:
        row = _row(store, task_id)
        if row is None:
            raise _not_found(task_id)
        return task_plan_store.decode_plan(row["payload"]), row["payload"]
    finally:
        store.close()


# ------------------------------------------------------------------------- plan projection


def _dump(value) -> dict:
    """A pydantic value as its canonical JSON shape: aliases applied, nulls dropped."""
    return value.model_dump(by_alias=True, mode="json", exclude_none=True)


def _criteria_tally(step) -> dict:
    """`{passed,total}` for one step's acceptance criteria."""
    return {"passed": sum(1 for criterion in step.acceptance_criteria
                          if criterion.status.value == "passed"),
            "total": len(step.acceptance_criteria)}


def _step_ref(plan, ready_step) -> dict:
    """One ready-frontier entry: the step's dispatch facts plus its relations."""
    step = plan.workflow.steps[ready_step.step_id]
    ref = {"id": step.id, "goal": step.goal, "effort": step.effort.value,
           "files": list(step.files), "resources": list(step.resources),
           "blocked_by": list(ready_step.blocked_by),
           "skipped_deps": list(ready_step.skipped_deps),
           "criteria": _criteria_tally(step)}
    if step.level is not None:
        ref["level"] = step.level.value
    if step.model is not None:
        ref["model"] = step.model
    return ref


def _ready_refs(plan) -> List[dict]:
    """The ready frontier as wire entries, topological order."""
    return [_step_ref(plan, ready_step) for ready_step in graph.ready(plan)]


def _blocked_refs(plan) -> List[dict]:
    """Remaining steps with a failed/skipped ancestor, as `{id,blocked_by[]}` entries."""
    return [{"id": blocked_step.step_id, "blocked_by": list(blocked_step.blocked_by)}
            for blocked_step in graph.blocked(plan)]


def _counts(plan) -> dict:
    """Step-status histogram with a stable key set."""
    counts = {"total": len(plan.workflow.steps), "pending": 0, "in_progress": 0, "complete": 0,
              "failed": 0, "skipped": 0}
    for step in plan.workflow.steps.values():
        counts[step.status.value] += 1
    return counts


def _criteria_total(plan) -> dict:
    """`{passed,total}` across the whole plan's acceptance criteria."""
    passed = total = 0
    for step in plan.workflow.steps.values():
        passed += sum(1 for criterion in step.acceptance_criteria
                      if criterion.status.value == "passed")
        total += len(step.acceptance_criteria)
    return {"passed": passed, "total": total}


def _build_plan(args, *, created_at: str, schema_: Optional[str] = None):
    """Draft arguments → a validated revision-0 ``TaskPlan`` document.

    Duplicate step ids are refused before the map swallows one; the model's V1–V12 invariants
    (self-dependency, unknown dependency, cycle, duplicate AC ids, id/timestamp grammar) are
    enforced by pydantic and reported as structured findings.
    """
    seen: Dict[str, bool] = {}
    for draft in args.steps:
        if draft.id in seen:
            raise _refuse(
                "invalid-arguments", f"duplicate step id {draft.id!r}",
                {"findings": [{"loc": "steps", "type": "duplicate-step-id",
                               "msg": f"duplicate step id {draft.id!r}"}]})
        seen[draft.id] = True
    steps: Dict[str, dict] = {}
    for draft in args.steps:
        steps[draft.id] = {
            "id": draft.id, "goal": draft.goal, "instructions": draft.instructions,
            "effort": draft.effort, "level": draft.level, "model": draft.model,
            "persona": draft.persona, "depends_on": list(draft.depends_on),
            "acceptance_criteria": [
                {"id": criterion.id, "statement": criterion.statement,
                 "check": criterion.check, "status": "unchecked", "evidence": []}
                for criterion in draft.acceptance_criteria],
            "files": list(draft.files), "resources": list(draft.resources),
            "status": "pending",
        }
    document = {
        "schema_version": model.SUPPORTED_SCHEMA_VERSION,
        "task_id": args.task_id, "task_description_ref": args.task_description_ref,
        "research_ref": args.research_ref, "task_context": args.task_context,
        "loop": args.loop, "source_refs": list(args.source_refs),
        "workflow": {"steps": steps}, "revision": 0,
        "created_at": created_at, "updated_at": created_at,
    }
    if schema_ is not None:
        document["$schema"] = schema_
    try:
        return model.TaskPlan.model_validate(document)
    except ValidationError as exc:
        raise _invalid_arguments("the plan document failed validation", exc) from exc


def _with_step(plan, step_id: str, new_step):
    """A copy of *plan* whose *step_id* is replaced (validated on the next store write)."""
    steps = dict(plan.workflow.steps)
    steps[step_id] = new_step
    return plan.model_copy(
        update={"workflow": plan.workflow.model_copy(update={"steps": steps})})


def _require_step(plan, step_id: str):
    """The named step or ``not-found``."""
    step = plan.workflow.steps.get(step_id)
    if step is None:
        raise _not_found(plan.task_id, step_id=step_id)
    return step


# ------------------------------------------------------------------------------ evidence


def _stamp_evidence(item: EvidenceInput):
    """One input evidence as a domain ``Evidence``, recorded_at defaulting to now."""
    return model.Evidence(kind=item.kind, summary=item.summary, ref=item.ref,
                          recorded_at=item.recorded_at or _utc_now())


def _evidence_key(evidence: List[Any]) -> str:
    """The replay key of an evidence list: sorted, ``recorded_at`` excluded."""
    normalized = [{"kind": item.kind.value if hasattr(item.kind, "value") else item.kind,
                   "summary": item.summary, "ref": item.ref}
                  for item in evidence]
    return json.dumps(sorted(normalized, key=lambda item: json.dumps(item, sort_keys=True)),
                      sort_keys=True)


def _terminal_replays(step, reason: Optional[str], evidence: List[Any]) -> bool:
    """True when a fail/skip replay names the same reason and the same evidence."""
    completion = step.completion
    if completion is None:
        return False
    return (completion.note or "") == (reason or "") and \
        _evidence_key(list(completion.evidence)) == _evidence_key(evidence)


# --------------------------------------------------------------------------- tool handlers


def _create_payload(plan, *, created: bool) -> dict:
    """The shared create shape: identity, revision, hash, frontier, quality findings."""
    return {"task_id": plan.task_id, "revision": plan.revision,
            "schema_version": plan.schema_version,
            "content_hash": model.content_hash(plan), "created": created,
            "steps": len(plan.workflow.steps), "ready": _ready_refs(plan),
            "waves": graph.waves(plan), "findings": _quality_findings(plan)}


def _quality_findings(plan) -> List[dict]:
    """Plan-quality findings as wire entries: `{kind, step_id}` each (zero-AC steps stay
    legal per V6 but visible; `integration_missing` marks a multi-sink workflow with no
    join step)."""
    return [{"kind": finding.kind, "step_id": finding.step_id}
            for finding in graph.plan_quality_findings(plan)]


def _replace_payload(plan, *, replaced: bool) -> dict:
    """The create shape plus the replace outcome."""
    payload = _create_payload(plan, created=False)
    payload["replaced"] = replaced
    return payload


def _tool_plan_create(args: PlanCreateInput) -> dict:
    """Create at revision 0, idempotently on identical authored content."""
    incoming = _build_plan(args, created_at=_utc_now())
    digest = model.content_hash(incoming)
    stamped = None
    with _write_transaction() as store:
        row = _row(store, args.task_id)
        if row is not None:
            current = task_plan_store.decode_plan(row["payload"])
            if model.content_hash(current) == digest:
                return _create_payload(current, created=False)
            raise _refuse(
                "already-exists",
                f"plan {args.task_id!r} already exists with different content; use plan_get "
                f"then plan_replace",
                {"current_revision": row["revision"]})
        stamped = task_plan_store.save_plan(incoming, expected_revision=None, store=store)
    _render(stamped)
    return _create_payload(stamped, created=True)


def _tool_plan_replace(args: PlanReplaceInput) -> dict:
    """Re-author the plan; identical content succeeds even on a stale revision."""
    stamped = None
    with _write_transaction() as store:
        row = _row(store, args.task_id)
        if row is None:
            raise _not_found(args.task_id)
        current = task_plan_store.decode_plan(row["payload"])
        incoming = _build_plan(args, created_at=current.created_at, schema_=current.schema_)
        if model.content_hash(incoming) == model.content_hash(current):
            return _replace_payload(current, replaced=False)
        if row["revision"] != args.expected_revision:
            raise _conflict(args.task_id, row["revision"], args.expected_revision)
        blocking = [step_id for step_id in graph.topological_order(current)
                    if current.workflow.steps[step_id].status.value
                    not in ("pending", "skipped")]
        if blocking:
            raise _refuse(
                "plan-in-progress",
                f"plan {args.task_id!r} has steps that left pending/skipped: {blocking}; "
                f"re-plan before execution",
                {"task_id": args.task_id, "blocking_steps": blocking})
        stamped = task_plan_store.save_plan(incoming, expected_revision=args.expected_revision,
                                            store=store)
    _render(stamped)
    return _replace_payload(stamped, replaced=True)


def _tool_plan_get(args: PlanGetInput) -> dict:
    """One of the four read projections, with an optional revision guard."""
    plan, _raw = _read_plan(args.task_id)
    if args.revision is not None and plan.revision != args.revision:
        raise _conflict(args.task_id, plan.revision, args.revision)
    summary = {"task_id": plan.task_id, "revision": plan.revision,
               "schema_version": plan.schema_version,
               "content_hash": model.content_hash(plan), "created_at": plan.created_at,
               "updated_at": plan.updated_at, "counts": _counts(plan),
               "ready": _ready_refs(plan), "blocked": _blocked_refs(plan)}
    if args.include == "summary":
        return summary
    if args.include == "full":
        return {**summary, "document": json.loads(task_plan_store.canonical_payload(plan))}
    if args.include == "steps":
        return {**summary, "steps": {step_id: _dump(plan.workflow.steps[step_id])
                                     for step_id in graph.topological_order(plan)}}
    return {"task_id": plan.task_id, "revision": plan.revision, "counts": _counts(plan),
            "criteria": _criteria_total(plan), "integration_ok": graph.integration_ok(plan),
            "integration_sink": graph.integration_sink(plan),
            "findings": _quality_findings(plan)}


def _tool_plan_export(args: PlanExportInput) -> dict:
    """The stored document verbatim, with the schema URL it should validate against."""
    plan, raw = _read_plan(args.task_id)
    return {"task_id": plan.task_id, "revision": plan.revision,
            "content_hash": model.content_hash(plan), "schema_url": SCHEMA_URL,
            "document": json.loads(raw)}


def _tool_step_get(args: StepGetInput) -> dict:
    """One step plus the relations a reviewer needs: ancestors, dependents, readiness."""
    plan, _raw = _read_plan(args.task_id)
    step = _require_step(plan, args.step_id)
    ready_ids = {ready_step.step_id for ready_step in graph.ready(plan)}
    blocked_by = [ancestor for ancestor in graph.ancestors(plan, args.step_id)
                  if plan.workflow.steps[ancestor].status.value in graph.FAILED_OR_SKIPPED]
    blocks = [step_id for step_id in graph.topological_order(plan)
              if step.id in plan.workflow.steps[step_id].depends_on]
    return {"revision": plan.revision, "step": _dump(step), "blocked_by": blocked_by,
            "blocks": blocks, "ready": step.id in ready_ids}


def _tool_steps_ready(args: StepsReadyInput) -> dict:
    """The dispatch frontier, the deferral waves, and the blocked remainder."""
    plan, _raw = _read_plan(args.task_id)
    return {"revision": plan.revision, "ready": _ready_refs(plan),
            "waves": graph.waves(plan) if args.waves else [],
            "blocked": _blocked_refs(plan)}


def _checklist_text(payload: dict) -> str:
    """The `format:"text"` status section."""
    lines = [f"# {payload['task_id']} — {payload['complete']}/{payload['total']} steps complete"]
    for index, row in enumerate(payload["steps"], start=1):
        lines.append(f"- [{row['marker']}] S{index} {row['goal']} "
                     f"({row['criteria']['passed']}/{row['criteria']['total']} criteria)")
    return "\n".join(lines)


def _tool_progress_checklist(args: ProgressChecklistInput) -> dict:
    """The derived checklist: topological order, glyphs, tallies, frontier."""
    plan, _raw = _read_plan(args.task_id)
    data = graph.progress_checklist_data(plan)
    steps = [{"id": row["id"], "goal": row["goal"], "status": row["status"],
              "marker": row["marker"],
              "criteria": {"passed": row["acs_passed"], "total": row["acs_total"]}}
             for row in data["steps"]]
    finding = graph.integration_finding(plan)
    payload = {"task_id": plan.task_id, "revision": plan.revision,
               "complete": data["complete"], "total": data["total"], "steps": steps,
               "next": [ready_step.step_id for ready_step in graph.ready(plan)],
               "blocked": _blocked_refs(plan),
               "integration_ok": graph.integration_ok(plan),
               "integration_finding": finding.step_id if finding is not None else None}
    if args.format == "text":
        payload["text"] = _checklist_text(payload)
    return payload


def _forced_start(plan, step_id: str, reason: str):
    """Start a blocked step anyway, recording the override as a note Evidence.

    The frozen graph op has no force path, so the view handed to it pretends the waiting
    dependencies are complete (only their statuses are read); the returned step keeps its
    real fields. The note is stored on ``completion.evidence`` because the S2 step model
    carries no step-level evidence list — ``completion.forced`` marks it as an override.
    """
    steps = dict(plan.workflow.steps)
    waiting = [dependency for dependency in dict.fromkeys(steps[step_id].depends_on)
               if steps[dependency].status.value not in graph.SATISFIED_DEPS]
    for dependency in waiting:
        steps[dependency] = steps[dependency].model_copy(
            update={"status": StepStatus.COMPLETE})
    view = plan.model_copy(
        update={"workflow": plan.workflow.model_copy(update={"steps": steps})})
    started = graph.start(view, step_id, now=_now_dt())
    note = model.Evidence(kind=model.EvidenceKind.NOTE, summary=reason,
                          recorded_at=_utc_now())
    completion = model.Completion(completed_by=None, forced=True, note=reason, evidence=[note])
    return started.model_copy(update={"completion": completion})


def _tool_step_start(args: StepStartInput) -> dict:
    """Advance a pending/failed step to in_progress; a replay is a no-op."""
    stamped = None
    with _write_transaction() as store:
        plan = _plan_at_revision(store, args.task_id, args.expected_revision)
        step = _require_step(plan, args.step_id)
        if step.status.value == "in_progress":
            return {"revision": plan.revision, "changed": False, "step": _dump(step)}
        if args.force and not (args.reason or "").strip():
            raise _invalid_arguments("force requires a non-empty reason")
        try:
            new_step = graph.start(plan, args.step_id, now=_now_dt())
        except graph.TransitionRefused as refused:
            if refused.code == "dependencies-incomplete" and args.force:
                new_step = _forced_start(plan, args.step_id, args.reason or "")
            else:
                raise _map_transition(refused) from refused
        stamped = task_plan_store.save_plan(
            _with_step(plan, args.step_id, new_step),
            expected_revision=args.expected_revision, store=store)
        payload_step = _dump(stamped.workflow.steps[args.step_id])
    _render(stamped)
    return {"revision": stamped.revision, "changed": True, "step": payload_step}


def _apply_criteria(step, results: Mapping[str, str]):
    """The step with the named criterion verdicts applied; other criteria untouched."""
    criteria = [
        criterion.model_copy(update={"status": model.CriterionStatus(results[criterion.id])})
        if criterion.id in results else criterion
        for criterion in step.acceptance_criteria]
    return step.model_copy(update={"acceptance_criteria": criteria})


def _completion_replays(step, results: Mapping[str, str], evidence: List[Any]) -> bool:
    """True when a complete-replay names the same AC verdicts and the same evidence."""
    stored = {criterion.id: criterion.status.value
              for criterion in step.acceptance_criteria}
    if any(stored.get(ac_id) != status for ac_id, status in results.items()):
        return False
    stored_evidence = step.completion.evidence if step.completion is not None else []
    return _evidence_key(list(stored_evidence)) == _evidence_key(evidence)


def _tool_step_complete(args: StepCompleteInput) -> dict:
    """Complete an in_progress step; a replay by completion hash is a no-op."""
    stamped = None
    with _write_transaction() as store:
        plan = _plan_at_revision(store, args.task_id, args.expected_revision)
        step = _require_step(plan, args.step_id)
        if args.force and not (args.reason or "").strip():
            raise _invalid_arguments("force requires a non-empty reason")
        known = {criterion.id for criterion in step.acceptance_criteria}
        unknown = sorted(set(args.criterion_results) - known)
        if unknown:
            raise _refuse(
                "invalid-arguments",
                f"step {args.step_id!r} has no acceptance criteria {unknown}",
                {"step_id": args.step_id, "unknown_criteria": unknown,
                 "known_criteria": sorted(known)})
        evidence = [_stamp_evidence(item) for item in args.evidence]
        if step.status.value == "complete":
            if _completion_replays(step, args.criterion_results, evidence):
                return {"revision": plan.revision, "changed": False, "step": _dump(step),
                        "ready": _ready_refs(plan)}
            raise _refuse(
                "invalid-transition",
                f"step {args.step_id!r} is already complete with a different completion; "
                f"re-plan to change it",
                {"step_id": args.step_id, "status": "complete", "allowed_transitions": []})
        updated = _apply_criteria(step, args.criterion_results)
        template = _with_step(plan, args.step_id, updated)
        try:
            completed = graph.complete(template, args.step_id, now=_now_dt(),
                                       force=args.force, note=args.note or args.reason)
        except graph.TransitionRefused as refused:
            raise _map_transition(refused) from refused
        completed = completed.model_copy(update={
            "completion": completed.completion.model_copy(update={"evidence": evidence})})
        stamped = task_plan_store.save_plan(
            _with_step(template, args.step_id, completed),
            expected_revision=args.expected_revision, store=store)
        payload_step = _dump(stamped.workflow.steps[args.step_id])
        ready = _ready_refs(stamped)
    _render(stamped)
    return {"revision": stamped.revision, "changed": True, "step": payload_step,
            "ready": ready}


def _tool_step_fail(args: StepFailInput) -> dict:
    """Fail an in_progress step; a same-reason, same-evidence replay is a no-op."""
    stamped = None
    with _write_transaction() as store:
        plan = _plan_at_revision(store, args.task_id, args.expected_revision)
        step = _require_step(plan, args.step_id)
        evidence = [_stamp_evidence(item) for item in args.evidence]
        if step.status.value == "failed":
            if _terminal_replays(step, args.reason, evidence):
                return {"revision": plan.revision, "changed": False, "step": _dump(step),
                        "blocked": _blocked_refs(plan)}
            raise _refuse(
                "invalid-transition",
                f"step {args.step_id!r} already failed with a different record",
                {"step_id": args.step_id, "status": "failed",
                 "allowed_transitions": list(graph.ALLOWED_TOOLS["failed"])})
        try:
            failed = graph.fail(plan, args.step_id)
        except graph.TransitionRefused as refused:
            raise _map_transition(refused) from refused
        completion = model.Completion(completed_by=None, forced=False, note=args.reason,
                                      evidence=evidence)
        stamped = task_plan_store.save_plan(
            _with_step(plan, args.step_id, failed.model_copy(update={"completion": completion})),
            expected_revision=args.expected_revision, store=store)
        payload_step = _dump(stamped.workflow.steps[args.step_id])
        blocked = _blocked_refs(stamped)
    _render(stamped)
    return {"revision": stamped.revision, "changed": True, "step": payload_step,
            "blocked": blocked}


def _tool_step_skip(args: StepSkipInput) -> dict:
    """Skip any non-terminal step (DR7 amendment); a same-reason replay is a no-op."""
    stamped = None
    with _write_transaction() as store:
        plan = _plan_at_revision(store, args.task_id, args.expected_revision)
        step = _require_step(plan, args.step_id)
        evidence = [_stamp_evidence(item) for item in args.evidence]
        if step.status.value == "skipped":
            if _terminal_replays(step, args.reason, evidence):
                return {"revision": plan.revision, "changed": False, "step": _dump(step),
                        "ready": _ready_refs(plan)}
            raise _refuse(
                "invalid-transition",
                f"step {args.step_id!r} is already skipped with a different record",
                {"step_id": args.step_id, "status": "skipped", "allowed_transitions": []})
        try:
            skipped = graph.skip(plan, args.step_id)
        except graph.TransitionRefused as refused:
            raise _map_transition(refused) from refused
        completion = model.Completion(completed_by=None, forced=False, note=args.reason,
                                      evidence=evidence)
        stamped = task_plan_store.save_plan(
            _with_step(plan, args.step_id,
                       skipped.model_copy(update={"completion": completion})),
            expected_revision=args.expected_revision, store=store)
        payload_step = _dump(stamped.workflow.steps[args.step_id])
        ready = _ready_refs(stamped)
    _render(stamped)
    return {"revision": stamped.revision, "changed": True, "step": payload_step,
            "ready": ready}


def _ac_payload(revision: int, changed: bool, step, criterion) -> dict:
    """`ac_check`'s response shape."""
    return {"revision": revision, "changed": changed, "step_id": step.id,
            "criterion": _dump(criterion), "criteria": _criteria_tally(step)}


def _tool_ac_check(args: AcCheckInput) -> dict:
    """Record one criterion verdict; the same verdict and evidence replay as a no-op."""
    stamped = None
    updated_criterion = None
    with _write_transaction() as store:
        plan = _plan_at_revision(store, args.task_id, args.expected_revision)
        step = _require_step(plan, args.step_id)
        if step.status.value == "complete":
            raise _refuse(
                "invalid-transition",
                f"step {args.step_id!r} is complete; acceptance criteria are frozen",
                {"step_id": args.step_id, "status": "complete", "allowed_transitions": []})
        criterion = next((candidate for candidate in step.acceptance_criteria
                          if candidate.id == args.ac_id), None)
        if criterion is None:
            raise _refuse(
                "invalid-arguments",
                f"step {args.step_id!r} has no acceptance criterion {args.ac_id!r}",
                {"step_id": args.step_id, "ac_id": args.ac_id,
                 "known_criteria": sorted(c.id for c in step.acceptance_criteria)})
        evidence = [_stamp_evidence(item) for item in args.evidence]
        if args.note:
            evidence.append(model.Evidence(kind=model.EvidenceKind.NOTE, summary=args.note,
                                           recorded_at=_utc_now()))
        if criterion.status.value == args.status and \
                _evidence_key(list(criterion.evidence)) == _evidence_key(evidence):
            return _ac_payload(plan.revision, False, step, criterion)
        updated_criterion = criterion.model_copy(
            update={"status": model.CriterionStatus(args.status),
                    "evidence": list(criterion.evidence) + evidence})
        updated_step = step.model_copy(update={"acceptance_criteria": [
            updated_criterion if candidate.id == args.ac_id else candidate
            for candidate in step.acceptance_criteria]})
        stamped = task_plan_store.save_plan(
            _with_step(plan, args.step_id, updated_step),
            expected_revision=args.expected_revision, store=store)
    _render(stamped)
    return _ac_payload(stamped.revision, True, stamped.workflow.steps[args.step_id],
                       updated_criterion)


# ------------------------------------------------------------------------------- rendering


def _render_markdown(plan) -> str:
    """The plan file: generated banner, one heading per step, one checkbox per criterion."""
    lines = [
        "<!-- generated by task-graph from .ai-badger/task-tracking/tracking.db — "
        f"do not edit; task_id={plan.task_id} revision={plan.revision} -->",
        f"# {plan.task_id} — task plan", ""]
    for index, step_id in enumerate(graph.topological_order(plan), start=1):
        step = plan.workflow.steps[step_id]
        lines.append(f"**S{index} — {step.goal} ({step.status.value})**")
        for criterion in step.acceptance_criteria:
            glyph = "x" if criterion.status.value == "passed" else " "
            lines.append(f"- [{glyph}] {criterion.id}: {criterion.statement}")
            if criterion.check:
                lines.append(f"  - check: {criterion.check}")
        lines.append("")
    return "\n".join(lines) + "\n"


def _render(plan) -> None:
    """Best-effort plan-file render after a write; a failure is logged, never fatal."""
    try:
        plans_dir = Path(task_plan_store.tracking_root()) / "plans"
        plans_dir.mkdir(parents=True, exist_ok=True)
        path = plans_dir / f"{plan.created_at[:10]}-{plan.task_id}.md"
        path.write_text(_render_markdown(plan), encoding="utf-8")
    except Exception as exc:  # pylint: disable=broad-except
        _log(f"render failed: {exc!r}")


def _default_root() -> Path:
    """The nearest ancestor holding the schema artifact or the project marker."""
    for ancestor in (SCRIPT_DIR, *SCRIPT_DIR.parents):
        if any((ancestor / relpath).is_file() for relpath in SCHEMA_RELPATHS) or \
                (ancestor / ".ai-badger" / "config.json").is_file():
            return ancestor
    return SCRIPT_DIR.parents[4]


def _schema_path(root: Path) -> Optional[Path]:
    """The checked-in schema under *root*, source layout first, scaffolded copy second."""
    for relpath in SCHEMA_RELPATHS:
        candidate = root / relpath
        if candidate.is_file():
            return candidate
    return None


def _pointer(parts: List[str]) -> str:
    """RFC 6901 JSON pointer for the path taken so far, escaping `~` and `/`."""
    if not parts:
        return "/"
    return "/" + "/".join(part.replace("~", "~0").replace("/", "~1") for part in parts)


def _first_difference(checked: Any, generated: Any,
                      parts: Optional[List[str]] = None) -> Optional[Tuple[str, Any, Any]]:
    """The first JSON pointer where two parsed documents differ, with both values."""
    parts = list(parts or [])
    if type(checked) is not type(generated):
        return _pointer(parts), checked, generated
    if isinstance(checked, dict):
        for key in sorted(set(checked) | set(generated)):
            if key not in checked or key not in generated:
                return _pointer(parts + [key]), checked.get(key), generated.get(key)
            found = _first_difference(checked[key], generated[key], parts + [key])
            if found:
                return found
        return None
    if isinstance(checked, list):
        if len(checked) != len(generated):
            index = min(len(checked), len(generated))
            return (_pointer(parts + [str(index)]), checked[index:index + 1],
                    generated[index:index + 1])
        for index, (left, right) in enumerate(zip(checked, generated)):
            found = _first_difference(left, right, parts + [str(index)])
            if found:
                return found
        return None
    if checked != generated:
        return _pointer(parts), checked, generated
    return None


def check_schema(root: Path) -> int:
    """Exit 0 when the checked-in schema matches this runtime's model, 1 otherwise.

    Generated-schema metadata (``$id``/``title``/``description``/``$schema``) is the emitter's
    business and is ignored here; the step-id grammar (`propertyNames`) is re-derived from the
    model and compared. This is the real prerequisite check: it proves pydantic loaded, the
    model built, and the artifact traveled with the server.
    """
    generated = model.TaskPlan.model_json_schema(mode="validation")
    for key in ("$id", "title", "description", "$schema"):
        generated.pop(key, None)
    workflow = generated.get("$defs", {}).get("Workflow", {})
    steps = workflow.get("properties", {}).get("steps", {})
    steps["propertyNames"] = {"pattern": model.STEP_ID_PATTERN}

    path = _schema_path(root)
    if path is None:
        print(f"STALE {SCHEMA_RELPATHS[0]}: missing under {root}; run the schema emitter")
        return 1
    checked = json.loads(path.read_text(encoding="utf-8"))
    for key in ("$id", "title", "description", "$schema"):
        checked.pop(key, None)
    difference = _first_difference(checked, generated)
    if difference is not None:
        pointer, got, want = difference
        print(f"STALE {path.name}: first difference at {pointer}")
        print(f"  checked in: {json.dumps(got, ensure_ascii=False)}")
        print(f"  generated:  {json.dumps(want, ensure_ascii=False)}")
        return 1
    print(f"ok {path} matches the TaskPlan model")
    return 0


# ---------------------------------------------------------------------------- dispatching


def _error_payload(name: str, code: str, detail: str, details: Optional[dict] = None,
                   *, retryable: bool = False) -> dict:
    """The closed error envelope: RFC 7807's field set over a stdio transport."""
    error = {"code": code, "title": ERROR_TITLES.get(code, code), "status": ERROR_STATUS.get(
        code, 500), "detail": detail, "instance": f"task-graph/tools/{name}",
        "retryable": retryable}
    if details:
        error["details"] = details
    return {"ok": False, "error": error}


def _plan_store_error(exc: task_plan_store.PlanStoreError) -> Tuple[str, str, dict, bool]:
    """A store refusal as `(code, detail, details, retryable)` for the envelope."""
    payload = exc.as_error()
    details = {key: value for key, value in payload.items()
               if key not in ("code", "message", "retryable")}
    return (payload.get("code", "store-error"), payload.get("message", str(exc)), details,
            bool(payload.get("retryable", getattr(exc, "retryable", False))))


def invoke_tool(name: str, arguments: Mapping) -> dict:
    """Run one tool and return its payload or its error envelope; raises only on unknown names.

    Argument validation, domain refusals, graph refusals and store refusals all become
    envelopes here, so both transports share one behaviour and the loop cannot raise.
    """
    if not isinstance(arguments, Mapping):
        return _error_payload(name, "invalid-arguments", "arguments must be a JSON object")
    spec = TOOL_BY_NAME[name]
    try:
        parsed = spec.model.model_validate(dict(arguments))
    except ValidationError as exc:
        return _error_payload(name, "invalid-arguments", "invalid tool arguments",
                              {"findings": _findings(exc)})
    try:
        return spec.handler(parsed)
    except ToolRefusal as refusal:
        return _error_payload(name, refusal.code, refusal.detail, refusal.details,
                              retryable=refusal.retryable)
    except graph.TransitionRefused as refused:
        mapped = _map_transition(refused)
        return _error_payload(name, mapped.code, mapped.detail, mapped.details,
                              retryable=mapped.retryable)
    except task_plan_store.PlanStoreError as exc:
        code, detail, details, retryable = _plan_store_error(exc)
        return _error_payload(name, code, detail, details, retryable=retryable)
    except ValidationError as exc:
        return _error_payload(name, "invalid-arguments", "the plan document failed validation",
                              {"findings": _findings(exc)})


class ToolSpec(NamedTuple):
    """One frozen tool: its name, description, argument model and handler."""

    name: str
    description: str
    model: Any
    handler: Callable[[Any], dict]
    read_only: bool
    idempotent: bool


TOOLS: Tuple[ToolSpec, ...] = (
    ToolSpec(
        "plan_create",
        "Create the task plan for a task_id at revision 0. Identical content is idempotent "
        "(created:false); different content for an existing task_id is already-exists. The "
        "reply carries the content hash, step count and the ready frontier.",
        PlanCreateInput, _tool_plan_create, False, True),
    ToolSpec(
        "plan_replace",
        "Re-author the whole plan under an expected_revision CAS. Refused with "
        "plan-in-progress once any step left pending/skipped. Replacing with identical "
        "content succeeds even on a stale revision (replaced:false).",
        PlanReplaceInput, _tool_plan_replace, False, True),
    ToolSpec(
        "plan_get",
        "Read the plan: include=summary (ids, revision, hash, counts, frontier), full (adds "
        "the document), steps (adds the step map) or state (counts, AC tallies, "
        "integration_ok, quality findings). revision guards the read against a stale one.",
        PlanGetInput, _tool_plan_get, True, True),
    ToolSpec(
        "plan_export",
        "Export the stored plan document verbatim, with the schema URL it validates against "
        "and the current content hash. Writes nothing.",
        PlanExportInput, _tool_plan_export, True, True),
    ToolSpec(
        "step_get",
        "Read one step with the relations a reviewer needs: failed/skipped ancestors, direct "
        "dependents, and whether it is currently ready.",
        StepGetInput, _tool_step_get, True, True),
    ToolSpec(
        "step_start",
        "Start a pending or failed step (failed starts are retries) and stamp started_at. A "
        "replay while in_progress is a no-op; force=true with a reason bypasses the "
        "dependency guard and records the override as a note Evidence.",
        StepStartInput, _tool_step_start, False, False),
    ToolSpec(
        "step_complete",
        "Complete an in_progress step with its acceptance-criterion verdicts and evidence. "
        "Unmet criteria refuse with criteria-unmet unless force=true and a reason are given. "
        "The same completion replayed is a no-op.",
        StepCompleteInput, _tool_step_complete, False, True),
    ToolSpec(
        "step_fail",
        "Fail an in_progress step with a reason and evidence; a later step_start is the "
        "retry. Replaying the same reason and evidence is a no-op.",
        StepFailInput, _tool_step_fail, False, True),
    ToolSpec(
        "step_skip",
        "Retire a non-terminal step (pending, in_progress or failed) with a reason. A skipped "
        "dependency still satisfies its dependents. Replaying the same record is a no-op.",
        StepSkipInput, _tool_step_skip, False, True),
    ToolSpec(
        "ac_check",
        "Record one acceptance-criterion verdict with evidence for a step that is not yet "
        "complete; it is a no-op when the same verdict and evidence are already recorded. "
        "Criteria are frozen once the step completes.",
        AcCheckInput, _tool_ac_check, False, True),
    ToolSpec(
        "steps_ready",
        "The dispatch frontier: ready steps, the deferral waves (files/resources conflicts "
        "defer the later member), and the blocked remainder. Offline by contract; hint "
        "layers live in the skill.",
        StepsReadyInput, _tool_steps_ready, True, True),
    ToolSpec(
        "progress_checklist",
        "The derived status view: per-step status, glyph and AC tally, the next ready steps, "
        "the blocked remainder, and the integration rule's verdict (integration_ok plus the "
        "finding id when no join step spans a multi-sink workflow); format=text also renders "
        "the status section.",
        ProgressChecklistInput, _tool_progress_checklist, True, True),
)
TOOL_BY_NAME: Dict[str, ToolSpec] = {spec.name: spec for spec in TOOLS}


def _tool_result(name: str, arguments: Mapping) -> dict:
    """One `tools/call` result: the payload plus its JSON text, flagged when it is an error."""
    structured = invoke_tool(name, arguments)
    is_error = structured.get("ok") is False
    return {"content": [{"type": "text",
                         "text": json.dumps(structured, ensure_ascii=False, sort_keys=True)}],
            "structuredContent": structured, "isError": is_error}


# ------------------------------------------------------------------------------- protocol


def _jsonrpc_error(request_id: Any, code: int, message: str) -> dict:
    """A JSON-RPC 2.0 error response; reserved for protocol failures only."""
    return {"jsonrpc": "2.0", "id": request_id, "error": {"code": code, "message": message}}


def _initialize(params: Mapping) -> dict:
    """Echo a supported requested protocol version, else reply the newest."""
    requested = params.get("protocolVersion")
    version = requested if requested in SUPPORTED_PROTOCOL_VERSIONS else LATEST_PROTOCOL_VERSION
    return {"protocolVersion": version,
            "capabilities": {"tools": {"listChanged": False}},
            "serverInfo": {"name": "task-graph", "version": _server_version()}}


def _tools_list() -> dict:
    """The frozen tool catalogue with pydantic-generated input schemas and annotations."""
    return {"tools": [
        {"name": spec.name, "description": spec.description,
         "inputSchema": spec.model.model_json_schema(mode="validation"),
         "annotations": {"readOnlyHint": spec.read_only,
                         "idempotentHint": spec.idempotent, "openWorldHint": False}}
        for spec in TOOLS]}


def _tools_call(request_id: Any, params: Mapping) -> dict:
    """One `tools/call`: unknown tool is protocol (-32602), domain failures are envelopes."""
    name = params.get("name")
    if not isinstance(name, str) or name not in TOOL_BY_NAME:
        return _jsonrpc_error(request_id, JSONRPC_INVALID_PARAMS, f"Unknown tool: {name!r}")
    arguments = params.get("arguments", {})
    if arguments is None:
        arguments = {}
    if not isinstance(arguments, dict):
        return _jsonrpc_error(request_id, JSONRPC_INVALID_REQUEST,
                              "Invalid params: `arguments` must be an object")
    return {"jsonrpc": "2.0", "id": request_id, "result": _tool_result(name, arguments)}


def _handle_message(message: Any) -> Optional[dict]:
    """Route one decoded message; None means a notification (no response is sent)."""
    request_id = message.get("id") if isinstance(message, dict) else None
    if not isinstance(message, dict) or message.get("jsonrpc") != "2.0" or \
            not isinstance(message.get("method"), str):
        return _jsonrpc_error(request_id, JSONRPC_INVALID_REQUEST, "Invalid Request")
    method = message["method"]
    if method.startswith("notifications/"):
        return None
    if "id" not in message:
        return None
    params = message.get("params")
    params = {} if params is None else params
    if not isinstance(params, dict):
        return _jsonrpc_error(request_id, JSONRPC_INVALID_REQUEST,
                              "Invalid params: expected an object")
    if method == "initialize":
        result = _initialize(params)
    elif method == "ping":
        result = {}
    elif method == "tools/list":
        result = _tools_list()
    elif method == "tools/call":
        return _tools_call(request_id, params)
    else:
        return _jsonrpc_error(request_id, JSONRPC_METHOD_NOT_FOUND,
                              f"Method not found: {method}")
    return {"jsonrpc": "2.0", "id": request_id, "result": result}


def _handle_line(line: str) -> Optional[dict]:
    """One raw line → one response (or None); nothing raises out of here."""
    try:
        message = json.loads(line)
    except ValueError as exc:
        _log(f"parse error: {exc}")
        return _jsonrpc_error(None, JSONRPC_PARSE_ERROR, "Parse error")
    try:
        return _handle_message(message)
    except Exception as exc:  # pylint: disable=broad-except
        request_id = message.get("id") if isinstance(message, dict) else None
        _log(f"internal error: {exc!r}")
        return _jsonrpc_error(request_id, JSONRPC_INTERNAL_ERROR,
                              f"Internal error: {type(exc).__name__}")


def serve(stdin=None, stdout=None) -> int:
    """The NDJSON loop: one JSON object per line in, one response per request out.

    Input is read as bytes where the stream offers them (``sys.stdin.buffer``): a line of
    invalid UTF-8 decodes with replacement and answers a JSON-RPC parse error, instead of
    killing the loop with the ``UnicodeDecodeError`` a text read would raise.
    """
    source = (getattr(sys.stdin, "buffer", sys.stdin) if stdin is None else stdin)
    sink = sys.stdout if stdout is None else stdout
    _log(f"ready ({_server_version()})")
    for raw in source:
        line = raw.decode("utf-8", errors="replace") if isinstance(raw, bytes) else raw
        line = line.strip()
        if not line:
            continue
        response = _handle_line(line)
        if response is not None:
            sink.write(json.dumps(response, ensure_ascii=False) + "\n")
            sink.flush()
    return 0


def main(argv=None) -> int:
    """`--check` validates the artifact; no arguments runs the stdio server."""
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--check", action="store_true",
                        help="validate the checked-in schema against the model and exit")
    parser.add_argument("--root", help="tree holding the checked-in schema (default: detected)")
    args = parser.parse_args(argv)
    if args.check:
        return check_schema(Path(args.root).resolve() if args.root else _default_root())
    return serve()


if __name__ == "__main__":
    sys.exit(main())
