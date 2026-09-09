#!/usr/bin/env python3
"""P6 integration tests + live-run report tooling (plan 2026-09-09 §P6, AC6.1–AC6.4).

P6 owns THIS file and the eval report; it never edits another package's files.
P2–P5 were built in parallel lanes against fixture contracts, so their reader
and writer shapes do not compose directly: the bridges below (owned here) derive
P4's and P5's input contracts from P3's actual artifacts — pure data
transformation of archived JSON, no package logic is re-implemented.

Load-bearing constraint (P3's live correction, also on the project bus): pair
metrics are computed on the top-8 MEMORY list in served order only. The memory
and code sections have independent 1.0-normalized scales and are never merged
by ranking — the chain test proves it with a code-section decoy that would
change every number if the sections were merged.

Tests (each `-k`-selectable):
- AC6.2 `-k chain`:         fixture chain end to end, no live deps:
                            synthetic corpus → P3 spec-builder → fake arm JSONs
                            → P3 metrics → P4 sample + FakeRunner committee
                            → P5 FakeRunner blind ab → report assembler →
                            every report number equals the fixture's number.
- AC6.1 `-k trace`:         every anchored number block in the real report is
                            byte-equal to a recomputation from the archived
                            artifacts, whose sha256s match
                            artifact-manifest.json; any orphan number fails.
- AC6.3 `-k report`:        all six report sections present, snapshot sha256,
                            budget spent, mechanical n=100, committee n=16,
                            comp_score = x/(3−abstentions).
- AC6.4 `-k merge_hygiene`: the PR construction (every path the task changed
                            except `src/`, path-scoped checkout onto origin/main)
                            yields an EMPTY src/ diff; skipped on main where
                            the assertion is trivially true.

CLI (used by the 6b live run; never runs under pytest):
    python scripts/tests/test_threshold_eval_integration.py bridge  --artifact-dir D
    python scripts/tests/test_threshold_eval_integration.py tables  --artifact-dir D
`bridge` derives metrics-committee.json / sample-ab.json / arm-<a>.ab.json from
the P3/P4 artifacts; `tables` writes artifact-manifest.json and prints the
anchored number blocks the report embeds verbatim.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import re
import subprocess
import sys
import tempfile
from collections import Counter
from pathlib import Path
from typing import Any

import pytest

REPO_ROOT = Path(__file__).resolve().parents[2]
ARTIFACT_DIR = REPO_ROOT / "docs" / "work" / "threshold-committee-eval"
REPORT_PATH = REPO_ROOT / "docs" / "work" / "2026-09-09-threshold-committee-eval.md"
MANIFEST_NAME = "artifact-manifest.json"

SEARCH_LIMIT = 8
RBO_P = 0.9


def _load_module(name: str, path: Path) -> Any:
    spec = importlib.util.spec_from_file_location(name, path)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module  # dataclass annotation resolution needs this
    spec.loader.exec_module(module)
    return module


p3 = _load_module(
    "threshold_eval_p3_runner",
    REPO_ROOT / "scripts" / "retrieval_tuning" / "run_threshold_eval.py",
)
p4 = _load_module(
    "threshold_eval_p4_committee",
    REPO_ROOT / "scripts" / "retrieval_tuning" / "committee_grade.py",
)
p5 = _load_module(
    "threshold_eval_p5_ab",
    REPO_ROOT / "scripts" / "retrieval_tuning" / "ab_compare.py",
)


# ---------------------------------------------------------------------------
# Plan-formula reference implementations (independent of the packages' code)
# ---------------------------------------------------------------------------


def reference_rbo(left: list[str], right: list[str], p: float = RBO_P) -> float:
    """Finite truncated RBO exactly as the plan pins it:
    `s += p**d * |A_d ∩ B_d| / d` for d = 1..min(len), result ×(1−p)."""
    seen_a: set[str] = set()
    seen_b: set[str] = set()
    total = 0.0
    for depth in range(1, min(len(left), len(right)) + 1):
        seen_a.add(left[depth - 1])
        seen_b.add(right[depth - 1])
        total += p**depth * len(seen_a & seen_b) / depth
    return (1 - p) * total


# ---------------------------------------------------------------------------
# Chain fixtures — a tiny synthetic corpus + fake MCP responses in P3's shape
# ---------------------------------------------------------------------------

HASH_RE = re.compile(r"^\d+\. hash=(\S+)$", re.MULTILINE)
LONG_SNIPPET = "word " * 150  # 700 chars: must be capped to 600 by P3


def _mem_hit(hash_value: str, ranking: float, source_file: str, snippet: str,
             chunk_index: int = 0) -> dict[str, Any]:
    return {"hash": hash_value, "ranking": ranking, "path": f"mem://{hash_value}",
            "sourceFile": source_file, "chunkIndex": chunk_index, "snippet": snippet}


def _memory_hits(qid: str, hashes: list[str]) -> list[dict[str, Any]]:
    hits = [
        _mem_hit(h, 0.95 - 0.05 * i, f"docs/{qid}-{h}.md", f"{qid} chunk {h} text")
        for i, h in enumerate(hashes)
    ]
    if qid == "C001":
        hits[2]["snippet"] = LONG_SNIPPET  # cap check: >600 chars served
    return hits


def _fake_response(memory: list[dict[str, Any]], code: list[dict[str, Any]]) -> dict[str, Any]:
    text = json.dumps({"data": {"results": memory, "code": code}})
    return {"jsonrpc": "2.0", "id": 1,
            "result": {"content": [{"type": "text", "text": text}]}}


def _synthetic_corpus() -> dict[str, Any]:
    """P2-corpus-shaped JSON: 3 queries over 2 projects (P3 consumes by contract)."""
    return {"queries": [
        {"id": "C001", "query": "how does the frobnicator cache entries?",
         "projectId": "proj-a", "expectedHash": "m-c001-1", "holdout": False},
        {"id": "C002", "query": "where is the retry budget configured?",
         "projectId": "proj-b", "expectedHash": "m-c002-6", "holdout": False},
        {"id": "C003", "query": "marker: unique content-targeted probe",
         "projectId": "proj-a", "expectedHash": None, "holdout": False},
    ]}


def _fixture_arm_documents(queries: list[Any]) -> tuple[dict[str, Any], dict[str, Any]]:
    """Fake arm JSONs exactly as P3's run_arm writes them, from fake MCP responses
    routed through P3's own extract_results (writer shape = P3's code path).

    Fixture design (expected pair numbers):
    - C001: identical 8-lists both arms (control) → overlap 8/8, RBO 0.5126…,
      plus a CODE-section decoy with a huge ranking that must NOT enter metrics.
    - C002: off m-c002-1..8; threshold m-c002-1..5 + t-c002-6..8 → overlap 5/8,
      drop 3 (sourceFile from off), backfill 3, anchor served by off only.
    - C003: 6 hits off / 6 threshold, shared 3 → overlap 3/8 (short list).
    """
    c001 = [f"m-c001-{i}" for i in range(1, 9)]
    c002_off = [f"m-c002-{i}" for i in range(1, 9)]
    c002_thr = [f"m-c002-{i}" for i in range(1, 6)] + [f"t-c002-{i}" for i in range(6, 9)]
    c003_off = [f"m-c003-{i}" for i in range(1, 7)]
    c003_thr = [f"m-c003-{i}" for i in range(1, 4)] + [f"t-c003-{i}" for i in range(4, 7)]
    decoy = [_mem_hit("code-decoy-c001", 99.0, "decoy/decoy.cs", "code decoy")]
    served: dict[str, dict[str, list[dict[str, Any]]]] = {
        "C001": {"off": c001, "threshold": list(c001)},
        "C002": {"off": c002_off, "threshold": c002_thr},
        "C003": {"off": c003_off, "threshold": c003_thr},
    }
    arms: dict[str, dict[str, Any]] = {}
    for arm in ("off", "threshold"):
        results: dict[str, dict[str, Any]] = {}
        for query in queries:
            memory = _memory_hits(query.id, served[query.id][arm])
            code = decoy if query.id == "C001" else []
            results[query.id] = {**p3.extract_results(_fake_response(memory, code)),
                                 "queryText": query.query}
        arms[arm] = {"arm": arm, "env": dict(p3.ARMS[arm]), "results": results}
    return arms["off"], arms["threshold"]


def _write_json(path: Path, payload: Any) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(payload, indent=1) + "\n", encoding="utf-8")
    return path


# ---------------------------------------------------------------------------
# Fake runners (no network, no subprocess)
# ---------------------------------------------------------------------------


class ChainCommitteeRunner:
    """P4 runner: schema-valid form per brief; chunk 3 graded 0; one garbage
    submission from grader 2 (exercises the re-ask path end to end)."""

    def __init__(self) -> None:
        self.calls: list[tuple[int, str]] = []
        self.garbage_pending = 1

    def run(self, brief: str, grader: Any) -> str:
        self.calls.append((grader.index, brief))
        if grader.index == 2 and self.garbage_pending:
            self.garbage_pending = 0
            return "this is not a form"
        chunks = [{"hash": h, "grade": "0" if i == 2 else "A",
                   "reason": "answers the query"}
                  for i, h in enumerate(HASH_RE.findall(brief))]
        form = {"slotId": re.search(r'slotId="([^"]+)"', brief).group(1),
                "queryId": re.search(r'queryId="([^"]+)"', brief).group(1),
                "arm": re.search(r'arm="([^"]+)"', brief).group(1),
                "chunks": chunks, "overall": "ok"}
        return json.dumps(form)


class ChainAbRunner:
    """P5 runner: deterministic per-grader picks first/second/first, one reason."""

    REASON = "The first list answers the query directly."

    def __init__(self) -> None:
        self.calls: list[list[str]] = []

    def __call__(self, argv: list[str]) -> str:
        self.calls.append(argv)
        pick = ["first", "second", "first"][(len(self.calls) - 1) % 3]
        return f"PICK: {pick}\nREASON: {self.REASON}"


# ---------------------------------------------------------------------------
# P6 bridges — derive P4/P5 input contracts from P3/P4 artifacts (no package
# logic re-implemented; the readers still validate what they receive)
# ---------------------------------------------------------------------------


def bridge_metrics_for_committee(arm_off: dict[str, Any], arm_threshold: dict[str, Any],
                                 metrics: dict[str, Any]) -> dict[str, Any]:
    """P3 artifacts → P4's load_metrics contract.

    Per-arm chunk lists come from p3.top8_hits — the top-8 MEMORY list in served
    order (P3's live correction: memory and code are never merged by ranking).
    Pair data (overlap, RBO, drop, backfill) is copied verbatim from metrics.json.
    """
    queries: list[dict[str, Any]] = []
    for qid, pair in metrics["pairs"].items():
        arms: dict[str, Any] = {}
        for arm, doc in (("off", arm_off), ("threshold", arm_threshold)):
            if qid not in doc["results"]:
                raise ValueError(f"{qid}: missing from {arm} arm results")
            arms[arm] = {"results": [
                {"hash": c["hash"], "snippet": c["snippet"],
                 "sourceFile": c.get("sourceFile"), "chunkIndex": c.get("chunkIndex")}
                for c in p3.top8_hits(doc["results"][qid])]
            }
        queries.append({
            "queryId": qid,
            "queryText": pair["queryText"],
            "projectId": pair["projectId"],
            "arms": arms,
            "pair": {"top8SetOverlap": pair["top8SetOverlap"], "rbo": pair["rbo"],
                     "drop": pair["drop"], "backfill": pair["backfill"]},
        })
    return {"queries": queries,
            "bridgedFrom": ["arm-off.json", "arm-threshold.json", "metrics.json"]}


def bridge_arm_for_ab(arm_doc: dict[str, Any], query_ids: list[str]) -> dict[str, Any]:
    """P3 arm JSON → P5's load_arm contract (memory top-8, served order, rank 1..8).

    Raises if a requested query serves ≠ 8 chunks — the caller must have excluded
    such queries via bridge_sample_for_ab first (P5's payload contract is 8/8).
    """
    queries: list[dict[str, Any]] = []
    for qid in query_ids:
        if qid not in arm_doc["results"]:
            raise ValueError(f"{qid}: missing from {arm_doc['arm']} arm results")
        chunks = p3.top8_hits(arm_doc["results"][qid])
        if len(chunks) != p5.CHUNKS_PER_LIST:
            raise ValueError(
                f"{qid}: arm {arm_doc['arm']!r} serves {len(chunks)} chunks; "
                f"the blind payload needs exactly {p5.CHUNKS_PER_LIST}")
        queries.append({
            "queryId": qid,
            "queryText": arm_doc["results"][qid]["queryText"],
            "results": [{"hash": c["hash"], "rank": rank, "snippet": c["snippet"]}
                        for rank, c in enumerate(chunks, 1)],
        })
    return {"arm": arm_doc["arm"], "env": arm_doc.get("env"), "queries": queries,
            "bridgedFrom": f"arm-{arm_doc['arm']}.json"}


def bridge_sample_for_ab(sample_doc: dict[str, Any], arm_off: dict[str, Any],
                         arm_threshold: dict[str, Any]
                         ) -> tuple[dict[str, Any], list[dict[str, Any]]]:
    """P4 sample.json → P5's load_sample contract (name'd trio, `sample` key).

    Queries serving ≠ 8 memory chunks in either arm cannot form a blind payload
    (P5's contract is exactly 8); they are excluded here and the exclusion is
    recorded in the derived sample header — reported, never silently dropped.
    """
    graders = [{"name": f"grader-{g['index']}", "provider": g["provider"],
                "model": g["model"]} for g in sample_doc["header"]["graders"]]
    sample: list[dict[str, Any]] = []
    excluded: list[dict[str, Any]] = []
    for entry in sample_doc["queries"]:
        qid = entry["queryId"]
        served = {arm: len(p3.top8_hits(doc["results"][qid]))
                  for arm, doc in (("off", arm_off), ("threshold", arm_threshold))}
        short = {arm: n for arm, n in served.items() if n != p5.CHUNKS_PER_LIST}
        if short:
            excluded.append({"queryId": qid, "stratum": entry.get("stratum"),
                             "served": served,
                             "reason": f"served {short} (the blind payload needs "
                                       f"{p5.CHUNKS_PER_LIST}/{p5.CHUNKS_PER_LIST})"})
        else:
            sample.append({"queryId": qid, "stratum": entry.get("stratum")})
    if not sample:
        raise ValueError("every sampled query serves <8 chunks — nothing to compare")
    doc = {"header": {"graders": graders, "excludedQueries": excluded,
                      "bridgedFrom": "sample.json"},
           "sample": sample}
    return doc, excluded


# ---------------------------------------------------------------------------
# Report assembler — every report number is derived here, from the artifacts
# ---------------------------------------------------------------------------


def _read_json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8"))


def _read_optional(path: Path) -> Any:
    return _read_json(path) if path.exists() else None


def _fmt(value: float | int | None, places: int = 3) -> str:
    return "n/a" if value is None else f"{value:.{places}f}"


def _fmt_overlap(count: int) -> str:
    return f"{count}/{SEARCH_LIMIT} ({count / SEARCH_LIMIT:.3f})"


def _anchor_cell(value: bool | None) -> str:
    return {True: "hit", False: "miss", None: "\u2014"}[value]


def _verdict(slot: dict[str, Any]) -> str:
    if slot["accepted"]:
        return "accepted"
    return f"exhausted({slot['exhaustReason']})"


def _mean(values: list[float]) -> float:
    return sum(values) / len(values)


def _top_counts(counter: Counter, limit: int = 15) -> dict[str, Any]:
    ranked = sorted(counter.items(), key=lambda kv: (-kv[1], kv[0]))
    return {"rows": [[key, count] for key, count in ranked[:limit]],
            "more": max(len(ranked) - limit, 0)}


def derive_numbers(artifact_dir: Path) -> dict[str, Any]:
    """Every number the report cites, derived from the archived artifacts alone.

    Optional artifacts render as n/a / blocked when absent: corpus-regenerated.json
    and run-metadata.json are live-run inputs; the committee and ab halves are
    absent when the H1 grader gate stopped the fleet (plan §P4-4c stop condition).
    """
    arm_off = _read_json(artifact_dir / "arm-off.json")
    arm_threshold = _read_json(artifact_dir / "arm-threshold.json")
    metrics = _read_json(artifact_dir / "metrics.json")
    corpus = _read_optional(artifact_dir / "corpus-regenerated.json")
    run_meta = _read_optional(artifact_dir / "run-metadata.json")
    h1_manifest = _read_optional(artifact_dir / "h1-smoke" / "forms-manifest.json")

    sample = _read_optional(artifact_dir / "sample.json")
    grades = _read_optional(artifact_dir / "committee-grades.json")
    committee = (_committee_numbers(sample, grades, committee_manifest=None)
                 if sample is not None and grades is not None else None)
    ab_raw = _read_optional(artifact_dir / "ab-results.json")
    ab = _ab_numbers(ab_raw) if ab_raw is not None else None
    h1 = None
    if h1_manifest is not None:
        kinds: dict[int, set[str]] = {}
        for entry in h1_manifest["forms"]:
            kinds.setdefault(entry["grader"], set()).add(entry["kind"])
        h1 = {"verdicts": {f"grader-{g}": ("valid" if "form" in seen else "no schema-valid form")
                           for g, seen in sorted(kinds.items())}}
    return {
        "mechanical": _mechanical_numbers(metrics),
        "committee": committee,
        "ab": ab,
        "h1": h1,
        "setup": _setup_numbers(arm_off, arm_threshold, corpus, run_meta),
        "budget": _budget_numbers(grades, ab, h1_manifest, run_meta),
    }


def _mechanical_numbers(metrics: dict[str, Any]) -> dict[str, Any]:
    pairs: dict[str, dict[str, Any]] = metrics["pairs"]
    rows: dict[str, list[Any]] = {}
    drop_counter: Counter = Counter()
    backfill_counter: Counter = Counter()
    file_targeted = anchors_off = anchors_thr = 0
    queries_with_drop = queries_with_backfill = 0
    for qid in sorted(pairs):
        pair = pairs[qid]
        auto = pair["expectedHashInTop8"]
        if pair["expectedHash"] is not None:
            file_targeted += 1
            anchors_off += 1 if auto["off"] else 0
            anchors_thr += 1 if auto["threshold"] else 0
        for entry in pair["drop"]:
            drop_counter[entry.get("sourceFile") or "(no sourceFile)"] += 1
        for entry in pair["backfill"]:
            backfill_counter[entry.get("sourceFile") or "(no sourceFile)"] += 1
        queries_with_drop += 1 if pair["drop"] else 0
        queries_with_backfill += 1 if pair["backfill"] else 0
        rows[qid] = [pair["projectId"], _fmt_overlap(pair["top8SetOverlapCount"]),
                     _fmt(pair["rbo"], 4), len(pair["drop"]), len(pair["backfill"]),
                     _anchor_cell(auto["off"]), _anchor_cell(auto["threshold"])]
    return {
        "rows": rows,
        "summary": {
            "pairCount": len(pairs),
            "meanOverlap": _fmt(_mean([p["top8SetOverlap"] for p in pairs.values()])),
            "meanRbo": _fmt(_mean([p["rbo"] for p in pairs.values()]), 4),
            "queriesWithDrop": queries_with_drop,
            "queriesWithBackfill": queries_with_backfill,
            "totalDropped": sum(drop_counter.values()),
            "totalBackfilled": sum(backfill_counter.values()),
            "fileTargeted": file_targeted,
            "anchorsHitOff": f"{anchors_off}/{file_targeted}",
            "anchorsHitThr": f"{anchors_thr}/{file_targeted}",
        },
        "dropBySourceFile": _top_counts(drop_counter),
        "backfillBySourceFile": _top_counts(backfill_counter),
    }


def _committee_numbers(sample: dict[str, Any], grades: dict[str, Any],
                       committee_manifest: dict[str, Any] | None) -> dict[str, Any]:
    slots = {(s["queryId"], s["arm"]): s for s in grades["slots"]}
    rows: dict[str, list[Any]] = {}
    for entry in sample["queries"]:
        qid = entry["queryId"]
        cells: list[Any] = [entry["stratum"], _fmt(entry["top8SetOverlap"])]
        for arm in ("off", "threshold"):
            if (qid, arm) not in slots:
                raise ValueError(f"slot {qid}__{arm} missing from committee-grades.json")
            slot = slots[(qid, arm)]
            cells += [len(slot["answerChunks"]) if slot["answerChunks"] is not None else None,
                      slot["rounds"], _verdict(slot)]
        rows[qid] = cells
    slot_list = grades["slots"]
    summary = {
        "totalSlots": len(slot_list),
        "accepted": sum(1 for s in slot_list if s["accepted"]),
        "exhausted": sum(1 for s in slot_list if s["exhausted"]),
        "acceptedFirstRound": sum(1 for s in slot_list
                                  if s["accepted"] and s["roundVerdicts"] == ["accepted"]),
        "acceptedAfterNudge": sum(1 for s in slot_list if s["accepted"]
                                  and s["replacements"] == 0 and s["rounds"] > 1),
        "acceptedAfterReplacement": sum(1 for s in slot_list
                                        if s["accepted"] and s["replacements"] > 0),
        "totalRounds": sum(s["rounds"] for s in slot_list),
        "totalNudges": sum(s["nudges"] for s in slot_list),
        "totalReplacements": sum(s["replacements"] for s in slot_list),
        "totalSubmissions": sum(s["submissions"] for s in slot_list),
        "totalReasks": sum(s["reasks"] for s in slot_list),
    }
    if committee_manifest is not None:
        payloads = sum(1 for e in committee_manifest["forms"] if e["kind"] == "payload")
        if payloads != summary["totalSubmissions"]:
            raise ValueError(
                f"forms-manifest.json payload count {payloads} != submissions "
                f"{summary['totalSubmissions']} — grader-call accounting is inconsistent")
    return {"rows": rows, "summary": summary,
            "composition": sample["header"]["composition"]}


def _ab_numbers(ab: dict[str, Any]) -> dict[str, Any]:
    rows: dict[str, list[Any]] = {}
    for query in ab["queries"]:
        rows[query["queryId"]] = [query["firstArm"], query["thresholdPicks"],
                                  query["abstentions"], _fmt(query["compScore"], 3)]
    scored = [q["compScore"] for q in ab["queries"] if q["compScore"] is not None]
    return {
        "rows": rows,
        "excluded": ab["header"].get("excludedQueries", []),
        "summary": {
            "queries": len(ab["queries"]),
            "graderCalls": ab["header"]["graderCalls"],
            "reAsks": ab["header"]["reAsks"],
            "abstentions": sum(q["abstentions"] for q in ab["queries"]),
            "scored": len(scored),
            "meanCompScore": _fmt(_mean(scored), 3) if scored else "n/a",
            "allAbstained": [q["queryId"] for q in ab["queries"] if q["compScore"] is None],
        },
        "distilled": {"coreSentences": ab["distilled"]["coreSentences"],
                      "frequencies": ab["distilled"]["frequencies"]},
    }


def _setup_numbers(arm_off: dict[str, Any], arm_threshold: dict[str, Any],
                   corpus: dict[str, Any] | None,
                   run_meta: dict[str, Any] | None) -> dict[str, Any]:
    return {
        "snapshotSha256": corpus["header"]["snapshotSha256"] if corpus else None,
        "corpusQueries": corpus["header"]["queryCount"] if corpus else None,
        "corpusSeed": corpus["header"].get("seed") if corpus else None,
        "copySha256": (run_meta or {}).get("copySha256"),
        "offEnv": arm_off.get("env"),
        "thresholdEnv": arm_threshold.get("env"),
    }


def _budget_numbers(grades: dict[str, Any] | None, ab_numbers: dict[str, Any] | None,
                    h1_manifest: dict[str, Any] | None,
                    run_meta: dict[str, Any] | None) -> dict[str, Any]:
    committee_calls = (sum(s["submissions"] for s in grades["slots"])
                       if grades is not None else None)
    committee_reasks = (sum(s["reasks"] for s in grades["slots"])
                        if grades is not None else None)
    h1_calls = None
    if h1_manifest is not None:
        h1_calls = sum(1 for e in h1_manifest["forms"] if e["kind"] == "payload")
    ab_calls = ab_numbers["summary"]["graderCalls"] if ab_numbers is not None else None
    ab_reasks = ab_numbers["summary"]["reAsks"] if ab_numbers is not None else None
    calls = [c for c in (h1_calls, committee_calls, ab_calls) if c is not None]
    wall = (run_meta or {}).get("wallClock")
    return {
        "h1Calls": h1_calls,
        "committeeCalls": committee_calls,
        "committeeReasks": committee_reasks,
        "abCalls": ab_calls,
        "abReAsks": ab_reasks,
        "totalCalls": sum(calls) if calls else None,
        "wallClock": wall,
        "copySha256": (run_meta or {}).get("copySha256"),
    }


def _anchor(name: str, sources: str, lines: list[str]) -> str:
    body = "\n".join(lines)
    return f"<!-- numbers:{name} -->\nSources: {sources}\n{body}\n<!-- /numbers:{name} -->"


def _table(header: list[str], rows: list[list[Any]]) -> list[str]:
    out = ["| " + " | ".join(header) + " |", "|" + "---|" * len(header)]
    out += ["| " + " | ".join(str(cell) for cell in row) + " |" for row in rows]
    return out


def render_number_blocks(numbers: dict[str, Any]) -> dict[str, str]:
    """The anchored number blocks the report embeds verbatim (AC6.1 compares
    these byte-for-byte against a recomputation from the artifacts)."""
    blocks: dict[str, str] = {}
    setup = numbers["setup"]
    blocks["setup"] = _anchor("setup", "corpus-regenerated.json, run-metadata.json, "
                              "arm-off.json, arm-threshold.json", _table(
        ["Item", "Value"],
        [["Snapshot sha256 (H3)", setup["snapshotSha256"] or "n/a"],
         ["Corpus queries", setup["corpusQueries"] if setup["corpusQueries"] is not None else "n/a"],
         ["Corpus seed", setup["corpusSeed"] if setup["corpusSeed"] is not None else "n/a"],
         ["Bank copy sha256", setup["copySha256"] or "n/a"],
         ["Arm `off` env", json.dumps(setup["offEnv"], sort_keys=True)],
         ["Arm `threshold` env", json.dumps(setup["thresholdEnv"], sort_keys=True)]]))

    mech = numbers["mechanical"]
    s = mech["summary"]
    blocks["mechanical-summary"] = _anchor(
        "mechanical-summary", "metrics.json", _table(
            ["Metric", "Value"],
            [["Pairs (n)", s["pairCount"]],
             ["Mean top-8 set overlap", s["meanOverlap"]],
             ["Mean RBO (p=0.9, truncated)", s["meanRbo"]],
             ["Queries with drops", s["queriesWithDrop"]],
             ["Queries with backfill", s["queriesWithBackfill"]],
             ["Chunks dropped by threshold (total)", s["totalDropped"]],
             ["Chunks backfilled by threshold (total)", s["totalBackfilled"]],
             ["File-targeted queries (with anchor)", s["fileTargeted"]],
             ["Anchor in top-8, arm off (H2 auto-grade)", s["anchorsHitOff"]],
             ["Anchor in top-8, arm threshold (H2 auto-grade)", s["anchorsHitThr"]]]))

    blocks["mechanical"] = _anchor(
        "mechanical", "metrics.json", _table(
            ["Query", "Project", "Top-8 overlap", "RBO p=0.9", "Drop", "Backfill",
             "Anchor off", "Anchor thr"],
            [[qid] + row for qid, row in mech["rows"].items()]))

    def merge_counters(drop: dict[str, Any], backfill: dict[str, Any]) -> list[list[str]]:
        merged: dict[str, list[str]] = {}
        for key, count in drop["rows"]:
            merged[key] = [str(count), "0"]
        for key, count in backfill["rows"]:
            merged.setdefault(key, ["0", "0"])[1] = str(count)
        rows = [[key, cells[0], cells[1]] for key, cells in
                sorted(merged.items(), key=lambda kv: (-int(kv[1][0]), -int(kv[1][1]), kv[0]))]
        more = drop["more"] + backfill["more"]
        if more:
            rows.append([f"\u2026 {more} more (metrics.json)", "", ""])
        return rows

    blocks["drop-backfill-sourcefiles"] = _anchor(
        "drop-backfill-sourcefiles", "metrics.json", _table(
            ["Source file", "Dropped by threshold", "Backfilled by threshold"],
            merge_counters(mech["dropBySourceFile"], mech["backfillBySourceFile"])))

    committee = numbers["committee"]
    if committee is None:
        verdict_rows = [[grader, verdict]
                        for grader, verdict in (numbers.get("h1") or {}).get("verdicts", {}).items()]
        blocked_rows = [["State", "BLOCKED \u2014 H1 stop condition (plan \u00a7P4-4c)"]]
        blocked_rows += [[grader, verdict] for grader, verdict in verdict_rows]
        blocked_rows.append(["Slots run", "0 of 32 \u2014 never started, no partial state"])
        blocks["committee-summary"] = _anchor(
            "committee-summary", "h1-smoke/forms-manifest.json", _table(
                ["Item", "Value"],
                [["State", "BLOCKED \u2014 committee fleet not run"],
                 ["Reason", "H1 grader-gate failure: the trio is incomplete "
                  "(owner decision on a substitute pending)"]]))
        blocks["committee"] = _anchor(
            "committee", "h1-smoke/forms-manifest.json", _table(
                ["Item", "Value"], blocked_rows))
    else:
        cs = committee["summary"]
        blocks["committee-summary"] = _anchor(
            "committee-summary", "committee-grades.json, sample.json, metrics-committee.json", _table(
            ["Metric", "Value"],
            [["Slots (queries x arms)", cs["totalSlots"]],
             ["Slots accepted (3/3 unanimity)", cs["accepted"]],
             ["\u2014 accepted in round 1", cs["acceptedFirstRound"]],
             ["\u2014 accepted after nudge", cs["acceptedAfterNudge"]],
             ["\u2014 accepted after replacement", cs["acceptedAfterReplacement"]],
             ["Slots exhausted (reported, never skipped)", cs["exhausted"]],
             ["Committee rounds total", cs["totalRounds"]],
             ["Nudge rounds total", cs["totalNudges"]],
             ["Replacements total", cs["totalReplacements"]],
             ["Grader submissions total (incl. re-asks)", cs["totalSubmissions"]],
             ["Malformed-form re-asks total", cs["totalReasks"]],
             ["Composition (changed picked / available)",
              f"{committee['composition']['pickedChanged']} / {committee['composition']['changedAvailable']}"],
             ["Composition (controls picked / available)",
              f"{committee['composition']['pickedControls']} / {committee['composition']['controlsAvailable']}"],
             ["Backfilled sample", str(committee["composition"]["backfilled"])]]))
        blocks["committee"] = _anchor(
            "committee", "committee-grades.json, sample.json, metrics-committee.json", _table(
                ["Query", "Stratum", "Overlap", "off: answer chunks", "off: rounds",
                 "off: verdict", "thr: answer chunks", "thr: rounds", "thr: verdict"],
                [[qid] + ["\u2014" if c is None else str(c) for c in row]
                 for qid, row in committee["rows"].items()]))

    abn = numbers["ab"]
    if abn is None:
        blocks["ab"] = _anchor(
            "ab", "h1-smoke/forms-manifest.json", _table(
                ["Item", "Value"],
                [["State", "BLOCKED \u2014 blind A/B not run (same H1 stop: the "
                  "grader trio is incomplete, comp_score /3 undefined)"]]))
        blocks["ab-summary"] = _anchor(
            "ab-summary", "h1-smoke/forms-manifest.json", _table(
                ["Item", "Value"],
                [["State", "BLOCKED \u2014 not run"],
                 ["Grader calls", "0"]]))
        blocks["ab-distilled"] = _anchor(
            "ab-distilled", "h1-smoke/forms-manifest.json", _table(
                ["Core sentence (deterministic distillation)", "Frequency"],
                [["\u2014 no blind pass ran, no reasons to distil", ""]]))
    else:
        asx = abn["summary"]
        ab_rows = [[qid, row[0], str(row[1]), str(row[2]), row[3]]
                   for qid, row in abn["rows"].items()]
        for exc in abn["excluded"]:
            ab_rows.append([exc["queryId"], "\u2014 excluded \u2014", "", "",
                            f"served off={exc['served']['off']}, threshold={exc['served']['threshold']}"])
        blocks["ab"] = _anchor(
            "ab", "ab-results.json, sample-ab.json, arm-off.ab.json, arm-threshold.ab.json", _table(
                ["Query", "List shown first", "Threshold picks", "Abstentions", "comp_score"],
                ab_rows))
        blocks["ab-summary"] = _anchor(
            "ab-summary", "ab-results.json", _table(
                ["Metric", "Value"],
                [["Queries compared", asx["queries"]],
                 ["Grader calls", asx["graderCalls"]],
                 ["Re-asks (malformed \u2192 abstention)", asx["reAsks"]],
                 ["Abstentions total", asx["abstentions"]],
                 ["Queries scored", asx["scored"]],
                 ["Mean comp_score (scored queries)", asx["meanCompScore"]],
                 ["All-abstained (unscored)", ", ".join(asx["allAbstained"]) or "none"]]))
        blocks["ab-distilled"] = _anchor(
            "ab-distilled", "ab-results.json", _table(
                ["Core sentence (deterministic distillation)", "Frequency"],
                [[sentence, str(abn["distilled"]["frequencies"][sentence])]
                 for sentence in abn["distilled"]["coreSentences"]]))

    budget = numbers["budget"]
    wall = budget["wallClock"] or {}
    budget_sources = ("h1-smoke/forms-manifest.json, run-metadata.json"
                      if numbers["committee"] is None else
                      "committee-grades.json, ab-results.json, "
                      "h1-smoke/forms-manifest.json, run-metadata.json")
    blocks["budget"] = _anchor(
        "budget", budget_sources, _table(
            ["Item", "Value"],
            [["H1 smoke grader calls", budget["h1Calls"] if budget["h1Calls"] is not None else "n/a"],
             ["Committee grader calls (submissions)", _fmt(budget["committeeCalls"], 0)],
             ["Committee malformed-form re-asks", _fmt(budget["committeeReasks"], 0)],
             ["Blind A/B grader calls", _fmt(budget["abCalls"], 0)],
             ["Blind A/B re-asks", _fmt(budget["abReAsks"], 0)],
             ["Total grader calls", _fmt(budget["totalCalls"], 0)],
             ["Wall clock: bank copy", _fmt(wall.get("copy"), 1) + "s" if wall.get("copy") is not None else "n/a"],
             ["Wall clock: H1 smoke", _fmt(wall.get("h1"), 1) + "s" if wall.get("h1") is not None else "n/a"],
             ["Wall clock: mechanical (P3)", _fmt(wall.get("mechanical"), 1) + "s" if wall.get("mechanical") is not None else "n/a"],
             ["Wall clock: committee (P4)", _fmt(wall.get("committee"), 1) + "s" if wall.get("committee") is not None else "n/a"],
             ["Wall clock: blind A/B (P5)", _fmt(wall.get("ab"), 1) + "s" if wall.get("ab") is not None else "n/a"],
             ["Wall clock: total", _fmt(wall.get("total"), 1) + "s" if wall.get("total") is not None else "n/a"]]))
    return blocks


def build_artifact_manifest(artifact_dir: Path) -> dict[str, Any]:
    """sha256 + size of every archived artifact file (the manifest excludes itself)."""
    entries = []
    for path in sorted(artifact_dir.rglob("*")):
        if not path.is_file() or path.name == MANIFEST_NAME or path.name.startswith("."):
            continue
        entries.append({"path": str(path.relative_to(artifact_dir)),
                        "bytes": path.stat().st_size,
                        "sha256": hashlib.sha256(path.read_bytes()).hexdigest()})
    return {"artifacts": entries}


# ---------------------------------------------------------------------------
# AC6.2 — fixture chain (-k chain)
# ---------------------------------------------------------------------------


def test_chain_corpus_to_report_numbers(tmp_path):
    # 1. synthetic corpus → P3 spec-builder: every call carries its own projectId
    corpus_path = _write_json(tmp_path / "corpus.json", _synthetic_corpus())
    queries = p3.load_corpus(corpus_path)
    assert [q.id for q in queries] == ["C001", "C002", "C003"]
    calls = p3.build_search_calls(queries)
    assert [c["arguments"]["projectId"] for c in calls] == ["proj-a", "proj-b", "proj-a"]
    assert all(c["name"] == "memory_search" and c["arguments"]["limit"] == SEARCH_LIMIT
               for c in calls)
    assert len({c["arguments"]["sessionId"] for c in calls}) == 1

    # 2. fake arm JSONs via P3's own response extraction (writer shape = P3's)
    arm_off, arm_threshold = _fixture_arm_documents(queries)
    _write_json(tmp_path / "arm-off.json", arm_off)
    _write_json(tmp_path / "arm-threshold.json", arm_threshold)
    assert len(arm_off["results"]["C001"]["memory"][2]["snippet"]) == p3.SNIPPET_MAX

    # 3. P3 metrics — the P3 live-correction constraint: memory top-8 only
    metrics = p3.compute_metrics(arm_off, arm_threshold, queries)
    _write_json(tmp_path / "metrics.json", metrics)
    pairs = metrics["pairs"]
    assert pairs["C001"]["top8SetOverlapCount"] == 8  # code decoy did NOT enter
    assert pairs["C002"]["top8SetOverlapCount"] == 5
    assert pairs["C003"]["top8SetOverlapCount"] == 3
    assert pairs["C001"]["rbo"] == pytest.approx(0.5126, abs=1e-3)
    assert pairs["C002"]["rbo"] == pytest.approx(reference_rbo(
        [f"m-c002-{i}" for i in range(1, 9)],
        [f"m-c002-{i}" for i in range(1, 6)] + [f"t-c002-{i}" for i in range(6, 9)]))
    assert pairs["C002"]["drop"] == [
        {"hash": f"m-c002-{i}", "sourceFile": f"docs/C002-m-c002-{i}.md"}
        for i in range(6, 9)]
    assert pairs["C002"]["backfill"] == [
        {"hash": f"t-c002-{i}", "sourceFile": f"docs/C002-t-c002-{i}.md"}
        for i in range(6, 9)]
    assert pairs["C002"]["expectedHashInTop8"] == {"off": True, "threshold": False}
    assert pairs["C003"]["expectedHashInTop8"] == {"off": None, "threshold": None}

    # 4. P6 bridge: P3 artifacts → P4's load_metrics contract
    metrics_doc = bridge_metrics_for_committee(arm_off, arm_threshold, metrics)
    _write_json(tmp_path / "metrics-committee.json", metrics_doc)
    bridged_c001 = metrics_doc["queries"][0]
    assert [c["hash"] for c in bridged_c001["arms"]["off"]["results"]] == \
           [f"m-c001-{i}" for i in range(1, 9)]  # memory served order, no decoy
    assert bridged_c001["pair"]["top8SetOverlap"] == 1.0

    # 5. P4 committee on the bridged metrics (FakeRunner, no network)
    committee_runner = ChainCommitteeRunner()
    grades = p4.run_committee(tmp_path / "metrics-committee.json", tmp_path,
                              seed=p4.SEED, runner=committee_runner)
    assert len(grades["slots"]) == 6  # 3 queries x 2 arms

    # 6. P6 bridge: P4 sample.json + P3 arm JSONs → P5's contracts; the ab
    #    bridge excludes C003 (serves 6/8) — recorded, never silent
    sample_doc = _read_json(tmp_path / "sample.json")
    ab_sample, excluded = bridge_sample_for_ab(sample_doc, arm_off, arm_threshold)
    _write_json(tmp_path / "sample-ab.json", ab_sample)
    ab_qids = [entry["queryId"] for entry in ab_sample["sample"]]
    _write_json(tmp_path / "arm-off.ab.json", bridge_arm_for_ab(arm_off, ab_qids))
    _write_json(tmp_path / "arm-threshold.ab.json", bridge_arm_for_ab(arm_threshold, ab_qids))
    assert [e["queryId"] for e in excluded] == ["C003"]
    assert ab_qids == ["C002", "C001"]

    # 7. P5 blind ab on the bridged inputs (FakeRunner, no network)
    ab_runner = ChainAbRunner()
    ab = p5.run_ab(tmp_path / "sample-ab.json", tmp_path / "arm-threshold.ab.json",
                   tmp_path / "arm-off.ab.json", seed=p5.DEFAULT_SEED,
                   runner=ab_runner, out_path=tmp_path / "ab-results.json",
                   forms_dir=tmp_path / "ab-forms")
    assert len(ab["queries"]) == 2

    # 8. report assembler: every report number equals the fixture's number
    numbers = derive_numbers(tmp_path)
    mech = numbers["mechanical"]
    assert mech["summary"]["pairCount"] == 3
    assert mech["summary"]["meanOverlap"] == "0.667"  # (1.000 + 0.625 + 0.375) / 3
    assert mech["summary"]["queriesWithDrop"] == 2 and mech["summary"]["totalDropped"] == 6
    assert mech["summary"]["totalBackfilled"] == 6
    assert mech["summary"]["anchorsHitOff"] == "2/2"
    assert mech["summary"]["anchorsHitThr"] == "1/2"  # C002's anchor is threshold-dropped
    assert mech["rows"]["C001"] == [
        "proj-a", "8/8 (1.000)", _fmt(reference_rbo(
            [f"m-c001-{i}" for i in range(1, 9)],
            [f"m-c001-{i}" for i in range(1, 9)]), 4), 0, 0, "hit", "hit"]
    assert mech["rows"]["C003"] == [
        "proj-a", "3/8 (0.375)", _fmt(reference_rbo(
            [f"m-c003-{i}" for i in range(1, 7)],
            [f"m-c003-{i}" for i in range(1, 4)] + [f"t-c003-{i}" for i in range(4, 7)]), 4),
        3, 3, "\u2014", "\u2014"]

    committee = numbers["committee"]
    cs = committee["summary"]
    assert cs["totalSlots"] == 6 and cs["accepted"] == 6 and cs["exhausted"] == 0
    assert cs["acceptedFirstRound"] == 6 and cs["acceptedAfterNudge"] == 0
    assert cs["acceptedAfterReplacement"] == 0
    assert cs["totalSubmissions"] == 19  # 18 forms + 1 malformed (re-asked)
    assert cs["totalReasks"] == 1
    # row = [stratum, overlap, off: chunks, off: rounds, off: verdict,
    #        thr: chunks, thr: rounds, thr: verdict]
    assert len(committee["rows"]["C001"]) == 8
    assert committee["rows"]["C001"][2] == 7 and committee["rows"]["C001"][5] == 7
    assert committee["rows"]["C003"][2] == 5 and committee["rows"]["C003"][5] == 5
    assert all(row[3] == 1 and row[6] == 1 for row in committee["rows"].values())

    # the ab bridge excludes C003 (serves 6/8) — reported, never silent
    assert list(numbers["ab"]["rows"]) == ["C002", "C001"]
    mapping = p5.assign_positions(list(numbers["ab"]["rows"]), p5.DEFAULT_SEED)
    for qid, row in numbers["ab"]["rows"].items():
        x = (2 if mapping[qid] == "threshold" else 1)  # picks first/second/first
        assert row == [mapping[qid], x, 0, _fmt(x / 3, 3)]
    assert numbers["ab"]["summary"]["graderCalls"] == 6
    assert numbers["ab"]["summary"]["abstentions"] == 0
    assert numbers["ab"]["distilled"]["coreSentences"] == [ChainAbRunner.REASON]
    assert numbers["ab"]["distilled"]["frequencies"] == {ChainAbRunner.REASON: 6}

    # the blind payload carries the memory section in served order — never the
    # code decoy, never a re-ranking (P3's live correction, enforced end to end)
    payload = (tmp_path / "ab-forms" / "C001.payload.txt").read_text(encoding="utf-8")
    assert "code-decoy-c001" not in payload
    payload_hashes = re.findall(r"^\d+\. \[([^\]]+)\]", payload, re.MULTILINE)
    assert payload_hashes == [f"m-c001-{i}" for i in range(1, 9)] * 1 + \
           [f"m-c001-{i}" for i in range(1, 9)]

    blocks = render_number_blocks(numbers)
    assert len(_block_rows(blocks["mechanical"])) == 3
    assert len(_block_rows(blocks["committee"])) == 3

    # the manifest mechanism the trace test relies on: every file archived+hashed
    manifest = build_artifact_manifest(tmp_path)
    on_disk = {str(p.relative_to(tmp_path)) for p in tmp_path.rglob("*")
               if p.is_file() and not p.name.startswith(".")}
    assert {e["path"] for e in manifest["artifacts"]} == on_disk - {MANIFEST_NAME}
    for entry in manifest["artifacts"]:
        file_path = tmp_path / entry["path"]
        assert hashlib.sha256(file_path.read_bytes()).hexdigest() == entry["sha256"]


# ---------------------------------------------------------------------------
# AC6.1 — traceability (-k trace) — needs the 6b artifacts + report
# ---------------------------------------------------------------------------


def test_trace_report_numbers_cite_archived_json():
    if not REPORT_PATH.exists():
        pytest.skip("report not written yet (6b end-to-end run)")
    assert_manifest_and_report_trace(REPORT_PATH, ARTIFACT_DIR)


def assert_manifest_and_report_trace(report_path: Path, artifact_dir: Path) -> None:
    """AC6.1: the report's anchored blocks are byte-equal to a recomputation from
    the archived artifacts; manifest sha256s match disk; no orphan citations."""
    manifest_path = artifact_dir / MANIFEST_NAME
    assert manifest_path.exists(), (
        f"{MANIFEST_NAME} must exist next to the report — run the `tables` CLI step")
    manifest = {entry["path"]: entry
                for entry in _read_json(manifest_path)["artifacts"]}
    # every manifest entry must match the on-disk bytes…
    for rel, entry in manifest.items():
        file_path = artifact_dir / rel
        assert file_path.is_file(), f"manifest entry missing on disk: {rel}"
        assert hashlib.sha256(file_path.read_bytes()).hexdigest() == entry["sha256"], rel
    # …and every archived file must have a manifest entry (self excluded)
    on_disk = {str(p.relative_to(artifact_dir)) for p in artifact_dir.rglob("*")
               if p.is_file() and not p.name.startswith(".")}
    assert on_disk - {MANIFEST_NAME} == set(manifest), "unarchived artifact files"

    report = report_path.read_text(encoding="utf-8")
    numbers = derive_numbers(artifact_dir)
    for name, block in render_number_blocks(numbers).items():
        assert block in report, f"report block {name!r} is stale — re-run the `tables` CLI"
    # every source artifact named in the report must be archived…
    for source in set(re.findall(r"^Sources: (.+)$", report, re.MULTILINE)):
        for artifact in source.split(", "):
            assert artifact in manifest, f"report cites unarchived artifact: {artifact}"
    # …and every top-level archived artifact must be cited somewhere
    top_level = {rel for rel in manifest if "/" not in rel} - {MANIFEST_NAME}
    for rel in sorted(top_level):
        assert rel in report, f"archived artifact never cited in the report: {rel}"


# ---------------------------------------------------------------------------
# AC6.3 — report completeness (-k report) — needs the 6b artifacts + report
# ---------------------------------------------------------------------------

SECTION_HEADINGS = [
    "1. Setup and snapshot",
    "2. Mechanical results (n=100)",
    "3. Committee grades (n=16)",
    "4. Blind A/B comp_score",
    "5. Budget spent",
    "6. Findings, limitations and hypotheses",
]


def test_report_sections_complete():
    if not REPORT_PATH.exists():
        pytest.skip("report not written yet (6b end-to-end run)")
    assert_report_complete(REPORT_PATH, ARTIFACT_DIR)


def assert_report_complete(report_path: Path, artifact_dir: Path) -> None:
    """AC6.3, state-conditional on the report's eval-state marker.

    - `complete`: all six sections, snapshot sha256, budget spent, mechanical
      n=100, committee n=16, comp_score = x/(3−abstentions) on the archived
      results — the strict completed-run contract.
    - `blocked-h1`: the fleet never ran (H1 grader-gate stop, plan §P4-4c);
      the mechanical/budget sections must still hold real numbers (n=100) and
      the committee/ab sections must be explicitly BLOCKED.
    """
    report = report_path.read_text(encoding="utf-8")
    state_match = re.search(r"<!-- eval-state: ([a-z0-9-]+) -->", report)
    assert state_match is not None, "report must carry an eval-state marker"
    state = state_match.group(1)

    numbers = derive_numbers(artifact_dir)
    blocks = render_number_blocks(numbers)

    if state == "complete":
        for heading in SECTION_HEADINGS:
            assert f"## {heading}" in report, f"missing report section: {heading}"
        snapshot = re.search(r"\b([0-9a-f]{64})\b", report)
        assert snapshot is not None, "snapshot sha256 (H3) missing from the report"
        corpus_header = _read_json(artifact_dir / "corpus-regenerated.json")["header"]
        assert snapshot.group(1) == corpus_header["snapshotSha256"]
        assert "grader calls" in report and "wall clock" in report
        assert len(_block_rows(blocks["mechanical"])) == 100
        assert len(_block_rows(blocks["committee"])) == 16
        # comp_score = x/(3 − abstentions), recomputed from the archived ab results
        ab = _read_json(artifact_dir / "ab-results.json")
        for query in ab["queries"]:
            assert query["compScore"] is None or query["compScore"] == pytest.approx(
                query["thresholdPicks"] / (3 - query["abstentions"]))
    elif state == "blocked-h1":
        assert numbers["committee"] is None and numbers["ab"] is None
        for heading in (SECTION_HEADINGS[0], SECTION_HEADINGS[1],
                        SECTION_HEADINGS[4], SECTION_HEADINGS[5]):
            assert f"## {heading}" in report, f"missing report section: {heading}"
        snapshot = re.search(r"\b([0-9a-f]{64})\b", report)
        assert snapshot is not None, "snapshot sha256 (H3) missing from the report"
        corpus_header = _read_json(artifact_dir / "corpus-regenerated.json")["header"]
        assert snapshot.group(1) == corpus_header["snapshotSha256"]
        assert len(_block_rows(blocks["mechanical"])) == 100
        assert "BLOCKED" in blocks["committee"] and "BLOCKED" in blocks["ab"]
        assert "grader calls" in report and "wall clock" in report
    else:
        raise AssertionError(f"unknown eval-state marker: {state!r}")


def _block_rows(block: str) -> list[list[str]]:
    rows = []
    for line in block.splitlines():
        if not line.startswith("|"):
            continue
        if set(line) <= {"|", "-", " "}:
            continue  # separator
        rows.append([cell.strip() for cell in line.strip("|").split("|")])
    return rows[1:]  # drop the header row


# ---------------------------------------------------------------------------
# AC6.4 — merge hygiene (-k merge_hygiene)
# ---------------------------------------------------------------------------


def _git(*args: str, cwd: Path | None = None) -> str:
    proc = subprocess.run(["git", *args], capture_output=True, text=True,
                          check=False, cwd=cwd or REPO_ROOT)
    assert proc.returncode == 0, f"git {' '.join(args)} failed: {proc.stderr.strip()}"
    return proc.stdout


def _current_branch() -> str:
    return _git("rev-parse", "--abbrev-ref", "HEAD").strip()


def _merge_allowed_pathspecs() -> list[str]:
    """Every path the task branch actually changed, except `src/` (plan §6c merge
    policy: the PR merges scripts/tests/docs only).

    Derived from the merge-base diff, not from `everything tracked`: origin/main
    keeps moving while the task branch sits on its base, and a broad checkout
    would silently revert main's newer scaffolding under paths the task never
    touched.
    """
    base = _git("merge-base", "HEAD", "origin/main").strip()
    changed = _git("diff", "--name-only", f"{base}..HEAD", "--", ".",
                   ":(exclude)src").splitlines()
    top_level = sorted({path.split("/")[0] for path in changed if path})
    pathspecs = [entry for entry in top_level if entry != "src"]
    assert pathspecs and "src" not in pathspecs
    return pathspecs


def test_merge_hygiene_pr_branch_has_empty_src_diff(tmp_path):
    branch = _current_branch()
    if branch == "main" or branch.endswith("/main"):
        pytest.skip("on main the src/ assertion is trivially true — gate is meaningless")
    base = "origin/main" if _git("rev-parse", "--verify", "--quiet", "origin/main") else "main"
    pathspecs = _merge_allowed_pathspecs()
    worktree = Path(tempfile.mkdtemp(prefix="merge-hygiene-pr-"))
    try:
        _git("worktree", "add", "--detach", str(worktree), base)
        # the 6c mechanic: path-scoped checkout of every changed path except src/
        _git("checkout", branch, "--", *pathspecs, cwd=worktree)
        src_diff = _git("diff", "--name-only", "HEAD", "--", "src/", cwd=worktree)
        assert src_diff == "", f"the PR would carry src/ changes:\n{src_diff}"
        # non-vacuous: the same PR construction must carry the task's real content
        assert _git("diff", "--name-only", "HEAD", "--", "scripts/", "docs/",
                    cwd=worktree) != ""
    finally:
        subprocess.run(["git", "worktree", "remove", "--force", str(worktree)],
                       capture_output=True, text=True, check=False, cwd=REPO_ROOT)


# ---------------------------------------------------------------------------
# CLI — live-run tooling (bridge + tables); never runs under pytest
# ---------------------------------------------------------------------------


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="P6 bridge/report tooling (plan §P6)")
    sub = parser.add_subparsers(dest="command", required=True)
    bridge = sub.add_parser("bridge", help="derive P4/P5 input contracts from P3/P4 artifacts")
    bridge.add_argument("--artifact-dir", type=Path, default=ARTIFACT_DIR)
    tables = sub.add_parser("tables", help="write artifact-manifest.json + print report tables")
    tables.add_argument("--artifact-dir", type=Path, default=ARTIFACT_DIR)
    args = parser.parse_args(argv)
    if args.command == "bridge":
        return _cli_bridge(args.artifact_dir)
    return _cli_tables(args.artifact_dir)


def _cli_bridge(artifact_dir: Path) -> int:
    arm_off = _read_json(artifact_dir / "arm-off.json")
    arm_threshold = _read_json(artifact_dir / "arm-threshold.json")
    metrics = _read_json(artifact_dir / "metrics.json")
    sample = _read_json(artifact_dir / "sample.json")

    metrics_doc = bridge_metrics_for_committee(arm_off, arm_threshold, metrics)
    _write_json(artifact_dir / "metrics-committee.json", metrics_doc)
    ab_sample, excluded = bridge_sample_for_ab(sample, arm_off, arm_threshold)
    _write_json(artifact_dir / "sample-ab.json", ab_sample)
    ab_qids = [entry["queryId"] for entry in ab_sample["sample"]]
    _write_json(artifact_dir / "arm-off.ab.json", bridge_arm_for_ab(arm_off, ab_qids))
    _write_json(artifact_dir / "arm-threshold.ab.json",
                bridge_arm_for_ab(arm_threshold, ab_qids))

    print(f"metrics-committee.json: {len(metrics_doc['queries'])} queries")
    print(f"sample-ab.json: {len(ab_qids)} queries for the blind pass")
    for exc in excluded:
        print(f"  EXCLUDED {exc['queryId']} ({exc['stratum']}): {exc['reason']}")
    return 0


def _cli_tables(artifact_dir: Path) -> int:
    manifest = build_artifact_manifest(artifact_dir)
    manifest_path = artifact_dir / MANIFEST_NAME
    manifest_path.write_text(json.dumps(manifest, indent=1) + "\n", encoding="utf-8")
    print(f"{MANIFEST_NAME}: {len(manifest['artifacts'])} files hashed")
    numbers = derive_numbers(artifact_dir)
    print("\n\n".join(render_number_blocks(numbers).values()))
    return 0


if __name__ == "__main__":  # pragma: no cover
    raise SystemExit(main())
