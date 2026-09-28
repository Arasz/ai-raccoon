"""Query pipeline: a port of pi's query-pipeline (planner, Jev scoring, merge, runner).

Imports neither sibling module: every effect (`post`, `search`, `score`, `prune`, the budget and
its clock) arrives as a parameter, so `memory_context.build()` is the only place that wires them.
Nothing here raises out of `plan`, `score` or `run`.
"""
from __future__ import annotations

import json
import math
import re
from typing import Any, Callable, Dict, List, Mapping, NamedTuple, Optional, Tuple

# ------------------------------------------------------------------ planner prose (pi, verbatim)

DELEGATOR_PERSONA = """# Delegator

## First turn

Read `.ai-badger/delegation.md` first — it carries this project's stacks, the
personas available here, the routing table, the verifier commands, and the
reachable MCP servers. If it is absent, read `.ai-badger/config.json`
(`stacks`, `commands`, `personaRouting`), list `.ai-badger/agents/`, and say
out loud that the delegation map is missing. Never infer a project's personas
or commands from memory.

## The contract

Mine, because they need the whole task in one head: decomposition and each
package's acceptance criterion; the brief; running build/test/lint and holding
the verdict; integration at the seams; arbitration between packages; anything
irreversible without a human; fixes under ~10 lines found while integrating.
Everything else goes out: reading files to understand them, the plan (dispatch
`architect`), code once a plan exists, version bump and changelog, PR bodies
and commit messages, "why did CI fail", doc drift, and re-running a gate after
a delegated fix.

## Dispatch procedure

1. **Is it a unit?** Under ~2,000 expected output tokens, do it here.
2. **Can I name the verifier?** No → dispatch the investigation, re-decide.
3. **Which persona?** Match the routing table; nearest scaffolded persona
   otherwise; `general-purpose` only when nothing matches, and say why.
4. **Which lane?** By the derivation the work needs, not its size — see below.
5. **Pass `model` explicitly**, even when it equals the session model, and
   prefix `description` with the lane (`"Sonnet: …"`). Silence inherits opus.
6. **Fan out in one message.** Independent packages share one tool block.

## Lanes

Pick by required derivation. Rates live in `skills/task/extensions/claude/`.

- **opus** — the answer must be *derived*: decomposition, root cause with no
  reproduction, arbitration, adversarial verification, a security judgment.
- **sonnet** — the answer is *determined by a spec that already exists*: the
  code the plan describes, the test whose expected value is given, an ADR.
- **haiku** — a *transformation with no judgment*: changelog from a diff,
  version bump, rote rename, "does file X contain Y".
- **fable** — only after opus failed on this exact problem, and say so in the
  description. The most expensive lane, not a cheap one.

## The floor and the fan-out

- Don't dispatch under ~2,000 expected output tokens. A cold start costs tens
  of thousands of cache-write tokens; below that floor you pay more than you
  save. Above it the saving is large, so this rule should rarely fire.
- Fan out independent packages in **one message** — the prompt cache window is
  minutes wide, and serial dispatches lose the warm prefix.
- Prefer one multi-turn subagent over N one-shots on the same material.
- Depth-2 fan-out is allowed: let a large package's persona dispatch further
  rather than exploding it into eight reports for you to integrate.

## No dispatch without a verifier

Name the check before writing the dispatch. Three tiers, in order:

1. a command from the project's `commands` that must pass;
2. a second, cheaper dispatch testing one specific property — adversarial
   ("prove this test fails without the fix"), never "review this";
3. reading the diff yourself — permitted only under ~100 lines.

If none applies the package is not delegable yet; decompose until one does.
**A subagent's summary is not evidence — re-run the gate.**

## Ledger

Keep a running table in the session, one row per dispatch: package, persona,
lane, verifier, verdict. Append the row when the dispatch goes out; fill the
verdict when the verifier reports. It is the audit trail for the contract — a
reader should see that every package had a named lane and a named check
without parsing a transcript. Report it at the end alongside what shipped.

Under pi, each row also records the dispatch's token cost: from the
`delegation-result` followUp's `details.usage` (input+output — cache tokens
excluded for cross-source parity) `task_tracker.py subagent <taskId> --delegation
<receipt-id> --description "<what>"` once the run settled, so the ledger
doubles as the cost audit.

## Scope boundary

Never writes the plan — dispatch `architect` and integrate the blueprint.
Never merges, tags, force-pushes or publishes. Never accepts an unverified
claim. Keeps its own volume small: the delegator's share of the session's
total output tokens stays under 25%. Reading full subagent outputs instead of
reports and verdicts, or writing the code itself, is the failure — that is the
boundary, and no tool ban can express it.

## Tags

`delegation` `orchestration` `cost` `model-routing` `autonomous`
"""

