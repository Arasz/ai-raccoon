#!/usr/bin/env python3
"""Blind A/B pass (P5) of the threshold-committee eval plan (2026-09-09).

Per sampled query this script renders a blind payload — the query plus the two
arms' 8-chunk lists, positions labelled only ``first``/``second`` — asks each of
the three graders for a forced choice, and restores the seeded
position→arm mapping only after grading. The pair is arbitrary: each arm's name
is read from its own ``arm`` JSON, so custom pairs (``chunk254``/``chunk1022``)
round-trip without the constants naming them. The mapping is archived solely in
``ab-results.json``; the payloads (archived under ``ab-forms/``) never name the
arms.

Input contracts (read as JSON; the producing packages are never imported):

- P4 ``sample.json``: ``{"header": {"graders": [{"provider": str|null,
  "model": str|null}, ...]}, "sample": [{"queryId": str}, ...]}`` — grader
  order is the trio order (grader-1 = session default).
- P3 ``arm-<name>.json``: ``{"arm": str, "queries": [{"queryId": str,
  "queryText": str, "results": [{"hash": str, "rank": int, "snippet": str},
  ...exactly 8, in rank order]}]}`` — extra keys on any object are ignored.

Grader CLI: ``pi -p --no-session [--provider P --model M] <payload>``, one
fresh headless session per call; ``--runner`` switches the CLI prefix. A
malformed response (no parseable ``PICK: first|second`` line) is re-asked once,
then recorded as an abstention — abstentions are reported and excluded from
the denominator (``comp_score = x/(3 − abstentions)``, x = graders whose pick
mapped to the threshold arm), never silently scored. Distillation of grader
reasons is deterministic Python (first-sentence extraction, dedup,
frequency-rank top-k) with zero runner invocations.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import random
import re
import shlex
import os
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from retrieval_tuning import repo_data  # noqa: E402

ARM_THRESHOLD = "threshold"
ARM_OFF = "off"
CHUNKS_PER_LIST = 8
DEFAULT_SEED = repo_data.GRADERS["DEFAULT_SEED"]
DEFAULT_RUNNER = "pi -p"
DEFAULT_TOP_K = 5

# The grader trio, equal by contract to P4 sample.json's header.graders
# (owner decision: #1 session default, #2 muse-spark, #3 mimo-v2.5-pro; #3 re-picked
# as openrouter/xiaomi/mimo-v2.5-pro after H1 auth failure on the native xiaomi provider).
# P3 AC2: the trio lives in data/graders.json.
TRIO = tuple(dict(grader) for grader in repo_data.GRADERS["TRIO"])

# The fixed payload surface — must never hint the arms (AC5.1). Everything a
# payload shares across queries and graders lives in these constants.
INSTRUCTION_BLOCK = """You are comparing two ranked lists of retrieved chunks for the query below.
Both lists were produced by retrieval variants of the same system over the same corpus.
Decide which list better answers the query as a whole, considering chunk relevance,
coverage and ranking quality.

