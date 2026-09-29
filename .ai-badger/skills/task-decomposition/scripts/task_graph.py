"""Graph operations over a `TaskPlan`: ready sets, deferral waves, guards, checklist.

Pure functions over the S2 model (`task_plan_model.py`): no persistence, no subprocess, no
network. Every `AcceptanceCriterion.check` string stays data — it is written down in the plan
and never executed here; the dispatch layer owns gate execution.

Transitions are DR7's five-state machine: `pending -> in_progress -> {complete, failed,
skipped}`, `failed -> in_progress` (retry), `complete`/`skipped` terminal. Refusals raise
`TransitionRefused` with a stable `code` and structured `details` for the transport to map.

Waves are DR7's deferral model, derived on every call and never stored: greedy packing in
(topological, id) order over the remaining steps, where a `files` or `resources` intersection
defers the later member to a later wave.

The `completion` slot is the only record v1 keeps: a forced note or a failure reason stays
readable until the step advances and the next move overwrites it (no event log).
"""
from __future__ import annotations

import heapq
import importlib.util
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Dict, List, NamedTuple, Optional, Set, Tuple

# ------------------------------------------------------------------- the sibling S2 model

_SCRIPT_DIR = Path(__file__).resolve().parent
_MODEL_PATH = _SCRIPT_DIR / "task_plan_model.py"


def _load_model():
    """Import the sibling `task_plan_model.py` once per process, under its bare name.

    The bare `sys.modules` key lets a later `import task_plan_model` from the store or the
    transport reuse this exact module object instead of loading a second copy of the model
    classes.
    """
    cached = sys.modules.get("task_plan_model")
    if cached is not None and Path(getattr(cached, "__file__", "")).resolve() == _MODEL_PATH:
        return cached
    spec = importlib.util.spec_from_file_location("task_plan_model", _MODEL_PATH)
    if spec is None or spec.loader is None:  # pragma: no cover - the model ships beside us
        raise ImportError(f"cannot load the task-plan model at {_MODEL_PATH}")
    module = importlib.util.module_from_spec(spec)
    sys.modules["task_plan_model"] = module
    try:
        spec.loader.exec_module(module)
    except Exception:
        sys.modules.pop("task_plan_model", None)
        raise
    return module


task_plan_model = _load_model()
Step = task_plan_model.Step
TaskPlan = task_plan_model.TaskPlan
StepStatus = task_plan_model.StepStatus
Completion = task_plan_model.Completion
PlanFinding = task_plan_model.PlanFinding

# ------------------------------------------------------------------- frozen semantics (DR7)

SATISFIED_DEPS = frozenset({"complete", "skipped"})
"""Dependency verdicts that satisfy a dependent's readiness (a skipped dep still counts)."""

REMAINING_STATUSES = frozenset({"pending", "in_progress", "failed"})
"""Statuses that still need dispatch; waves cover exactly these."""

FAILED_OR_SKIPPED = frozenset({"failed", "skipped"})
"""Ancestor verdicts that make a remaining descendant `blocked`."""

TRANSITIONS: Dict[str, Tuple[str, ...]] = {
    "pending": ("in_progress", "skipped"),
    "in_progress": ("complete", "failed", "skipped"),
    "failed": ("in_progress", "skipped"),
    "complete": (),
    "skipped": (),
}
"""The five-state machine's allowed moves, keyed and valued by status value.

DR7 amendment (wave-1 join ruling): `skip` is reachable from every non-terminal state —
retiring a never-started step must not force a `start` first (P2-B1's `step_skip` row).
"""

MARKERS: Dict[str, str] = {
    "complete": "x",
    "in_progress": "~",
    "failed": "!",
    "skipped": "-",
    "pending": " ",
}
"""`progress_checklist` glyphs, one per status."""

INTEGRATION_FINDING_KIND = "integration_missing"
"""Plan-quality finding kind for a multi-sink workflow with no join step."""

ALLOWED_TOOLS: Dict[str, Tuple[str, ...]] = {
    "pending": ("step_start", "step_skip"),
    "failed": ("step_start", "step_skip"),
    "in_progress": ("step_complete", "step_fail", "step_skip"),
    "complete": (),
    "skipped": (),
}
"""For each status, the tool names that may move the step out of it: the wire's
``allowed_transitions``. Terminal states allow nothing."""


class TransitionRefused(Exception):
    """A guard refusal: `code` is stable for transport mapping, `details` is structured."""

    def __init__(self, code: str, message: str, details: Optional[Dict] = None):
        super().__init__(message)
        self.code = code
        self.details = dict(details or {})


