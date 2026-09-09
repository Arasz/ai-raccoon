"""P4 committee grading for the threshold-committee-eval (plan §P4).

Slot = (query, arm); each slot is graded by a fixed trio of headless graders
(`pi -p --no-session [--provider P --model M]`, `--runner` switches the CLI),
one gated form each per committee round. An attempt = one round = 3 parallel
grader submissions. Consistency = 3/3 identical per-chunk grade vectors —
majority is never accepted. An inconsistent round nudges ("argue against your
own grades"); a query version gets at most 3 rounds, then it is REPLACED by a
fresh query from the same stratum which inherits the chain-wide cap of 9
rounds per slot (3 versions x 3 rounds — a 4th version is unreachable).
Exhausted slots are reported (`"exhausted": true`), never skipped. A malformed
form is re-asked with the same brief at most 2 times (logged, not
round-consuming); a still-invalid form is void and the round cannot reach 3/3.

Reads P3's `metrics.json` strictly by contract (path, never by import):
{"queries": [{queryId, queryText, projectId,
              arms: {off|threshold: {results: [{hash, snippet, sourceFile?,
                                               chunkIndex?}]}},
              pair: {top8SetOverlap, ...}}]}
(a top-level list is also accepted). Sampling stratifies on the mechanical
diff ONLY: changed = top-8 SET overlap < 1.0, controls = == 1.0.

Artifacts (under --out-dir): `sample.json`, `forms/<slot>/<round>/grader-<n>.json`
(+ `.payload.txt` per submission, `.invalid.txt` for rejected responses,
`-reask-<k>` stems for re-asks), `forms-manifest.json` (sha256 of every
archived form and payload), `committee-grades.json`.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import logging
import random
import re
import shlex
import os
import subprocess
from collections.abc import Sequence
from dataclasses import dataclass
from pathlib import Path
from typing import Any

log = logging.getLogger(__name__)

DEFAULT_RUNNER = "pi -p"
SEED = 42
ARMS = ("off", "threshold")
SAMPLE_SIZE = 16
CHANGED_PICK = 10
MAX_ROUNDS_PER_VERSION = 3  # initial round + <=2 nudges
MAX_CHAIN_ROUNDS_PER_SLOT = 9  # 3 versions x 3 rounds; spans the whole chain
MAX_REASKS = 2
NUDGE_PHRASE = "argue against your own grades"
FORM_KEYS = frozenset({"slotId", "queryId", "arm", "chunks", "overall"})
CHUNK_KEYS = frozenset({"hash", "grade", "reason"})
GRADES = frozenset({"A", "0"})

SMOKE_QUERY_ID = "smoke-h1"
SMOKE_SLOT_ID = f"{SMOKE_QUERY_ID}__off"


@dataclass(frozen=True)
class Grader:
    """One member of the grader trio; provider=None means the session default."""

    index: int
    provider: str | None
    model: str | None

    @property
    def label(self) -> str:
        if self.provider is None:
            return "session-default"
        return f"{self.provider}/{self.model}"


# Owner decision (research record): #1 session default, #2 muse-spark, #3 mio.
GRADER_TRIO = (
    Grader(1, None, None),
    Grader(2, "openrouter", "meta/muse-spark-1.3-contributor"),
    Grader(3, "openrouter", "xiaomi/mimo-v2.5-pro"),
)


@dataclass(frozen=True)
class Chunk:
    hash: str
    snippet: str
    source_file: str = ""


@dataclass(frozen=True)
class QueryMetrics:
    """One query's per-arm retrieval payloads plus its mechanical-diff stratum."""

    query_id: str
    query_text: str
    project_id: str
    arms: dict[str, tuple[Chunk, ...]]
    top8_set_overlap: float

    @property
    def stratum(self) -> str:
        return "changed" if self.top8_set_overlap < 1.0 else "controls"


def grader_header() -> list[dict[str, Any]]:
    """The grader-trio config as recorded in sample.json (P5 asserts equality)."""
    return [
        {"index": g.index, "provider": g.provider, "model": g.model, "label": g.label}
        for g in GRADER_TRIO
    ]


