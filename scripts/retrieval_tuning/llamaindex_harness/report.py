"""Eval results -> markdown report (P3; import-safe, argparse + main(argv)).

The five required sections (asserted by test_llamaindex_harness_report.py):
Scope and routing (M1 decision), Method (M2/M3/M6 seams and metric forks),
Per-query results, Aggregates, Parity-gap discussion.
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path


def _short(h: str) -> str:
    return h[:12] if isinstance(h, str) else "-"


def _bucket_projects(buckets: list) -> list[str]:
    """Project ids from observed params.json bucket strings ('proj/scope')."""
    return sorted({b.split("/")[0] for b in buckets if isinstance(b, str) and "/" in b})


def render(results: dict, context: dict) -> str:
    """results.json dict + context dict -> the full markdown report."""
    s = results["summary"]
    rows = results["rows"]
    n, paired = s["n"], s["n_paired"]
    corpus_size = context.get("corpus_size", n)
    lines = [
        "# LlamaIndex fusion harness — eval report",
        "",
        f"Date: {context.get('date', '?')}. Corpus: {context.get('corpus', '?')} "
        f"(evaluated {n} of {corpus_size}).",
        f"Bank copy entries: {context.get('copy_entries', '?')}. "
        f"Ingested rows: {context.get('store_rows', '?')} "
        f"(content={context.get('content', '?')}, "
        f"structure={context.get('structure', '?')}, "
        f"fts={context.get('fts', '?')}, headed={context.get('headed', '?')}).",
        f"Model: {context.get('model', '?')} ({context.get('model_bytes', '?')} "
        f"HF cache). Store: {context.get('store_bytes', '?')}.",
    ]
    if n < corpus_size:
        lines.append(f"Restriction: this is a documented SUBSET eval — {n} of "
                     f"{corpus_size} queries (integration gate / time-boxed run).")
    stale = results.get("staleAnchors") or context.get("stale_anchors") or []
    if stale:
        lines.append(f"Stale anchors ({len(stale)}, re-chunked upstream — unhittable "
                     f"by either leg, counted in d): {', '.join(stale)}.")
    lines += [
        "",
        "## Scope and routing",
        "",
        "M1: each query ran at its own corpus targetProjectId/targetScope on "
        "BOTH systems (no 75-row file-targeted restriction). The harness store "
        "ingests every targeted bucket (project buckets incl. custom scopes, "
        "plus the global shared tier); project buckets in this store: "
        + (", ".join(_bucket_projects(context.get("buckets", []))) or "?") + ".",
        "Both systems ran at a uniform limit 8 (overrides the corpus "
        "searchLimit=5; candidate window max(limit*3,100)=100 either way).",
        "",
        "## Method",
        "",
        "- ai-raccoon leg: scratch server over a bank copy via "
        "scripts/src/retrieval_tuning/server.py + mcp.py (M2 — BumpAccessAsync "
        "mutates the live bank, so no live-bank searches); settings-driven "
        "call shape, per-query routing, kind=memory.",
        f"- Harness leg: FusionRetriever over Chroma content + structure "
        f"collections, dual-vector fused at structureAlpha="
        f"{context.get('structure_alpha', '?')} (M3 — content-only fork NOT taken).",
        "- Metrics (M6 forks from scoring.py, justified): hit = EXACT served "
        "hash equality (scoring.resolve_gain allows prefix + source fallback "
        "— would credit near-misses as parity); per-query F1 vs the singleton "
        "expected set (hit-rate PRIMARY, mean F1 secondary — a singleton "
        "collapses nDCG/MRR to rank-discounted hits, while F1's precision "
        "term prices the served-set size the 0.6 floor + Take(8) shape); "
        "agreement MCC over the paired hit/miss table (single-system MCC is "
        "degenerate — actual=1 every trial). Transport failures are recorded "
        "per query and excluded pairwise.",
        "",
        "## Per-query results",
        "",
        "| id | target | exp | harness hit/F1 | ai-raccoon hit/F1 | agree |"
        " fts/vec | error |",
        "|---|---|---|---|---|---|---|---|",
    ]
    for r in rows:
        h, a = r["harness"], r["airaccoon"]
        if h.get("error") or a.get("error"):
            agree = "excluded"
        else:
            agree = "yes" if h["hit"] == a["hit"] else "NO"
        legs = f"{h.get('fts_hit', '?')}/{h.get('vector_hit', '?')}"
        err = h.get("error") or a.get("error") or ""
        lines.append(
            f"| {r['id']} | {r.get('targetProjectId')}/{r.get('targetScope')} | "
            f"{_short(r['expectedHash'])} | {h['hit']} {h['f1']:.3f} | "
            f"{a['hit']} {a['f1']:.3f} | {agree} | {legs} | {err} |")
    hs, az = s["harness"], s["airaccoon"]
    cont = s["contingency"]
    mcc_line = f"{s['mcc']:.4f}" if s["mcc"] is not None else f"null ({s['mcc_reason']})"
    lines += [
        "",
        "## Aggregates",
        "",
        f"Queries: n={n}, paired (both legs clean)={paired}.",
        f"Harness: hit-rate={hs['hit_rate']:.3f} (n={hs['n']}), "
        f"mean F1={hs['mean_f1']:.3f}.",
        f"ai-raccoon: hit-rate={az['hit_rate']:.3f} (n={az['n']}), "
        f"mean F1={az['mean_f1']:.3f}.",
        f"Agreement table: both-hit a={cont['a']}, harness-only b={cont['b']}, "
        f"ai-raccoon-only c={cont['c']}, neither d={cont['d']} "
        f"(cells sum to {cont['a'] + cont['b'] + cont['c'] + cont['d']}).",
        f"Agreement MCC: {mcc_line}.",
        "Recompute path: results.json preserves per-query served hashes plus "
        "the harness fts_hit/vector_hit legs, so post-hoc relevance metrics "
        "recompute from the hash-preserving golden without new retrieval "
        "(rerun report.py on results.json). Singleton-F1 here is a parity "
        "verdict, not a relevance verdict.",
        "",
        "## Parity-gap discussion",
        "",
        "- Embedding gap (bank local ONNX SFR-Embedding-Code-400M_R vs "
        "harness public HF weights, same architecture): the per-query fts/vec "
        "column separates it — queries where FTS hits but the vector leg "
        "misses are embedding-gap evidence; where both legs miss, the fusion "
        "cannot recover regardless of weights.",
        f"- Structure gap: {context.get('headed', '?')} of "
        f"{context.get('store_rows', '?')} rows carry heading_path structure "
        "texts (structureAlpha=0.5 fuse; missing structure scores 0). "
        "Section-targeted misses on unheaded rows are structure-gap, not "
        "fusion-gap.",
        "- No harness knob was tuned to close either gap (plan: measure and "
        "report, never tune silently).",
        "",
        "### Provisional gap counts (P1; P2 refines into the classified taxonomy "
        "reusing these numbers)",
        "",
        _gap_table(s.get("gaps")),
        "",
        _exclusion_lines(results, context),
        "",
    ]
    return "\n".join(lines)


def _gap_table(gaps: dict | None) -> str:
    """Provisional c-cell counts table (harness-relative, bank-hit/harness-miss only)."""
    if not gaps:
        return ("Gap counts not recorded in this results.json "
                "(pre-P1 golden — rerun evaluate.py to populate summary.gaps).")
    rows = ["| bucket | n | reading |",
            "|---|---|---|",
            f"| c_cell (bank-hit/harness-miss of {gaps.get('n_paired', '?')} paired) "
            f"| {gaps.get('c_cell', '?')} | harness deficit under test |",
            f"| c_fts_only | {gaps.get('c_fts_only', '?')} | legs split: embedding-gap evidence |",
            f"| c_vec_only | {gaps.get('c_vec_only', '?')} | a leg had it, fusion lost it |",
            f"| c_both_legs | {gaps.get('c_both_legs', '?')} | both legs hit, fusion lost it |",
            f"| c_neither_leg | {gaps.get('c_neither_leg', '?')} | unrecoverable by fusion |",
            f"| c_unknown | {gaps.get('c_unknown', '?')} | leg columns absent (data gap) |"]
    return "\n".join(rows)


def _exclusion_lines(results: dict, context: dict) -> str:
    """Exclusion-manifest disclosure: seed-equal with the corpus header."""
    excluded = results.get("excludedProjects") or context.get("excluded_projects") or []
    if not excluded:
        return "Excluded projects: none — every corpus-targeted bucket was ingested."
    lines = [f"Excluded projects ({len(excluded)}, seed-equal with the corpus "
             "header excludedProjects — documented there, never ad hoc):"]
    for e in excluded:
        lines.append(f"- {e.get('projectId')} → {e.get('canonicalId')} "
                     f"({e.get('embeddedRows', '?')} embedded rows): {e.get('reason', '')}")
    return "\n".join(lines)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--results", required=True, help="results.json from evaluate.py")
    parser.add_argument("--out", required=True, help="markdown report target")
    parser.add_argument("--context", required=True,
                        help="JSON object: date/corpus/counts/model/buckets (eval provenance)")
    args = parser.parse_args(argv)
    results = json.loads(Path(args.results).read_text())
    text = render(results, json.loads(args.context))
    Path(args.out).write_text(text)
    print(f"report: {args.out} ({len(text)} chars, "
          f"n={results['summary']['n']})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