PLANNER_ADDENDUM = """## Retrieval-query planning (this call's only role)

The delegation procedures above are context, not instructions for this call: you
have no tools, you must not read files or search memory, and you must not
dispatch anything. Your entire output is one JSON object of the shape
{"concepts":[{"name":"<short concept name>","queries":["<query>","<query>"]}]}.
Group retrieval queries by core concept; emit 2 to 6 queries total, each at most
300 characters; each query must stand alone (name the actual thing, not "this"
or "the issue"). Query the mechanism/decision content you expect in a software
project's docs and code, not the user's complaints. Output ONLY the JSON object
— no prose, no code fences. If you emit any fragment before the final object,
the final object must still be complete and valid."""

PLANNER_USER_PREFIX = (
    "You are a retrieval-query planner for an ai-raccoon memory bank. Do not use any tools. Do "
    "not read files. Do not search memory. Analyze the USER REQUEST below and produce focused "
    "memory_search queries for a hybrid (keyword + embedding) bank.\n"
    "\n"
    "Constraints:\n"
    "- Group queries by core concept. One concept may need several angles; several concepts "
    "may each need a few angles.\n"
    "- 2 to 6 queries total, each at most 300 characters.\n"
    "- Each query must stand alone (name the actual thing, not \"this\" or \"the issue\") and must "
    "fit comfortably inside a 254-token embedding window.\n"
    "- Query the mechanism/decision content you expect to exist in a software project's docs "
    "and code, not the user's complaints or pleasantries.\n"
    "- Output ONLY one JSON object, no prose and no code fences, exactly this shape:\n"
    "{\"concepts\":[{\"name\":\"<short concept name>\",\"queries\":[\"<query>\",\"<query>\"]}]}\n"
    "\n"
    "USER REQUEST:\n"
    "<<<\n"
)

CHAT_PATH = "/api/v1/chat/completions"
PLAN_TEXT_MAX = 65536
# A plan object sits at most this many braces deep; deeper spans are never decoded, which
# bounds parse_plan's work on pathological text to PLAN_DEPTH_MAX decodes.
PLAN_DEPTH_MAX = 8
CONCEPT_NAME_MAX = 120
CONCEPT_QUERIES_MAX = 4
QUERY_MAX = 300
TOTAL_QUERIES_MIN = 2
TOTAL_QUERIES_MAX = 6

PLANNER_REASONS = ("no-model", "timeout", "transport", "empty-text", "no-json-object",
                   "invalid-shape")
RUN_REASONS = ("ok", "no-candidates", "search-error", "budget-exhausted")

# ECMAScript WhiteSpace and LineTerminator; twin of memory_context.JS_SPACE (a test compares them).
JS_SPACE = ("\t\n\v\f\r \u00a0\u1680"
            + "".join(chr(c) for c in range(0x2000, 0x200B))
            + "\u2028\u2029\u202f\u205f\u3000\ufeff")

# ------------------------------------------------------------------ Jev (pi jev-client.ts)

BATCH_MAX = 12
ATTEMPTS = 3
ATTEMPT_SECONDS = 15.0
STATE_CHAR_CAP = 32000
EXCERPT_CHAR_CAP = 500
MODEL = "typesafe/jev-1.13"
DECISIONS_PATH = "/api/alpha/decisions"
SCORE_QUESTION = ("How much does `candidate` help answer or implement the user's request in the "
                  "state? Rate only this candidate.")