def load_metrics(path: Path) -> list[QueryMetrics]:
    """Load P3's metrics.json by the contract documented in the module docstring."""
    data = json.loads(Path(path).read_text(encoding="utf-8"))
    raw_queries = data["queries"] if isinstance(data, dict) else data
    queries: list[QueryMetrics] = []
    for raw in raw_queries:
        query_id = str(raw["queryId"])
        arms: dict[str, tuple[Chunk, ...]] = {}
        for arm in ARMS:
            if arm not in raw["arms"]:
                raise ValueError(f"metrics query {query_id!r} is missing arm {arm!r}")
            arm_data = raw["arms"][arm]
            results = arm_data["results"] if isinstance(arm_data, dict) else arm_data
            arms[arm] = tuple(
                Chunk(
                    hash=str(result["hash"]),
                    snippet=str(result.get("snippet", "")),
                    source_file=str(
                        result.get("sourceFile", result.get("source_file", ""))
                    ),
                )
                for result in results
            )
        pair = raw.get("pair", {})
        overlap = pair.get("top8SetOverlap", pair.get("top8_set_overlap"))
        if overlap is None:
            raise ValueError(
                f"metrics query {query_id!r} is missing pair.top8SetOverlap"
            )
        overlap = float(overlap)
        if not 0.0 <= overlap <= 1.0:
            raise ValueError(
                f"metrics query {query_id!r}: top8SetOverlap out of range: {overlap}"
            )
        queries.append(
            QueryMetrics(
                query_id=query_id,
                query_text=str(raw["queryText"]),
                project_id=str(raw.get("projectId", "")),
                arms=arms,
                top8_set_overlap=overlap,
            )
        )
    return queries


def stratify(
    queries: Sequence[QueryMetrics],
) -> tuple[list[QueryMetrics], list[QueryMetrics]]:
    """Split on the mechanical diff ONLY: overlap < 1.0 changed, == 1.0 controls."""
    changed = sorted(
        (q for q in queries if q.top8_set_overlap < 1.0), key=lambda q: q.query_id
    )
    controls = sorted(
        (q for q in queries if q.top8_set_overlap == 1.0), key=lambda q: q.query_id
    )
    return changed, controls


def pick_sample(
    changed: Sequence[QueryMetrics], controls: Sequence[QueryMetrics], seed: int = SEED
) -> tuple[list[QueryMetrics], list[QueryMetrics]]:
    """Seeded pick of min(10, |changed|) changed + controls backfilled to 16."""
    rng = random.Random(seed)
    ordered_changed = sorted(changed, key=lambda q: q.query_id)
    ordered_controls = sorted(controls, key=lambda q: q.query_id)
    n_changed = min(CHANGED_PICK, len(ordered_changed))
    picked_changed = list(rng.sample(ordered_changed, n_changed))
    wanted_controls = max(SAMPLE_SIZE - n_changed, 0)
    picked_controls = list(
        rng.sample(ordered_controls, min(wanted_controls, len(ordered_controls)))
    )
    return picked_changed, picked_controls


def write_sample_json(
    path: Path,
    *,
    seed: int,
    picked_changed: Sequence[QueryMetrics],
    picked_controls: Sequence[QueryMetrics],
    n_changed: int,
    n_controls: int,
) -> None:
    """Persist sample.json; the header carries the grader-trio config (P5 reads it)."""
    doc = {
        "header": {
            "seed": seed,
            "sampleSize": SAMPLE_SIZE,
            "changedPick": CHANGED_PICK,
            "graders": grader_header(),
            "stratification": (
                "top-8 SET overlap between arms: changed < 1.0, controls == 1.0 "
                "(mechanical diff only, never grades)"
            ),
            "composition": {
                "changedAvailable": n_changed,
                "controlsAvailable": n_controls,
                "pickedChanged": len(picked_changed),
                "pickedControls": len(picked_controls),
                "total": len(picked_changed) + len(picked_controls),
                "backfilled": len(picked_changed) < CHANGED_PICK,
            },
        },
        "queries": [
            {
                "queryId": q.query_id,
                "stratum": q.stratum,
                "top8SetOverlap": q.top8_set_overlap,
            }
            for q in [*picked_changed, *picked_controls]
        ],
    }
    Path(path).write_text(json.dumps(doc, indent=2) + "\n", encoding="utf-8")


