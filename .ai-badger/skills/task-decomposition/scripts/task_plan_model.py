"""Typed task-plan domain model: pydantic schema, invariants V1–V12, content hash (DR5/DR6).

The checked-in artifact `schemas/task-plan.schema.json` (emitted by `tooling/task_plan_schema.py`)
is a projection of these models. JSON Schema can express required fields, closed enums, id
patterns, `minProperties` and `additionalProperties: false`, but it cannot express a dependency
graph: a `workflow.steps` key equalling its step's `id` (V2), every `depends_on` naming a known
step (V4), and acyclicity (V5) are pydantic-only and therefore enforced on every load, not only
by the artifact. Timestamps UTC-only (V10) and completion consistency (V11/V12) are likewise
runtime rules.

`content_hash` is the DR5 idempotency normal form: SHA-256 over canonical JSON of the authored
content only, excluding `revision`, `created_at`, `updated_at`, `$schema` and all runtime state.
"""
from __future__ import annotations

import hashlib
import json
from datetime import datetime, timedelta
from enum import Enum
from graphlib import CycleError, TopologicalSorter
from typing import Annotated, Callable, Dict, List, Literal, NamedTuple, Optional

from pydantic import AfterValidator, BaseModel, ConfigDict, Field, model_validator

SUPPORTED_SCHEMA_VERSION = 1
"""The only `schema_version` this build can load; unsupported rows fail closed."""

PLAN_UPGRADES: Dict[int, Callable[[dict], dict]] = {}
"""Upgrade hooks keyed by the version they upgrade FROM: `v -> f(payload) -> payload'`."""

STEP_ID_PATTERN = r"^[a-z0-9][a-z0-9._-]*$"
TASK_ID_PATTERN = r"^[a-z0-9][a-z0-9-]*$"

_FORBID = ConfigDict(extra="forbid", populate_by_name=True)


class Loop(str, Enum):
    """Task loop the plan belongs to (distinct from per-step effort)."""

    LOW = "low"
    HIGH = "high"


class Effort(str, Enum):
    """Step execution effort."""

    LOW = "low"
    MEDIUM = "medium"
    HIGH = "high"


class Level(str, Enum):
    """Delegation tier the step asks for (composition with the delegation precedence)."""

    LOW = "low"
    MEDIUM = "medium"
    HIGH = "high"


class StepStatus(str, Enum):
    """Step lifecycle state (transitions are DR7's guard matrix, not the model's)."""

    PENDING = "pending"
    IN_PROGRESS = "in_progress"
    COMPLETE = "complete"
    FAILED = "failed"
    SKIPPED = "skipped"


class CriterionStatus(str, Enum):
    """Acceptance-criterion verdict; the single source of AC state."""

    UNCHECKED = "unchecked"
    PASSED = "passed"
    FAILED = "failed"


class EvidenceKind(str, Enum):
    """What kind of record a piece of evidence is."""

    COMMAND = "command"
    TEST = "test"
    ARTIFACT = "artifact"
    REVIEW = "review"
    NOTE = "note"


def _parse_utc(value: str) -> datetime:
    """Parse an ISO-8601 timestamp, requiring an explicit UTC offset (V10)."""
    text = value[:-1] + "+00:00" if value.endswith(("Z", "z")) else value
    try:
        parsed = datetime.fromisoformat(text)
    except ValueError as exc:
        raise ValueError(f"{value!r} is not an ISO-8601 timestamp: {exc}") from exc
    if parsed.tzinfo is None or parsed.utcoffset() != timedelta(0):
        raise ValueError(f"{value!r} is not UTC; use a +00:00 or Z offset (V10)")
    return parsed


def _validate_utc(value: str) -> str:
    """AfterValidator for `UtcTimestamp`: reject non-UTC or unparseable strings."""
    _parse_utc(value)
    return value


UtcTimestamp = Annotated[str, AfterValidator(_validate_utc)]