SCORE_CRITERIA = (
    "unrelated — it does not touch the request",
    "related background — same area, but answers none of the request",
    "partially answers — covers one need, misses the rest",
    "directly answers — a specific need in the request is answered or implemented",
)
ERROR_KINDS = ("misrouted-refusal", "auth", "billing", "rate-limited", "server",
               "transport-timeout", "malformed", "missing-key")
RETRYABLE_KINDS = ("server", "transport-timeout", "malformed", "rate-limited")
_STATUS_KINDS = {400: "misrouted-refusal", 401: "auth", 402: "billing", 429: "rate-limited"}

# ------------------------------------------------------------------ runner (pi pipeline.ts)

POOL_MAX = 48
MERGE_SLOTS = 5
SEARCH_STOP_MARGIN = 0.5
FALLBACK_MIN_SECONDS = 1.0

_DECIMAL = re.compile(r"[+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?")


class Limits(NamedTuple):
    """Stage limits in seconds (pi's totalMs, plannerMs, searchMs, scoreMs)."""

    total: float
    planner: float
    search: float
    score: float


LIMITS = Limits(90.0, 15.0, 15.0, 8.0)


class PlanResult(NamedTuple):
    """Planner outcome: `ok` with a `{"concepts": [...]}` plan, else a `PLANNER_REASONS` reason."""

    status: str
    reason: str
    plan: Optional[Dict[str, Any]]


class Verdict(NamedTuple):
    """One classified Jev reply: `ok` with answers by question name, or an `ERROR_KINDS` entry."""

    kind: str
    answers: Dict[str, Optional[float]]


class RunResult(NamedTuple):
    """Runner outcome as pi's `PipelineResult`, without the timing counters."""

    status: str
    reason: str
    mem: List[Dict[str, Any]]
    code: List[Dict[str, Any]]
    queries: List[str]
    candidates: int
    scored: int


# ------------------------------------------------------------------ small helpers


def js_trim(text: str) -> str:
    """JS `String.prototype.trim`."""
    return text.strip(JS_SPACE)


def _utf16_len(text: str) -> int:
    """JS `.length`: UTF-16 code units."""
    return len(text.encode("utf-16-le")) // 2


def _finite(value: Any) -> bool:
    return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value)


def _refuse_constant(name: str) -> Any:
    raise ValueError(f"{name} is not JSON")


_DECODER = json.JSONDecoder(parse_constant=_refuse_constant)


def _loads(raw: Any) -> Any:
    """Strict JSON (no NaN/Infinity, like JS `JSON.parse`) from bytes or text; raises ValueError."""
    text = raw.decode("utf-8") if isinstance(raw, (bytes, bytearray)) else raw
    if not isinstance(text, str):
        raise ValueError("not text")
    return _DECODER.decode(text)


def _reply(reply: Any) -> Optional[Tuple[int, Any]]:
    """`(status, body)` of a `Reply` that arrived; None when it carries an `error` or is junk."""
    status = getattr(reply, "status", None)
    if getattr(reply, "error", None) or not isinstance(status, int) or isinstance(status, bool):
        return None
    return status, getattr(reply, "body", b"")


def _is_timeout(reply: Any) -> bool:
    return getattr(reply, "error", None) == "timeout"


# ------------------------------------------------------------------ planner


def build_user_prompt(query: str) -> str:
    """pi's `buildPlannerUserPrompt`: the prefix, the raw query, then `>>>`."""
    return f"{PLANNER_USER_PREFIX}{query}\n>>>"


def _object_spans(text: str) -> List[Tuple[int, int]]:
    """Every brace-balanced span opening at most `PLAN_DEPTH_MAX` deep, by start; braces
    inside strings are inert."""
    stack: List[int] = []
    spans: List[Tuple[int, int]] = []
    in_string = escaped = False
    for index, char in enumerate(text):
        if in_string:
            if escaped:
                escaped = False
            elif char == "\\":
                escaped = True
            elif char == '"':
                in_string = False
        elif char == '"':
            in_string = True
        elif char == "{":
            stack.append(index)
        elif char == "}" and stack:
            depth = len(stack)
            start = stack.pop()
            if depth <= PLAN_DEPTH_MAX:
                spans.append((start, index))
    spans.sort()
    return spans