Respond in exactly this format:
PICK: first
REASON: <one sentence>"""
QUERY_LABEL = "Query: "
FIRST_HEADER = "=== first list ==="
SECOND_HEADER = "=== second list ==="
CHUNK_FMT = "{rank}. [{hash}] {snippet}"

PICK_RE = re.compile(r"^PICK:\s*(first|second)\s*$", re.MULTILINE | re.IGNORECASE)
REASON_RE = re.compile(r"^REASON:\s*(.+)$", re.MULTILINE | re.IGNORECASE | re.DOTALL)


def other_arm(arm: str, arm_a: str = ARM_THRESHOLD, arm_b: str = ARM_OFF) -> str:
    """The other arm of the pair (threshold ↔ off by default)."""
    return arm_b if arm == arm_a else arm_a


def assign_positions(query_ids: list[str], seed: int,
                     arm_a: str = ARM_THRESHOLD, arm_b: str = ARM_OFF) -> dict[str, str]:
    """Map queryId → the arm shown first, randomized by seed (per-query derived seed)."""
    return {
        qid: arm_a if random.Random(f"{seed}:{qid}").random() < 0.5 else arm_b
        for qid in query_ids
    }


def _flatten(text: str) -> str:
    return " ".join(text.split())


def render_payload(query_text: str, first_chunks: list[dict], second_chunks: list[dict]) -> str:
    """Render the blind payload: instruction block + query + the two labelled lists."""
    lines = [INSTRUCTION_BLOCK, "", QUERY_LABEL + query_text, "", FIRST_HEADER]
    lines += [
        CHUNK_FMT.format(rank=rank, hash=c["hash"], snippet=_flatten(c["snippet"]))
        for rank, c in enumerate(first_chunks, 1)
    ]
    lines += ["", SECOND_HEADER]
    lines += [
        CHUNK_FMT.format(rank=rank, hash=c["hash"], snippet=_flatten(c["snippet"]))
        for rank, c in enumerate(second_chunks, 1)
    ]
    return "\n".join(lines) + "\n"


def parse_response(text: str) -> tuple[str, str] | None:
    """Return (pick, reason) for a well-formed grader response, else None.

    The pick is load-bearing; a missing reason line degrades to "".
    """
    pick_match = PICK_RE.search(text)
    if pick_match is None:
        return None
    reason_match = REASON_RE.search(text)
    reason = reason_match.group(1).strip() if reason_match else ""
    return pick_match.group(1).lower(), reason


def build_grader_argv(runner_cmd: str, payload: str, provider: str | None, model: str | None) -> list[str]:
    """One fresh headless grader session: <runner> --no-session [--provider P --model M] <payload>."""
    argv = shlex.split(runner_cmd) + ["--no-session"]
    if provider:
        argv += ["--provider", provider]
    if model:
        argv += ["--model", model]
    argv.append(payload)
    return argv


def subprocess_runner(argv: list[str]) -> str:
    """Real runner: one headless grader subprocess; non-zero exit → unparseable output."""
    proc = subprocess.run(
        argv,
        capture_output=True,
        text=True,
        check=False,
        env={**os.environ, "PI_BADGER_MEM_RAG": "0"},
    )
    return proc.stdout if proc.returncode == 0 else ""


def comp_score(x: int, abstentions: int) -> float | None:
    """x/(3 − abstentions); None when every grader abstained (reported, never scored)."""
    denominator = len(TRIO) - abstentions
    if denominator == 0:
        return None
    return x / denominator


def first_sentence(text: str) -> str:
    """The first sentence of a reason (deterministic split on . ! ?)."""
    stripped = text.strip()
    match = re.match(r"(.+?[.!?])(?:\s|$)", stripped, re.DOTALL)
    return match.group(1).strip() if match else stripped


def _normalize(sentence: str) -> str:
    return re.sub(r"\s+", " ", sentence).strip().lower().rstrip(".")


def _core_sentence_counts(reasons: list[str]) -> tuple[list[str], dict[str, int]]:
    """First sentences of reasons, deduped on normalized text: ranked originals + counts.

    Rank = frequency descending, ties broken by first appearance.
    """
    counts: dict[str, int] = {}
    representative: dict[str, str] = {}
    order: dict[str, int] = {}
    for reason in reasons:
        sentence = first_sentence(reason)
        if not sentence:
            continue
        key = _normalize(sentence)
        if key not in representative:
            representative[key] = sentence
            order[key] = len(order)
            counts[key] = 0
        counts[key] += 1
    ranked_keys = sorted(representative, key=lambda key: (-counts[key], order[key]))
    ranked = [representative[key] for key in ranked_keys]
    return ranked, {representative[key]: counts[key] for key in ranked_keys}


def distill(reasons: list[str], top_k: int = DEFAULT_TOP_K) -> list[str]:
    """Deterministic core-sentence distillation: first sentence per reason,
    dedup, frequency-rank, top-k."""
    ranked, _ = _core_sentence_counts(reasons)
    return ranked[:top_k]


def load_sample(sample_path: Path) -> tuple[list[dict], list[str]]:
    """Read P4's sample.json by contract; refuse a grader record ≠ the TRIO constants."""
    data = json.loads(sample_path.read_text(encoding="utf-8"))
    graders = [
        {"name": g.get("name"), "provider": g.get("provider"), "model": g.get("model")}
        for g in data["header"]["graders"]
    ]
    if graders != [dict(g) for g in TRIO]:
        raise ValueError(
            f"{sample_path}: header graders {graders} != the P5 trio constants {list(TRIO)}"
        )
    query_ids = [entry["queryId"] for entry in data["sample"]]
    if not query_ids:
        raise ValueError(f"{sample_path}: empty sample")
    return graders, query_ids


