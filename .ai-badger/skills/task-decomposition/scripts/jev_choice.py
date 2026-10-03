#!/usr/bin/env python3
# /// script
# requires-python = ">=3.10"
# ///
"""Jev advisory for the task-decomposition skill: prompts, caps, fail-safe gates.

Advisory only — the MCP server is offline by contract (plan DR9); this module post-processes
`steps_ready` output. Every flag is default-off and a disabled or failed call returns no
proposal (tier) or the safe `serialize` direction (waves).

Parser semantics are ported from pi's `decision-router-client.ts` (`clampProbability` /
`parseAnswer` / `parseJevResponseBody`). Nothing in the parser or the classifier raises: bodies
become `malformed`, answers become per-question rejects, transport failures become `None`.
The one network surface is the vendored `openrouter_client.py` beside this file. A loopback
`AI_BADGER_JEV_ENDPOINT` (a local decider) always works; OpenRouter or any other remote host
needs https and `AI_BADGER_ALLOW_THIRD_PARTY=1`, and a `dataPolicy`-locked project admits only the
hosts its `allowHosts` lists — the client's `egress_allowed` decides. A custom endpoint
gets only `AI_BADGER_JEV_ENDPOINT_KEY`, never the OpenRouter key; keys appear only in the
Authorization header, never in a log.
"""
from __future__ import annotations

import argparse
import functools
import importlib.util
import json
import math
import os
import sys
import time
from pathlib import Path
from typing import Any, Callable, Dict, List, Mapping, NamedTuple, Optional, Tuple

# ------------------------------------------------------------------ vendored client

_CLIENT_KEY = "ai_badger_task_decomposition__openrouter_client"


def _load_client() -> Any:
    """The sibling `openrouter_client.py` under its own module key."""
    cached = sys.modules.get(_CLIENT_KEY)
    if cached is not None:
        return cached
    path = Path(__file__).resolve().parent / "openrouter_client.py"
    spec = importlib.util.spec_from_file_location(_CLIENT_KEY, path)
    if spec is None or spec.loader is None:  # pragma: no cover - the file is vendored
        raise ImportError(f"cannot load the vendored client at {path}")
    module = importlib.util.module_from_spec(spec)
    sys.modules[_CLIENT_KEY] = module
    try:
        spec.loader.exec_module(module)
    except Exception:
        sys.modules.pop(_CLIENT_KEY, None)
        raise
    return module


client = _load_client()

# ------------------------------------------------------------------ frozen constants (DR9)

MODEL_DEFAULT = "typesafe/jev-1.13"
ENDPOINT_DEFAULT = "https://openrouter.ai/api/alpha/decisions"
DECISIONS_PATH = "/api/alpha/decisions"

MASTER_ENV = "AI_BADGER_JEV"
TIER_ENV = "AI_BADGER_JEV_TIER"
WAVES_ENV = "AI_BADGER_JEV_WAVES"
MODEL_ENV = "AI_BADGER_JEV_MODEL"
ENDPOINT_ENV = "AI_BADGER_JEV_ENDPOINT"
ENDPOINT_KEY_ENV = "AI_BADGER_JEV_ENDPOINT_KEY"
TIMEOUT_ENV = "AI_BADGER_JEV_TIMEOUT_MS"
TEST_BASE_ENV = client.TEST_BASE_ENV

PAIR_CAP = 10
STATE_CHAR_CAP = 32000
INSTRUCTION_CHAR_CAP = 1000
ATTEMPTS = 2
ATTEMPT_SECONDS = 8.0
DEFAULT_TIMEOUT_MS = int(ATTEMPT_SECONDS * 1000)

TIER_UPGRADE_GATE = 0.6
WAVE_SHARE_GATE = 0.7
SHARE_WAVE = "share-wave"
SERIALIZE = "serialize"

DETAIL_CAP = 120

TIER_INSTRUCTIONS = ("Which model tier is sufficient to implement this step as specified? "
                     "Judge the derivation the step demands, not its size.")
TIER_CRITERIA = {
    "low": "Mechanical, single-file or rename-level change",
    "medium": "Multi-file change needing judgement",
    "high": "Design, debugging, architecture",
}
WAVE_INSTRUCTIONS = ("Both steps are ready (their dependencies are done). May they run as "
                     "concurrent lanes in this wave, each in its own worktree?")
WAVE_CRITERIA = {
    "share-wave": ("No shared writable external resource: no shared port, service, database, "
                   "lockfile, generated artifact outside the tree, or ordered output contract "
                   "between the two — concurrency cannot invalidate either lane's green run"),
    "serialize": ("Any shared writable external resource, ordered output contract, or race "
                  "between the two — run them in separate waves, dependencies first"),
}

