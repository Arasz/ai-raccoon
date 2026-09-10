#!/usr/bin/env python3
"""Static re-derivation of the external-validation findings (no model, seconds).

Reproduces, from the frozen artifacts only:
  1. the markup-debris census and its stratified hit/F1 split (recomputed;
     25/99 on the historical pair, 23/99 on the refreshed golden);
  2. the C026 stale-but-scored sensitivity (all 99 vs drop-stale 98);
  3. the shared-scope 0/3 anomaly, including the direct fts.db rank of the
     expected hash under the same expression/scope the harness uses;
  4. corpus searchLimit audit;
  5. params buckets vs excluded-projects manifest;
  6. bank-copy watch/count/drift check.

Run (from this checkout; results/corpus default to this lane):
  python3 docs/work/mmr_transfer_checks.py

C9 note: section 2 (C026 stale-but-scored sensitivity) is historical — the
refreshed corpus has no stale-scored row and report.py now asserts
staleAnchors ∩ scored == ∅; section 1's census+strata signature is what
llamaindex_harness.report adapted for the eval report.
"""
from __future__ import annotations

import argparse
import json
import math
import re
import sqlite3
import sys
from collections import Counter
from pathlib import Path

LANE = Path(__file__).resolve().parents[2]
DEBRIS = re.compile(r'[{}\[\]|<>\\]|://|```|"line"|@[a-z0-9-]+/|\S{60,}')
FLAGGED = []


def debris(qid: str, text: str) -> bool:
    if bool(DEBRIS.search(text)) or len(text) > 160:
        FLAGGED.append(qid)
        return True
    return False


def mcc(a: int, b: int, c: int, d: int):
    den = math.sqrt((a + b) * (a + c) * (b + d) * (c + d))
    return None if den == 0 else (a * d - b * c) / den