class ReadyStep(NamedTuple):
    """One dispatchable step, with the skipped dependencies and blocking ancestors it has."""

    step_id: str
    status: str
    skipped_deps: List[str]
    blocked_by: List[str]


class BlockedStep(NamedTuple):
    """One remaining step with at least one failed or skipped ancestor."""

    step_id: str
    blocked_by: List[str]


# ----------------------------------------------------------------------------- traversal


def _dependents(plan) -> Dict[str, List[str]]:
    """`step_id -> its direct dependents`, one entry each, keys in workflow order."""
    dependents: Dict[str, List[str]] = {step_id: [] for step_id in plan.workflow.steps}
    for step_id, step in plan.workflow.steps.items():
        for dependency in dict.fromkeys(step.depends_on):
            dependents[dependency].append(step_id)
    return dependents


def topological_order(plan) -> List[str]:
    """Step ids in dependency order; ties broken by id, so the order is deterministic."""
    steps = plan.workflow.steps
    indegree = {step_id: len(set(step.depends_on)) for step_id, step in steps.items()}
    dependents = _dependents(plan)
    heap = [step_id for step_id, degree in indegree.items() if degree == 0]
    heapq.heapify(heap)
    order: List[str] = []
    while heap:
        step_id = heapq.heappop(heap)
        order.append(step_id)
        for child in sorted(dependents[step_id]):
            indegree[child] -= 1
            if indegree[child] == 0:
                heapq.heappush(heap, child)
    return order


def _ancestor_map(plan) -> Dict[str, Set[str]]:
    """Transitive `depends_on` closure per step, memoized over one topological walk."""
    steps = plan.workflow.steps
    memo: Dict[str, Set[str]] = {}

    def visit(step_id: str) -> Set[str]:
        known = memo.get(step_id)
        if known is not None:
            return known
        found: Set[str] = set()
        for dependency in steps[step_id].depends_on:
            found.add(dependency)
            found |= visit(dependency)
        memo[step_id] = found
        return found

    for step_id in topological_order(plan):
        visit(step_id)
    return memo


def ancestors(plan, step_id: str) -> List[str]:
    """Transitive dependencies of one step, in topological order; `[]` for a root."""
    _require_step(plan, step_id)
    order = topological_order(plan)
    closure = _ancestor_map(plan)[step_id]
    return [candidate for candidate in order if candidate in closure]


def _require_step(plan, step_id: str) -> Step:
    step = plan.workflow.steps.get(step_id)
    if step is None:
        raise TransitionRefused(
            "not-found", f"plan {plan.task_id!r} has no step {step_id!r}",
            {"task_id": plan.task_id, "step_id": step_id})
    return step


def _require_status(step, allowed: Set[str]) -> None:
    if step.status.value not in allowed:
        status = step.status.value
        raise TransitionRefused(
            "invalid-transition",
            f"step {step.id!r} is {status!r}; allowed tools: "
            f"{list(ALLOWED_TOOLS[status]) or 'none (terminal)'}",
            {"step_id": step.id, "status": status,
             "allowed_transitions": list(ALLOWED_TOOLS[status])})


def _waiting_on(plan, step) -> List[str]:
    """Direct dependencies still incomplete, declaration order, duplicates removed."""
    return [dependency for dependency in dict.fromkeys(step.depends_on)
            if plan.workflow.steps[dependency].status.value not in SATISFIED_DEPS]


def _now_utc(now: Optional[datetime]) -> str:
    moment = now if now is not None else datetime.now(timezone.utc)
    if moment.tzinfo is None or moment.utcoffset() != timedelta(0):
        raise ValueError(f"{moment!r} is not UTC; pass an aware UTC datetime")
    return moment.isoformat(timespec="seconds").replace("+00:00", "Z")


# ------------------------------------------------------------------------------ ready set


def ready(plan) -> List[ReadyStep]:
    """Pending/failed steps whose every dependency is complete or skipped, topo-ordered.

    A skipped dependency satisfies readiness by policy (DR7), so it is surfaced separately
    in `skipped_deps`; `blocked_by` names failed/skipped ancestors the dispatcher may still
    want to know about.
    """
    steps = plan.workflow.steps
    order = topological_order(plan)
    closure = _ancestor_map(plan)
    result: List[ReadyStep] = []
    for step_id in order:
        step = steps[step_id]
        status = step.status.value
        if status not in ("pending", "failed"):
            continue
        if _waiting_on(plan, step):
            continue
        skipped_deps = [dependency for dependency in dict.fromkeys(step.depends_on)
                        if steps[dependency].status.value == "skipped"]
        blocked_by = [ancestor for ancestor in order if ancestor in closure[step_id]
                      and steps[ancestor].status.value in FAILED_OR_SKIPPED]
        result.append(ReadyStep(step_id, status, skipped_deps, blocked_by))
    return result