ERROR_KINDS = ("misrouted-refusal", "auth", "billing", "rate-limited", "server",
               "transport-timeout", "malformed", "missing-key")
RETRYABLE_KINDS = ("server", "transport-timeout", "malformed", "rate-limited")
_STATUS_KINDS = {400: "misrouted-refusal", 401: "auth", 402: "billing", 429: "rate-limited"}


# --------------------------------------------------------------------- budget

# Ported from memory_context.py (`Budget`, ~20 stdlib-only lines) rather than vendoring that
# whole module: it is a ~700-line prompt hook whose proxy session and sibling loading are
# irrelevant here (DR9 owns this deliberate deviation from full-module vendoring).
class Budget:
    """One wall-clock deadline; `child(s)` never outlives its parent."""

    def __init__(self, seconds: float, clock: Callable[[], float] = time.monotonic,
                 *, deadline: Optional[float] = None):
        self.clock = clock
        self.deadline = clock() + seconds if deadline is None else deadline

    def remaining(self) -> float:
        """Seconds left, never negative."""
        return max(0.0, self.deadline - self.clock())

    def expired(self) -> bool:
        """True once the deadline has passed."""
        return self.remaining() <= 0

    def child(self, seconds: float) -> "Budget":
        """A budget ending at the earlier of this deadline and now + *seconds*."""
        return Budget(0, self.clock, deadline=min(self.deadline, self.clock() + seconds))


# ------------------------------------------------------------------ parser types


class Usage(NamedTuple):
    """Token/cost counters from the response envelope; non-numbers become 0."""

    input_tokens: int = 0
    output_tokens: int = 0
    cost: float = 0.0


class ChoiceAnswer(NamedTuple):
    """A validated choice answer: winner, clamped probabilities and clamped confidence."""

    type: str
    choice: str
    probabilities: Dict[str, float]
    confidence: float


class AnswerParse(NamedTuple):
    """One question's parse: `ok` with a `ChoiceAnswer`, or a reject reason and capped detail."""

    status: str
    answer: Optional[ChoiceAnswer] = None
    reason: Optional[str] = None
    detail: str = ""


class BodyParse(NamedTuple):
    """A whole response body: `ok` with per-question parses, or a body-level reject."""

    status: str
    answers: Dict[str, AnswerParse]
    reason: Optional[str] = None
    detail: str = ""
    model: str = ""
    id: str = ""
    provider: str = ""
    usage: Usage = Usage()


class QuestionSpec(NamedTuple):
    """One asked question: expected answer `type` and, for choice, the known option keys."""

    type: str = "choice"
    options: Optional[Tuple[str, ...]] = None


# ------------------------------------------------------------------ parser (pi port)


def _refuse_constant(name: str) -> Any:
    raise ValueError(f"{name} is not JSON")


_STRICT = json.JSONDecoder(parse_constant=_refuse_constant)


def _loads(raw: Any) -> Any:
    """Strict JSON (no NaN/Infinity, like JS `JSON.parse`) from bytes or text; raises ValueError."""
    text = raw.decode("utf-8") if isinstance(raw, (bytes, bytearray)) else raw
    if not isinstance(text, str):
        raise ValueError("not text")
    return _STRICT.decode(text)


def _is_number(value: Any) -> bool:
    return isinstance(value, (int, float)) and not isinstance(value, bool)


def _str_field(record: Mapping, key: str) -> str:
    value = record.get(key)
    return value if isinstance(value, str) else ""


def _detail(text: str) -> str:
    """Per-answer details never leak keys, headers, bodies or prompt text; capped at 120."""
    return text if len(text) <= DETAIL_CAP else text[:DETAIL_CAP]


def clamp_probability(value: Any) -> float:
    """Clamp a probability into [0, 1]; NaN and non-numbers become 0. Never raises."""
    if not _is_number(value) or math.isnan(value):
        return 0.0
    if value <= 0:
        return 0.0
    if value >= 1:
        return 1.0
    return float(value)


