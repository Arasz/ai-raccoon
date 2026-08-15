"""Cap sweep for the FTS leg under real pasted noise.

Partition respected: TUNING ids are swept on; HELD-OUT (A8,A9,A10 -- the ids
HeldOutRetrievalGateTests pins) are only read after.
Also tests the question-FIRST vs question-LAST assumption, because real long
queries do not reliably put the question first (measured on the live bank).
"""
import json, re, sqlite3, math, statistics

REPO = "/Users/arasz/RiderProjects/ai-raccoon/.claude/worktrees/agent-a7e13b075ba6a32a9"
con = sqlite3.connect(f"file:{REPO}/tests/AiRaccoon.Tests/Resources/jsaa-memory.db?mode=ro", uri=True)

TOK = re.compile(r"[^\W]+", re.UNICODE)
RESERVED = {"and", "or", "not", "near"}
STOP = {"what","is","the","how","does","about","are","do","can","should","will","would",
        "could","has","have","been","was","were","being","a","an","in","on","at","to",
        "for","of","by","with","from"}
TUNING = {"A1","A2","A3","A4","A5","A6","A7","S2","C1","C2","C5"}
HELDOUT = {"A8","A9","A10"}


def rel_suffix(e):
    r = e.split("#")[0]
    if r.startswith("docs:adr:"): return "docs/adr/" + r[len("docs:adr:"):]
    if r.startswith("ai-badger:invariants/"): return ".ai-badger/invariants/" + r[len("ai-badger:invariants/"):]
    raise SystemExit(e)


def plan(q, mode, cap=None):
    raw = [t for t in (m.group(0).lower() for m in TOK.finditer(q)) if t not in RESERVED]
    content = [t for t in raw if t not in STOP]
    if not content: return ""
    if len(content) == 1: return content[0]
    if len(content) <= 4: return " AND ".join(content)
    terms = raw
    if mode != "current":
        seen, out = set(), []
        for t in terms:
            if t not in seen: seen.add(t); out.append(t)
        terms = out
    if cap: terms = terms[:cap]
    return " OR ".join(terms)


K = 5
IDEAL = sum(1.0 / math.log2(i + 2) for i in range(K))


def ndcg(ranked, suffix):
    return sum(1.0 / math.log2(i + 2) for i, d in enumerate(ranked[:K]) if d.endswith(suffix)) / IDEAL


def run(expr):
    if not expr: return []
    return [r[0] for r in con.execute(
        "SELECT e.source_file FROM entries_fts f JOIN entries e ON e.id=f.rowid "
        "WHERE entries_fts MATCH ? ORDER BY bm25(entries_fts) LIMIT ?", (expr, K))]


cat = json.load(open(f"{REPO}/scripts/baseline-queries.json"))
cat = cat if isinstance(cat, list) else cat.get("queries", cat)
grade = [q for q in cat if q.get("expectedSource") and not q.get("negativeTest")]

live = sqlite3.connect("file:/Users/arasz/.ai-raccoon/memory.db?mode=ro", uri=True)
noises = [r[0] for r in live.execute(
    "SELECT query FROM search_quality WHERE length(query)>1200 ORDER BY length(query)")]
live.close()
picks = [("small", noises[len(noises)//4]), ("median", noises[len(noises)//2]), ("largest", noises[-1])]

CAPS = [8, 12, 16, 24, 32, 48, 64, 128, 256, 512, None]


def mean(sel, fn):
    v = [fn(q) for q in grade if q["id"] in sel]
    return statistics.mean(v) if v else float("nan")


print("BARE (no noise) ceiling:")
for label, sel in (("tuning", TUNING), ("held-out", HELDOUT), ("all", {q['id'] for q in grade})):
    print(f"  {label:>9}: {mean(sel, lambda q: ndcg(run(plan(q['query'],'current')), rel_suffix(q['expectedSource']))):.4f}")

for nlabel, noise in picks:
    for order in ("question-first", "question-last"):
        print(f"\n=== noise={nlabel} ({len(noise)} ch)  order={order} ===")
        print(f"{'variant':>16} {'tuning':>8} {'held-out':>9}")

        def combined(q):
            return (q["query"] + "\n" + noise) if order == "question-first" else (noise + "\n" + q["query"])

        def score(q, mode, cap):
            return ndcg(run(plan(combined(q), mode, cap)), rel_suffix(q["expectedSource"]))

        t = mean(TUNING, lambda q: score(q, "current", None))
        h = mean(HELDOUT, lambda q: score(q, "current", None))
        print(f"{'current':>16} {t:>8.4f} {h:>9.4f}")
        for cap in CAPS:
            t = mean(TUNING, lambda q: score(q, "dedup", cap))
            h = mean(HELDOUT, lambda q: score(q, "dedup", cap))
            name = f"dedup+cap{cap}" if cap else "dedup (no cap)"
            print(f"{name:>16} {t:>8.4f} {h:>9.4f}")