def blocked(plan) -> List[BlockedStep]:
    """Remaining steps with at least one failed or skipped ancestor, topo-ordered."""
    steps = plan.workflow.steps
    order = topological_order(plan)
    closure = _ancestor_map(plan)
    result: List[BlockedStep] = []
    for step_id in order:
        if steps[step_id].status.value not in REMAINING_STATUSES:
            continue
        culprits = [ancestor for ancestor in order if ancestor in closure[step_id]
                    and steps[ancestor].status.value in FAILED_OR_SKIPPED]
        if culprits:
            result.append(BlockedStep(step_id, culprits))
    return result


# --------------------------------------------------------------------- waves (deferral)


def _conflicts(left, right) -> Optional[str]:
    """The shared-resource reason two steps may not share a wave, or None."""
    if set(left.files) & set(right.files):
        return "file"
    if set(left.resources) & set(right.resources):
        return "resource"
    return None


def waves(plan) -> List[List[str]]:
    """DR7's deferral waves over every remaining step, recomputed on each call.

    Greedy packing in (topological, id) order: each step lands in the earliest wave whose
    dependencies all sit in earlier waves and no already-packed member shares a file or a
    resource with it. A conflict defers the later member to a later wave; `wave[0]` is the
    ready prefix admitted by that order and rule.
    """
    steps = plan.workflow.steps
    packed: List[List[str]] = []
    position: Dict[str, int] = {}
    for step_id in topological_order(plan):
        step = steps[step_id]
        if step.status.value not in REMAINING_STATUSES:
            continue
        dependencies = dict.fromkeys(step.depends_on)
        earliest = max((position[dependency] + 1 for dependency in dependencies
                        if dependency in position), default=0)
        slot = earliest
        while slot < len(packed) and any(
                _conflicts(step, steps[other]) is not None for other in packed[slot]):
            slot += 1
        if slot == len(packed):
            packed.append([])
        packed[slot].append(step_id)
        position[step_id] = slot
    return packed


# ----------------------------------------------------------------------------- guards


def start(plan, step_id: str, *, now: Optional[datetime] = None) -> Step:
    """Advance a pending or failed step to in_progress; guards readiness, sets `started_at`."""
    step = _require_step(plan, step_id)
    _require_status(step, {"pending", "failed"})
    waiting = _waiting_on(plan, step)
    if waiting:
        raise TransitionRefused(
            "dependencies-incomplete",
            f"step {step_id!r} waits on incomplete dependencies {waiting}",
            {"step_id": step_id, "waiting_on": waiting})
    return step.model_copy(update={
        "status": StepStatus.IN_PROGRESS,
        "started_at": _now_utc(now),
        "completed_at": None,
        "completion": None,
    })


def complete(plan, step_id: str, *, now: Optional[datetime] = None, force: bool = False,
             note: Optional[str] = None) -> Step:
    """Advance an in_progress step to complete, guarded by dependencies and criteria.

    Every acceptance criterion must have passed. With `force=True` and a non-empty `note`
    the criteria guard is bypassed instead: the refusal-free result records
    `completion.forced` plus the note and leaves every AC status exactly as it was.
    """
    step = _require_step(plan, step_id)
    if step.status.value == "complete":
        raise TransitionRefused(
            "already_complete", f"step {step_id!r} is already complete",
            {"step_id": step_id, "status": "complete"})
    _require_status(step, {"in_progress"})
    waiting = _waiting_on(plan, step)
    if waiting:
        raise TransitionRefused(
            "dependencies-incomplete",
            f"step {step_id!r} waits on incomplete dependencies {waiting}",
            {"step_id": step_id, "waiting_on": waiting})
    unresolved = [criterion.id for criterion in step.acceptance_criteria
                  if criterion.status.value == "unchecked"]
    failed = [criterion.id for criterion in step.acceptance_criteria
              if criterion.status.value == "failed"]
    if (unresolved or failed) and not (force and (note or "").strip()):
        raise TransitionRefused(
            "criteria-unmet",
            f"step {step_id!r} has unresolved criteria {unresolved} and failed criteria "
            f"{failed}; force=true with a non-empty note bypasses this guard",
            {"step_id": step_id, "unresolved": unresolved, "failed": failed})
    completion = Completion(completed_by=None, forced=bool(force),
                            note=note if force else None, evidence=[])
    return step.model_copy(update={
        "status": StepStatus.COMPLETE,
        "completed_at": _now_utc(now),
        "completion": completion,
    })