def build_brief(
    query: QueryMetrics,
    arm: str,
    slot_id: str,
    grader: Grader,
    *,
    nudge: bool = False,
    previous_form: dict[str, Any] | None = None,
) -> str:
    """The grader brief: PoC rubric, form-only output, optional self-nudge.

    The nudge instruction is a property of the ROUND — every grader in a nudge
    round gets it, including one whose earlier form was void (it has no prior
    grades to argue against, but it must still re-grade under the nudge).
    """
    lines = [
        f"You are grader #{grader.index} on a 3-grader retrieval-quality committee.",
        "",
        f"Query (projectId={query.project_id}):",
        query.query_text,
        "",
        f'Chunks retrieved for arm "{arm}" (rank order):',
    ]
    for i, chunk in enumerate(query.arms[arm], start=1):
        lines.append(f"{i}. hash={chunk.hash}")
        if chunk.source_file:
            lines.append(f"   file: {chunk.source_file}")
        lines.append(f"   {chunk.snippet}")
    lines += [
        "",
        (
            "Grade EACH chunk: "
            '"A" (A = 1: the chunk answers the query) or "0" (0: it does not) — PoC rubric.'
        ),
        "",
        "Output ONLY the JSON form below — no free-form verdict, no markdown, no commentary:",
        (
            '{"slotId": "<slotId>", "queryId": "<queryId>", "arm": "<arm>", '
            '"chunks": [{"hash": "<hash>", "grade": "A"|"0", "reason": "<one sentence>"}, ...], '
            '"overall": "<one sentence>"}'
        ),
        "",
        (
            f'Use slotId="{slot_id}", queryId="{query.query_id}", arm="{arm}", and exactly the '
            "hashes listed above, each exactly once."
        ),
    ]
    if nudge:
        lines += [
            "",
            f"Nudge: {NUDGE_PHRASE}. Reconsider every chunk grade; you may change your mind.",
        ]
        if previous_form is not None:
            lines += ["Your previous form was:", json.dumps(previous_form)]
        lines += ["", "Return a fresh complete form in the same output-only format."]
    return "\n".join(lines)


def parse_form(raw: str | None) -> dict[str, Any] | None:
    """Extract a JSON object from grader stdout (tolerates markdown fences)."""
    text = (raw or "").strip()
    if not text:
        return None
    if text.startswith("```"):
        text = re.sub(r"^```[a-zA-Z0-9_-]*\s*", "", text)
        text = re.sub(r"\s*```\s*$", "", text)
    candidates = [text]
    start, end = text.find("{"), text.rfind("}")
    if 0 <= start < end:
        candidates.append(text[start : end + 1])
    for candidate in candidates:
        try:
            obj = json.loads(candidate)
        except json.JSONDecodeError:
            continue
        if isinstance(obj, dict):
            return obj
    return None


def _validate_form(
    form: Any,
    *,
    slot_id: str,
    query_id: str,
    arm: str,
    expected_hashes: set[str],
) -> bool:
    """Hand-rolled mirror of committee_form.schema.json (no new deps).

    Beyond the schema: slotId/queryId/arm must match the brief, and the chunk
    list must cover exactly the brief's hashes — an incomplete form cannot
    carry a complete per-chunk grade vector, so it never counts.
    """
    if not isinstance(form, dict) or set(form) != FORM_KEYS:
        return False
    if form["slotId"] != slot_id or form["queryId"] != query_id or form["arm"] != arm:
        return False
    if not isinstance(form["overall"], str) or not form["overall"].strip():
        return False
    chunks = form["chunks"]
    if not isinstance(chunks, list) or not chunks:
        return False
    seen: list[str] = []
    for chunk in chunks:
        if not isinstance(chunk, dict) or set(chunk) != CHUNK_KEYS:
            return False
        if chunk["grade"] not in GRADES:
            return False
        if not isinstance(chunk["hash"], str) or chunk["hash"] not in expected_hashes:
            return False
        if not isinstance(chunk["reason"], str) or not chunk["reason"].strip():
            return False
        seen.append(chunk["hash"])
    return sorted(seen) == sorted(expected_hashes)


