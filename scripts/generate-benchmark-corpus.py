#!/usr/bin/env python3
"""Generates the real-world retrieval corpus for the ai-raccoon embedding benchmark.

Reads real markdown docs from the user's three repos (read-only), extracts
title + a short verbatim body per doc, and emits two C# files:
  - Corpus/RealWorldCorpus.cs  (documents)
  - Corpus/RealWorldQueries.cs (queries with honest ground-truth judgments)

Query ground truth:
  (a) doc-derived: query restates a doc's heading -> that doc is relevant
  (b) cross-repo clusters: topic keyword groups -> docs whose title/body
      actually covers the topic (verified by scanning body text)
  (c) every RelevantDocId is validated to exist in the document set.
"""
import os, re, sys, hashlib

REPOS = {
    "jsaa": "/Users/arasz/RiderProjects/job-search-ai-assistant",
    "badger": "/Users/arasz/RiderProjects/ai-badger",
    "home": "/Users/arasz/RiderProjects/arasz-home-page",
}
OUT = "/Users/arasz/RiderProjects/ai-raccoon/benchmarks/AiRaccoon.Benchmarks/Corpus"
MAX_BODY_CHARS = 420  # ~2-4 sentences

def read(path):
    try:
        with open(path, encoding="utf-8", errors="replace") as f:
            return f.read()
    except OSError:
        return ""

def strip_md(text):
    text = re.sub(r"^#{1,6}\s*", "", text, flags=re.M)  # headings
    text = re.sub(r"`([^`]*)`", r"\1", text)             # inline code
    text = re.sub(r"\[([^\]]*)\]\([^)]*\)", r"\1", text)  # links
    text = re.sub(r"^\s*[-*]\s*", "", text, flags=re.M)  # list bullets
    text = re.sub(r"[|>]", " ", text)                     # tables/blockquotes
    text = re.sub(r"\s+", " ", text).strip()
    return text

def first_heading(md, default):
    m = re.search(r"^#\s+(.+)$", md, flags=re.M)
    return strip_md(m.group(1))[:100] if m else default

def body_excerpt(md):
    """First meaningful paragraph(s) after the first heading, up to MAX_BODY_CHARS."""
    text = strip_md(md)
    # drop the leading title (first sentence equals the heading usually)
    sentences = re.split(r"(?<=[.!?])\s+", text)
    body = ""
    for s in sentences:
        if len(s.strip()) < 25:  # skip heading-like fragments
            continue
        body = (body + " " + s).strip()
        if len(body) >= MAX_BODY_CHARS:
            break
    return body[:MAX_BODY_CHARS] or text[:MAX_BODY_CHARS]

def safe_id(name):
    return re.sub(r"[^a-z0-9-]", "-", name.lower()).strip("-")[:70]

def csharp_literal(text):
    # raw string literal with safe delimiter; bodies contain quotes/apostrophes
    quotes = text.count('"')
    delim = '"' * (3 if quotes < 500 else 4)
    return f'{delim}\n{text}\n{delim}'