def parse_answer(name: str, entry: Any, question: QuestionSpec) -> AnswerParse:
    """One answer against its question spec; per-question reject, never a raise."""
    if not isinstance(entry, dict):
        return AnswerParse("reject", None, "mis-keyed",
                           _detail(f'answer "{name}" is missing or not an object'))
    if entry.get("type") != question.type:
        return AnswerParse("reject", None, "type-mismatch",
                           _detail(f'answer "{name}" is not a {question.type} answer'))
    choice = entry.get("choice")
    if not isinstance(choice, str) or choice == "":
        return AnswerParse("reject", None, "type-mismatch",
                           _detail(f'answer "{name}" choice is not a string'))
    if question.options is not None and choice not in question.options:
        return AnswerParse("reject", None, "unknown-choice",
                           _detail(f'answer "{name}" winner is outside the live catalogue'))
    winner_only = AnswerParse("ok", ChoiceAnswer("choice", choice, {choice: 1.0}, 0.0))
    probabilities = entry.get("probabilities")
    if not isinstance(probabilities, dict):
        return winner_only
    raw_winner = probabilities.get(choice)
    if not _is_number(raw_winner) or not math.isfinite(raw_winner):
        return winner_only
    clean = {str(option): clamp_probability(value) for option, value in probabilities.items()}
    confidence = entry.get("confidence")
    value = clamp_probability(confidence) if _is_number(confidence) else 0.0
    return AnswerParse("ok", ChoiceAnswer("choice", choice, clean, value))


def _usage(value: Any) -> Usage:
    record = value if isinstance(value, dict) else {}

    def number(candidate: Any) -> Any:
        return candidate if _is_number(candidate) and not math.isnan(candidate) else 0

    return Usage(number(record.get("input_tokens")), number(record.get("output_tokens")),
                 number(record.get("cost")))


def parse_response_body(text: Any, spec: Mapping[str, QuestionSpec]) -> BodyParse:
    """pi's `parseJevResponseBody`: body-level rejects plus per-question parses, never a raise."""
    try:
        raw = _loads(text)
    except (ValueError, UnicodeDecodeError, RecursionError):
        return BodyParse("reject", {}, "malformed", "response body is not valid JSON")
    if not isinstance(raw, dict):
        return BodyParse("reject", {}, "malformed", "response body is not a JSON object")
    if "error" in raw and "answers" not in raw:
        return BodyParse("reject", {}, "error-envelope",
                         "response carries an error envelope, not answers")
    answers = raw.get("answers")
    answers = answers if isinstance(answers, dict) else {}
    parsed = {name: parse_answer(name, answers.get(name), question)
              for name, question in spec.items()}
    return BodyParse("ok", parsed, model=_str_field(raw, "model"), id=_str_field(raw, "id"),
                     provider=_str_field(raw, "provider"), usage=_usage(raw.get("usage")))


# ------------------------------------------------------------------ env surface


def tier_enabled(env: Mapping[str, str]) -> bool:
    """Master and tier both the literal `"1"`; absent or any other value is off (zero network)."""
    return env.get(MASTER_ENV) == "1" and env.get(TIER_ENV) == "1"


def waves_enabled(env: Mapping[str, str]) -> bool:
    """Master and waves both the literal `"1"`; absent or any other value is off."""
    return env.get(MASTER_ENV) == "1" and env.get(WAVES_ENV) == "1"


def model(env: Mapping[str, str]) -> str:
    """`AI_BADGER_JEV_MODEL`, defaulting to the frozen wire model."""
    value = env.get(MODEL_ENV)
    return value if isinstance(value, str) and value else MODEL_DEFAULT


def timeout_seconds(env: Mapping[str, str]) -> float:
    """Per-attempt timeout from `AI_BADGER_JEV_TIMEOUT_MS`; unset/garbage/non-positive → 8 s."""
    raw = env.get(TIMEOUT_ENV)
    if raw is None:
        return ATTEMPT_SECONDS
    try:
        parsed = math.floor(float(raw))
    except (TypeError, ValueError, OverflowError):
        return ATTEMPT_SECONDS
    if not math.isfinite(parsed) or parsed <= 0:
        return ATTEMPT_SECONDS
    return parsed / 1000


Target = Tuple[str, Optional[str]]
Refusal = Tuple[str, str]


def _refused(code: str, reason: str) -> Tuple[None, Refusal]:
    return None, (code, reason)


def _custom_target(url: str, env: Mapping[str, str]) -> Tuple[Optional[Target], Optional[Refusal]]:
    """`AI_BADGER_JEV_ENDPOINT` with its own key (empty is keyless): https, or http to loopback,
    which egress then judges; plain http elsewhere is refused before the key is read, a dirty key
    resolves nothing, and the OpenRouter key is never sent there."""
    if not url.startswith(("https://", "http://")):
        return _refused("bad-endpoint", f"bad {ENDPOINT_ENV}: https, or http to loopback only")
    if url.startswith("http://") and not client.is_loopback(url):
        return _refused("plaintext", f"{ENDPOINT_ENV} is plain http to a non-loopback host: "
                                     f"use https")
    raw = env.get(ENDPOINT_KEY_ENV)
    if not raw:
        return (url, None), None
    if client._clean_key(raw) != raw:  # pylint: disable=protected-access
        return _refused("bad-endpoint-key", f"bad {ENDPOINT_KEY_ENV}: blank, spaced or non-ASCII")
    return (url, raw), None


