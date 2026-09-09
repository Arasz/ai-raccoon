"""Both-systems eval (P3; import-safe, argparse + main(argv)).

For each corpus query: harness FusionRetriever.retrieve() at the query's own
targetProjectId/targetScope (M1 ingestion already covers every targeted
bucket) vs the ai-raccoon leg on a scratch-server copy (M2: BumpAccessAsync
mutates the live bank, so never 100 live-bank searches). Both legs run at a
uniform limit of 8 (M6: overrides the corpus searchLimit=5; the candidate
window is max(limit*3,100)=100 either way, so the legs see identical windows).

Metric forks from scripts/src/retrieval_tuning/scoring.py (M6, justified):
- scoring.resolve_gain allows expectedHash PREFIX + source_file SUFFIX/anchor
  fallback; here a hit is EXACT hash equality — both systems serve full
  content hashes, so a prefix would credit a near-miss as parity.
- scoring reports nDCG@5/MRR/hit over ranked relevance; the corpus carries a
  SINGLETON relevant set per query, which collapses nDCG to a rank-discounted
  hit. F1 adds precision over the served-set size (the 0.6 floor + Take(8)
  shape |R|, the thing most likely to differ between the systems), and the
  inter-system agreement MCC answers the actual parity question (do the two
  systems succeed/fail on the same queries). Hit-rate is primary, F1
  secondary, per the plan.
- A single-system MCC is degenerate (actual=1 for every trial), so only the
  agreement MCC is computed, null-with-reason on a zero denominator.
- Transport failures are recorded per query and excluded pairwise from the
  contingency/MCC; means use per-system success denominators, reported.
"""

from __future__ import annotations

import argparse
import json
import math
import sys
import uuid
from pathlib import Path

EVAL_LIMIT = 8


def hit_singleton(expected_hash: str, served_hashes: list[str]) -> int:
    """1 iff the exact expected hash is in the served set (no prefix fallback)."""
    return 1 if expected_hash in served_hashes else 0


def f1_singleton(expected_hash: str,
                 served_hashes: list[str]) -> tuple[int, float, float, float]:
    """(hit, precision, recall, F1) vs the singleton expected set E={expected}."""
    hit = hit_singleton(expected_hash, served_hashes)
    precision = hit / len(served_hashes) if served_hashes else 0.0
    recall = float(hit)
    f1 = 2 * precision * recall / (precision + recall) if (precision + recall) else 0.0
    return hit, precision, recall, f1


def mcc_agreement(harness_hits: list[int],
                  airaccoon_hits: list[int]) -> tuple[float | None, str | None]:
    """Inter-system agreement phi over paired binary hits (a=both,b=h-only,c=a-only,d=neither)."""
    a = sum(1 for h, s in zip(harness_hits, airaccoon_hits) if h and s)
    b = sum(1 for h, s in zip(harness_hits, airaccoon_hits) if h and not s)
    c = sum(1 for h, s in zip(harness_hits, airaccoon_hits) if not h and s)
    d = sum(1 for h, s in zip(harness_hits, airaccoon_hits) if not h and not s)
    denominator = math.sqrt((a + b) * (a + c) * (b + d) * (c + d))
    if denominator == 0:
        return None, (f"zero denominator (a={a},b={b},c={c},d={d}): one system or "
                      "outcome is constant — report hit-rate, not MCC")
    return (a * d - b * c) / denominator, None


def partition_null_anchors(entries: list[dict]) -> tuple[list[dict], list]:
    """Split scorable entries from null-anchor rows (C034 shape).

    Content-targeted corpus rows carry expectedHash: null by generator
    contract; run_eval refuses them (missing expectedHash), so main() filters
    them here and records their ids as stale — accounted, never scored."""
    scorable = [e for e in entries
                if isinstance(e.get("expectedHash"), str) and e["expectedHash"]]
    null_ids = [e.get("id") for e in entries if e not in scorable]
    return scorable, null_ids