def collect_docs():
    docs = []  # (id, title, body, source, topic_keywords)
    seen = {}
    topics = {
        # cluster keywords -> topic label for cross-repo queries
        "ai-badger": ["ai-badger", "badger"],
        "github-auth": ["github", "pat", "oauth", "token"],
        "event-grid": ["event grid", "service bus", "storage queue"],
        "durable": ["durable function"],
        "ci-cost": ["ci", "cost", "github actions", "workflow"],
        "validation": ["fluentvalidation", "validation"],
        "identity": ["uuid", "version 7", "identifier"],
        "mcp": ["mcp", "model context protocol"],
        "tdd": ["tdd", "test-driven", "failing test"],
        "security": ["secret", "key vault", "credential", "api key"],
    }

    def add(repo, rel, id_prefix, extra_topics=()):
        path = os.path.join(REPOS[repo], rel)
        if not os.path.isfile(path):
            return
        md = read(path)
        if len(md) < 60:
            return
        title = first_heading(md, os.path.basename(rel))
        body = body_excerpt(md)
        if not body:
            return
        doc_id = f"{id_prefix}-{safe_id(os.path.splitext(os.path.basename(rel))[0])}"
        doc_id = doc_id[:70]
        if doc_id in seen:
            return
        seen[doc_id] = True
        lower = (title + " " + body).lower()
        found = [label for label, kws in topics.items() if any(k in lower for k in kws)]
        found += list(extra_topics)
        docs.append({"id": doc_id, "title": title, "body": body,
                     "source": f"{repo}:{rel}", "topics": sorted(set(found))})

    # --- job-search-ai-assistant ADRs + reference/work docs ---
    jsaa = REPOS["jsaa"]
    for adr in sorted(os.listdir(os.path.join(jsaa, "docs/adr"))):
        if adr.endswith(".md"):
            add("jsaa", f"docs/adr/{adr}", "jsaa-adr")
    for f in ["README.md"]:
        add("jsaa", f, "jsaa")
    for rel in ["docs/reference/behaviour-specification.md", "docs/reference/domain-glossary.md",
                "docs/work/plans/2026-07-21-cv-orchestration-reliability.md"]:
        add("jsaa", rel, "jsaa-doc")

    # --- ai-badger ADRs + invariants + a few skills ---
    badger = REPOS["badger"]
    for adr in sorted(os.listdir(os.path.join(badger, "docs/adr"))):
        if adr.endswith(".md"):
            add("badger", f"docs/adr/{adr}", "badger-adr")
    inv_dir = os.path.join(badger, "features/common/invariants")
    if os.path.isdir(inv_dir):
        for inv in sorted(os.listdir(inv_dir)):
            if inv.endswith(".md"):
                add("badger", f"features/common/invariants/{inv}", "badger-invariant")
    for rel in ["README.md", "docs/changelog/README.md"]:
        add("badger", rel, "badger-doc")

    # --- arasz-home-page ADRs + architecture docs ---
    home = REPOS["home"]
    for adr in sorted(os.listdir(os.path.join(home, "docs/adr"))):
        if adr.endswith(".md"):
            add("home", f"docs/adr/{adr}", "home-adr")
    for rel in ["docs/contact-backend-architecture.md", "README.md"]:
        add("home", rel, "home-doc")

    # --- .remember notes (short docs + query sources), skip logs/ ---
    for repo, base in REPOS.items():
        rem = os.path.join(base, ".remember")
        if not os.path.isdir(rem):
            continue
        for f in ["recent.md", "archive.md"]:
            p = os.path.join(rem, f)
            if os.path.isfile(p):
                add(repo, f".remember/{f}", f"{repo}-remember")
        for f in sorted(os.listdir(rem)):
            if f.startswith("today-") and f.endswith(".done.md"):
                add(repo, f".remember/{f}", f"{repo}-remember")

    return docs

def build_queries(docs):
    by_id = {d["id"]: d for d in docs}
    queries = []  # (id, text, relevant_ids, judgment)

    def add_query(qid, text, relevant, judgment):
        relevant = [r for r in relevant if r in by_id]
        if relevant:
            queries.append((qid, text, relevant, judgment))

    # (a) doc-derived: restate a doc's decision/heading as a question;
    # relevant = the doc itself + other non-remember docs verified to cover the
    # same topic. Daily .remember notes mention many topics but don't *cover*
    # decisions, so they are excluded from relevance sets (query sources only) —
    # otherwise every ai-badger query would list 15+ relevant docs and the
    # metrics would hit a ceiling. Sample every 3rd doc for the easy tier.
    topic_docs = {}
    for d in docs:
        if "-remember-" in d["id"]:
            continue  # daily notes: not cluster members
        for t in d["topics"]:
            topic_docs.setdefault(t, []).append(d["id"])
    sampled = sorted(docs, key=lambda d: d["id"])[::3]
    for d in sampled:
        title = d["title"].strip(" ?.")
        question = f"What does the project decide or document about: {title}?"
        relevant = [d["id"]] + [x for x in topic_docs.get(d["topics"][0], []) if x != d["id"]] if d["topics"] else [d["id"]]
        add_query(f"doc-{safe_id(d['id'])}", question, relevant,
                  f"// judgment: query restates the decision/heading of {d['id']}; same-topic docs keyword-verified")

    # (b) cross-repo topic clusters
    cluster_queries = {
        "ai-badger": ("How does a repository adopt and execute ai-badger skills rather than just store them?", "badger"),
        "github-auth": ("How is a GitHub identity proven to belong to a person during sign-in?", "home"),
        "event-grid": ("Why was Event Grid chosen over Service Bus topics and Storage Queue?", "home"),
        "durable": ("How are long-running workflows implemented with Durable Functions?", "home"),
        "ci-cost": ("How is CI spend kept under control in these repositories?", "jsaa"),
        "validation": ("Which library is used to keep domain validation rules colocated with the models?", "jsaa"),
        "identity": ("What API generates time-ordered unique identifiers?", "jsaa"),
        "mcp": ("How does a server expose tools to AI assistants over the Model Context Protocol?", "jsaa"),
        "tdd": ("What rule governs when production code may be written relative to tests?", "badger"),
        "security": ("How are secrets kept out of tracked files and source control?", "badger"),
    }
    for topic, (question, _) in cluster_queries.items():
        relevant = [d["id"] for d in docs if topic in d["topics"] and "-remember-" not in d["id"]]
        if relevant:
            add_query(f"cluster-{topic}", question, relevant,
                      f"// judgment: all docs whose title/body covers '{topic}' were keyword-verified")

    # (c) .remember-derived: cite a note's referenced artifact
    remember_queries = {
        "What did the rename that made signal sources consistent across frontend and backend change?":
            ["jsaa-adr-0083"],
    }
    for question, relevant in remember_queries.items():
        add_query("remember-signal-sources", question, relevant,
                  "// judgment: derived from .remember note citing ADR-0083; verified by reading it")

    return queries