def load_arm(arm_path: Path) -> tuple[str, dict[str, dict]]:
    """Read P3's arm JSON by contract → (arm name, {queryId: {queryText, chunks}})."""
    data = json.loads(arm_path.read_text(encoding="utf-8"))
    name = data.get("arm")
    if not isinstance(name, str) or not name:
        raise ValueError(f"{arm_path}: missing arm name (`arm`)")
    queries: dict[str, dict] = {}
    for q in data["queries"]:
        results = q["results"]
        if len(results) != CHUNKS_PER_LIST:
            raise ValueError(f"{arm_path}: query {q['queryId']} has {len(results)} results, expected {CHUNKS_PER_LIST}")
        queries[q["queryId"]] = {
            "queryText": q["queryText"],
            "chunks": [
                {"hash": c["hash"], "rank": c["rank"], "snippet": c["snippet"]} for c in results
            ],
        }
    return name, queries


def run_ab(
    sample_path: Path,
    arm_a_path: Path,
    arm_b_path: Path,
    *,
    seed: int,
    runner,
    runner_cmd: str = DEFAULT_RUNNER,
    out_path: Path,
    forms_dir: Path | None = None,
    top_k: int = DEFAULT_TOP_K,
) -> dict:
    """One blind pass over the sample; returns the ab-results.json document.

    The pair is arbitrary: each artifact's `arm` field names it, positions are
    labelled only first/second in the payloads, and the position→arm mapping is
    restored into the results after grading."""
    graders, query_ids = load_sample(sample_path)
    arm_a_name, arm_a = load_arm(arm_a_path)
    arm_b_name, arm_b = load_arm(arm_b_path)
    if arm_a_name == arm_b_name:
        raise ValueError(f"both arm artifacts are named {arm_a_name!r} — the pair must differ")
    missing = [qid for qid in query_ids if qid not in arm_a or qid not in arm_b]
    if missing:
        raise ValueError(f"sampled queries missing from an arm JSON: {missing}")
    mapping = assign_positions(query_ids, seed, arm_a_name, arm_b_name)
    if forms_dir is None:
        forms_dir = out_path.parent / "ab-forms"
    forms_dir.mkdir(parents=True, exist_ok=True)

    results: dict = {
        "header": {
            "seed": seed,
            "runnerCmd": runner_cmd,
            "trio": graders,
            "topK": top_k,
            "samplePath": str(sample_path),
            "armNames": [arm_a_name, arm_b_name],
            "armAPath": str(arm_a_path),
            "armBPath": str(arm_b_path),
        },
        "queries": [],
    }
    re_asks = 0
    for qid in query_ids:
        first_arm = mapping[qid]
        second_arm = other_arm(first_arm, arm_a_name, arm_b_name)
        query_text = arm_a[qid]["queryText"]
        if arm_b[qid]["queryText"] != query_text:
            raise ValueError(f"{qid}: arms disagree on queryText")
        first_chunks = (arm_a if first_arm == arm_a_name else arm_b)[qid]["chunks"]
        second_chunks = (arm_a if second_arm == arm_a_name else arm_b)[qid]["chunks"]
        payload = render_payload(query_text, first_chunks, second_chunks)
        payload_path = forms_dir / f"{qid}.payload.txt"
        payload_path.write_text(payload, encoding="utf-8")

        grader_records = []
        for grader in graders:
            argv = build_grader_argv(runner_cmd, payload, grader["provider"], grader["model"])
            parsed = parse_response(runner(argv))
            re_ask_count = 0
            if parsed is None:  # malformed → re-ask once, then abstention
                re_ask_count = 1
                parsed = parse_response(runner(argv))
            abstained = parsed is None
            pick = parsed[0] if parsed else None
            pick_arm = first_arm if pick == "first" else second_arm if pick == "second" else None
            grader_records.append(
                {
                    "name": grader["name"],
                    "provider": grader["provider"],
                    "model": grader["model"],
                    "pick": pick,
                    "pickArm": pick_arm,
                    "reason": parsed[1] if parsed else None,
                    "reAskCount": re_ask_count,
                    "abstained": abstained,
                }
            )
        re_asks += sum(g["reAskCount"] for g in grader_records)
        abstentions = sum(1 for g in grader_records if g["abstained"])
        arm_a_picks = sum(1 for g in grader_records if g["pickArm"] == arm_a_name)
        results["queries"].append(
            {
                "queryId": qid,
                "queryText": query_text,
                "firstArm": first_arm,
                "secondArm": second_arm,
                "payloadSha256": hashlib.sha256(payload_path.read_bytes()).hexdigest(),
                "graders": grader_records,
                "abstentions": abstentions,
                "thresholdPicks": arm_a_picks,
                "compScore": comp_score(arm_a_picks, abstentions),
            }
        )

    reasons = [
        g["reason"]
        for q in results["queries"]
        for g in q["graders"]
        if not g["abstained"] and g["reason"]
    ]
    ranked_sentences, sentence_counts = _core_sentence_counts(reasons)
    core_sentences = ranked_sentences[:top_k]
    results["header"]["graderCalls"] = len(query_ids) * len(graders)
    results["header"]["reAsks"] = re_asks
    results["distilled"] = {
        "topK": top_k,
        "coreSentences": core_sentences,
        "frequencies": {sentence: sentence_counts[sentence] for sentence in core_sentences},
    }
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(json.dumps(results, indent=2) + "\n", encoding="utf-8")
    return results


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Blind A/B pass (P5) of the threshold-committee eval")
    parser.add_argument("--sample", required=True, type=Path, help="P4 sample.json")
    parser.add_argument("--arm-a", "--arm-threshold", required=True, type=Path,
                        dest="arm_a", help="first arm's P3 JSON (--arm-threshold: threshold)")
    parser.add_argument("--arm-b", "--arm-off", required=True, type=Path,
                        dest="arm_b", help="second arm's P3 JSON (--arm-off: off)")
    parser.add_argument("--out", required=True, type=Path, help="ab-results.json output path")
    parser.add_argument("--forms-dir", type=Path, default=None, help="payload archive (default: <out>.parent/ab-forms)")
    parser.add_argument("--seed", type=int, default=DEFAULT_SEED)
    parser.add_argument("--runner", default=DEFAULT_RUNNER, help='grader CLI prefix (default "pi -p")')
    parser.add_argument("--top-k", type=int, default=DEFAULT_TOP_K, dest="top_k")
    args = parser.parse_args(argv)

    results = run_ab(
        args.sample,
        args.arm_a,
        args.arm_b,
        seed=args.seed,
        runner=subprocess_runner,
        runner_cmd=args.runner,
        out_path=args.out,
        forms_dir=args.forms_dir,
        top_k=args.top_k,
    )
    queries = results["queries"]
    print(f"queries graded: {len(queries)}")
    print(f"grader calls: {results['header']['graderCalls']} (+{results['header']['reAsks']} re-asks)")
    print(f"abstentions: {sum(q['abstentions'] for q in queries)}")
    all_abstained = [q["queryId"] for q in queries if q["compScore"] is None]
    if all_abstained:
        print(f"all-abstained queries (unscored): {all_abstained}")
    print("core sentences:")
    for sentence in results["distilled"]["coreSentences"]:
        print(f"  - {sentence}")
    print(f"results: {args.out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