def aggregate_gaps(rows: list[dict]) -> dict:
    """Provisional leg-gap counts over paired rows (P1; P2 refines into the
    classified taxonomy reusing these numbers, never redefining them).

    Computed ONLY on the c-cell (bank-hit/harness-miss) from the existing
    fts_hit/vector_hit columns — never misattributing agreement as deficit.
    Conservation: the five c_* buckets sum to c_cell. Rows without leg
    columns land in c_unknown (a data gap, not a retrieval gap)."""
    paired = [r for r in rows if not r["harness"].get("error")
              and not r["airaccoon"].get("error")]
    c_rows = [r for r in paired if r["airaccoon"]["hit"] == 1
              and r["harness"]["hit"] == 0]
    gaps = {"n_paired": len(paired), "c_cell": len(c_rows),
            "c_fts_only": 0, "c_vec_only": 0, "c_both_legs": 0,
            "c_neither_leg": 0, "c_unknown": 0}
    for r in c_rows:
        fts_hit = r["harness"].get("fts_hit")
        vec_hit = r["harness"].get("vector_hit")
        if fts_hit == 1 and vec_hit == 0:
            gaps["c_fts_only"] += 1  # legs split: embedding-gap evidence
        elif fts_hit == 0 and vec_hit == 1:
            gaps["c_vec_only"] += 1  # a leg had it, fusion lost it
        elif fts_hit == 1 and vec_hit == 1:
            gaps["c_both_legs"] += 1  # both legs hit, fusion lost it
        elif fts_hit == 0 and vec_hit == 0:
            gaps["c_neither_leg"] += 1  # unrecoverable by fusion
        else:
            gaps["c_unknown"] += 1
    return gaps


def _score_side(expected_hash: str, outcome: dict) -> dict:
    hashes = list(outcome.get("hashes") or [])
    hit, precision, recall, f1 = f1_singleton(expected_hash, hashes)
    scored: dict = {"hashes": hashes, "hit": hit, "precision": precision,
                    "recall": recall, "f1": f1, "error": outcome.get("error")}
    for key in ("fts_hit", "vector_hit"):  # harness leg diagnostics (M3 split)
        if key in outcome:
            scored[key] = outcome[key]
    return scored


def run_eval(entries: list[dict], harness_fn, airaccoon_fn) -> dict:
    """Score every entry on both systems; fail loud on corpus-contract drift."""
    if not entries:
        raise ValueError("run_eval: empty corpus")
    rows = []
    for entry in entries:
        if entry.get("negativeTest"):
            raise ValueError(f"run_eval: {entry.get('id')}: negativeTest=true — "
                             "the singleton-F1 contract needs an exclusion rule first")
        expected = entry.get("expectedHash")
        if not isinstance(expected, str) or not expected:
            raise ValueError(f"run_eval: {entry.get('id')}: missing expectedHash")
        rows.append({
            "id": entry.get("id"), "query": entry["query"],
            "targetProjectId": entry.get("targetProjectId"),
            "targetScope": entry.get("targetScope"),
            "expectedHash": expected,
            "harness": _score_side(expected, harness_fn(entry)),
            "airaccoon": _score_side(expected, airaccoon_fn(entry)),
        })
        if len(rows) % 10 == 0:
            print(f"eval: {len(rows)}/{len(entries)} queries scored", flush=True)

    def summarize(side: str) -> dict:
        ok = [r for r in rows if not r[side].get("error")]
        return {"n": len(ok),
                "hit_rate": sum(r[side]["hit"] for r in ok) / len(ok) if ok else 0.0,
                "mean_f1": sum(r[side]["f1"] for r in ok) / len(ok) if ok else 0.0}

    paired = [r for r in rows if not r["harness"].get("error")
              and not r["airaccoon"].get("error")]
    harness_hits = [r["harness"]["hit"] for r in paired]
    airaccoon_hits = [r["airaccoon"]["hit"] for r in paired]
    a = sum(1 for h, s in zip(harness_hits, airaccoon_hits) if h and s)
    b = sum(1 for h, s in zip(harness_hits, airaccoon_hits) if h and not s)
    c = sum(1 for h, s in zip(harness_hits, airaccoon_hits) if not h and s)
    mcc, mcc_reason = mcc_agreement(harness_hits, airaccoon_hits)
    return {"rows": rows,
            "summary": {"n": len(rows), "n_paired": len(paired),
                        "harness": summarize("harness"),
                        "airaccoon": summarize("airaccoon"),
                        "contingency": {"a": a, "b": b, "c": c,
                                        "d": len(paired) - a - b - c},
                        "mcc": mcc, "mcc_reason": mcc_reason,
                        "gaps": aggregate_gaps(rows)}}


