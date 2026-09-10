#!/usr/bin/env python3
"""Golden diff under the one tolerance term (P3 AC4; P4 AC2 reruns).

Compares a candidate `results.json` against a frozen golden:

- EXACT: per-query served hashes (ordered), hit/precision/recall, contingency,
  MCC (null-through), query/target/expected-hash fields, staleAnchors.
- TOLERANT: mean-F1 and hit-rates at +/-1e-9.
- ALLOW-LISTED (may differ): sessionId + provenance pins (modelRevision,
  modelBytes, copyPath, copySnapshotSha256, corpusSnapshotSha256,
  excludedProjects, resolvedBuckets).
- ADDITIVE (candidate-only): the P2 `repeats` block. When present its per-metric
  means must agree with the candidate summary under the same tolerance and the
  unstable served-set lists must be empty; golden C predates the block.
  `summary.gaps.taxonomy` is additive inside `summary` and not compared.
- NOT allow-listed: staleAnchors, ingestedAt (absent by construction).

Usage:
    python3 diff_golden.py <golden.json> <candidate.json>

Exit 0 = clean under the tolerance; 1 = differences (printed, first 20).
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

TOL = 1e-9

ALLOW = {"sessionId", "modelRevision", "modelBytes", "copyPath",
         "copySnapshotSha256", "corpusSnapshotSha256", "excludedProjects",
         "resolvedBuckets"}

ROW_FLOAT_KEYS = ("precision", "recall", "f1")
SIDE_SUMMARY_KEYS = ("n", "hit_rate", "mean_f1")
REPEAT_METRICS = (("harness_hit_rate", "harness", "hit_rate"),
                  ("airaccoon_hit_rate", "airaccoon", "hit_rate"),
                  ("harness_mean_f1", "harness", "mean_f1"),
                  ("airaccoon_mean_f1", "airaccoon", "mean_f1"))


def close(a, b) -> bool:
    return abs(float(a) - float(b)) <= TOL


def row_diff(a: dict, b: dict, i: int) -> list[str]:
    problems: list[str] = []
    if a["id"] != b["id"]:
        problems.append(f"row {i}: id {a['id']} != {b['id']}")
        return problems
    for side in ("harness", "airaccoon"):
        for key in sorted(set(a[side]) | set(b[side])):
            av, bv = a[side].get(key), b[side].get(key)
            if key in ROW_FLOAT_KEYS:
                if not close(av, bv):
                    problems.append(f"{a['id']}/{side}.{key}: {av} != {bv}")
            elif key == "hashes":
                if av != bv:
                    problems.append(f"{a['id']}/{side}.hashes differ "
                                    f"(ordered: {av} != {bv})")
            elif av != bv:
                problems.append(f"{a['id']}/{side}.{key}: {av!r} != {bv!r}")
    for key in ("query", "targetProjectId", "targetScope", "expectedHash"):
        if a.get(key) != b.get(key):
            problems.append(f"{a['id']}.{key}: {a.get(key)!r} != {b.get(key)!r}")
    return problems


def summary_diff(golden: dict, cand: dict) -> list[str]:
    problems: list[str] = []
    for sub in ("n", "n_paired"):
        if golden.get(sub) != cand.get(sub):
            problems.append(f"summary.{sub}: {golden.get(sub)} != {cand.get(sub)}")
    for side in ("harness", "airaccoon"):
        gs, cs = golden.get(side) or {}, cand.get(side) or {}
        for key in SIDE_SUMMARY_KEYS:
            gv, cv = gs.get(key), cs.get(key)
            if key == "n":
                if gv != cv:
                    problems.append(f"summary.{side}.n: {gv} != {cv}")
            elif not close(gv, cv):
                problems.append(f"summary.{side}.{key}: {gv} != {cv}")
    if golden.get("contingency") != cand.get("contingency"):
        problems.append(f"contingency: {golden.get('contingency')} != "
                        f"{cand.get('contingency')}")
    if golden.get("mcc") != cand.get("mcc"):
        problems.append(f"mcc: {golden.get('mcc')} != {cand.get('mcc')}")
    return problems


def repeats_diff(golden_repeats, cand_repeats, cand_summary: dict) -> list[str]:
    """Additive P2 block: consistency-checked, never golden-compared."""
    if golden_repeats is None and cand_repeats is None:
        return []
    if golden_repeats is not None:
        # A golden with repeat data is a different contract; compare it exactly.
        return [] if golden_repeats == cand_repeats else [
            "repeats: golden carries a repeat block that the candidate does not match"]
    if not isinstance(cand_repeats, dict):
        return [f"repeats: expected an object, got {cand_repeats!r}"]
    problems: list[str] = []
    n = cand_repeats.get("n")
    if not isinstance(n, int) or n < 1:
        problems.append(f"repeats.n: {n!r} is not a positive repeat count")
    metrics = cand_repeats.get("metrics") or {}
    for name, side, key in REPEAT_METRICS:
        mean = (metrics.get(name) or {}).get("mean")
        ref = (cand_summary.get(side) or {}).get(key)
        if mean is None or ref is None:
            problems.append(f"repeats.metrics.{name}.mean missing")
        elif not close(mean, ref):
            problems.append(f"repeats.metrics.{name}.mean {mean} != "
                            f"summary.{side}.{key} {ref}")
    mcc_mean = (metrics.get("mcc") or {}).get("mean")
    mcc_ref = cand_summary.get("mcc")
    if (mcc_mean is None) != (mcc_ref is None) or (
            mcc_mean is not None and not close(mcc_mean, mcc_ref)):
        problems.append(f"repeats.metrics.mcc.mean {mcc_mean} != summary.mcc {mcc_ref}")
    for leg, ids in (cand_repeats.get("unstable") or {}).items():
        if ids:
            problems.append(f"repeats.unstable.{leg}: served sets moved across "
                            f"repeats: {ids}")
    return problems


def diff(golden: dict, cand: dict) -> list[str]:
    """All differences between golden and candidate under the tolerance term."""
    problems: list[str] = []
    for key in sorted(set(golden) | set(cand)):
        if key in ALLOW or key == "rows":
            continue
        gv, cv = golden.get(key), cand.get(key)
        if key == "summary":
            problems.extend(summary_diff(gv or {}, cv or {}))
        elif key == "repeats":
            problems.extend(repeats_diff(gv, cv, cand.get("summary") or {}))
        elif gv != cv:
            problems.append(f"{key}: {gv!r} != {cv!r}")
    if len(golden.get("rows", [])) != len(cand.get("rows", [])):
        problems.append(f"row count: {len(golden.get('rows', []))} != "
                        f"{len(cand.get('rows', []))}")
    else:
        for i, (a, b) in enumerate(zip(golden["rows"], cand["rows"])):
            problems.extend(row_diff(a, b, i))
    return problems


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("golden", type=Path, help="frozen golden results.json")
    parser.add_argument("candidate", type=Path, help="candidate results.json")
    args = parser.parse_args(argv)

    golden = json.loads(args.golden.read_text())
    cand = json.loads(args.candidate.read_text())
    print(f"golden={args.golden}")
    print(f"cand  ={args.candidate}")
    print(f"corpusSnapshotSha256: golden={str(golden.get('corpusSnapshotSha256'))[:12]} "
          f"cand={str(cand.get('corpusSnapshotSha256'))[:12]}")
    print(f"staleAnchors: golden={golden.get('staleAnchors')} "
          f"cand={cand.get('staleAnchors')}")
    problems = diff(golden, cand)
    if problems:
        print(f"DIFF: {len(problems)} problem(s)")
        for p in problems[:20]:
            print("  -", p)
        return 1
    print("CLEAN: no differences under the single tolerance term "
          "(exact hashes/hits/contingency/MCC, mean-F1 +/-1e-9)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