def emit_cs(docs, queries):
    os.makedirs(OUT, exist_ok=True)

    # RealWorldCorpus.cs
    lines = [
        "// AUTO-GENERATED from the real repositories (job-search-ai-assistant, ai-badger,",
        "// arasz-home-page) + their .remember notes. Regenerate with scripts/generate-benchmark-corpus.py.",
        "// Bodies are verbatim excerpts (2-4 sentences) from the source files.",
        "namespace AiRaccoon.Benchmarks.Corpus;",
        "",
        "/// <summary>Real-world retrieval corpus: docs from the user's actual repositories.</summary>",
        "public static class RealWorldCorpus",
        "{",
        "    public static IReadOnlyList<CorpusDocument> Documents { get; } =",
        "    [",
    ]
    for d in docs:
        lines.append(f'        new CorpusDocument("{d["id"]}", {csharp_literal(d["title"])}, {csharp_literal(d["body"])}),')
    lines += [
        "    ];",
        "}",
        "",
    ]
    with open(os.path.join(OUT, "RealWorldCorpus.cs"), "w") as f:
        f.write("\n".join(lines))

    # RealWorldQueries.cs
    lines = [
        "// AUTO-GENERATED from the real repositories; each query carries a // judgment comment",
        "// documenting how its relevance set was verified.",
        "using AiRaccoon.Benchmarks.Corpus;",
        "",
        "namespace AiRaccoon.Benchmarks.Corpus;",
        "",
        "/// <summary>Real-world queries with honest ground-truth relevance judgments.</summary>",
        "public static class RealWorldQueries",
        "{",
        "    public static IReadOnlyList<CorpusQuery> Queries { get; } =",
        "    [",
    ]
    for qid, text, relevant, judgment in queries:
        ids = ", ".join(f'"{r}"' for r in relevant)
        lines.append(f"        {judgment}")
        lines.append(f'        new CorpusQuery("{qid}", {csharp_literal(text)}, [{ids}]),')
    lines += [
        "    ];",
        "}",
        "",
    ]
    with open(os.path.join(OUT, "RealWorldQueries.cs"), "w") as f:
        f.write("\n".join(lines))

    return len(docs), len(queries)

if __name__ == "__main__":
    docs = collect_docs()
    queries = build_queries(docs)
    nd, nq = emit_cs(docs, queries)
    # validation
    ids = {d["id"] for d in docs}
    bad = [q for q in queries if any(r not in ids for r in q[2])]
    empty = [q for q in queries if not q[2]]
    print(f"docs: {nd}, queries: {nq}")
    print(f"queries with unknown relevant ids: {len(bad)}")
    print(f"queries with empty relevance: {len(empty)}")
    total_tokens = sum(len(d["body"].split()) for d in docs) * 1.3
    print(f"~{int(total_tokens)} doc tokens")
