"""Evaluate scorer rankings against reference usefulness labels."""
import json, math, subprocess, sys

def spearman(xs, ys):
    def rank(v):
        order = sorted(range(len(v)), key=lambda i: v[i])
        r = [0.0]*len(v); i = 0
        while i < len(order):
            j = i
            while j+1 < len(order) and v[order[j+1]] == v[order[i]]: j += 1
            avg = (i + j)/2 + 1
            for k in range(i, j+1): r[order[k]] = avg
            i = j+1
        return r
    rx, ry = rank(xs), rank(ys)
    mx, my = sum(rx)/len(rx), sum(ry)/len(ry)
    num = sum((a-mx)*(b-my) for a,b in zip(rx,ry))
    den = math.sqrt(sum((a-mx)**2 for a in rx)*sum((b-my)**2 for b in ry))
    return num/den if den else 0.0

def ndcg(ids_ranked, rel, k=10):
    dcg = sum(rel.get(i,0)/math.log2(p+2) for p,i in enumerate(ids_ranked[:k]))
    ideal = sorted(rel.values(), reverse=True)[:k]
    idcg = sum(r/math.log2(p+2) for p,r in enumerate(ideal))
    return dcg/idcg if idcg else 0.0

def evaluate(scores_by_id, labeled, name, heldout_ids=None):
    rows = [(c['id'], scores_by_id[c['id']], c['usefulness']) for c in labeled
            if heldout_ids is None or c['id'] in heldout_ids]
    sp = spearman([r[1] for r in rows], [r[2] for r in rows])
    ranked = [r[0] for r in sorted(rows, key=lambda r: -r[1])]
    rel = {r[0]: r[2] for r in rows}
    n = ndcg(ranked, rel)
    scope = f"held-out n={len(rows)}" if heldout_ids else f"full n={len(rows)}"
    print(f"{name:12s} {scope:16s} spearman={sp:+.3f}  nDCG@10={n:.3f}")
    return sp, n

if __name__ == '__main__':
    labeled = json.load(open('labeled_all.json'))
    # incumbent baseline
    inc = {c['id']: c['score'] for c in labeled}
    evaluate(inc, labeled, 'incumbent')
    for agent in sys.argv[1:]:
        out = subprocess.run(['python3', f'agent{agent}/scorer.py', 'candidates.json'],
                             capture_output=True, text=True, check=True)
        scores = {r['id']: r['score'] for r in json.loads(out.stdout)}
        train_ids = {c['id'] for c in json.load(open(f'agent{agent}/train_labeled.json'))}
        held = {c['id'] for c in labeled} - train_ids
        evaluate(scores, labeled, f'agent{agent}', held)
        evaluate(scores, labeled, f'agent{agent}')