def fail(plan, step_id: str) -> Step:
    """Advance an in_progress step to failed; a later `start` is a retry."""
    step = _require_step(plan, step_id)
    _require_status(step, {"in_progress"})
    return step.model_copy(update={"status": StepStatus.FAILED})


def skip(plan, step_id: str) -> Step:
    """Advance a non-terminal step to skipped (terminal) — the retired-step path.

    DR7 amendment (wave-1 join ruling): reachable from pending/in_progress/failed; a
    pending step is retired directly instead of being started just to be skipped.
    """
    step = _require_step(plan, step_id)
    _require_status(step, {"pending", "in_progress", "failed"})
    return step.model_copy(update={"status": StepStatus.SKIPPED})


# ------------------------------------------------------------------------ integration sink


def _sinks(plan) -> List[str]:
    """Steps nothing depends on (the dependency DAG's maximal elements), topo-ordered."""
    dependents = _dependents(plan)
    return [step_id for step_id in topological_order(plan) if not dependents[step_id]]


def integration_sink(plan) -> Optional[str]:
    """The step whose ancestor set is every other step (DR8's join), or None."""
    steps = plan.workflow.steps
    order = topological_order(plan)
    closure = _ancestor_map(plan)
    for step_id in order:
        if closure[step_id] == set(steps) - {step_id}:
            return step_id
    return None


def integration_ok(plan) -> bool:
    """True unless the workflow has more than one sink and no join step spanning them."""
    if len(_sinks(plan)) <= 1:
        return True
    return integration_sink(plan) is not None


def integration_finding(plan) -> Optional[PlanFinding]:
    """The `integration_missing` finding for a multi-sink workflow with no join, else None."""
    if integration_ok(plan):
        return None
    sinks = _sinks(plan)
    return PlanFinding(
        INTEGRATION_FINDING_KIND, sinks[0],
        f"workflow has {len(sinks)} sinks {sinks} and no join step depending on every "
        f"other sink")


def plan_quality_findings(plan) -> List[PlanFinding]:
    """The model's findings (`step_without_acs`) plus this module's integration finding."""
    findings = list(task_plan_model.plan_quality_findings(plan))
    finding = integration_finding(plan)
    if finding is not None:
        findings.append(finding)
    return findings


# ------------------------------------------------------------------------------ progress


def progress_checklist_data(plan) -> dict:
    """The machine-readable checklist: topological order, glyphs, AC tallies, header counts.

    A forced completion shows up through its tally (`acs_passed < acs_total`) rather than a
    glyph of its own, so the marker column stays a pure status projection.
    """
    steps = plan.workflow.steps
    complete_count = 0
    rows: List[dict] = []
    for step_id in topological_order(plan):
        step = steps[step_id]
        status = step.status.value
        if status == "complete":
            complete_count += 1
        rows.append({
            "id": step_id,
            "goal": step.goal,
            "status": status,
            "marker": MARKERS[status],
            "acs_passed": sum(1 for criterion in step.acceptance_criteria
                              if criterion.status.value == "passed"),
            "acs_total": len(step.acceptance_criteria),
        })
    return {"task_id": plan.task_id, "revision": plan.revision,
            "complete": complete_count, "total": len(steps), "steps": rows}


__all__ = [
    "ALLOWED_TOOLS",
    "BlockedStep",
    "Completion",
    "FAILED_OR_SKIPPED",
    "INTEGRATION_FINDING_KIND",
    "MARKERS",
    "PlanFinding",
    "REMAINING_STATUSES",
    "ReadyStep",
    "SATISFIED_DEPS",
    "Step",
    "StepStatus",
    "TRANSITIONS",
    "TaskPlan",
    "TransitionRefused",
    "ancestors",
    "blocked",
    "complete",
    "fail",
    "integration_finding",
    "integration_ok",
    "integration_sink",
    "plan_quality_findings",
    "progress_checklist_data",
    "ready",
    "skip",
    "start",
    "task_plan_model",
    "topological_order",
    "waves",
]