def _normalize_plan(value: Any) -> Optional[Dict[str, Any]]:
    """pi's `normalizePlan`: keep valid concepts in order, truncate to the query limits."""
    if not isinstance(value, dict) or not isinstance(value.get("concepts"), list):
        return None
    concepts, total = [], 0
    for entry in value["concepts"]:
        if total >= TOTAL_QUERIES_MAX:
            break
        if not isinstance(entry, dict):
            continue
        name, raw_queries = entry.get("name"), entry.get("queries")
        if not isinstance(name, str) or not isinstance(raw_queries, list):
            continue
        name = js_trim(name)
        if not 1 <= _utf16_len(name) <= CONCEPT_NAME_MAX:
            continue
        queries: List[str] = []
        for raw in raw_queries:
            if len(queries) >= CONCEPT_QUERIES_MAX or total + len(queries) >= TOTAL_QUERIES_MAX:
                break
            if not isinstance(raw, str):
                continue
            query = js_trim(raw)
            if 1 <= _utf16_len(query) <= QUERY_MAX:
                queries.append(query)
        if not queries:
            continue
        concepts.append({"name": name, "queries": queries})
        total += len(queries)
    if not concepts or total < TOTAL_QUERIES_MIN:
        return None
    return {"concepts": concepts}


def parse_plan(text: Any) -> PlanResult:
    """pi's `parsePlan`: the last complete JSON object that validates; text over `PLAN_TEXT_MAX`
    is `no-json-object` without scanning, and an object deeper than `PLAN_DEPTH_MAX` is skipped."""
    if not isinstance(text, str) or js_trim(text) == "":
        return PlanResult("fallback", "empty-text", None)
    if len(text) > PLAN_TEXT_MAX:
        return PlanResult("fallback", "no-json-object", None)
    parsed_any = False
    for start, end in reversed(_object_spans(text)):
        try:
            value, stop = _DECODER.raw_decode(text, start)
        except (ValueError, RecursionError):
            continue
        if stop != end + 1:
            continue
        parsed_any = True
        plan_value = _normalize_plan(value)
        if plan_value is not None:
            return PlanResult("ok", "ok", plan_value)
    return PlanResult("fallback", "invalid-shape" if parsed_any else "no-json-object", None)


def planner_model(override: Optional[str], resolver: Any, registry: Any) -> Optional[str]:
    """The OpenRouter model id: a `vendor/name` override, else the resolver's `medium` pin."""
    if isinstance(override, str) and override.strip():
        ref = override.strip()
        return ref.removeprefix("openrouter/") if "/" in ref else None
    if resolver is None:
        return None
    try:
        ident = resolver.resolve(level="medium", groups=resolver.load_groups(registry))
    except Exception:  # pylint: disable=broad-exception-caught
        return None
    return ident.removeprefix("openrouter/") if isinstance(ident, str) and ident else None


def _chat_text(document: Any) -> str:
    """`choices[0].message.content` as a string, else its `text` parts joined, else ""."""
    try:
        content = document["choices"][0]["message"]["content"]
    except (KeyError, IndexError, TypeError):
        return ""
    if isinstance(content, str):
        return content
    if not isinstance(content, list):
        return ""
    return "".join(part["text"] for part in content if isinstance(part, dict)
                   and part.get("type") == "text" and isinstance(part.get("text"), str))


