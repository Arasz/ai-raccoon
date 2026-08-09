"""Rebuild a scoring-eval fixture by rejoining committed usefulness labels to the local bank.

The labels (id/hash/usefulness) are committed; the row bodies they refer to quote private-repo
docs and are not. This joins the two by `hash` so the fixture is derivable rather than a lost
artifact — see docs/adr/0018-promotion-scoring-v2.md.

    python3 rebuild_fixture.py reference-labels.json ~/.ai-raccoon/memory.db out.json
"""
import json
import sqlite3
import sys

FIELDS = "hash, path, value, source_file, created_at, access_count, rating"


def rebuild(labels_path, db_path, out_path):
    labels = json.load(open(labels_path))
    con = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True)
    rows, missing = [], []
    for label in labels:
        # A hash can repeat across scopes; the lowest id is the original project-scope row.
        hit = con.execute(
            f"SELECT {FIELDS} FROM entries WHERE hash = ? ORDER BY id LIMIT 1", (label["hash"],)
        ).fetchone()
        if hit is None:
            missing.append(label["id"])
            continue
        h, path, value, source_file, created_at, access_count, rating = hit
        rows.append({
            "id": label["id"],
            "project_id": label["project_id"],
            "hash": h,
            "path": path or "",
            "value": value or "",
            "source_file": source_file,
            "created_at": created_at,
            "access_count": access_count,
            "rating": rating,
            "usefulness": label["usefulness"],
        })
    json.dump(rows, open(out_path, "w"), indent=1)
    return rows, missing


if __name__ == "__main__":
    labels_path, db_path, out_path = sys.argv[1:4]
    rebuilt, missing = rebuild(labels_path, db_path, out_path)
    print(f"rebuilt {len(rebuilt)}/{len(rebuilt) + len(missing)} rows -> {out_path}")
    if missing:
        print(f"dropped (no longer in the bank): {missing}")
