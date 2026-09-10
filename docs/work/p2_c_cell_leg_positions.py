#!/usr/bin/env python3
"""P2 AC2 evidence — per-row c-cell leg positions from the frozen golden.

Reads docs/work/results-f1.json, selects the c-cell rows (bank-hit/harness-miss)
and, for each, measures on the frozen store:

- the expected hash's rank in the FULL FTS and dual-vector candidate windows
  (max(limit*3,100)=100) and its rank inside a top-8 slice of each list — the
  window-vs-top-8 distinction the taxonomy rests on;
- the stage the anchor dies at (per-leg content dedupe, relative floor,
  Take(8)) using the same stage-by-stage replica as the C10 trace;
- the P2 label from llamaindex_harness.evaluate.gap_label, cross-checked against
  the golden's fts_hit/vector_hit columns.

Output is a table plus a summary line (`window hits beyond top-8`). Run under
the lane's memwatch convention, one heavy process at a time:

    python3 memwatch.py --cap-mb 6144 --log /tmp/p2-c-cell-mem.log \\
        python3 docs/work/p2_c_cell_leg_positions.py --offline

Defaults point at the frozen P1 store and this lane's frozen golden.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import sys
from pathlib import Path

LANE = Path(__file__).resolve().parents[2]


def parse_args() -> argparse.Namespace:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--harness-dir", type=Path,
                    default=LANE / "scripts" / "retrieval_tuning")
    ap.add_argument("--golden", type=Path,
                    default=LANE / "docs" / "work" / "results-f1.json")
    ap.add_argument("--store-dir", type=Path, default=Path("/tmp/p1-full-store"))
    ap.add_argument("--limit", type=int, default=8)
    ap.add_argument("--offline", action="store_true", default=True)
    return ap.parse_args()


def find(rows, wanted):
    for rank, (h, score) in enumerate(rows, 1):
        if h == wanted:
            return rank, score
    return None, None


def main() -> int:
    args = parse_args()
    sys.path.insert(0, str(args.harness_dir))
    from llamaindex_harness import evaluate, fusion, ingest, retrieve  # noqa: PLC0415

    golden = json.loads(args.golden.read_text(encoding="utf-8"))
    c_rows = [r for r in golden["rows"]
              if r["airaccoon"]["hit"] == 1 and r["harness"]["hit"] == 0]
    print(f"c-cell rows: {len(c_rows)} of {len(golden['rows'])} scored "
          f"(golden c_cell={golden['summary']['gaps']['c_cell']})")

    handle = ingest.open_store(args.store_dir)
    model = ingest.create_embedding_model(offline=args.offline)

    beyond_top8 = {"fts": 0, "vector": 0}
    labels: dict[str, int] = {}
    print(f"{'id':<5} {'label':<13} {'fts':>9} {'vec':>9} {'pre':>5} {'post':>5} "
          f"{'floor':>6} {'stage':<14} golden_legs label_check")
    for row in c_rows:
        qid, expected = row["id"], row["expectedHash"]
        project = row.get("targetProjectId") or "ai-raccoon"
        scope = row.get("targetScope") or "project"
        retriever = retrieve.FusionRetriever(
            handle, query_embed=model.get_query_embedding,
            project_id=project, scope=scope, default_limit=args.limit)

        fts_rows, plan = retriever.fts_leg(row["query"], args.limit)
        vec_rows = retriever.vector_leg(row["query"], args.limit)
        fts_rank, _ = find(fts_rows, expected)
        vec_rank, _ = find(vec_rows, expected)
        if fts_rank is not None and fts_rank > args.limit:
            beyond_top8["fts"] += 1
        if vec_rank is not None and vec_rank > args.limit:
            beyond_top8["vector"] += 1

        label = evaluate.gap_label(row)
        labels[label] = labels.get(label, 0) + 1

        payload_ids = {h for h, _ in fts_rows} | {h for h, _ in vec_rows}
        payloads = retriever._payloads(sorted(payload_ids))

        def with_payload(rows):
            return [(h, s, payloads[h][0], payloads[h][1].get("path", ""))
                    for h, s in rows if h in payloads]

        legs: list[tuple[str, float, list[str]]] = []
        carriers: dict = {}
        stage = "both-legs-miss"
        for name, rows, weight, ascending in (
                ("fts", fts_rows, retriever._fts_weight, True),
                ("vector", vec_rows, retriever._vector_weight, False)):
            if not rows or weight == 0:
                continue
            deduped = retrieve.dedupe_by_content(with_payload(rows), ascending=ascending)
            kept = [h for h, _ in deduped]
            if expected in [h for h, _ in rows] and expected not in kept:
                stage = f"{name}-dedupe"
            legs.append((name, weight, kept))
            for h, _ in deduped:
                carriers.setdefault(h, payloads[h])

        pre_rank = post_rank = None
        floor_ok = None
        if legs:
            paths = {h: carriers[h][1].get("path", "") for h in carriers}
            fused = fusion.fuse_rrf(legs, paths, retriever._rrf_k, 0.0, 2**31 - 1)
            pre_rank = next((i for i, (h, _) in enumerate(fused, 1)
                             if h == expected), None)
            candidates = [
                fusion.RankedHit(h, r, paths.get(h, ""),
                                 carriers[h][1].get("source_file") or None,
                                 int(carriers[h][1].get("chunk_index", 0)),
                                 int(carriers[h][1].get("total_chunks", 0)))
                for h, r in fused]
            source_lambda = 0.0 if plan.is_path_query else retriever._lambda
            refused = fusion.fuse_rrf(
                [("fused", 1.0, [c.hash for c in candidates])],
                {c.hash: c.path for c in candidates}, retriever._rrf_k, 0.0, 2**31 - 1)
            by_hash = {c.hash: c for c in candidates}
            rescored = [fusion.RankedHit(h, r, by_hash[h].path, by_hash[h].source_file,
                                         by_hash[h].chunk_index, by_hash[h].total_chunks)
                        for h, r in refused]
            ranked_all = fusion.rank_affinity(rescored, source_lambda,
                                              retriever._threshold, retriever._formula)
            exp_hit = next((c for c in ranked_all if c.hash == expected), None)
            post_rank = next((i for i, c in enumerate(ranked_all, 1)
                              if c.hash == expected), None)
            if exp_hit is not None:
                floor_ok = exp_hit.ranking >= retriever._min_rel
            if stage == "both-legs-miss":
                if exp_hit is None:
                    stage = "pre-merge"
                elif not floor_ok:
                    stage = "relative-floor"
                elif post_rank > args.limit:
                    stage = "Take(8)"
                else:
                    stage = "served"

        golden_legs = f"{row['harness'].get('fts_hit')}/{row['harness'].get('vector_hit')}"
        computed_legs = f"{1 if fts_rank else 0}/{1 if vec_rank else 0}"
        label_check = "ok" if computed_legs == golden_legs else "MISMATCH"

        def fmt(rank):
            if rank is None:
                return "-"
            return f"{rank}{'!' if rank > args.limit else ''}"

        print(f"{qid:<5} {label:<13} {fmt(fts_rank):>9} {fmt(vec_rank):>9} "
              f"{str(pre_rank or '-'):>5} {str(post_rank or '-'):>5} "
              f"{str(floor_ok):>6} {stage:<14} {golden_legs:<11} {label_check}")

    print()
    print(f"window hits beyond top-{args.limit}: fts={beyond_top8['fts']} "
          f"vector={beyond_top8['vector']} (! marks a rank inside the 100-window "
          "but outside top-8)")
    print(f"labels: {labels}")
    handle.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
