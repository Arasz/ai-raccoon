---
name: debug-issue
description: >-
  Use when a bug report or failing test names a symptom and the code path producing it is not yet
  known — trace the call chain from symptom to entry point before proposing a fix. Trigger
  phrases: "why does this fail", "trace this bug", "find where this is called from", "what calls
  this function", "did a recent change cause this". Not a replacement for the general
  reproduce-isolate-fix discipline (a `systematic-debugging` skill, if present, governs that
  overall loop); reach for this skill specifically for the tracing step — once a symptom is
  located and the call chain to its entry point still needs walking before a hypothesis is formed.
version: 1.0.0
author: ai-badger, after the code-review-graph skill templates
license: MIT
platforms: [linux, macos, windows]
scope: optIn
metadata:
  hermes:
    tags: [debugging, tracing, call-graph]
    related_skills: [review-changes, refactor-safely]
---

# Debug issue

Trace the call chain before hypothesizing. The failure this skill exists to prevent: proposing a
fix before the call chain from symptom to entry point has actually been traced.

The workflow derives from the skill templates the `code-review-graph` project auto-installs
(MIT, © 2026 Tirth Kanani), rewritten here to be tool-agnostic: every step carries a baseline
that needs no graph server.

## Steps

1. **Locate the code from the symptom.**
   Accelerated: a code-graph semantic-search call (e.g. `semantic_search_nodes`-shaped; discover
   the real tool name from the server's own listing) finds relevant code by meaning, not string
   match. Baseline: grep for the error message, log line, or another distinctive identifier
   (field name, status code, exception type) across the source tree.

2. **Trace callers and callees from that point — both directions**, not just one.
   Accelerated: a graph query for callers-of/callees-of the located symbol (e.g. `query_graph`-
   shaped with `callers_of`/`callees_of` patterns). Baseline: the IDE's find-references /
   go-to-definition, or `grep -rn` the symbol for callers and read the body for callees. Walk
   outward until reaching a test that exercises this path or a public entry point (HTTP handler,
   CLI command, message consumer, orchestration trigger).

3. **Follow the full execution path through the suspected area** — the entry point that triggers
   the bug is often several hops from where the symptom surfaces.
   Accelerated: a flow query (e.g. `get_flow`-shaped) returns the whole path in one call.
   Baseline: read each hop in sequence, noting where state changes or a branch could diverge.

4. **Check whether a recent change introduced it.**
   Accelerated: a change-detection call (e.g. `detect_changes`-shaped) flags recent risk-scored
   changes near the traced path. Baseline: `git log -S'<symbol>' --oneline -- <path>` or
   `git blame` the suspected lines — recent changes are the most common source of new issues,
   check this before assuming the bug is old.

5. **Only now form a hypothesis**, stated as "execution reaches line X via path Y, and the bug is
   Z because of change W" — or "the bug predates commit V because no recent change touched this
   path". A hypothesis with no traced path behind it is a guess, not a diagnosis.

## Gotchas

No environment-specific gotchas known.

## Red flags — STOP

- Proposing a fix before step 2 has produced an actual caller/callee list
- Tracing only one direction — callers alone miss what the function itself calls into
- Treating "similar code exists elsewhere" as tracing this call chain
- Skipping step 4 — a large share of new bugs are yesterday's diff; the history check is cheap
  relative to guessing

A diagnosis is not finished until it names the specific call path (caller → … → entry point)
showing the bug is reachable, and states whether a specific recent change is implicated. "I think
it's this function" with no traced path is not a diagnosis.