def _resolve(env: Mapping[str, str], cwd: Optional[str]
             ) -> Tuple[Optional[Target], Optional[Refusal]]:
    """The decisions target, or the `(code, reason)` that names why none is permitted."""
    if TEST_BASE_ENV in env:
        key = client.api_key(env)
        if key is None:
            return _refused("no-key", "no OpenRouter key: OPENROUTER_API_KEY is unset or dirty")
        base = client.api_base(env, key)
        if base is None:
            return _refused("bad-test-base", f"bad {TEST_BASE_ENV}: loopback with an sk-test- key")
        target: Target = (base + DECISIONS_PATH, key)
    elif env.get(ENDPOINT_ENV):
        custom, refusal = _custom_target(env[ENDPOINT_ENV], env)
        if custom is None:
            return None, refusal
        target = custom
    else:
        key = client.api_key(env)
        if key is None:
            return _refused("no-key", f"no OpenRouter key: set OPENROUTER_API_KEY, or "
                                      f"{ENDPOINT_ENV} to a loopback decider")
        target = (ENDPOINT_DEFAULT, key)
    if not client.egress_allowed(target[0], env, cwd or "."):
        return None, _egress_refusal(target[0], env, cwd or ".")
    try:
        client.proxy_for(target[0], env)
    except ValueError as err:
        cause = getattr(err, "cause", client.MALFORMED)
        return _refused(f"proxy-{cause}", f"https_proxy/HTTPS_PROXY is {cause}: use "
                                          f"http://host:port or host:port, without credentials")
    return target, None


def _egress_refusal(url: str, env: Mapping[str, str], cwd: str) -> Refusal:
    """Why `egress_allowed` refused *url*: a malformed host, no opt-in, or the lock that denies
    its host — one with no `allowHosts`, a voided list, or a valid list that lacks the host."""
    host = client.dialled_host(url)
    if host is None:
        return "bad-endpoint", f"bad {ENDPOINT_ENV}: the host to dial is ambiguous"
    if not client.opted_in(env):
        return "not-opted-in", (f"{client.ALLOW_ENV} is not 1 (or set {ENDPOINT_ENV} "
                                f"to a loopback decider)")
    lock = client.denying_lock(url, cwd)
    if lock is None:
        return "locked", "inconsistent lock state"
    if lock.path is None:
        return "locked", f"locked by {lock.cause}"
    if lock.hosts is not None:
        return "locked", f"{host} is not in the allowHosts of {lock.path}"
    if lock.cause == client.POLICY:
        return "locked", f"locked by {lock.path} (no allowHosts)"
    return "locked", f"locked by {lock.path}: {lock.cause}"


def endpoint_target(env: Mapping[str, str], cwd: Optional[str] = None) -> Optional[Target]:
    """`(url, key)` for the decisions call, or `None`: the loopback test seam, else a custom
    endpoint with its own key, else OpenRouter; a non-loopback url needs the third-party opt-in."""
    return _resolve(env, cwd)[0]


def endpoint_refusal(env: Mapping[str, str], cwd: Optional[str] = None) -> Optional[Refusal]:
    """`(code, reason)` when no decisions endpoint is permitted, else `None`."""
    return _resolve(env, cwd)[1]


# ------------------------------------------------------------------ prompt builders (R4 §3-4)


def _ac_statements(value: Any) -> List[str]:
    """Acceptance criteria as strings, or as mappings reduced to their `statement`."""
    out: List[str] = []
    for item in value if isinstance(value, (list, tuple)) else []:
        if isinstance(item, Mapping):
            item = item.get("statement")
        if isinstance(item, str):
            out.append(item)
    return out


def tier_step(step: Mapping) -> Dict[str, Any]:
    """The wire `state.step` body (R4 §3 names) from a plan step mapping."""
    return {
        "id": str(step.get("id") or ""),
        "goal": str(step.get("goal") or ""),
        "instructions": str(step.get("instructions") or ""),
        "acceptance_criteria": _ac_statements(step.get("acceptance_criteria")),
        "declared_effort": str(step.get("effort") or step.get("declared_effort") or ""),
        "files": [str(item) for item in (step.get("files") or [])],
        "verifier": str(step.get("verifier") or ""),
    }


