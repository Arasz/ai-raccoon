import sqlite3, re, statistics, collections

con = sqlite3.connect("file:/Users/arasz/.ai-raccoon/memory.db?mode=ro", uri=True)
rows = [r[0] for r in con.execute("SELECT query FROM search_quality")]
con.close()

TOK = re.compile(r"[^\W]+", re.UNICODE)  # approx [\p{L}\p{N}_]+
RESERVED = {"and", "or", "not", "near"}

recs = []
for q in rows:
    raw = [m.group(0).lower() for m in TOK.finditer(q)]
    raw = [t for t in raw if t not in RESERVED]
    recs.append((len(q), len(raw), len(set(raw))))

print(f"queries: {len(recs)}")
chars = sorted(r[0] for r in recs)
raws = sorted(r[1] for r in recs)
dis = sorted(r[2] for r in recs)


def pct(a, p):
    if not a:
        return 0
    return a[min(len(a) - 1, int(len(a) * p / 100))]


for name, a in (("chars", chars), ("raw_tokens", raws), ("distinct", dis)):
    print(f"{name:11s} median={statistics.median(a):>9.0f} p90={pct(a,90):>7} "
          f"p95={pct(a,95):>7} p99={pct(a,99):>8} max={a[-1]:>8}")

print()
for n in (32, 48, 64, 96, 128, 192, 256, 384, 512):
    over_raw = sum(1 for r in recs if r[1] > n)
    over_d = sum(1 for r in recs if r[2] > n)
    print(f"cap {n:>4}: raw-token queries over = {over_raw:>4} ({100*over_raw/len(recs):5.1f}%) | "
          f"distinct over = {over_d:>4} ({100*over_d/len(recs):5.1f}%)")

print()
worst = max(recs, key=lambda r: r[1])
print(f"worst query: {worst[0]} chars, {worst[1]} raw tokens, {worst[2]} distinct "
      f"-> dedup alone removes {100*(1-worst[2]/worst[1]):.1f}% of OR terms")

# how much weight the top repeated terms carry in the worst query
qmax = max(rows, key=lambda q: len(q))
raw = [m.group(0).lower() for m in TOK.finditer(qmax) if m.group(0).lower() not in RESERVED]
c = collections.Counter(raw)
top = c.most_common(10)
print(f"top repeated terms in worst query: {top}")
print(f"top-10 terms are {100*sum(n for _, n in top)/len(raw):.1f}% of all OR term slots")