def grade_vector(form: dict[str, Any]) -> tuple[tuple[str, str], ...]:
    """Per-chunk grade vector as a sorted (hash, grade) tuple — the comparison key."""
    return tuple(sorted((c["hash"], c["grade"]) for c in form["chunks"]))


class SubprocessGraderRunner:
    """Headless grader CLI; a fresh session per call (`pi -p --no-session`)."""

    def __init__(self, runner_cmd: str = DEFAULT_RUNNER, timeout_s: float = 600.0):
        self._base = shlex.split(runner_cmd)
        if not self._base:
            raise ValueError("runner command must not be empty")
        self._timeout_s = timeout_s

    def run(self, brief: str, grader: Grader) -> str:
        cmd = [*self._base, "--no-session"]
        if grader.provider:
            cmd += ["--provider", grader.provider, "--model", grader.model]
        cmd.append(brief)
        try:
            proc = subprocess.run(
                cmd,
                capture_output=True,
                text=True,
                check=False,
                timeout=self._timeout_s,
                env={**os.environ, "PI_BADGER_MEM_RAG": "0"},
            )
        except subprocess.TimeoutExpired:
            log.error(
                "grader %d (%s) timed out after %.0fs",
                grader.index,
                grader.label,
                self._timeout_s,
            )
            return ""
        if proc.returncode != 0:
            log.error(
                "grader %d (%s) exited %d: %s",
                grader.index,
                grader.label,
                proc.returncode,
                proc.stderr.strip()[:400],
            )
            return ""
        return proc.stdout


class _FormArchive:
    """Archives every grader submission (form or rejection) + payload; sha256 manifest."""

    def __init__(self, forms_root: Path):
        self._root = Path(forms_root) / "forms"
        self._entries: list[dict[str, Any]] = []

    def archive_submission(
        self,
        slot_id: str,
        round_no: int,
        grader_index: int,
        reask_no: int,
        payload: str,
        raw: str | None,
        form: dict[str, Any] | None,
    ) -> None:
        directory = self._root / slot_id / str(round_no)
        directory.mkdir(parents=True, exist_ok=True)
        stem = (
            f"grader-{grader_index}"
            if reask_no == 0
            else f"grader-{grader_index}-reask-{reask_no}"
        )
        self._add(
            directory / f"{stem}.payload.txt",
            payload,
            slot_id,
            round_no,
            grader_index,
            "payload",
        )
        if form is None:
            self._add(
                directory / f"{stem}.invalid.txt",
                raw or "",
                slot_id,
                round_no,
                grader_index,
                "invalid",
            )
        else:
            self._add(
                directory / f"{stem}.json",
                json.dumps(form, indent=2) + "\n",
                slot_id,
                round_no,
                grader_index,
                "form",
            )

    def _add(
        self,
        path: Path,
        text: str,
        slot_id: str,
        round_no: int,
        grader_index: int,
        kind: str,
    ) -> None:
        path.write_text(text, encoding="utf-8")
        self._entries.append(
            {
                "path": path.relative_to(self._root.parent).as_posix(),
                "slotId": slot_id,
                "round": round_no,
                "grader": grader_index,
                "kind": kind,
                "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
            }
        )

    def write_manifest(self, forms_root: Path) -> Path:
        """Merge this archive's entries into forms-manifest.json (per-slot calls append)."""
        manifest_path = Path(forms_root) / "forms-manifest.json"
        existing: dict[str, dict[str, Any]] = {}
        if manifest_path.exists():
            try:
                existing = {
                    e["path"]: e
                    for e in json.loads(manifest_path.read_text(encoding="utf-8")).get(
                        "forms", []
                    )
                }
            except json.JSONDecodeError:
                existing = {}
        for entry in self._entries:
            existing[entry["path"]] = entry
        entries = sorted(
            existing.values(),
            key=lambda e: (e["slotId"], e["round"], e["grader"], e["kind"], e["path"]),
        )
        manifest_path.write_text(
            json.dumps({"forms": entries}, indent=2) + "\n", encoding="utf-8"
        )
        return manifest_path