def wave_step(step: Mapping) -> Dict[str, Any]:
    """The wire `state.steps[]` body (R4 §4 names); `runs` carries the plan's `resources`."""
    return {
        "id": str(step.get("id") or ""),
        "goal": str(step.get("goal") or ""),
        "instructions": str(step.get("instructions") or ""),
        "files": [str(item) for item in (step.get("files") or [])],
        "runs": [str(item) for item in (step.get("runs") or step.get("resources") or [])],
    }


def pair_names(ids: List[str]) -> List[str]:
    """`<id_i>_<id_j>` for every i < j in input order; deterministic and chunk-safe."""
    return [f"{a}_{b}" for index, a in enumerate(ids) for b in ids[index + 1:]]


def _compact(state: Mapping) -> str:
    return json.dumps(state, ensure_ascii=False, separators=(",", ":"))


def _capped(step: Mapping) -> Dict[str, Any]:
    """The step with `instructions` cut to `INSTRUCTION_CHAR_CAP`; nothing else changes."""
    capped = dict(step)
    instructions = capped.get("instructions")
    capped["instructions"] = (instructions if isinstance(instructions, str)
                              else "")[:INSTRUCTION_CHAR_CAP]
    return capped


def _goals_only(step: Mapping) -> Dict[str, Any]:
    return {"id": step.get("id", ""), "goal": step.get("goal", "")}


def _fit_tier_state(state: Dict[str, Any]) -> Dict[str, Any]:
    """Full, then instructions capped at 1000, then id+goal; the step is never dropped."""
    if len(_compact(state)) <= STATE_CHAR_CAP:
        return state
    capped = {**state, "step": _capped(state["step"])}
    if len(_compact(capped)) <= STATE_CHAR_CAP:
        return capped
    return {**capped, "step": _goals_only(capped["step"])}


def _fit_wave_state(state: Dict[str, Any]) -> Dict[str, Any]:
    """Full, then instructions capped at 1000, then id+goal; steps are never dropped."""
    if len(_compact(state)) <= STATE_CHAR_CAP:
        return state
    capped = {**state, "steps": [_capped(step) for step in state["steps"]]}
    if len(_compact(capped)) <= STATE_CHAR_CAP:
        return capped
    return {**capped, "steps": [_goals_only(step) for step in capped["steps"]]}


def build_tier_request(step: Mapping, *, model_id: Optional[str] = None) -> Tuple[str, Dict]:
    """`(name, body)` for one step's tier question; name is `<id>_tier` (R4 §3)."""
    wire = _fit_tier_state({"step": tier_step(step)})
    name = f"{wire['step'].get('id', '')}_tier"
    return name, {
        "model": model_id or MODEL_DEFAULT,
        "state": wire,
        "questions": {name: {"type": "choice", "instructions": TIER_INSTRUCTIONS,
                             "criteria": dict(TIER_CRITERIA)}},
    }


def build_wave_request(ready: List[Mapping], *, done: Any = (), edges: Any = (),
                       model_id: Optional[str] = None) -> Tuple[List[str], Dict]:
    """`(names, body)` for every ready pair; names are `<id_i>_<id_j>` (R4 §4)."""
    state = _fit_wave_state({
        "steps": [wave_step(step) for step in ready],
        "done": [str(item) for item in done],
        "edges": [[str(pair[0]), str(pair[1])] for pair in edges],
    })
    names = pair_names([step.get("id", "") for step in state["steps"]])
    return names, {
        "model": model_id or MODEL_DEFAULT,
        "state": state,
        "questions": {name: {"type": "choice", "instructions": WAVE_INSTRUCTIONS,
                             "criteria": dict(WAVE_CRITERIA)} for name in names},
    }


# ------------------------------------------------------------------ fail-safe gates (DR9)


class TierProposal(NamedTuple):
    """An advisory upgrade: the tier and the classifier's confidence."""

    level: str
    confidence: float


def propose_tier(step: Mapping, answer: Optional[ChoiceAnswer]) -> Optional[TierProposal]:
    """`high` at/above the gate, unless `level`/`model` is declared; never a demotion."""
    if not isinstance(answer, ChoiceAnswer) or answer.choice != "high":
        return None
    if not isinstance(step, Mapping) or step.get("level") or step.get("model"):
        return None
    if not answer.confidence >= TIER_UPGRADE_GATE:  # negated form so NaN fails closed
        return None
    return TierProposal("high", answer.confidence)


def pair_decision(answer: Optional[ChoiceAnswer]) -> str:
    """`share-wave` only on the exact winner at/above the gate; everything else serializes."""
    if not isinstance(answer, ChoiceAnswer) or answer.choice != SHARE_WAVE:
        return SERIALIZE
    if not answer.confidence >= WAVE_SHARE_GATE:  # negated form so NaN fails closed
        return SERIALIZE
    return SHARE_WAVE


