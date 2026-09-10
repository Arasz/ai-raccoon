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
import hashlib
import json
import math
import os
import re
import sqlite3
import subprocess
import sys
import threading
import uuid
from pathlib import Path

from . import scopes
from retrieval_tuning import repo_data

EVAL_LIMIT = repo_data.KNOBS["EVAL_LIMIT"]


def _sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with Path(path).open("rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def model_revision_check(recorded: str | None, revision: str, nbytes: int) -> dict:
    """Eval-side freeze gate (C3/F4): the query encoder must be the store's weights.

    A cache advance between ingest and eval would silently change every query
    vector and the golden, so a missing record, a mismatched revision, or an
    empty cache is loud, never skipped."""
    if not recorded:
        raise ValueError("store params carry no modelRevision — cannot verify the frozen weights")
    if revision != recorded:
        raise ValueError(
            f"model weights revision {revision!r} != store's embedded revision {recorded!r}: "
            "query vectors would drift from the store — reuse the pinned cache")
    if nbytes <= 0:
        raise ValueError(f"model weights resolve empty (bytes={nbytes})")
    return {"modelRevision": revision, "modelBytes": nbytes}


def scratch_copy_check(db_path: Path, recorded_sha: str | None,
                       recorded_entries: int | None) -> tuple[dict, list[str], list[str]]:
    """(provenance, failures, warnings) for the bank-leg scratch copy, pre-run (C3).

    The harness leg reads the store (built from --copy) while the bank leg
    reads scratch-data-root/memory.db; row divergence compares two universes
    and is a failure. A SHA-only change is an access-bump mutation from a
    prior run — warned, with the row-count guard still in force."""
    db_path = Path(db_path)
    if not db_path.exists():
        return {}, [f"scratch data root has no {db_path.name} for the bank leg"], []
    sha = _sha256_file(db_path)
    try:
        conn = sqlite3.connect(f"file:{db_path.resolve()}?mode=ro", uri=True)
        try:
            rows = conn.execute("SELECT count(*) FROM entries").fetchone()[0]
        finally:
            conn.close()
    except sqlite3.Error as exc:
        return {}, [f"cannot read scratch copy entries: {exc}"], []
    failures, warnings = [], []
    if recorded_entries is not None and rows != recorded_entries:
        failures.append(f"scratch copy rows {rows} != store copy rows {recorded_entries}")
    if recorded_sha and sha != recorded_sha:
        warnings.append(f"scratch copy sha {sha[:12]}... != store copy sha {recorded_sha[:12]}... "
                        "(row count matches: access-bump mutation from a prior run)")
    return ({"scratchPath": str(db_path), "scratchSnapshotSha256": sha, "scratchRows": rows},
            failures, warnings)


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
    """P1 provisional leg-gap counts + the P2 classified taxonomy over the same rows.

    Computed ONLY on the c-cell (bank-hit/harness-miss) from the existing
    fts_hit/vector_hit columns — never misattributing agreement as deficit.
    Conservation: the five c_* buckets sum to c_cell; the taxonomy cells sum to
    paired. Rows without leg columns land in c_unknown (a data gap, not a
    retrieval gap)."""
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
    gaps["taxonomy"] = gap_taxonomy(rows)
    return gaps


# C9 debris signature (shared with report.py so the taxonomy and the
# stratification split on ONE definition, never two).
DEBRIS_SIGNATURE = re.compile(r'[{}\[\]|<>\\]|://|```|"line"|@[a-z0-9-]+/|\S{60,}')

GAP_LABELS = ("none", "fusion", "embedding", "unrecoverable", "unknown")


def debris_query(text: str) -> bool:
    """True when a query text carries the markup/JSON-debris signature (C9)."""
    return bool(DEBRIS_SIGNATURE.search(text or "")) or len(text or "") > 160


def gap_label(row: dict) -> str:
    """P2 AC2 per-row label over the EXISTING window-level leg columns.

    'none'          — not a harness deficit under test (harness hit, or the
                      bank missed too: agreement is never a deficit)
    'fusion'        — a leg's candidate window held the anchor but the fused
                      pipeline did not serve it (dedupe/RRF/floor/Take(8))
    'embedding'     — no leg window held it, on a clean natural-language query
    'unrecoverable' — no leg window held it, on a debris/tool-call artifact
    'unknown'       — leg diagnostics absent (an unpaired error row too)

    The leg columns are window hits: build_harness_fn reads the FULL leg lists
    at candidate_window(8)=100, so window-rank 9-100 hits count as held."""
    harness, bank = row.get("harness", {}), row.get("airaccoon", {})
    if harness.get("error") or bank.get("error"):
        return "unknown"
    if not (bank.get("hit") == 1 and harness.get("hit") == 0):
        return "none"
    fts_hit, vec_hit = harness.get("fts_hit"), harness.get("vector_hit")
    if fts_hit is None or vec_hit is None:
        return "unknown"
    if fts_hit == 1 or vec_hit == 1:
        return "fusion"
    return "unrecoverable" if debris_query(row.get("query", "")) else "embedding"


def gap_columns(rows: list[dict]) -> dict[str, str]:
    """Per-row taxonomy labels keyed by query id (per-row evidence, every row)."""
    return {r["id"]: gap_label(r) for r in rows}


def gap_taxonomy(rows: list[dict]) -> dict:
    """AC2 counts over PAIRED rows; the five cells sum to n_paired.

    'none' is the c-cell complement, so conservation holds by construction.
    unknownShare is the classifier-health bar (<= 5% else the classifier or
    the data failed, not retrieval)."""
    paired = [r for r in rows
              if not r["harness"].get("error") and not r["airaccoon"].get("error")]
    cells = {label: 0 for label in GAP_LABELS}
    for row in paired:
        cells[gap_label(row)] += 1
    c_cell = sum(n for label, n in cells.items() if label != "none")
    return {"n_paired": len(paired), "c_cell": c_cell, "cells": cells,
            "unknownShare": (cells["unknown"] / len(paired)) if paired else 0.0}


def _served_sets(run: dict, side: str) -> dict[str, tuple]:
    """Per-query served hash SETS (order-insensitive) for one leg."""
    return {row["id"]: tuple(sorted(set(row[side].get("hashes") or [])))
            for row in run.get("rows", [])}


def summarize_repeats(runs: list[dict]) -> dict:
    """P2 AC1: aggregate N independent run_eval results.

    Per-metric mean/min/max over runs; MCC is null-through (None runs are
    skipped and counted, never coerced to 0). Served SETS are diffed per query
    per leg across runs — a query whose set differs (or that is missing from a
    run) is unstable jitter, reported by id. Order-only differences are stable.
    Repeats with a FRESH bank scratch guard weight drift, server
    nondeterminism and bank drift; the article pins weights revision/bytes."""
    if not runs:
        raise ValueError("summarize_repeats: no runs")
    metrics: dict = {}
    for name, side, key in (("harness_hit_rate", "harness", "hit_rate"),
                            ("airaccoon_hit_rate", "airaccoon", "hit_rate"),
                            ("harness_mean_f1", "harness", "mean_f1"),
                            ("airaccoon_mean_f1", "airaccoon", "mean_f1")):
        values = [float(run["summary"][side][key]) for run in runs]
        metrics[name] = {"mean": sum(values) / len(values),
                         "min": min(values), "max": max(values)}
    mcc_values = [run["summary"].get("mcc") for run in runs]
    present = [float(v) for v in mcc_values if v is not None]
    metrics["mcc"] = {
        "mean": sum(present) / len(present) if present else None,
        "min": min(present) if present else None,
        "max": max(present) if present else None,
        "nullCount": len(mcc_values) - len(present)}

    unstable: dict[str, list[str]] = {}
    for side in ("harness", "airaccoon"):
        per_run = [_served_sets(run, side) for run in runs]
        ids = sorted({row["id"] for run in runs for row in run.get("rows", [])})
        unstable[side] = [
            qid for qid in ids
            if any(run_sets.get(qid) != per_run[0].get(qid) for run_sets in per_run[1:])]
    return {"n": len(runs), "metrics": metrics, "unstable": unstable}


def fresh_scratch_copy(base_db: Path, data_root: Path) -> Path:
    """The copy half of the scratch composition (P3: one home in
    retrieval_tuning.scratch; re-exported here for the repeat-loop gates)."""
    from retrieval_tuning.scratch import fresh_scratch_copy as _impl  # noqa: PLC0415

    return _impl(base_db, data_root)


def _sibling_import(name: str):
    """Import a plain script beside the package (make_quiesced_scratch, memwatch)."""
    import importlib  # noqa: PLC0415 — stdlib
    sibling_dir = Path(__file__).resolve().parents[1]
    if str(sibling_dir) not in sys.path:
        sys.path.insert(0, str(sibling_dir))
    return importlib.import_module(name)


def scratch_row_stability(db_path: Path) -> dict:
    """C11 run recipe: entries count + max(created_at/updated_at), read-only.

    The SQL lives in make_quiesced_scratch.row_stability (one definition, the
    helper the quiesced base was built with); this opens the scratch copy RO."""
    row_stability = _sibling_import("make_quiesced_scratch").row_stability
    conn = sqlite3.connect(f"file:{Path(db_path).resolve()}?mode=ro", uri=True)
    try:
        return row_stability(conn)
    finally:
        conn.close()


def _default_tree_rss_mb() -> int:
    return _sibling_import("memwatch").tree_rss_mb(os.getpid())


class RssSampler:
    """Keep the process tree's peak RSS while a repeat runs (P2 AC1 evidence).

    Periodic sampling is a backstop, not a cgroup: a spike shorter than the
    interval can be missed (the same limitation memwatch documents).
    tree_rss_mb is reused from memwatch so the ps sampling has one owner."""

    def __init__(self, rss_fn=None, interval: float = 2.0) -> None:
        self._rss_fn = rss_fn or _default_tree_rss_mb
        self._interval = interval
        self._stop = threading.Event()
        self._thread: threading.Thread | None = None
        self.peak = 0

    def _sample_loop(self) -> None:
        while not self._stop.is_set():
            try:
                self.peak = max(self.peak, int(self._rss_fn()))
            except (OSError, ValueError, subprocess.SubprocessError):
                pass
            self._stop.wait(self._interval)

    def __enter__(self) -> "RssSampler":
        self._thread = threading.Thread(target=self._sample_loop, daemon=True)
        self._thread.start()
        return self

    def __exit__(self, *exc) -> bool:
        self._stop.set()
        if self._thread is not None:
            self._thread.join(timeout=10)
        return False


def compose_results(out: dict, stale_anchors: list, model_provenance: dict,
                    store_params: dict, session_id: str,
                    repeats: dict | None = None) -> dict:
    """Assemble the written results.json (C3/P1 provenance + P2 blocks).

    staleAnchors is ALWAYS present, empty list included; the repeat block is
    added only in base mode so the single-run artifact shape is unchanged."""
    composed = dict(out)
    composed["sessionId"] = session_id
    composed["staleAnchors"] = list(stale_anchors)
    composed.update(model_provenance)
    composed["copyPath"] = store_params.get("copyPath")
    composed["copySnapshotSha256"] = store_params.get("copySnapshotSha256")
    composed["corpusSnapshotSha256"] = store_params.get("corpusSnapshotSha256")
    composed["excludedProjects"] = store_params.get("excludedProjects", [])
    composed["resolvedBuckets"] = store_params.get("resolvedBuckets", [])
    if repeats is not None:
        composed["repeats"] = repeats
    return composed


def run_once(entries: list[dict], harness_fn, scratch_data_root, binary: str,
             session_id: str) -> dict:
    """One paired eval against one scratch data root (fresh scratch server)."""
    from retrieval_tuning.server import start_server  # noqa: PLC0415 — needs scripts/src
    with start_server(scratch_data_root, binary=binary) as server:
        print(f"scratch server on port {server.port} (never 7721)", flush=True)
        return run_eval(entries, harness_fn, build_airaccoon_fn(server, session_id))


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
        scope = entry.get("targetScope") or scopes.DEFAULT_QUERY_SCOPE
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

    client = server.client

    def fn(entry: dict) -> dict:
        try:
            parsed = client._call_tool("memory_search", {
                "projectId": entry.get("targetProjectId") or "ai-raccoon",
                "query": entry["query"],
                # Corpus custom -> bank project (SearchContexts.cs: project
                # covers custom labels; the bank refuses scope=custom).
                "scope": scopes.normalize_scope(
                    entry.get("targetScope") or scopes.DEFAULT_QUERY_SCOPE),
                "limit": EVAL_LIMIT,
                "minRelativeScore": repo_data.KNOBS["PARAMS"]["minRelativeScore"],
                "kind": repo_data.KNOBS["PARAMS"]["kind"],      # the same post-floor, post-limit shape
                "sessionId": session_id,
            })
            results = MCPClient._extract_results(
                parsed, kind=repo_data.KNOBS["PARAMS"]["kind"])
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
    taxonomy = (out.get("summary", {}).get("gaps") or {}).get("taxonomy")
    if taxonomy is None:
        taxonomy = gap_taxonomy(out.get("rows", []))
    if taxonomy["unknownShare"] > 0.05:
        failures.append(
            f"gap taxonomy unknown share {taxonomy['unknownShare']:.1%} > 5% cap "
            f"({taxonomy['cells']['unknown']} of {taxonomy['n_paired']} paired): "
            "the classifier or the leg columns failed, not the retrieval")
    return failures


def anchor_verdict(n_entries: int, stale_ids: list) -> str:
    """P2 AC3: 'refuse' when no anchor resolves, 'warn' when some, else 'clean'.

    Pure so the gate choice is testable: a store where every corpus anchor is
    stale is the wrong store/copy (refuse, never a silent all-miss run); a
    partial stale set is upstream re-chunking (warn-and-record)."""
    n_stale = len(stale_ids)
    if n_entries > 0 and n_stale >= n_entries:
        return "refuse"
    return "warn" if n_stale else "clean"


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


def _checked_scratch_check(db_path: Path, store_params: dict, label: str = ""):
    """scratch_copy_check with warn/fail printing; None means the run must stop."""
    prov, failures, warnings = scratch_copy_check(
        db_path, store_params.get("copySnapshotSha256"),
        (store_params.get("counts") or {}).get("copyEntries"))
    prefix = f"{label}: " if label else ""
    for warning in warnings:
        print(f"WARNING: {prefix}{warning}", flush=True)
    if failures:
        for failure in failures:
            print(f"FAIL: {prefix}{failure}")
        return None
    return prov


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--corpus", required=True)
    parser.add_argument("--store-dir", required=True)
    parser.add_argument("--scratch-data-root", default=None,
                        help="data-root seeded from a bank copy; a scratch server serves it "
                             "(M2, single run)")
    parser.add_argument("--scratch-base", default=None,
                        help="quiesced base bank DB; every repeat starts from a FRESH copy")
    parser.add_argument("--scratch-root", default=None,
                        help="parent directory for per-repeat scratch data roots")
    parser.add_argument("--repeats", type=int, default=1,
                        help="N independent runs, each from a fresh copy of --scratch-base")
    parser.add_argument("--binary", default="ai-raccoon")
    parser.add_argument("--out", required=True, help="results.json target")
    parser.add_argument("--limit-queries", type=int, default=None)
    parser.add_argument("--offline", action="store_true",
                        help="reuse cached HF weights; fail instead of downloading")
    args = parser.parse_args(argv)

    if args.repeats < 1:
        parser.error("--repeats must be >= 1")
    base_mode = args.scratch_base is not None or args.scratch_root is not None
    if base_mode and args.scratch_data_root:
        parser.error("--scratch-data-root cannot be combined with "
                     "--scratch-base/--scratch-root")
    if base_mode and not (args.scratch_base and args.scratch_root):
        parser.error("--scratch-base and --scratch-root are required together")
    if args.repeats > 1 and not base_mode:
        parser.error("--repeats > 1 requires --scratch-base and --scratch-root")
    if not base_mode and not args.scratch_data_root:
        parser.error("one scratch mode is required: --scratch-data-root, or "
                     "--scratch-base + --scratch-root")

    _, repo = _self_paths()
    sys.path.insert(0, str(repo / "scripts" / "src"))

    entries = json.loads(Path(args.corpus).read_text())
    if isinstance(entries, dict):  # header-shaped corpus: header + queries
        entries = entries["queries"]
    if args.limit_queries is not None:
        entries = entries[:args.limit_queries]
    store_params = json.loads((Path(args.store_dir) / "params.json").read_text())

    # C3 provenance gates BEFORE any retrieval: frozen weights, same copy universe.
    try:
        from . import ingest as ingest_mod  # noqa: PLC0415 — lazy: keeps module import-safe
        revision, nbytes = ingest_mod.model_weights_info(
            store_params.get("model") or ingest_mod.MODEL_NAME)
        model_provenance = model_revision_check(store_params.get("modelRevision"),
                                                revision, nbytes)
        ingest_mod.check_pinned_revision(revision)
    except (ValueError, ImportError) as exc:
        print(f"FAIL: model provenance: {exc}")
        return 1

    if not base_mode:
        scratch_prov = _checked_scratch_check(
            Path(args.scratch_data_root) / "memory.db", store_params)
        if scratch_prov is None:
            return 1
        print(f"model: revision={model_provenance['modelRevision'][:12]}... "
              f"bytes={model_provenance['modelBytes']} | scratch copy "
              f"sha={scratch_prov['scratchSnapshotSha256'][:12]}... "
              f"rows={scratch_prov['scratchRows']} | copy path "
              f"{store_params.get('copyPath')}", flush=True)
    else:
        print(f"model: revision={model_provenance['modelRevision'][:12]}... "
              f"bytes={model_provenance['modelBytes']} | scratch base "
              f"{args.scratch_base} ({args.repeats} fresh copies) | copy path "
              f"{store_params.get('copyPath')}", flush=True)

    scorable, null_anchors = partition_null_anchors(entries)
    stale_anchors = check_anchors_resolve(scorable, Path(args.store_dir))
    stale_anchors = sorted(set(stale_anchors) | set(null_anchors))
    verdict = anchor_verdict(len(entries), stale_anchors)
    if verdict == "refuse":
        print(f"FAIL: no corpus anchor resolves against this store — wrong store/copy? "
              f"({len(stale_anchors)} stale of {len(entries)})")
        return 1
    if verdict == "warn":
        print(f"WARNING: {len(stale_anchors)} stale anchors (re-chunked upstream, "
              f"unhittable by either leg): {stale_anchors}", flush=True)
    session_id = f"llamaindex-harness-{uuid.uuid4().hex[:12]}"

    harness_fn = build_harness_fn(Path(args.store_dir), offline=args.offline)
    repeats_block = None
    out = None
    try:
        if not base_mode:
            out = run_once(scorable, harness_fn, args.scratch_data_root,
                           args.binary, session_id)
            failures = eval_gate_failures(out)
            if failures:
                for failure in failures:
                    print(f"FAIL: {failure}")
                return 1
            Path(args.out).write_text(json.dumps(compose_results(
                out, stale_anchors, model_provenance, store_params, session_id),
                indent=2))
        else:
            from retrieval_tuning.scratch import ScratchRefused, scratch_server  # noqa: PLC0415

            base = Path(args.scratch_base)
            runs: list[dict] = []
            run_meta: list[dict] = []
            for i in range(1, args.repeats + 1):
                data_root = Path(args.scratch_root) / f"repeat-{i}"
                state: dict = {}

                def prepare_scratch(db, _label=f"repeat {i}", _state=state):
                    # Copy done, no server yet: the refusal/mutation checks run
                    # here so a refused scratch never serves (P3 composition).
                    _state["prov"] = _checked_scratch_check(db, store_params,
                                                            label=_label)
                    if _state["prov"] is None:
                        raise ScratchRefused(f"{_label}: scratch copy check failed")
                    _state["before"] = scratch_row_stability(db)

                try:
                    with scratch_server(base, data_root, binary=args.binary,
                                        before_start=prepare_scratch) as server:
                        print(f"scratch server on port {server.port} (never 7721)",
                              flush=True)
                        with RssSampler() as sampler:
                            out_i = run_eval(scorable, harness_fn,
                                             build_airaccoon_fn(server, session_id))
                except ScratchRefused:
                    return 1
                after = scratch_row_stability(data_root / "memory.db")
                scratch_prov, before = state["prov"], state["before"]
                failures = eval_gate_failures(out_i)
                if failures:
                    for failure in failures:
                        print(f"FAIL: repeat {i}: {failure}")
                    return 1
                if before != after:
                    print(f"FAIL: repeat {i} row stability changed: {before} -> {after} "
                          "(the bank leg wrote rows through the eval)")
                    return 1
                runs.append(out_i)
                run_meta.append({
                    "repeat": i, "scratchDataRoot": str(data_root),
                    "scratchSnapshotSha256": scratch_prov["scratchSnapshotSha256"],
                    "scratchRows": scratch_prov["scratchRows"],
                    "rowStability": before, "peakRssMb": sampler.peak})
                repeats_block = summarize_repeats(runs)
                repeats_block.update({
                    "modelRevision": model_provenance["modelRevision"],
                    "modelBytes": model_provenance["modelBytes"],
                    "scratchBase": str(base), "scratchRoot": str(args.scratch_root),
                    "runs": list(run_meta)})
                # Checkpoint after every repeat: a long campaign keeps its runs.
                Path(args.out).write_text(json.dumps(compose_results(
                    runs[0], stale_anchors, model_provenance, store_params,
                    session_id, repeats=repeats_block), indent=2))
            out = runs[0]
    finally:
        harness_fn.close()  # type: ignore[attr-defined]

    s = out["summary"]
    repeat_note = f" repeats={repeats_block['n']}" if repeats_block else ""
    print(f"eval: n={s['n']} paired={s['n_paired']} stale={len(stale_anchors)}"
          f"{repeat_note} "
          f"harness hit-rate={s['harness']['hit_rate']:.3f} f1={s['harness']['mean_f1']:.3f} | "
          f"ai-raccoon hit-rate={s['airaccoon']['hit_rate']:.3f} f1={s['airaccoon']['mean_f1']:.3f} | "
          f"mcc={s['mcc']} cont={s['contingency']}")
    if repeats_block is not None:
        unstable = repeats_block["unstable"]
        if unstable["harness"] or unstable["airaccoon"]:
            print(f"FAIL: unstable served sets across repeats: "
                  f"harness={unstable['harness']} airaccoon={unstable['airaccoon']}")
            return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