def plan(query: str, *, post: Callable, base: Optional[str], key: Optional[str],
         model: Optional[str], budget: Any) -> PlanResult:
    """Ask the planner model for retrieval queries; any failure is a `PLANNER_REASONS` fallback."""
    try:
        if model is None:
            return PlanResult("fallback", "no-model", None)
        if base is None or key is None:
            return PlanResult("fallback", "transport", None)
        if budget.remaining() <= 0:
            return PlanResult("fallback", "timeout", None)
        body = {"model": model, "messages": [
            {"role": "system", "content": DELEGATOR_PERSONA + "\n\n" + PLANNER_ADDENDUM},
            {"role": "user", "content": build_user_prompt(query)},
        ]}
        reply = post(base + CHAT_PATH, body, key, budget)
        if _is_timeout(reply):
            return PlanResult("fallback", "timeout", None)
        parts = _reply(reply)
        if parts is None or parts[0] != 200:
            return PlanResult("fallback", "transport", None)
        try:
            document = _loads(parts[1])
        except (ValueError, UnicodeDecodeError, RecursionError):
            return PlanResult("fallback", "transport", None)
        return parse_plan(_chat_text(document))
    except Exception:  # pylint: disable=broad-exception-caught
        return PlanResult("fallback", "transport", None)


# ------------------------------------------------------------------ Jev


def build_score_question(candidate: Mapping) -> Dict[str, Any]:
    """One `score` question: path `path ?? sourceFile ?? ""`, kind default `memory`."""
    path = candidate.get("path")
    if path is None:
        path = candidate.get("sourceFile")
    kind = candidate.get("kind")
    snippet = candidate.get("snippet")
    return {
        "type": "score",
        "instructions": {
            "candidate": {
                "path": "" if path is None else path,
                "kind": "memory" if kind is None else kind,
                "excerpt": (snippet if isinstance(snippet, str) else "")[:EXCERPT_CHAR_CAP],
            },
            "question": SCORE_QUESTION,
        },
        "criteria": list(SCORE_CRITERIA),
    }


def _answer(entry: Any) -> Optional[float]:
    if not isinstance(entry, dict) or entry.get("type") != "score":
        return None
    if not _finite(entry.get("score")):
        return None
    return float(min(3.0, max(0.0, entry["score"])))


def classify(reply: Any, names: List[str]) -> Verdict:
    """pi's `classifyScoreResponse` over a `Reply`; timeout or transport → `transport-timeout`."""
    parts = _reply(reply)
    if parts is None:
        return Verdict("transport-timeout", {})
    status, body = parts
    if status in _STATUS_KINDS:
        return Verdict(_STATUS_KINDS[status], {})
    if status != 200:
        return Verdict("server", {})
    try:
        document = _loads(body)
    except (ValueError, UnicodeDecodeError, RecursionError):
        return Verdict("malformed", {})
    if not isinstance(document, dict):
        return Verdict("malformed", {})
    if "error" in document and "answers" not in document:
        return Verdict("server", {})
    answers = document.get("answers")
    answers = answers if isinstance(answers, dict) else {}
    return Verdict("ok", {name: _answer(answers.get(name)) for name in names})


def _score_batch(body: Dict[str, Any], names: List[str], *, post: Callable, url: str, key: str,
                 budget: Any) -> Dict[str, Optional[float]]:
    """Up to `ATTEMPTS` attempts, retrying only `RETRYABLE_KINDS`; no sleep between them."""
    for _ in range(ATTEMPTS):
        if budget.remaining() <= 0:
            return {}
        verdict = classify(post(url, body, key, budget.child(ATTEMPT_SECONDS)), names)
        if verdict.kind == "ok":
            return verdict.answers
        if verdict.kind not in RETRYABLE_KINDS:
            return {}
    return {}


def score(query: str, pool: List[Mapping], *, post: Callable, base: Optional[str],
          key: Optional[str], budget: Any) -> List[Optional[float]]:
    """Jev scores for *pool* in order, batches of `BATCH_MAX`; a failed batch leaves None."""
    results: List[Optional[float]] = [None] * len(pool)
    try:
        if not pool or base is None or not isinstance(key, str) or not key.strip():
            return results
        state = query[:STATE_CHAR_CAP]
        for start in range(0, len(pool), BATCH_MAX):
            if budget.remaining() <= 0:
                break
            batch = range(start, min(start + BATCH_MAX, len(pool)))
            questions = {f"c{i}": build_score_question(pool[i]) for i in batch}
            answers = _score_batch({"model": MODEL, "state": state, "questions": questions},
                                   list(questions), post=post, url=base + DECISIONS_PATH,
                                   key=key, budget=budget)
            for i in batch:
                results[i] = answers.get(f"c{i}")
    except Exception:  # pylint: disable=broad-exception-caught
        return [None] * len(pool)
    return results


