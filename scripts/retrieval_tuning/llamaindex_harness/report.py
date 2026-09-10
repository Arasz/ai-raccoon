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

from . import evaluate
from .evaluate import debris_query

# C9 query-composition signature now lives beside the AC2 taxonomy (one
# definition, shared with evaluate.gap_label); re-exported for callers.


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
        "| id | target | exp | harness hit/F1 | ai-raccoon hit/F1 | agree | gap |"
        " fts/vec | error |",
        "|---|---|---|---|---|---|---|---|---|",
    ]
    gap_labels = evaluate.gap_columns(rows)
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
            f"{a['hit']} {a['f1']:.3f} | {agree} | {gap_labels.get(r['id'], 'unknown')} | "
            f"{legs} | {err} |")
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
        *_repeat_lines(results),
        *_stratification_lines(rows),
        "## Parity-gap discussion",
        "",
        "- Embedding seam (C13, owner decision pending): the bank's ONNX manifest pins "
        "CLS pooling while the HF snapshot ships no sentence-transformers config, so the "
        "harness falls back to that library's mean-pooling default — same weights, "
        "different vectors (measured stored-vector cos 0.50–0.63 for identical text). "
        "The taxonomy below labels rows against MEASURED leg positions and never claims "
        "the harness embedding equals the bank's: a row whose FTS window held the anchor "
        "is `fusion` even when the vector leg missed (C10), and only rows where BOTH "
        "windows missed are `embedding`/`unrecoverable` (composition-aware, C9).",
        f"- Structure gap: {context.get('headed', '?')} of "
        f"{context.get('store_rows', '?')} rows carry heading_path structure "
        "texts (structureAlpha=0.5 fuse; missing structure scores 0). "
        "Section-targeted misses on unheaded rows are structure-gap, not "
        "fusion-gap.",
        "- No harness knob was tuned to close either gap (plan: measure and "
        "report, never tune silently).",
        "",
        *_classified_gap_table(rows),
        *_shared_scope_lines(rows),
        _exclusion_lines(results, context),
        "",
    ]
    return "\n".join(lines)


def _repeat_lines(results: dict) -> list[str]:
    """P2 AC1: repeat-run spread table + unstable ids + what the repeats guard.

    Absent for single-run artifacts (no repeats block), so golden reports from
    a one-shot run stay unchanged."""
    repeats = results.get("repeats")
    if not repeats:
        return []
    metrics = repeats.get("metrics") or {}

    def _spread(name: str) -> str:
        m = metrics.get(name) or {}
        if m.get("mean") is None:
            return "—"
        return f"{m['mean']:.4f} [{m['min']:.4f}–{m['max']:.4f}]"

    unstable = repeats.get("unstable") or {}
    revision = repeats.get("modelRevision") or results.get("modelRevision") or "?"
    nbytes = repeats.get("modelBytes") or results.get("modelBytes") or "?"
    return [
        f"### Repeat-run spread ({repeats.get('n', '?')} independent runs, fresh bank "
        "scratch per repeat; min/max are across runs)",
        "",
        "| metric | mean [min–max] |",
        "|---|---|",
        f"| harness hit-rate | {_spread('harness_hit_rate')} |",
        f"| ai-raccoon hit-rate | {_spread('airaccoon_hit_rate')} |",
        f"| harness mean F1 | {_spread('harness_mean_f1')} |",
        f"| ai-raccoon mean F1 | {_spread('airaccoon_mean_f1')} |",
        f"| agreement MCC | {_spread('mcc')} |",
        "",
        f"Unstable served-set query ids across repeats: harness="
        f"{', '.join(unstable.get('harness') or []) or 'none'}, ai-raccoon="
        f"{', '.join(unstable.get('airaccoon') or []) or 'none'}. A non-empty list "
        "is jitter a golden must not tolerate — the run exits nonzero and never "
        "tolerance-blesses a moved anchor.",
        f"Variance guarded: weight drift (weights revision "
        f"{str(revision)[:12]}... / {nbytes} bytes pinned), server nondeterminism "
        "(fresh scratch copy + fresh server per repeat), and bank drift (row-stability "
        "snapshot per repeat, C11). Process-level harness determinism remains the P1 "
        "C7 double-run gate; repeats here share one read-only harness store.",
        "",
    ]


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


def _classified_gap_table(rows: list[dict]) -> list[str]:
    """P2 AC2: the single classified taxonomy replacing the P1 provisional table.

    Labels come from evaluate.gap_label over the window-level fts_hit/vector_hit
    columns; conservation (cells sum to paired) is structural and the unknown
    share is asserted against its 5% cap before publication."""
    tax = evaluate.gap_taxonomy(rows)
    if tax["unknownShare"] > 0.05:
        raise ValueError(
            f"gap taxonomy unknown share {tax['unknownShare']:.1%} > 5% cap "
            f"({tax['cells']['unknown']} of {tax['n_paired']} paired): the "
            "classifier or the leg columns failed, not the retrieval")
    cells = tax["cells"]
    readings = {
        "none": "no harness deficit under test (harness hit, or the bank missed "
                "too — agreement is never a deficit)",
        "fusion": "a leg's candidate window held the anchor; the fused pipeline "
                  "did not serve it (dedupe/RRF/floor/Take(8))",
        "embedding": "no leg window held it on clean prose — representation side "
                     "(the harness mean-pooling vs bank CLS seam qualifies this, C13)",
        "unrecoverable": "no leg window held it on a debris/tool-call artifact — "
                         "no representation recovers it",
        "unknown": "leg diagnostics absent (data gap)",
    }
    lines = [
        "### Classified gap taxonomy (the single table; P1 provisional counts "
        "consumed, never redefined)",
        "",
        "Labels are computed per row from the existing `fts_hit`/`vector_hit` "
        "columns, which are CANDIDATE-WINDOW hits (max(limit*3,100)=100), never "
        "each leg's top-8: a window-rank 9–100 hit counts as held and therefore "
        "labels `fusion`, not `embedding`. Harness-relative and restricted to the "
        "c-cell (bank-hit/harness-miss); agreement is never misattributed as "
        "deficit.",
        "",
        "| label | n | reading |",
        "|---|---|---|",
    ]
    for label in evaluate.GAP_LABELS:
        lines.append(f"| {label} | {cells[label]} | {readings[label]} |")
    lines.append(
        f"| c_cell (bank-hit/harness-miss of {tax['n_paired']} paired) | "
        f"{tax['c_cell']} | deficit labels sum; unknown share "
        f"{tax['unknownShare']:.1%} (cap 5%) |")
    return lines + [""]


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