def _self_paths() -> tuple[Path, Path]:
    harness_dir = Path(__file__).resolve().parent
    repo = harness_dir.parents[2]
    return harness_dir, repo


def build_harness_fn(store_dir: Path, offline: bool = False):
    """Per-(project,scope) FusionRetrievers over an opened store, real query embeddings."""
    from . import ingest, retrieve  # noqa: PLC0415 — lazy: keeps module import-safe

    handle = ingest.open_store(Path(store_dir))
    model = ingest.create_embedding_model(offline=offline)
    cache: dict = {}

    def fn(entry: dict) -> dict:
        project = entry.get("targetProjectId") or "ai-raccoon"
        scope = entry.get("targetScope") or "project"
        key = (project, scope)
        if key not in cache:
            cache[key] = retrieve.FusionRetriever(
                handle, query_embed=model.get_query_embedding,
                project_id=project, scope=scope, default_limit=EVAL_LIMIT)
        retriever = cache[key]
        try:
            served = retriever.retrieve(entry["query"], limit=EVAL_LIMIT)
            hashes = [n.node.node_id for n in served]
            expected = entry["expectedHash"]
            fts_rows, _ = retriever.fts_leg(entry["query"], EVAL_LIMIT)
            vec_rows = retriever.vector_leg(entry["query"], EVAL_LIMIT)
            return {"hashes": hashes,
                    "fts_hit": hit_singleton(expected, [h for h, _ in fts_rows]),
                    "vector_hit": hit_singleton(expected, [h for h, _ in vec_rows]),
                    "error": None}
        except Exception as exc:  # noqa: BLE001 — recorded, and main() fails loud
            return {"hashes": [], "error": f"{type(exc).__name__}: {exc}"}

    fn.close = handle.close  # type: ignore[attr-defined]
    return fn


def build_airaccoon_fn(server, session_id: str) -> object:
    """The ai-raccoon leg through the scratch server's MCP client (M6 seam).

    server.search(entry) is NOT reused verbatim: it hardcodes the corpus
    searchLimit (5), and the scripts/src client predates the server's required
    sessionId argument (refused as invalid-argument without it). So this calls
    client._call_tool directly with per-query routing + uniform limit 8 +
    memory kind + the harness floor (minRelativeScore 0.6) + an explicit
    session id, otherwise the same settings-driven shape; extraction reuses
    MCPClient._extract_results.
    """
    from retrieval_tuning.mcp import MCPClient  # noqa: PLC0415 — needs scripts/src
    from llamaindex_harness import scopes  # noqa: PLC0415 — stdlib-only, CI-safe

    client = server.client

    def fn(entry: dict) -> dict:
        try:
            parsed = client._call_tool("memory_search", {
                "projectId": entry.get("targetProjectId") or "ai-raccoon",
                "query": entry["query"],
                # Corpus custom -> bank project (SearchContexts.cs: project
                # covers custom labels; the bank refuses scope=custom).
                "scope": scopes.normalize_scope(entry.get("targetScope") or "project"),
                "limit": EVAL_LIMIT,
                "minRelativeScore": 0.6,  # the harness floor: both legs serve
                "kind": "memory",      # the same post-floor, post-limit shape
                "sessionId": session_id,
            })
            results = MCPClient._extract_results(parsed, kind="memory")
            hashes = []
            for row in results:
                h = row.get("hash") if isinstance(row, dict) else None
                if not isinstance(h, str) or not h:
                    return {"hashes": [], "error": "result row without hash"}
                hashes.append(h)
            return {"hashes": hashes, "error": None}
        except Exception as exc:  # noqa: BLE001 — transport: logged, excluded pairwise
            return {"hashes": [], "error": f"{type(exc).__name__}: {exc}"}

    return fn


def eval_gate_failures(out: dict) -> list[str]:
    """Fail-loud gates over a run_eval result (pure; main() exits nonzero)."""
    failures = []
    harness_errors = [r["id"] for r in out["rows"] if r["harness"].get("error")]
    if harness_errors:
        failures.append(
            f"harness errors on {len(harness_errors)} queries: {harness_errors[:5]}")
    prod_errors = [r["id"] for r in out["rows"] if r["airaccoon"].get("error")]
    if prod_errors:
        # A silent prod leg would publish harness-only numbers as a comparison.
        failures.append(
            f"ai-raccoon errors on {len(prod_errors)} queries: {prod_errors[:5]}")
    if out.get("summary", {}).get("n_paired", 0) == 0:
        failures.append("zero paired rows: no query scored on both systems")
    return failures