# ------------------------------------------------------------------ merge (pi merge.ts)


def server_rank(hit: Mapping) -> float:
    """A finite number, or a decimal-literal string parsed; anything else ranks last."""
    raw = hit.get("ranking")
    if _finite(raw):
        return float(raw)
    if isinstance(raw, str) and _DECIMAL.fullmatch(js_trim(raw)):
        value = float(js_trim(raw))
        if math.isfinite(value):
            return value
    return math.inf


def doc_key(hit: Mapping) -> str:
    """Document identity: `path:` + trimmed path/sourceFile, else `hash:` + trimmed hash."""
    path = hit.get("path")
    if path is None:
        path = hit.get("sourceFile")
    path = js_trim(path) if isinstance(path, str) else ""
    if path not in ("", "?"):
        return f"path:{path}"
    digest = hit.get("hash")
    return f"hash:{js_trim(digest) if isinstance(digest, str) else ''}"


def _merge_order(indexed: Tuple[int, Mapping]) -> Tuple[int, float, float, int]:
    index, hit = indexed
    value = hit.get("score")
    scored = _finite(value)
    return (0 if scored else 1, -float(value) if scored else 0.0, server_rank(hit), index)


def merge_select(candidates: List[Mapping], slots: int = MERGE_SLOTS
                 ) -> Tuple[List[Mapping], List[Mapping]]:
    """pi's `mergeSelect`: the best chunk per document, then backfill from chosen documents."""
    ranked = [hit for _, hit in sorted(enumerate(candidates), key=_merge_order)]
    chosen: List[Mapping] = []
    chosen_hashes, seen_docs = set(), set()
    for hit in ranked:
        if len(chosen) == slots:
            break
        key = doc_key(hit)
        if key in seen_docs:
            continue
        seen_docs.add(key)
        chosen.append(hit)
        chosen_hashes.add(hit.get("hash"))
    for hit in ranked:
        if len(chosen) >= slots:
            break
        if hit.get("hash") in chosen_hashes or doc_key(hit) not in seen_docs:
            continue
        chosen.append(hit)
        chosen_hashes.add(hit.get("hash"))
    return ([hit for hit in chosen if hit.get("kind") == "memory"],
            [hit for hit in chosen if hit.get("kind") == "code"])


# ------------------------------------------------------------------ runner (pi pipeline.ts)


def dedupe_queries(concepts: List[Mapping], query: str) -> List[Tuple[str, str]]:
    """Planned `(query, concept)` pairs, trimmed, without blanks, repeats or the input query."""
    seen = {js_trim(query)}
    out: List[Tuple[str, str]] = []
    for concept in concepts:
        for raw in concept["queries"]:
            text = js_trim(raw)
            if text and text not in seen:
                seen.add(text)
                out.append((text, concept["name"]))
    return out


def _annotate(hits: Any, kind: str, query: str, concept: str) -> List[Dict[str, Any]]:
    if not isinstance(hits, list):
        return []
    return [{**hit, "kind": kind, "query": query, "concept": concept}
            for hit in hits if isinstance(hit, dict)]


def _found(search: Callable, query: str, budget: Any) -> Optional[Tuple[Any, Any]]:
    """`(mem, code)` from one search, or None when it failed, raised or returned junk."""
    try:
        mem, code = search(query, budget)
    except Exception:  # pylint: disable=broad-exception-caught
        return None
    return mem, code


def make_pool(mem: List[Mapping], code: List[Mapping], prune: Callable) -> List[Dict[str, Any]]:
    """Prune each kind, concatenate memory then code, stable-sort by server rank, cap `POOL_MAX`."""
    joined = list(prune(mem)) + list(prune(code))
    ordered = sorted(enumerate(joined), key=lambda pair: (server_rank(pair[1]), pair[0]))
    return [hit for _, hit in ordered][:POOL_MAX]