# ------------------------------------------------------------------ classify and calls


class Verdict(NamedTuple):
    """One classified reply: `ok` with answers by question name, or an `ERROR_KINDS` entry."""

    kind: str
    answers: Dict[str, Optional[ChoiceAnswer]]


def _reply(reply: Any) -> Optional[Tuple[int, Any]]:
    """`(status, body)` of a `Reply` that arrived; `None` when it carries an `error` or junk."""
    status = getattr(reply, "status", None)
    if getattr(reply, "error", None) or not isinstance(status, int) or isinstance(status, bool):
        return None
    return status, getattr(reply, "body", b"")


def _spec_from_body(body: Mapping, names: List[str]) -> Dict[str, QuestionSpec]:
    """The asked questions' specs: `criteria` keys become the known options (pi's spec)."""
    questions = body.get("questions") if isinstance(body, Mapping) else None
    questions = questions if isinstance(questions, Mapping) else {}
    spec: Dict[str, QuestionSpec] = {}
    for name in names:
        question = questions.get(name)
        question = question if isinstance(question, Mapping) else {}
        criteria = question.get("criteria")
        spec[name] = QuestionSpec(str(question.get("type") or "choice"),
                                  tuple(criteria) if isinstance(criteria, Mapping) else None)
    return spec


def classify(reply: Any, spec: Mapping[str, QuestionSpec]) -> Verdict:
    """pi's `readDecision` over a `Reply`; transport failures become `transport-timeout`."""
    parts = _reply(reply)
    if parts is None:
        return Verdict("transport-timeout", {})
    status, body = parts
    if status in _STATUS_KINDS:
        return Verdict(_STATUS_KINDS[status], {})
    if status != 200:
        return Verdict("server", {})
    parsed = parse_response_body(body, spec)
    if parsed.status != "ok":
        kind = "server" if parsed.reason == "error-envelope" else "malformed"
        return Verdict(kind, {})
    return Verdict("ok", {name: answer.answer if answer.status == "ok" else None
                          for name, answer in parsed.answers.items()})


def request_choices(body: Mapping, names: List[str], *, env: Mapping[str, str],
                    post: Optional[Callable] = None, budget: Optional[Budget] = None,
                    clock: Callable[[], float] = time.monotonic, cwd: Optional[str] = None,
                    ) -> Dict[str, Optional[ChoiceAnswer]]:
    """One decisions body, at most `ATTEMPTS` attempts, no sleep, never raises."""
    names = list(names)
    failed = {name: None for name in names}
    if not names:
        return failed
    try:
        target = endpoint_target(env, cwd)
        if target is None:
            return failed
        url, key = target
        envelope = budget if budget is not None else Budget(timeout_seconds(env) * ATTEMPTS,
                                                            clock=clock)
        transport = post if post is not None else functools.partial(
            client.post_json, env=env, cwd=cwd or ".")
        spec = _spec_from_body(body, names)
        per_attempt = timeout_seconds(env)
        for _ in range(ATTEMPTS):
            if envelope.remaining() <= 0:
                break
            verdict = classify(transport(url, body, key, envelope.child(per_attempt)), spec)
            if verdict.kind == "ok":
                return {name: verdict.answers.get(name) for name in names}
            if verdict.kind not in RETRYABLE_KINDS:
                break
    except Exception:  # pylint: disable=broad-except
        return failed
    return failed


def tier_proposals(steps: Any, *, env: Mapping[str, str], post: Optional[Callable] = None,
                   clock: Callable[[], float] = time.monotonic,
                   budget: Optional[Budget] = None,
                   cwd: Optional[str] = None) -> Dict[str, TierProposal]:
    """Advisory upgrades by question name; `{}` when off, nothing asked, or a call fails.

    One `budget` bounds every step's call when the caller threads one through; without it a
    fresh `timeout * ATTEMPTS` window is created for the whole run, never per step.
    """
    if not tier_enabled(env):
        return {}
    envelope = budget if budget is not None else Budget(timeout_seconds(env) * ATTEMPTS,
                                                       clock=clock)
    proposals: Dict[str, TierProposal] = {}
    for step in steps:
        try:
            if not isinstance(step, Mapping) or step.get("level") or step.get("model"):
                continue
            name, body = build_tier_request(step, model_id=model(env))
            answers = request_choices(body, [name], env=env, post=post, clock=clock,
                                      budget=envelope, cwd=cwd)
            proposal = propose_tier(step, answers.get(name))
            if proposal is not None:
                proposals[name] = proposal
        except Exception:  # pylint: disable=broad-except
            continue
    return proposals