def stats(rows, label: str):
    n = len(rows)
    if not n:
        print(f"  {label:<34} n=0")
        return
    hh = sum(x["harness"]["hit"] for x in rows)
    ah = sum(x["airaccoon"]["hit"] for x in rows)
    a = sum(1 for x in rows if x["harness"]["hit"] and x["airaccoon"]["hit"])
    b = sum(1 for x in rows if x["harness"]["hit"] and not x["airaccoon"]["hit"])
    c = sum(1 for x in rows if not x["harness"]["hit"] and x["airaccoon"]["hit"])
    d = sum(1 for x in rows if not x["harness"]["hit"] and not x["airaccoon"]["hit"])
    m = mcc(a, b, c, d)
    print(f"  {label:<34} n={n:<3} harness={hh/n:.4f} bank={ah/n:.4f} "
          f"gap={(ah-hh)/n:+.4f} a={a} b={b} c={c} d={d} mcc={m:.4f}")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--results", type=Path,
                    default=LANE / "docs" / "work" / "results-f1.json")
    ap.add_argument("--corpus", type=Path,
                    default=LANE / "scripts" / "retrieval_tuning" / "corpora"
                    / "project-corpus-100.json")
    ap.add_argument("--fts-db", type=Path, default=Path("/tmp/p1-full-store/fts.db"))
    ap.add_argument("--params", type=Path, default=Path("/tmp/p1-full-store/params.json"))
    ap.add_argument("--copy", type=Path, default=Path("/tmp/p1-live-copy.db"))
    ap.add_argument("--harness-dir", type=Path,
                    default=LANE / "scripts" / "retrieval_tuning")
    args = ap.parse_args()

    results = json.loads(args.results.read_text(encoding="utf-8"))
    rows = results["rows"]
    corpus = json.loads(args.corpus.read_text(encoding="utf-8"))
    entries = {q["id"]: q for q in corpus["queries"]}
    by_id = {r["id"]: r for r in rows}

    print("== 1. debris census + stratified split ==")
    flagged = [r for r in rows if debris(r["id"], entries[r["id"]]["query"])]
    clean = [r for r in rows if r["id"] not in set(FLAGGED)]
    print(f"  flagged {len(flagged)}/{len(rows)}: {sorted(r['id'] for r in flagged)}")
    stats(rows, "all scored")
    stats(clean, "clean only")
    stats(flagged, "debris only")

    print("\n== 2. stale-but-scored sensitivity ==")
    scored_ids = {r["id"] for r in rows}
    stale = results.get("staleAnchors") or []
    stale_scored = sorted(s for s in stale if s in scored_ids)
    stale_filtered = sorted(s for s in stale if s not in scored_ids)
    print(f"  staleAnchors={stale} scored={stale_scored} filtered={stale_filtered}")
    stats(rows, "all scored")
    if stale_scored:
        stats([r for r in rows if r["id"] not in set(stale_scored)],
              f"drop stale-scored {','.join(stale_scored)}")
    else:
        print("  (no stale anchor is scored — nothing to sensitivity-test)")

    print("\n== 3. shared-scope anomaly (fts.db direct rank) ==")
    sys.path.insert(0, str(args.harness_dir))
    from llamaindex_harness import fts as fts_plan  # noqa: PLC0415

    con = sqlite3.connect(f"file:{args.fts_db}?mode=ro", uri=True)
    shared_total = con.execute(
        "SELECT count(*) FROM docs_fts WHERE scope='shared'").fetchone()[0]
    for r in rows:
        if r["targetScope"] != "shared":
            continue
        h = r["expectedHash"]
        plan = fts_plan.build_plan(r["query"])
        rank = None
        if plan.expression:
            rr = con.execute(
                "SELECT hash, bm25(docs_fts,1.0,8.0,4.0) r FROM docs_fts"
                " WHERE docs_fts MATCH ? AND scope='shared'"
                " ORDER BY r, hash LIMIT 100", (plan.expression,)).fetchall()
            rank = next((i for i, (hh, _) in enumerate(rr, 1) if hh == h), None)
        print(f"  {r['id']}: harness fts={r['harness'].get('fts_hit')} "
              f"vec={r['harness'].get('vector_hit')} final={r['harness']['hit']} "
              f"bank={r['airaccoon']['hit']} | direct fts rank={rank}/{shared_total}")
    con.close()

    print("\n== 4. corpus searchLimit audit ==")
    print(f"  {dict(Counter(q.get('searchLimit') for q in corpus['queries']))}")

    print("\n== 5. params buckets vs excluded manifest ==")
    if args.params.exists():
        p = json.loads(args.params.read_text(encoding="utf-8"))
        excluded = {e["projectId"] for e in p.get("excludedProjects", [])}
        stray = [b for b in p.get("buckets", [])
                 if b.split("/")[0] in excluded and not b.endswith("/shared")]
        print(f"  excluded={sorted(excluded)}; non-shared excluded buckets in store={stray}")
        print(f"  resolvedBuckets={p.get('resolvedBuckets')}")
    else:
        print("  params.json missing")

    print("\n== 6. bank copy provenance/hermeticity ==")
    if args.copy.exists():
        import os, time  # noqa: PLC0415
        con = sqlite3.connect(f"file:{args.copy}?mode=ro", uri=True)
        n = con.execute("SELECT count(*) FROM entries").fetchone()[0]
        watches = con.execute("SELECT count(*) FROM watches").fetchone()[0]
        mx = con.execute(
            "SELECT datetime(max(created_at),'unixepoch','localtime'),"
            " datetime(max(updated_at),'unixepoch','localtime') FROM entries").fetchone()
        con.close()
        mt = time.strftime("%Y-%m-%d %H:%M:%S", time.localtime(os.path.getmtime(args.copy)))
        print(f"  copy entries={n} watches={watches} mtime={mt} "
              f"max_created={mx[0]} max_updated={mx[1]}")
        print("  note: any row activity after the copy mtime is ingest drift")
    else:
        print("  copy missing")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
