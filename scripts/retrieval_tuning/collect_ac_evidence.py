#!/usr/bin/env python3
"""P1 AC evidence collector: results.json + store params + eval log -> verdicts.

Every expectation is DERIVED from the corpus and the store (the scorable count,
the stale set = null-anchored + anchors missing from the store, the manifest
seed-equality) or from the frozen contract literals. The model gate compares
the recorded revision to ``ingest.PINNED_MODEL_REVISION`` — a non-empty string
is not a pin (F5), and the results must carry the same weights identity.

Usage:
    python3 collect_ac_evidence.py --results <results.json> --store <store-dir> \
        --corpus <project-corpus-100.json> --eval-log <eval.log>

Exit 0 = every gate holds; prints each criterion with its evidence.
"""

from __future__ import annotations

import argparse
import json
import sqlite3
import sys
from pathlib import Path

FROZEN_KNOBS = {
    "rrfK": 60, "ftsWeight": 1, "vectorWeight": 1, "limit": 8,
    "minRelativeScore": 0.6, "sourceLambda": 0.1,
    "consolidationThreshold": 0.1, "docScoreFormula": "max",
    "candidateWindow": "max3x100", "structureAlpha": 0.5,
}


def model_pin_failures(params: dict, results: dict, pinned_revision: str) -> list[str]:
    """The frozen-weights gate: recorded == pinned, bytes > 0, results agree."""
    recorded = params.get("modelRevision")
    failures: list[str] = []
    if not recorded or recorded != pinned_revision:
        failures.append(f"modelRevision {recorded!r} != pinned {pinned_revision!r}")
    if not isinstance(params.get("modelBytes"), int) or params["modelBytes"] <= 0:
        failures.append(f"modelBytes {params.get('modelBytes')!r} is not a positive byte count")
    if results.get("modelRevision") != recorded:
        failures.append(f"results.modelRevision {results.get('modelRevision')!r} != "
                        f"params.modelRevision {recorded!r}")
    return failures


def _stored_ids(store_dir: Path) -> set[str]:
    """Store content ids from the FTS mirror (read-only; no Chroma open needed)."""
    conn = sqlite3.connect(f"file:{(store_dir / 'fts.db').resolve()}?mode=ro", uri=True)
    try:
        return {r[0] for r in conn.execute("SELECT hash FROM docs_fts")}
    finally:
        conn.close()


def main(argv: list[str] | None = None, pinned_revision: str | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--results", required=True)
    parser.add_argument("--store", required=True)
    parser.add_argument("--corpus", required=True)
    parser.add_argument("--eval-log", required=True)
    args = parser.parse_args(argv)

    from llamaindex_harness import evaluate  # noqa: PLC0415 — stdlib-only module
    if pinned_revision is None:
        from llamaindex_harness import ingest  # noqa: PLC0415 — heavy; collector runs in the eval env
        pinned_revision = ingest.PINNED_MODEL_REVISION

    results = json.loads(Path(args.results).read_text())
    params = json.loads((Path(args.store) / "params.json").read_text())
    corpus = json.loads(Path(args.corpus).read_text())
    log = Path(args.eval_log).read_text(errors="replace")
    failures: list[str] = []

    def check(name: str, cond: bool, evidence: str) -> None:
        print(f"[{'PASS' if cond else 'FAIL'}] {name}: {evidence}")
        if not cond:
            failures.append(name)

    scorable = [q for q in corpus["queries"] if q.get("expectedHash")]
    null_ids = [q["id"] for q in corpus["queries"] if not q.get("expectedHash")]
    stored = _stored_ids(Path(args.store))
    missing = sorted({q["expectedHash"] for q in scorable} - stored)
    expected_stale = sorted(set(null_ids) | set(missing))

    s = results["summary"]
    check("AC2 summary.n == scorable count", s["n"] == len(scorable),
          f"n={s['n']} scorable={len(scorable)} "
          f"(100 = {len(scorable)} scored + {len(null_ids)} null-anchor accounted)")
    check("AC2 staleAnchors == corpus/store-derived stale set",
          set(results.get("staleAnchors", [])) == set(expected_stale),
          f"staleAnchors={results.get('staleAnchors')} expected={expected_stale} "
          f"(null={null_ids}, missing-from-store={missing})")
    check("AC2 eval_gate_failures == []",
          evaluate.eval_gate_failures(results) == [],
          f"failures={evaluate.eval_gate_failures(results)}")
    warning = f"WARNING: {len(expected_stale)} stale anchors"
    check("AC4 WARNING line matches the derived stale count", warning in log,
          [line for line in log.splitlines() if "WARNING" in line][:2] or f"no {warning!r} line")
    check("AC1 buckets cover corpus or manifest",
          all(q["targetProjectId"] in (params.get("resolvedBuckets", [])
               + [e["projectId"] for e in params.get("excludedProjects", [])]
               + [e["canonicalId"] for e in params.get("excludedProjects", [])])
              for q in corpus["queries"]),
          f"resolvedBuckets={len(params.get('resolvedBuckets', []))} "
          f"excluded={len(params.get('excludedProjects', []))}")
    check("manifest seed-equal to header",
          params.get("excludedProjects") == corpus["header"]["excludedProjects"],
          "params.excludedProjects == header.excludedProjects")
    check("snapshot SHA recorded",
          results.get("corpusSnapshotSha256") == corpus["header"]["snapshotSha256"],
          f"{str(results.get('corpusSnapshotSha256'))[:12]}...")
    pin_failures = model_pin_failures(params, results, pinned_revision)
    check("model revision is the pinned revision",
          not pin_failures,
          f"params.modelRevision={str(params.get('modelRevision'))[:12]}... "
          f"pinned={str(pinned_revision)[:12]}... bytes={params.get('modelBytes')}"
          + (f" [{'; '.join(pin_failures)}]" if pin_failures else ""))
    check("copy provenance recorded",
          bool(params.get("copyPath"))
          and params.get("copySnapshotSha256") == results.get("copySnapshotSha256"),
          f"copyPath={params.get('copyPath')} "
          f"sha={str(params.get('copySnapshotSha256'))[:12]}...")
    check("corpus snapshot == store copy snapshot",
          params.get("corpusSnapshotSha256") == params.get("copySnapshotSha256"),
          f"corpus={str(params.get('corpusSnapshotSha256'))[:12]}... "
          f"copy={str(params.get('copySnapshotSha256'))[:12]}...")
    check("frozen knobs untouched",
          all(params.get(k) == v for k, v in FROZEN_KNOBS.items()),
          "rrfK/weights/limit/floor/lambda/threshold/Max/Max3X100/alpha per contract")
    gaps = s.get("gaps", {})
    check("gap counts conserved",
          gaps.get("c_cell") == (gaps.get("c_fts_only", 0) + gaps.get("c_vec_only", 0)
           + gaps.get("c_both_legs", 0) + gaps.get("c_neither_leg", 0)
           + gaps.get("c_unknown", 0)),
          f"gaps={gaps}")
    cont = s["contingency"]
    check("contingency sums to paired",
          cont["a"] + cont["b"] + cont["c"] + cont["d"] == s["n_paired"],
          f"cont={cont} paired={s['n_paired']}")
    print(f"hit-rates: harness={s['harness']['hit_rate']:.3f} "
          f"f1={s['harness']['mean_f1']:.3f} | "
          f"bank={s['airaccoon']['hit_rate']:.3f} f1={s['airaccoon']['mean_f1']:.3f} | "
          f"mcc={s['mcc']}")
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