def _scores(scorer: Callable, query: str, pool: List[Dict[str, Any]], budget: Any
             ) -> List[Dict[str, Any]]:
    """The pool with Jev scores joined by hash; any failure leaves every score None."""
    try:
        values = scorer(query, pool, budget=budget)
    except Exception:  # pylint: disable=broad-exception-caught
        values = None
    by_hash: Dict[Any, Optional[float]] = {}
    if isinstance(values, list) and len(values) == len(pool):
        for hit, value in zip(pool, values):
            by_hash[hit.get("hash")] = float(value) if _finite(value) else None
    return [{**hit, "score": by_hash.get(hit.get("hash"))} for hit in pool]


def _fallback(reason: str, query: str, search: Callable, prune: Callable, budget: Any,
              limits: Limits) -> RunResult:
    """One search on the input query; the original reason survives unless that search fails."""
    left = budget.remaining()
    if left < FALLBACK_MIN_SECONDS:
        return RunResult("fallback", reason, [], [], [], 0, 0)
    share = min(limits.search, max(0.0, left - limits.score))
    found = _found(search, query, budget.child(share)) if share > 0 else None
    if found is None:
        return RunResult("fallback", "search-error", [], [], [], 0, 0)
    mem = prune(_annotate(found[0], "memory", query, "fallback"))
    code = prune(_annotate(found[1], "code", query, "fallback"))
    return RunResult("fallback", reason, mem[:MERGE_SLOTS], code[:MERGE_SLOTS], [],
                     len(mem) + len(code), 0)


# pylint: disable=redefined-outer-name
def _stages(query: str, *, plan: Callable, search: Callable, score: Callable,
            prune: Callable, budget: Any, limits: Limits) -> RunResult:
    def fallback(reason: str) -> RunResult:
        return _fallback(reason, query, search, prune, budget, limits)

    planner_share = min(limits.planner, max(0.0, limits.total - limits.search - limits.score))
    try:
        planned = plan(query, budget=budget.child(planner_share))
    except Exception:  # pylint: disable=broad-exception-caught
        planned = None
    status = getattr(planned, "status", None)
    if status != "ok":
        reason = getattr(planned, "reason", None)
        return fallback(reason if status == "fallback" and reason in PLANNER_REASONS
                        else "transport")
    queries = dedupe_queries(planned.plan["concepts"], query)
    if not queries:
        return fallback("invalid-shape")

    mem_hits: List[Dict[str, Any]] = []
    code_hits: List[Dict[str, Any]] = []
    for text, concept in queries:
        if budget.remaining() <= limits.score + SEARCH_STOP_MARGIN:
            break
        share = min(limits.search, max(0.0, budget.remaining() - limits.score))
        found = _found(search, text, budget.child(share))
        if found is None:
            continue
        mem_hits += _annotate(found[0], "memory", text, concept)
        code_hits += _annotate(found[1], "code", text, concept)

    pool = make_pool(mem_hits, code_hits, prune)
    if not pool:
        return fallback("no-candidates")
    scored = _scores(score, query, pool, budget.child(limits.score))
    if budget.expired():
        return RunResult("fallback", "budget-exhausted", [], [], [], 0, 0)
    mem, code = merge_select(scored, MERGE_SLOTS)
    return RunResult("pipeline", "ok", mem, code, [text for text, _ in queries], len(pool),
                     sum(1 for hit in scored if hit["score"] is not None))


def run(query: str, *, plan: Callable, search: Callable, score: Callable,
        prune: Callable, budget: Any, limits: Optional[Limits] = None) -> RunResult:
    """pi's `runPipeline` under one budget: plan, search, score, merge; else fall back."""
    try:
        return _stages(query, plan=plan, search=search, score=score, prune=prune, budget=budget,
                       limits=limits or LIMITS)
    except Exception:  # pylint: disable=broad-exception-caught
        return RunResult("fallback", "search-error", [], [], [], 0, 0)