def _submit(
    runner: Any,
    grader: Grader,
    brief: str,
    *,
    slot_id: str,
    query_id: str,
    arm: str,
    round_no: int,
    expected_hashes: set[str],
    archive: _FormArchive,
) -> tuple[dict[str, Any] | None, int, int]:
    """One grader in one round: submit, re-ask malformed forms <=2 times, then void."""
    re_asks = 0
    submissions = 0
    while True:
        raw = runner.run(brief, grader)
        submissions += 1
        form = parse_form(raw)
        if form is not None and _validate_form(
            form,
            slot_id=slot_id,
            query_id=query_id,
            arm=arm,
            expected_hashes=expected_hashes,
        ):
            archive.archive_submission(
                slot_id, round_no, grader.index, re_asks, brief, raw, form
            )
            return form, submissions, re_asks
        archive.archive_submission(
            slot_id, round_no, grader.index, re_asks, brief, raw, None
        )
        if re_asks >= MAX_REASKS:
            log.warning(
                "slot %s round %d grader %d: form void after %d re-asks; the round cannot reach 3/3",
                slot_id,
                round_no,
                grader.index,
                re_asks,
            )
            return None, submissions, re_asks
        re_asks += 1
        log.warning(
            "slot %s round %d grader %d: malformed form (submission %d); "
            "re-asking with the same brief (not round-consuming)",
            slot_id,
            round_no,
            grader.index,
            submissions,
        )


def _pick_replacement(
    candidates: Sequence[QueryMetrics], stratum: str, used: set[str], rng: random.Random
) -> QueryMetrics | None:
    """Fresh query from the same stratum, not used anywhere in the run."""
    pool = sorted(
        (c for c in candidates if c.stratum == stratum and c.query_id not in used),
        key=lambda q: q.query_id,
    )
    if not pool:
        log.warning("no fresh %s-stratum query left for a replacement", stratum)
        return None
    picked = rng.choice(pool)
    used.add(picked.query_id)
    log.info("replacement picked: %s (%s stratum)", picked.query_id, stratum)
    return picked