def wave_hints(ready: Any, *, done: Any = (), edges: Any = (), env: Mapping[str, str],
               post: Optional[Callable] = None,
               clock: Callable[[], float] = time.monotonic,
               budget: Optional[Budget] = None, cwd: Optional[str] = None) -> Dict[str, str]:
    """Pair name -> `share-wave`/`serialize`; off returns `{}`, anything else serializes.

    One `budget` bounds every chunk's call when the caller threads one through; without it a
    fresh `timeout * ATTEMPTS` window is created for the whole run, never per chunk.
    """
    if not waves_enabled(env):
        return {}
    steps = [step for step in ready if isinstance(step, Mapping)]
    names = pair_names([str(step.get("id") or "") for step in steps])
    if not names:
        return {}
    envelope = budget if budget is not None else Budget(timeout_seconds(env) * ATTEMPTS,
                                                       clock=clock)
    decisions = {name: SERIALIZE for name in names}
    try:
        chosen = model(env)
        for start in range(0, len(steps), PAIR_CAP):
            chunk = steps[start:start + PAIR_CAP]
            chunk_names, body = build_wave_request(chunk, done=done, edges=edges,
                                                   model_id=chosen)
            answers = request_choices(body, chunk_names, env=env, post=post, clock=clock,
                                      budget=envelope, cwd=cwd)
            for name in chunk_names:
                if pair_decision(answers.get(name)) == SHARE_WAVE:
                    decisions[name] = SHARE_WAVE
        return decisions
    except Exception:  # pylint: disable=broad-except
        return {name: SERIALIZE for name in names}


# ------------------------------------------------------------------ CLI (B1)


class InputError(ValueError):
    """A plan document the CLI cannot resolve; rendered as a clean error envelope."""


def _plan_document(document: Any) -> Mapping:
    """The plan inside a plan file: the document itself, or a plan/plan_export envelope."""
    if not isinstance(document, Mapping):
        raise InputError("the plan document must be a JSON object")
    for key in ("document", "plan"):
        candidate = document.get(key)
        if isinstance(candidate, Mapping) and isinstance(candidate.get("workflow"), Mapping):
            return candidate
    if isinstance(document.get("workflow"), Mapping):
        return document
    raise InputError("the plan document has no workflow.steps")


def _plan_steps(plan: Mapping) -> List[Mapping]:
    """The plan's steps in document order; stored plans key them by id, drafts list them."""
    workflow = plan.get("workflow")
    steps = workflow.get("steps") if isinstance(workflow, Mapping) else None
    if isinstance(steps, Mapping):
        ordered = [step for step in steps.values() if isinstance(step, Mapping)]
    elif isinstance(steps, list):
        ordered = [step for step in steps if isinstance(step, Mapping)]
    else:
        ordered = []
    if not ordered:
        raise InputError("the plan document has no steps")
    return ordered


def _step_id(step: Any) -> str:
    """A step's id from a mapping or a bare id string; empty when neither."""
    if isinstance(step, Mapping):
        return str(step.get("id") or "")
    return str(step or "")


def _status(step: Mapping) -> str:
    """A step's status; a draft without one is pending."""
    return str(step.get("status") or "pending")


def _resolve_ready(document: Mapping, steps: List[Mapping]) -> List[Mapping]:
    """The ready set: the caller's `ready` hint, else the `waves` union, else derived.

    A hint entry naming a plan step resolves to the plan's full step (instructions and all);
    an id the plan does not carry is kept as given. Derived readiness is pending/failed with
    every dependency complete or skipped — the rule `steps_ready` applies.
    """
    by_id = {_step_id(step): step for step in steps}
    hint = document.get("ready")
    if not isinstance(hint, list):
        waves = document.get("waves")
        hint = ([item for wave in waves if isinstance(wave, list) for item in wave]
                if isinstance(waves, list) else None)
    if isinstance(hint, list):
        ready = []
        for item in hint:
            step = by_id.get(_step_id(item))
            if step is not None:
                ready.append(step)
            elif isinstance(item, Mapping):
                ready.append(item)
        return ready
    done = {_step_id(step) for step in steps if _status(step) in ("complete", "skipped")}
    return [step for step in steps
            if _status(step) in ("pending", "failed")
            and all(str(dependency) in done
                    for dependency in step.get("depends_on") or [])]