class Evidence(BaseModel):
    """One recorded proof attached to a criterion or a completion."""

    model_config = _FORBID

    kind: EvidenceKind
    summary: str
    ref: Optional[str] = None
    recorded_at: UtcTimestamp


class Completion(BaseModel):
    """How a step came to rest: who closed it, and the note when force was used."""

    model_config = _FORBID

    completed_by: Optional[str] = None
    forced: bool
    note: Optional[str] = None
    evidence: List[Evidence]


class AcceptanceCriterion(BaseModel):
    """One checkable requirement on a step; `status` and `evidence` are its only state."""

    model_config = _FORBID

    id: str
    statement: str
    check: Optional[str] = None
    status: CriterionStatus
    evidence: List[Evidence]


class Step(BaseModel):
    """One unit of work in the plan graph."""

    model_config = _FORBID

    id: str = Field(pattern=STEP_ID_PATTERN)
    goal: str
    instructions: str
    effort: Effort
    level: Optional[Level] = None
    model: Optional[str] = None
    persona: Optional[str] = None
    depends_on: List[str]
    acceptance_criteria: List[AcceptanceCriterion]
    files: List[str]
    resources: List[str]
    status: StepStatus
    started_at: Optional[UtcTimestamp] = None
    completed_at: Optional[UtcTimestamp] = None
    completion: Optional[Completion] = None

    @model_validator(mode="after")
    def _unique_criteria(self) -> "Step":
        seen: Dict[str, bool] = {}
        for criterion in self.acceptance_criteria:
            if criterion.id in seen:
                raise ValueError(
                    f"step {self.id!r} has duplicate acceptance-criterion id "
                    f"{criterion.id!r} (V6)")
            seen[criterion.id] = True
        return self

    @model_validator(mode="after")
    def _completion_consistency(self) -> "Step":
        if self.status is StepStatus.IN_PROGRESS and self.started_at is None:
            raise ValueError(
                f"step {self.id!r} is in_progress but started_at is missing (V11)")
        if self.status is StepStatus.COMPLETE:
            if self.completed_at is None:
                raise ValueError(
                    f"step {self.id!r} is complete but completed_at is missing (V11)")
            unpassed = [criterion.id for criterion in self.acceptance_criteria
                        if criterion.status is not CriterionStatus.PASSED]
            if unpassed:
                if self.completion is None or not self.completion.forced:
                    raise ValueError(
                        f"step {self.id!r} is complete with un-passed acceptance criteria "
                        f"{unpassed}; set completion.forced=true with a note (V11)")
                if not (self.completion.note or "").strip():
                    raise ValueError(
                        f"step {self.id!r} forces completion; completion.note must say "
                        f"why (V11)")
        return self

    @model_validator(mode="after")
    def _timestamps_ordered(self) -> "Step":
        if self.started_at is not None and self.completed_at is not None:
            if _parse_utc(self.completed_at) < _parse_utc(self.started_at):
                raise ValueError(
                    f"step {self.id!r} completed_at precedes started_at (V12)")
        return self


def _find_cycle(steps: Dict[str, Step]) -> List[str]:
    """One dependency cycle as the node path `a -> b -> a`, or [] when none is reachable."""
    edges = {step_id: sorted(step.depends_on) for step_id, step in steps.items()}
    state: Dict[str, int] = {}
    path: List[str] = []

    def visit(node: str) -> List[str]:
        state[node] = 1
        path.append(node)
        for child in edges.get(node, ()):
            if state.get(child, 0) == 1:
                return path[path.index(child):] + [child]
            if state.get(child, 0) == 0:
                found = visit(child)
                if found:
                    return found
        path.pop()
        state[node] = 2
        return []

    for node in sorted(edges):
        if state.get(node, 0) == 0:
            found = visit(node)
            if found:
                return found
    return []