def grade_slot(
    query: QueryMetrics,
    arm: str,
    runner: Any,
    forms_root: Path,
    replacement_candidates: Sequence[QueryMetrics] = (),
    replacement_rng: random.Random | None = None,
    used_query_ids: set[str] | None = None,
) -> dict[str, Any]:
    """Run one slot's full replacement chain (plan §4c) and archive every form."""
    if arm not in ARMS:
        raise ValueError(f"unknown arm {arm!r}; expected one of {ARMS}")
    slot_id = f"{query.query_id}__{arm}"
    stratum = query.stratum
    archive = _FormArchive(forms_root)
    rng = replacement_rng if replacement_rng is not None else random.Random(SEED)
    used = used_query_ids if used_query_ids is not None else {query.query_id}

    versions: list[dict[str, Any]] = []
    round_verdicts: list[str] = []
    chain_rounds = 0
    submissions_total = 0
    reasks_total = 0
    accepted_vector: list[dict[str, str]] | None = None
    exhaust_reason: str | None = None
    current = query

    while True:
        version_rounds = 0
        previous_forms: dict[int, dict[str, Any]] = {}
        while (
            version_rounds < MAX_ROUNDS_PER_VERSION
            and chain_rounds < MAX_CHAIN_ROUNDS_PER_SLOT
        ):
            version_rounds += 1
            chain_rounds += 1
            is_nudge = version_rounds > 1
            expected_hashes = {c.hash for c in current.arms[arm]}
            forms: dict[int, dict[str, Any] | None] = {}
            for grader in GRADER_TRIO:
                brief = build_brief(
                    current,
                    arm,
                    slot_id,
                    grader,
                    nudge=is_nudge,
                    previous_form=previous_forms.get(grader.index),
                )
                form, submissions, re_asks = _submit(
                    runner,
                    grader,
                    brief,
                    slot_id=slot_id,
                    query_id=current.query_id,
                    arm=arm,
                    round_no=chain_rounds,
                    expected_hashes=expected_hashes,
                    archive=archive,
                )
                forms[grader.index] = form
                submissions_total += submissions
                reasks_total += re_asks
            if all(f is not None for f in forms.values()):
                vectors = [grade_vector(forms[g.index]) for g in GRADER_TRIO]
                if len(set(vectors)) == 1:  # 3/3 identical; majority never accepted
                    round_verdicts.append("accepted")
                    accepted_vector = [{"hash": h, "grade": g} for h, g in vectors[0]]
                    break
            round_verdicts.append("inconsistent")
            previous_forms = {i: f for i, f in forms.items() if f is not None}
        versions.append(
            {
                "queryId": current.query_id,
                "rounds": version_rounds,
                "outcome": "accepted" if accepted_vector is not None else "exhausted",
            }
        )
        if accepted_vector is not None:
            break
        if chain_rounds >= MAX_CHAIN_ROUNDS_PER_SLOT:
            exhaust_reason = "chain-cap"
            break
        fresh = _pick_replacement(replacement_candidates, stratum, used, rng)
        if fresh is None:
            exhaust_reason = "no-fresh-replacement"
            break
        versions[-1]["outcome"] = "replaced"
        log.info(
            "slot %s: version %s exhausted %d rounds; replaced by %s",
            slot_id,
            current.query_id,
            version_rounds,
            fresh.query_id,
        )
        current = fresh

    answer_chunks: list[str] | None = None
    if accepted_vector is not None:
        answer_chunks = [
            pair["hash"] for pair in accepted_vector if pair["grade"] == "A"
        ]
    report = {
        "slotId": slot_id,
        "queryId": query.query_id,
        "arm": arm,
        "accepted": accepted_vector is not None,
        "exhausted": exhaust_reason is not None,
        "exhaustReason": exhaust_reason,
        "rounds": chain_rounds,
        "attempts": chain_rounds,  # an attempt = one committee round = 3 submissions
        "nudges": chain_rounds - len(versions),
        "replacements": len(versions) - 1,
        "submissions": submissions_total,
        "reasks": reasks_total,
        "versions": versions,
        "roundVerdicts": round_verdicts,
        "gradeVector": accepted_vector,
        "answerChunks": answer_chunks,
    }
    archive.write_manifest(forms_root)
    return report


def run_committee(
    metrics_path: Path,
    out_dir: Path,
    *,
    seed: int = SEED,
    runner: Any | None = None,
    runner_cmd: str = DEFAULT_RUNNER,
) -> dict[str, Any]:
    """Sample from metrics.json, grade all 2x-sample slots, write the P4 artifacts."""
    metrics_path = Path(metrics_path)
    out_dir = Path(out_dir)
    queries = load_metrics(metrics_path)
    changed, controls = stratify(queries)
    picked_changed, picked_controls = pick_sample(changed, controls, seed=seed)
    out_dir.mkdir(parents=True, exist_ok=True)
    write_sample_json(
        out_dir / "sample.json",
        seed=seed,
        picked_changed=picked_changed,
        picked_controls=picked_controls,
        n_changed=len(changed),
        n_controls=len(controls),
    )
    runner = runner if runner is not None else SubprocessGraderRunner(runner_cmd)
    replacement_rng = random.Random(seed)
    used = {q.query_id for q in picked_changed} | {q.query_id for q in picked_controls}
    pool_changed = [q for q in changed if q.query_id not in used]
    pool_controls = [q for q in controls if q.query_id not in used]
    slots: list[dict[str, Any]] = []
    for query in [*picked_changed, *picked_controls]:
        pool = pool_changed if query.stratum == "changed" else pool_controls
        for arm in ARMS:
            log.info("grading slot %s__%s", query.query_id, arm)
            slots.append(
                grade_slot(
                    query,
                    arm,
                    runner,
                    out_dir,
                    replacement_candidates=pool,
                    replacement_rng=replacement_rng,
                    used_query_ids=used,
                )
            )
    grades = {
        "header": {
            "seed": seed,
            "runner": runner_cmd,
            "graders": grader_header(),
            "metricsPath": str(metrics_path),
        },
        "slots": slots,
    }
    (out_dir / "committee-grades.json").write_text(
        json.dumps(grades, indent=2) + "\n", encoding="utf-8"
    )
    return grades


