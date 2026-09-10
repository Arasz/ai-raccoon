#!/usr/bin/env python3
"""C10 repro — trace where the shared-scope expected hash dies (C020/C065/C081).

Exact replica of `FusionRetriever._retrieve_with_limit` with instrumentation at
every stage: fts leg -> vector leg -> per-leg content dedupe -> pre-merge RRF ->
re-fuse -> affinity -> relative floor -> Take(8). Prints the expected hash's
rank/score at each stage and a verdict naming the stage that lost it.

Run from anywhere (no cwd assumptions); the harness package is added to
sys.path from --harness-dir. The embedding model loads once (~1-2 GB RSS), so
run it under your memwatch convention, e.g.:

  python3 /tmp/memwatch.py 6144 /tmp/p1-red/c10-trace-mem.log \
    python3 /tmp/p1-red/c10_shared_leg_trace.py --offline

Defaults point at the frozen P1 store (/tmp/p1-full-store) and this lane's
frozen corpus. Measured outcome (2026-09-10, per-stage, all three shared rows):
the expected hash is rank 1 in the fts leg, absent from the dual-vector leg
(C081: rank 95), kept by per-leg content-dedupe, ranked 28/32/10 by pre-merge
RRF, unchanged by affinity, above the 0.6 relative floor (0.6932/0.6630/0.8714)
and dropped by the final Take(8). The drop is the limit, not dedupe/RRF/floor/
affinity; the vector-input scores behind the outranking candidates are the
divergent stage (the measured ONNX-vs-HF model-conversion seam: bank ONNX
self-consistency 0.9826, ONNX-vs-HF 0.5934 on the same text; the harness
already pools CLS at cos 1.00000 vs the model-card recipe — the earlier pooling
explanation is retracted, C14; see docs/work/2026-09-10-p1-c10-shared-fusion-trace.md).
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
                    default=LANE / "scripts" / "retrieval_tuning",
                    help="dir containing the llamaindex_harness package")
    ap.add_argument("--corpus", type=Path,
                    default=LANE / "scripts" / "retrieval_tuning" / "corpora"
                    / "project-corpus-100.json")
    ap.add_argument("--store-dir", type=Path, default=Path("/tmp/p1-full-store"))
    ap.add_argument("--ids", default="C019,C065,C081")
    ap.add_argument("--limit", type=int, default=8)
    ap.add_argument("--offline", action="store_true", default=True)
    return ap.parse_args()


def main() -> int:
    args = parse_args()
    sys.path.insert(0, str(args.harness_dir))
    from llamaindex_harness import fusion, ingest, retrieve  # noqa: PLC0415

    handle = ingest.open_store(args.store_dir)
    model = ingest.create_embedding_model(offline=args.offline)
    corpus = json.loads(args.corpus.read_text(encoding="utf-8"))
    entries = {q["id"]: q for q in corpus["queries"]}

    for qid in [i.strip() for i in args.ids.split(",") if i.strip()]:
        entry = entries[qid]
        expected = entry["expectedHash"]
        project = entry["targetProjectId"]
        scope = entry["targetScope"]
        retriever = retrieve.FusionRetriever(
            handle, query_embed=model.get_query_embedding,
            project_id=project, scope=scope, default_limit=args.limit)
        window = retrieve.candidate_window(args.limit, retriever._window_mode)
        print(f"=== {qid}  {project}/{scope}  window={window}  "
              f"expected={expected[:12]}  query={entry['query'][:70]!r}")

        fts_rows, plan = retriever.fts_leg(entry["query"], args.limit)
        vec_rows = retriever.vector_leg(entry["query"], args.limit)

        def find(rows, wanted):
            for rank, (h, score) in enumerate(rows, 1):
                if h == wanted:
                    return rank, score
            return None, None

        fts_rank, fts_score = find(fts_rows, expected)
        vec_rank, vec_score = find(vec_rows, expected)
        print(f"  fts leg   : n={len(fts_rows):<4} expected rank={fts_rank} score={fts_score}")
        print(f"  vector leg: n={len(vec_rows):<4} expected rank={vec_rank} score={vec_score}")

        payload_ids = {h for h, _ in fts_rows} | {h for h, _ in vec_rows}
        payloads = retriever._payloads(sorted(payload_ids))

        def with_payload(rows):
            return [(h, s, payloads[h][0], payloads[h][1].get("path", ""))
                    for h, s in rows if h in payloads]

        # --- exact copy of _retrieve_with_limit from here on, with prints ---
        legs: list[tuple[str, float, list[str]]] = []
        carriers: dict = {}
        for name, rows, weight, ascending in (
                ("fts", fts_rows, retriever._fts_weight, True),
                ("vector", vec_rows, retriever._vector_weight, False)):
            if not rows or weight == 0:
                continue
            deduped = retrieve.dedupe_by_content(with_payload(rows), ascending=ascending)
            kept = [h for h, _ in deduped]
            if expected not in [h for h, _ in rows]:
                where = "absent pre-dedupe"
            elif expected in kept:
                where = "kept"
            else:
                where = "DEDUPED AWAY"
            print(f"  {name} dedupe: expected {where} (leg n={len(kept)})")
            if where == "DEDUPED AWAY":
                exp_val = payloads.get(expected, ("", {}))[0]
                exp_content = hashlib.sha256((exp_val or "").encode()).hexdigest()
                for h, _s, val, path in with_payload(rows):
                    if hashlib.sha256((val or "").encode()).hexdigest() == exp_content:
                        print(f"     same-content row in {name} leg: {h[:12]} path={path}")
            legs.append((name, weight, kept))
            for h, _ in deduped:
                carriers.setdefault(h, payloads[h])

        if not legs:
            print("  VERDICT: both legs empty after dedupe")
            continue

        paths = {h: carriers[h][1].get("path", "") for h in carriers}
        fused = fusion.fuse_rrf(legs, paths, retriever._rrf_k, 0.0, 2**31 - 1)
        pre_rank = next((i for i, (h, _) in enumerate(fused, 1) if h == expected), None)
        pre_score = dict(fused).get(expected)
        print(f"  pre-merge RRF: n={len(fused):<4} expected rank={pre_rank} score={pre_score}")

        candidates = [
            fusion.RankedHit(h, r, paths.get(h, ""),
                             carriers[h][1].get("source_file") or None,
                             int(carriers[h][1].get("chunk_index", 0)),
                             int(carriers[h][1].get("total_chunks", 0)))
            for h, r in fused]

        # merge_results, replicated exactly so every intermediate is visible
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
        peak = max((c.ranking for c in ranked_all), default=0.0)
        served = [c for c in ranked_all if c.ranking >= retriever._min_rel][:args.limit]
        print(f"  post-affinity: expected rank={post_rank}/{len(ranked_all)} "
              f"ranking={None if exp_hit is None else round(exp_hit.ranking, 4)} "
              f"(floor {retriever._min_rel}, peak {round(peak, 4)})")
        print(f"  served top-{args.limit}: {[c.hash[:10] for c in served]}")

        if exp_hit is None:
            verdict = ("absent from both leg windows (never retrieved) — "
                       "not a fusion loss; check ANN-vs-exact recall or the query embedding")
        elif exp_hit.ranking < retriever._min_rel:
            verdict = (f"below relative floor ({exp_hit.ranking:.4f} < "
                       f"{retriever._min_rel}) — fusion/floor loss, legs had it")
        elif post_rank > args.limit:
            verdict = f"outside top-{args.limit} after floor (rank {post_rank})"
        else:
            verdict = "served (unexpected for this repro)"
        print(f"  VERDICT: {verdict}")
        print()

    handle.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
