"""Eval results -> markdown report (P3; import-safe, argparse + main(argv)).

The five required sections (asserted by test_llamaindex_harness_report.py):
Scope and routing (M1 decision), Method (M2/M3/M6 seams and metric forks),
Per-query results, Aggregates, Parity-gap discussion.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

# C9 query-composition signature. Adapted from docs/work/mmr_transfer_checks.py
# (the sibling session's census+strata script): markup/JSON debris and
# unbroken long tokens flag queries whose text is a tool-call artifact rather
# than a natural-language question. Never a hardcoded id list — ids shift with
# corpus regeneration, the signature does not.
DEBRIS_SIGNATURE = re.compile(r'[{}\[\]|<>\\]|://|```|"line"|@[a-z0-9-]+/|\S{60,}')


def debris_query(text: str) -> bool:
    """True when a query text carries the markup/JSON-debris signature."""
    return bool(DEBRIS_SIGNATURE.search(text or "")) or len(text or "") > 160


def stratify_rows(rows: list[dict]) -> dict[str, list[dict]]:
    """Split scored rows into debris/clean strata by query text (C9)."""
    strata: dict[str, list[dict]] = {"debris": [], "clean": []}
    for row in rows:
        strata["debris" if debris_query(row.get("query", "")) else "clean"].append(row)
    return strata


def stratum_stats(rows: list[dict]) -> dict:
    """Hit-rate/mean-F1 per stratum; rates are None for an empty stratum."""
    n = len(rows)
    if not n:
        return {"n": 0, "harness_hit_rate": None, "bank_hit_rate": None,
                "harness_mean_f1": None, "bank_mean_f1": None}
    return {
        "n": n,
        "harness_hit_rate": sum(r["harness"]["hit"] for r in rows) / n,
        "bank_hit_rate": sum(r["airaccoon"]["hit"] for r in rows) / n,
        "harness_mean_f1": sum(r["harness"]["f1"] for r in rows) / n,
        "bank_mean_f1": sum(r["airaccoon"]["f1"] for r in rows) / n,
    }


def stale_scored_intersection(results: dict) -> list[str]:
    """staleAnchors that were nevertheless scored — must be empty (C9 invariant)."""
    scored = {r["id"] for r in results.get("rows", [])}
    return sorted(s for s in (results.get("staleAnchors") or []) if s in scored)


def _short(h: str) -> str:
    return h[:12] if isinstance(h, str) else "-"


def _bucket_projects(buckets: list) -> list[str]:
    """Project ids from observed params.json bucket strings ('proj/scope')."""
    return sorted({b.split("/")[0] for b in buckets if isinstance(b, str) and "/" in b})


def _spelling(bucket: str, counts: dict) -> str:
    """'proj/scope (n)' when a count is known, the bare spelling otherwise."""
    n = counts.get(bucket) if isinstance(counts, dict) else None
    return f"{bucket} ({n})" if n is not None else bucket


def _scope_lines(results: dict, context: dict) -> list[str]:
    """Scope/routing prose: resolved project buckets vs extra shared-tier spellings.

    F3: observed bucket spellings include raw project_id folds (aib/shared),
    whose rows are global and ARE served by shared/all — listing them among
    resolved project buckets conflates the two."""
    resolved = [p for p in (results.get("resolvedBuckets") or []) if isinstance(p, str)]
    bucket_counts = context.get("bucket_counts") or {}
    if not bucket_counts:
        bucket_counts = {b: None for b in context.get("buckets", []) if isinstance(b, str)}
    extras = [b for b in bucket_counts
              if b.split("/")[-1] == "shared" and b.split("/")[0] not in set(resolved)]
    lines = ["M1: each query ran at its own corpus targetProjectId/targetScope on "
             "BOTH systems (no 75-row file-targeted restriction). The harness store "
             "ingests every targeted bucket (project buckets incl. custom scopes, "
             "plus the global shared tier)."]
    if resolved:
        lines.append(f"Resolved project buckets ({len(resolved)}): "
                     + ", ".join(resolved) + ".")
    else:
        lines.append("Project buckets in this store: "
                     + (", ".join(_bucket_projects(context.get("buckets", []))) or "?") + ".")
    if extras:
        lines.append("Additional shared-tier spellings present in the store (raw "
                     "project_id folds; those rows are global and are served by "
                     "shared/all queries): "
                     + ", ".join(_spelling(b, bucket_counts) for b in extras) + ".")
    return lines


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
    revision = results.get("modelRevision") or context.get("model_revision")
    copy_sha = results.get("copySnapshotSha256") or context.get("copy_snapshot_sha256")
    if revision or copy_sha:
        lines.append(
            f"Provenance: weights revision {str(revision or '?')[:12]}... "
            f"({results.get('modelBytes') or context.get('model_bytes_raw') or '?'} bytes); "
            f"bank copy {results.get('copyPath') or context.get('copy_path') or '?'} "
            f"(sha256 {str(copy_sha or '?')[:12]}...).")
    stale = results.get("staleAnchors") or context.get("stale_anchors") or []
    if n + len([s for s in stale if s not in {r["id"] for r in rows}]) < corpus_size:
        lines.append(f"Restriction: this is a documented SUBSET eval — {n} of "
                     f"{corpus_size} queries (integration gate / time-boxed run).")
    if stale:
        lines.append(f"Stale anchors ({len(stale)} — unhittable by either leg: "
                     f"absent anchors still score into d, null-anchored rows are "
                     f"filtered pre-eval and unscored): {', '.join(stale)}.")
    stale_scored = stale_scored_intersection(results)
    if stale_scored:
        raise ValueError(
            f"stale-anchor invariant broken: staleAnchors ∩ scored = {stale_scored}; "
            "a filtered-stale id must never appear in rows")
    lines.append("Stale-anchor invariant (asserted): staleAnchors ∩ scored = ∅.")
    lines += [
        "",
        "## Scope and routing",
        "",
        *_scope_lines(results, context),
        "Both systems ran at a uniform limit 8 (the corpus records "
        "searchLimit=8 for all 100 queries; candidate window "
        "max(limit*3,100)=100 either way).",
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
        *_stratification_lines(rows),
        "## Parity-gap discussion",
        "",
        "- Embedding gap (bank local ONNX SFR-Embedding-Code-400M_R vs "
        "harness public HF weights, same architecture): the same model name does "
        "not imply the same vectors — the bank's manifest pins CLS pooling while "
        "the HF snapshot ships no sentence-transformers config, so the harness "
        "falls back to that library's mean-pooling default; measured stored-vector "
        "agreement for the same text is partial (C10 evidence doc). The per-query "
        "fts/vec column locates the divergence, but the label is per row: P2 AC2 "
        "resolves fusion-drop vs embedding-gap, and C10 proved the shared-scope "
        "rows are fusion-drop. Where both legs miss, the fusion cannot recover "
        "regardless of weights.",
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
        *_shared_scope_lines(rows),
        _exclusion_lines(results, context),
        "",
    ]
    return "\n".join(lines)


def _stratification_lines(rows: list[dict]) -> list[str]:
    """C9: recomputed query-composition stratification + composition disclosure."""
    strata = stratify_rows(rows)
    stats = {name: stratum_stats(members) for name, members in strata.items()}

    def _fmt(value: float | None) -> str:
        return "—" if value is None else f"{value:.3f}"

    lines = [
        "### Query-composition stratification (recomputed from the scored corpus rows)",
        "",
        "| stratum | n | harness hit-rate | ai-raccoon hit-rate | harness mean F1 |"
        " ai-raccoon mean F1 |",
        "|---|---|---|---|---|---|",
    ]
    for name, reading in (("debris", "query text carries markup/JSON debris"),
                          ("clean", "natural-language query")):
        s = stats[name]
        lines.append(
            f"| {name} ({reading}) | {s['n']} | {_fmt(s['harness_hit_rate'])} | "
            f"{_fmt(s['bank_hit_rate'])} | {_fmt(s['harness_mean_f1'])} | "
            f"{_fmt(s['bank_mean_f1'])} |")
    lines += [
        "",
        "Disclosure: relevance-flavoured readings (\"bank finds what harness "
        "misses\", any embedding-gap narrative) are **composition-sensitive** — "
        "the debris stratum is a tool-call-artifact subset whose stratified rates "
        "differ from the clean stratum, so aggregate gaps partly reflect corpus "
        "composition. The parity/pipeline reading (identical fusion pipeline on "
        "both legs, exact-hash hits) stands. The split is recomputed here from the "
        "scored rows' query text by signature (regex adapted from "
        "docs/work/mmr_transfer_checks.py), never a hardcoded id list.",
        "",
    ]
    return lines


def _shared_scope_lines(rows: list[dict]) -> list[str]:
    """C10: shared-scope rows traced to the Take(8) limit, never embedding-gap labels."""
    shared = [r["id"] for r in rows if r.get("targetScope") == "shared"]
    if not shared:
        return []
    return [
        f"C10 trace note: shared-scope rows ({', '.join(shared)}) were traced "
        "stage-by-stage — the anchor survived per-leg content-dedupe, RRF, affinity "
        "and the relative floor, and was dropped by Take(8) after dual-leg candidates "
        "outranked it (the harness/bank vector scores diverge at the embedding seam, "
        "measured). These rows are fusion-drop; never mapped to \"embedding-gap "
        "evidence\".",
        "",
    ]


def _gap_table(gaps: dict | None) -> str:
    """Provisional c-cell counts table (harness-relative, bank-hit/harness-miss only)."""
    if not gaps:
        return ("Gap counts not recorded in this results.json "
                "(pre-P1 golden — rerun evaluate.py to populate summary.gaps).")
    rows = ["| bucket | n | reading |",
            "|---|---|---|",
            f"| c_cell (bank-hit/harness-miss of {gaps.get('n_paired', '?')} paired) "
            f"| {gaps.get('c_cell', '?')} | harness deficit under test |",
            f"| c_fts_only | {gaps.get('c_fts_only', '?')} | legs split: FTS held it,"
            " vector leg missed — P2 AC2 resolves fusion-drop vs embedding-gap per row"
            " (C10: not per se embedding-gap) |",
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
                     f"(committed={e.get('committedRows', '?')}, "
                     f"shared={e.get('sharedRows', '?')}, "
                     f"embedded={e.get('embeddedRows', '?')}): {e.get('reason', '')}")
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