SMOKE_QUERY = QueryMetrics(
    query_id=SMOKE_QUERY_ID,
    query_text="Which storage engine backs the AiRaccoon agent-memory bank?",
    project_id="ai-raccoon",
    arms={
        arm: (
            Chunk(
                "smoke-hash-1",
                "AiRaccoon stores agent memory in SQLite via the sqlite-memory backend: "
                "one bank file per machine.",
                "src/AiRaccoon.Infrastructure/Sqlite/MemoryBank.cs",
            ),
            Chunk(
                "smoke-hash-2",
                "The hermes gateway routes platform adapters to chat sessions.",
                "src/Hermes/Gateway.cs",
            ),
        )
        for arm in ARMS
    },
    top8_set_overlap=1.0,
)


def smoke_graders(runner_cmd: str = DEFAULT_RUNNER, out_dir: Path | None = None) -> int:
    """H1 gate: one live grader call per model must return a schema-valid form."""
    runner = SubprocessGraderRunner(runner_cmd)
    archive = _FormArchive(out_dir) if out_dir is not None else None
    expected_hashes = {c.hash for c in SMOKE_QUERY.arms["off"]}
    failures: list[Grader] = []
    for grader in GRADER_TRIO:
        brief = build_brief(SMOKE_QUERY, "off", SMOKE_SLOT_ID, grader)
        raw = runner.run(brief, grader)
        form = parse_form(raw)
        valid = form is not None and _validate_form(
            form,
            slot_id=SMOKE_SLOT_ID,
            query_id=SMOKE_QUERY_ID,
            arm="off",
            expected_hashes=expected_hashes,
        )
        if valid:
            log.info(
                "H1 smoke grader %d (%s): schema-valid form", grader.index, grader.label
            )
        else:
            failures.append(grader)
            log.error(
                "H1 smoke grader %d (%s): no schema-valid form (raw output archived)",
                grader.index,
                grader.label,
            )
        if archive is not None:
            archive.archive_submission(
                SMOKE_SLOT_ID, 1, grader.index, 0, brief, raw, form if valid else None
            )
    if archive is not None:
        archive.write_manifest(out_dir)
    if failures:
        log.error("H1 smoke FAILED for: %s", ", ".join(g.label for g in failures))
        return 1
    log.info("H1 smoke passed: 3/3 graders returned schema-valid forms")
    return 0


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="P4 committee grader (threshold-committee-eval)"
    )
    parser.add_argument(
        "--smoke-graders",
        action="store_true",
        help="H1 gate: one live grader call per model; exit non-zero on any failure",
    )
    parser.add_argument("--metrics", type=Path, help="P3 metrics.json to sample from")
    parser.add_argument(
        "--out-dir",
        type=Path,
        default=Path("docs/work/threshold-committee-eval"),
        help="artifact directory (sample.json, forms/, forms-manifest.json, committee-grades.json)",
    )
    parser.add_argument(
        "--runner",
        default=DEFAULT_RUNNER,
        help='grader CLI template, e.g. "pi -p" or "claude -p"',
    )
    parser.add_argument("--seed", type=int, default=SEED)
    args = parser.parse_args(argv)
    logging.basicConfig(
        level=logging.INFO, format="%(levelname)s %(name)s: %(message)s"
    )
    if args.smoke_graders:
        return smoke_graders(args.runner, args.out_dir)
    if args.metrics is None:
        parser.error("--metrics is required unless --smoke-graders is given")
    grades = run_committee(
        args.metrics, args.out_dir, seed=args.seed, runner_cmd=args.runner
    )
    accepted = sum(1 for slot in grades["slots"] if slot["accepted"])
    log.info(
        "committee grading done: %d/%d slots accepted", accepted, len(grades["slots"])
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
