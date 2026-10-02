#!/usr/bin/env python3
"""Integrity validation of the interrupted blind-grade artifacts (new file, read-only inputs).

Establishes whether blind/sample.json, sample-p5.json, committee-metrics.json and the arm
JSONs are complete and mutually consistent, and whether the existing ab-forms payloads
equal what the driver would render (so only the grader calls are missing).
"""
from __future__ import annotations

import hashlib
import importlib.util
import json
import sys
from pathlib import Path

REPO = Path("/Users/arasz/RiderProjects/ai-raccoon/.ai-badger/worktrees/"
            "air-254-vs-1022-retrieval-measurement-809")
S = Path("/tmp/aira-ab-254-vs-1022")
BLIND = S / "blind"
ARMS = {"chunk254": "off", "chunk1022": "threshold"}


def load_module(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    mod = importlib.util.module_from_spec(spec)
    sys.modules[name] = mod
    spec.loader.exec_module(mod)
    return mod


def main() -> int:
    rte = load_module("rte", REPO / "scripts/retrieval_tuning/run_threshold_eval.py")
    cg = load_module("committee_grade", REPO / "scripts/retrieval_tuning/committee_grade.py")
    p5 = load_module("ab_compare", REPO / "scripts/retrieval_tuning/ab_compare.py")
    sys.path.insert(0, str(REPO / "scripts/src"))

    failures = []
    def check(cond, msg):
        print(("PASS " if cond else "FAIL ") + msg)
        if not cond:
            failures.append(msg)

    run_dir = S / "runs/run-1"
    metrics = json.loads((run_dir / "metrics.json").read_text())
    arm_docs = {name: json.loads((run_dir / f"arm-{name}.json").read_text()) for name in ARMS}

    # --- rebuild the exact P3->P4 bridge blind_sample.py builds
    committee_queries = []
    for qid, pair in metrics["pairs"].items():
        arms = {}
        for name, slot in ARMS.items():
            arms[slot] = {"results": [
                {"hash": c["hash"], "snippet": c["snippet"],
                 "sourceFile": c.get("sourceFile"), "chunkIndex": c.get("chunkIndex")}
                for c in rte.top8_hits(arm_docs[name]["results"][qid])]}
        committee_queries.append({
            "queryId": qid, "queryText": pair["queryText"], "projectId": pair["projectId"],
            "arms": arms,
            "pair": {"top8SetOverlap": pair["top8SetOverlap"], "rbo": pair["rbo"],
                     "drop": pair["drop"], "backfill": pair["backfill"]}})
    rebuilt_bridge = {"queries": committee_queries}

    cm = json.loads((BLIND / "committee-metrics.json").read_text())
    check(len(cm["queries"]) == 100, f"committee-metrics queries == 100 (got {len(cm['queries'])})")
    check(cm == rebuilt_bridge, "committee-metrics.json equals the rebuild from runs/run-1")

    changed, controls = cg.stratify(cg.load_metrics(BLIND / "committee-metrics.json"))
    check(len(changed) == 100 and len(controls) == 0,
          f"stratify: changed={len(changed)} controls={len(controls)} (all overlap < 1.0)")
    picked_changed, picked_controls = cg.pick_sample(changed, controls, seed=cg.SEED)
    sample = json.loads((BLIND / "sample.json").read_text())
    check(sample["header"]["seed"] == cg.SEED == 42, f"sample seed == 42 (got {sample['header']['seed']})")
    check(sample["header"]["composition"] == {
        "changedAvailable": 100, "controlsAvailable": 0,
        "pickedChanged": len(picked_changed), "pickedControls": len(picked_controls),
        "total": len(picked_changed) + len(picked_controls), "backfilled": False},
        f"sample composition reconciles (picked changed={len(picked_changed)}, controls={len(picked_controls)})")
    expected_pick = [{"queryId": q.query_id, "stratum": q.stratum,
                      "top8SetOverlap": q.top8_set_overlap}
                     for q in [*picked_changed, *picked_controls]]
    check(sample["queries"] == expected_pick, "sample.json queries equal seeded re-pick (seed 42)")
    # graders header vs P5 TRIO
    graders = [{"name": f"grader-{g['index']}", "provider": g["provider"], "model": g["model"]}
               for g in sample["header"]["graders"]]
    check(graders == [dict(g) for g in p5.TRIO], "sample.json graders == ab_compare TRIO constants")

    # --- sample-p5 reconciliation
    p5doc = json.loads((BLIND / "sample-p5.json").read_text())
    served = {name: {qid: len(rte.top8_hits(arm_docs[name]["results"][qid]))
                     for qid in [e["queryId"] for e in sample["queries"]]} for name in ARMS}
    excluded = [{"queryId": e["queryId"],
                 "served": {name: served[name][e["queryId"]] for name in ARMS}}
                for e in sample["queries"]
                if any(served[name][e["queryId"]] != p5.CHUNKS_PER_LIST for name in ARMS)]
    kept = [{"queryId": e["queryId"], "stratum": e["stratum"]}
            for e in sample["queries"]
            if all(served[name][e["queryId"]] == p5.CHUNKS_PER_LIST for name in ARMS)]
    check(p5doc["sample"] == kept, f"sample-p5 sample == sample.json minus short lists ({len(kept)} kept)")
    check(p5doc["header"]["excludedQueries"] == excluded,
          f"sample-p5 excluded == recomputed ({excluded})")
    check(p5doc["header"]["bridgedFrom"] == "sample.json", "sample-p5 bridgedFrom == sample.json")
    check(p5doc["header"]["graders"] == graders, "sample-p5 graders == sample.json graders")

    # --- arm docs (P3->P5 bridge)
    for name in ARMS:
        doc = json.loads((BLIND / f"arm-{name}.json").read_text())
        check(doc["arm"] == name, f"arm doc names arm={name}")
        qids = [q["queryId"] for q in doc["queries"]]
        check(qids == [e["queryId"] for e in kept], f"arm-{name} carries exactly the {len(kept)} kept queries")
        ok = True
        for q in doc["queries"]:
            qid = q["queryId"]
            exp = [{"hash": c["hash"], "rank": r, "snippet": c["snippet"]}
                   for r, c in enumerate(rte.top8_hits(arm_docs[name]["results"][qid]), 1)]
            ok = ok and q["results"] == exp and q["queryText"] == arm_docs[name]["results"][qid]["queryText"]
        check(ok, f"arm-{name} results/queryText equal the recomputed top-8 bridge")

    # --- existing payloads: do they equal a fresh render? (proves only grader calls were missing)
    mapping = p5.assign_positions([e["queryId"] for e in kept], p5.DEFAULT_SEED,
                                  "chunk254", "chunk1022")
    arm_a = {q["queryId"]: q for q in json.loads((BLIND / "arm-chunk254.json").read_text())["queries"]}
    arm_b = {q["queryId"]: q for q in json.loads((BLIND / "arm-chunk1022.json").read_text())["queries"]}
    forms = BLIND / "ab-forms"
    payloads = sorted(p.name for p in forms.glob("*.payload.txt"))
    check(len(payloads) == len(kept), f"ab-forms payload count == {len(kept)} (got {len(payloads)})")
    match = True
    for qid in [e["queryId"] for e in kept]:
        first_arm = mapping[qid]
        second_arm = "chunk1022" if first_arm == "chunk254" else "chunk254"
        src = arm_a if first_arm == "chunk254" else arm_b
        second_src = arm_b if second_arm == "chunk1022" else arm_a
        exp_payload = p5.render_payload(src[qid]["queryText"], src[qid]["results"], second_src[qid]["results"])
        got = (forms / f"{qid}.payload.txt").read_text() if (forms / f"{qid}.payload.txt").exists() else ""
        if got != exp_payload:
            match = False
            print(f"  payload mismatch: {qid}")
    check(match, "all 9 ab-forms payloads equal a fresh deterministic render (seed 20260909)")

    # --- the decisive completeness fact
    check(not (BLIND / "ab-results.json").exists(),
          "ab-results.json absent -> blind pass INCOMPLETE (only grader calls/write-out missing)")

    print()
    print(f"RESULT: {'ALL CHECKS PASS' if not failures else f'{len(failures)} FAILURES'}")
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