def missing_anchors(entries: list[dict], stored_ids: set) -> list:
    """Corpus-staleness gate (pure): entry ids whose expectedHash is not stored."""
    return [e.get("id") for e in entries if e.get("expectedHash") not in stored_ids]


def check_anchors_resolve(entries: list[dict], store_dir: Path) -> list:
    """Corpus-staleness check: ids whose expectedHash is not in the store.

    Returns the missing ids (warn-and-record upstream); a totally empty
    intersection means a wrong store, which main() refuses to run against."""
    from . import ingest  # noqa: PLC0415 — lazy: keeps module import-safe

    handle = ingest.open_store(Path(store_dir))
    try:
        return missing_anchors(entries, set(handle.content.get(include=[])["ids"]))
    finally:
        handle.close()


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--corpus", required=True)
    parser.add_argument("--store-dir", required=True)
    parser.add_argument("--scratch-data-root", required=True,
                        help="data-root seeded from a bank copy; a scratch server serves it (M2)")
    parser.add_argument("--binary", default="ai-raccoon")
    parser.add_argument("--out", required=True, help="results.json target")
    parser.add_argument("--limit-queries", type=int, default=None)
    parser.add_argument("--offline", action="store_true",
                        help="reuse cached HF weights; fail instead of downloading")
    args = parser.parse_args(argv)

    _, repo = _self_paths()
    sys.path.insert(0, str(repo / "scripts" / "src"))
    from retrieval_tuning.server import start_server  # noqa: PLC0415 — needs scripts/src

    entries = json.loads(Path(args.corpus).read_text())
    if isinstance(entries, dict):  # header-shaped corpus: header + queries
        entries = entries["queries"]
    if args.limit_queries is not None:
        entries = entries[:args.limit_queries]
    scorable, null_anchors = partition_null_anchors(entries)
    stale_anchors = check_anchors_resolve(scorable, Path(args.store_dir))
    stale_anchors = sorted(set(stale_anchors) | set(null_anchors))
    if len(stale_anchors) == len(entries):
        raise ValueError("no corpus anchor resolves against this store — wrong store/copy?")
    if stale_anchors:
        print(f"WARNING: {len(stale_anchors)} stale anchors (re-chunked upstream, "
              f"unhittable by either leg): {stale_anchors}", flush=True)
    session_id = f"llamaindex-harness-{uuid.uuid4().hex[:12]}"

    harness_fn = build_harness_fn(Path(args.store_dir), offline=args.offline)
    try:
        with start_server(args.scratch_data_root, binary=args.binary) as server:
            print(f"scratch server on port {server.port} (never 7721)", flush=True)
            out = run_eval(scorable, harness_fn,
                           build_airaccoon_fn(server, session_id))
    finally:
        harness_fn.close()  # type: ignore[attr-defined]

    failures = eval_gate_failures(out)
    if failures:
        for failure in failures:
            print(f"FAIL: {failure}")
        return 1
    out["sessionId"] = session_id
    out["staleAnchors"] = stale_anchors
    store_params = json.loads((Path(args.store_dir) / "params.json").read_text())
    out["corpusSnapshotSha256"] = store_params.get("corpusSnapshotSha256")
    out["excludedProjects"] = store_params.get("excludedProjects", [])
    out["resolvedBuckets"] = store_params.get("resolvedBuckets", [])
    Path(args.out).write_text(json.dumps(out, indent=2))
    s = out["summary"]
    print(f"eval: n={s['n']} paired={s['n_paired']} stale={len(stale_anchors)} "
          f"harness hit-rate={s['harness']['hit_rate']:.3f} f1={s['harness']['mean_f1']:.3f} | "
          f"ai-raccoon hit-rate={s['airaccoon']['hit_rate']:.3f} f1={s['airaccoon']['mean_f1']:.3f} | "
          f"mcc={s['mcc']} cont={s['contingency']}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