def _resolve_context(document: Mapping,
                     steps: List[Mapping]) -> Tuple[List[str], List[List[str]]]:
    """`(done, edges)` hints, derived from the plan when the document does not carry them."""
    done = document.get("done")
    if not isinstance(done, list):
        done = [_step_id(step) for step in steps if _status(step) == "complete"]
    edges = document.get("edges")
    if not isinstance(edges, list):
        edges = [[str(dependency), _step_id(step)]
                 for step in steps for dependency in step.get("depends_on") or []]
    return ([str(item) for item in done],
            [[str(pair[0]), str(pair[1])] for pair in edges
             if isinstance(pair, (list, tuple)) and len(pair) == 2])


def run(document: Any, *, want_tier: bool, want_waves: bool, env: Mapping[str, str],
        post: Optional[Callable] = None, cwd: Optional[str] = None) -> Dict[str, Any]:
    """The advisory envelope: `ok` with both proposal maps (plus `proxy_faults` tokens when the
    proxy failed a call), `off` with a `refusal` code and its `reason` when nothing is enabled or
    no decisions endpoint (or its proxy) is permitted.

    One `Budget` covers the whole invocation, so tier and wave calls share a single deadline
    and a slow plan cannot multiply the window per chunk.
    """
    plan = _plan_document(document)
    steps = _plan_steps(plan)
    ready = _resolve_ready(document, steps)
    done, edges = _resolve_context(document, steps)
    tier_on = want_tier and tier_enabled(env)
    waves_on = want_waves and waves_enabled(env)
    if not tier_on and not waves_on:
        reason = (f"{MASTER_ENV} is not set to 1" if env.get(MASTER_ENV) != "1"
                  else f"requested capabilities are off: {TIER_ENV}/{WAVES_ENV} must be 1")
        return {"status": "off", "refusal": "flags-off", "reason": reason,
                "tier_proposals": {}, "wave_hints": {}}
    refusal = endpoint_refusal(env, cwd)
    if refusal is not None:
        return {"status": "off", "refusal": refusal[0], "reason": refusal[1],
                "tier_proposals": {}, "wave_hints": {}}
    budget = Budget(timeout_seconds(env) * ATTEMPTS)
    client.take_proxy_faults()
    proposals = (tier_proposals(steps, env=env, post=post, budget=budget, cwd=cwd)
                 if tier_on else {})
    hints = (wave_hints(ready, done=done, edges=edges, env=env, post=post, budget=budget,
                        cwd=cwd)
             if waves_on else {})
    payload = {"status": "ok",
               "tier_proposals": {name: {"level": proposal.level,
                                         "confidence": proposal.confidence}
                                  for name, proposal in proposals.items()},
               "wave_hints": dict(hints)}
    faults = client.take_proxy_faults()
    if faults:
        payload["proxy_faults"] = sorted(faults)
    return payload


def _read_document(source: str) -> Any:
    """The JSON document at *source*, or stdin when it is `-`."""
    try:
        text = sys.stdin.read() if source == "-" else Path(source).read_text(encoding="utf-8")
    except OSError as exc:
        raise InputError(f"cannot read {source!r}: {exc}") from exc
    try:
        return json.loads(text)
    except ValueError as exc:
        raise InputError(f"plan document is not valid JSON: {exc}") from exc


def main(argv=None) -> int:
    """Run the advisory CLI; 0 for ok/off, 1 for a clean error envelope, 2 for usage."""
    parser = argparse.ArgumentParser(
        prog="jev_choice.py", description=__doc__,
        formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("plan", help="plan JSON file (a plan_export document or the plan), "
                                     "or - for stdin")
    group = parser.add_mutually_exclusive_group()
    group.add_argument("--tier", action="store_true", help="ask only the tier question")
    group.add_argument("--waves", action="store_true", help="ask only the wave questions")
    group.add_argument("--both", action="store_true", help="ask both (the default)")
    parser.add_argument("--json", action="store_true",
                        help="emit JSON (the only format; accepted for symmetry with the CLI twin)")
    args = parser.parse_args(argv)
    chosen = args.tier or args.waves or args.both
    want_tier = args.tier or args.both or not chosen
    want_waves = args.waves or args.both or not chosen
    try:
        payload = run(_read_document(args.plan), want_tier=want_tier, want_waves=want_waves,
                      env=os.environ, cwd=os.getcwd())
    except Exception as exc:  # pylint: disable=broad-except  # the CLI never tracebacks
        payload = {"status": "error", "reason": f"{type(exc).__name__}: {exc}",
                   "tier_proposals": {}, "wave_hints": {}}
    print(json.dumps(payload, ensure_ascii=False, sort_keys=True, indent=2))
    return 1 if payload["status"] == "error" else 0


if __name__ == "__main__":
    sys.exit(main())