class Workflow(BaseModel):
    """The non-empty step map that is also a DAG."""

    model_config = _FORBID

    steps: Annotated[Dict[str, Step], Field(min_length=1)]

    @model_validator(mode="after")
    def _keys_match_ids(self) -> "Workflow":
        mismatched = sorted(key for key, step in self.steps.items() if key != step.id)
        if mismatched:
            raise ValueError(
                f"workflow.steps key must equal step.id (V2); mismatched keys: {mismatched}")
        return self

    @model_validator(mode="after")
    def _dependencies_resolve(self) -> "Workflow":
        for step_id, step in self.steps.items():
            for dependency in step.depends_on:
                if dependency == step_id:
                    raise ValueError(
                        f"step {step_id!r} lists itself in depends_on (V4)")
                if dependency not in self.steps:
                    raise ValueError(
                        f"step {step_id!r} depends on unknown step {dependency!r} (V4)")
        return self

    @model_validator(mode="after")
    def _acyclic(self) -> "Workflow":
        edges = {step_id: set(step.depends_on) for step_id, step in self.steps.items()}
        try:
            TopologicalSorter(edges).prepare()
        except CycleError as exc:
            cycle = _find_cycle(self.steps)
            chain = " -> ".join(cycle) if cycle else "unresolved"
            raise ValueError(f"step dependencies contain a cycle: {chain} (V5)") from exc
        return self


class TaskPlan(BaseModel):
    """The persisted task plan: one immutable-identity authored document plus runtime state."""

    model_config = _FORBID

    schema_version: Literal[SUPPORTED_SCHEMA_VERSION]
    task_id: str = Field(pattern=TASK_ID_PATTERN)
    task_description_ref: str
    research_ref: Optional[str] = None
    task_context: str
    loop: Loop
    source_refs: List[str]
    schema_: Optional[str] = Field(default=None, alias="$schema")
    workflow: Workflow
    revision: int = Field(ge=0)
    created_at: UtcTimestamp
    updated_at: UtcTimestamp


class PlanFinding(NamedTuple):
    """A non-fatal plan-quality observation, never a validation error (V6)."""

    kind: str
    step_id: str
    message: str


def plan_quality_findings(plan: TaskPlan) -> List[PlanFinding]:
    """Plan-quality observations: `step_without_acs` for every AC-less step, id-sorted (V6)."""
    return [
        PlanFinding("step_without_acs", step_id,
                    f"step {step_id!r} has no acceptance criteria")
        for step_id in sorted(plan.workflow.steps)
        if not plan.workflow.steps[step_id].acceptance_criteria
    ]


def authored_content(plan: TaskPlan) -> dict:
    """The author-owned projection of DR5: steps id-sorted so map insertion order is not hashed."""
    return {
        "task_id": plan.task_id,
        "task_description_ref": plan.task_description_ref,
        "research_ref": plan.research_ref,
        "task_context": plan.task_context,
        "loop": plan.loop.value,
        "source_refs": list(plan.source_refs),
        "steps": [
            {
                "id": step.id,
                "goal": step.goal,
                "instructions": step.instructions,
                "effort": step.effort.value,
                "level": step.level.value if step.level is not None else None,
                "model": step.model,
                "persona": step.persona,
                "depends_on": list(step.depends_on),
                "files": list(step.files),
                "resources": list(step.resources),
                "acceptance_criteria": [
                    {"id": criterion.id, "statement": criterion.statement,
                     "check": criterion.check}
                    for criterion in step.acceptance_criteria
                ],
            }
            for step in (plan.workflow.steps[step_id]
                         for step_id in sorted(plan.workflow.steps))
        ],
    }


def content_hash(plan: TaskPlan) -> str:
    """SHA-256 over the canonical JSON of the authored content only (DR5)."""
    canonical = json.dumps(authored_content(plan), sort_keys=True, separators=(",", ":"))
    return hashlib.sha256(canonical.encode("utf-8")).hexdigest()
