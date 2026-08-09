"""Score every candidate scorer in a tournament round against a train/validation/holdout split.

    python3 score_round.py <split-dir> <scorer.py> [<scorer.py> ...]

`<split-dir>` holds split_train.json / split_validation.json / split_holdout.json, each a JSON
array of labeled candidates. Rows carrying `_owner_labeled` are reported separately: those labels
come from the human owner, the rest from a rater jury, and a model that gains on one while losing
the other has not improved (docs/work/2026-08-09-promotion-scoring-tournament.md).
"""
import json
import os
import subprocess
import sys

from eval import ndcg, spearman


def score_with(scorer, rows):
    payload = [{k: v for k, v in r.items() if k != "usefulness" and not k.startswith("_")} for r in rows]
    tmp = f"{scorer}.candidates.json"
    with open(tmp, "w", encoding="utf-8") as fh:
        json.dump(payload, fh)
    try:
        out = subprocess.run([sys.executable, scorer, tmp], capture_output=True, text=True, check=True)
    finally:
        os.unlink(tmp)
    return {r["id"]: r["score"] for r in json.loads(out.stdout)}


def measure(rows, scores):
    xs = [scores[r["id"]] for r in rows]
    ys = [r["usefulness"] for r in rows]
    ranked = [r["id"] for r in sorted(rows, key=lambda r: -scores[r["id"]])]
    return spearman(xs, ys), ndcg(ranked, {r["id"]: r["usefulness"] for r in rows})


def main(argv):
    split_dir, scorers = argv[1], argv[2:]
    splits = {n: json.load(open(os.path.join(split_dir, f"split_{n}.json")))
              for n in ("train", "validation", "holdout")}
    owner = [r for rows in splits.values() for r in rows if r.get("_owner_labeled")]

    print(f"{'scorer':28s} {'train':>16s} {'validation':>16s} {'holdout':>16s} {'owner-guard':>13s}")
    for scorer in scorers:
        scores = score_with(scorer, [r for rows in splits.values() for r in rows])
        cells = []
        for n in ("train", "validation", "holdout"):
            sp, nd = measure(splits[n], scores)
            cells.append(f"{sp:+.3f}/{nd:.3f}")
        owner_sp, _ = measure(owner, scores)
        name = os.path.basename(os.path.dirname(scorer)) or os.path.basename(scorer)
        print(f"{name:28s} {cells[0]:>16s} {cells[1]:>16s} {cells[2]:>16s} {owner_sp:>+13.3f}")
    print(f"\nn: train={len(splits['train'])} validation={len(splits['validation'])} "
          f"holdout={len(splits['holdout'])} owner-guard={len(owner)}  (spearman/nDCG@10)")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
