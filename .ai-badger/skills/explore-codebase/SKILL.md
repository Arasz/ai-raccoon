---
name: explore-codebase
description: >-
  Use when arriving at an unfamiliar codebase, or an unfamiliar region of a known one, and the
  question is "what is here and how is it arranged" rather than "where is this specific thing".
  Trigger phrases: "help me understand this repo", "what does this project do", "where does X
  live", "walk me through the architecture", "I'm new to this codebase", "what are the main
  modules". Not for tracing one symptom to its cause — that is `debug-issue`; not for judging a
  diff — that is `review-changes`. Reach for this one when orienting, and switch to those once
  you know where to look.
version: 1.0.0
author: ai-badger, after the code-review-graph skill templates
license: MIT
platforms: [linux, macos, windows]
metadata:
  hermes:
    tags: [exploration, architecture, orientation, onboarding]
    related_skills: [debug-issue, review-changes, refactor-safely]
---

# Explore codebase

Orient before reading. The failure this skill exists to prevent: opening files in the order a
search engine returned them and calling the resulting impression an understanding.

The workflow derives from the skill templates the `code-review-graph` project auto-installs
(MIT, © 2026 Tirth Kanani), rewritten here to be tool-agnostic: every step carries a baseline
that needs no graph server.

## Steps

1. **Establish the shape before the content** — how large, what languages, how many entry
   points.
   Accelerated: a graph-stats call (e.g. `list_graph_stats`-shaped; discover the real tool name
   from the server's own listing). Baseline: `git ls-files | wc -l`, `cloc` or
   `git ls-files | sed 's/.*\.//' | sort | uniq -c | sort -rn` for the language split, and the
   project's own manifest (`package.json`, `*.csproj`, `pyproject.toml`) for its declared
   dependencies and entry points.

2. **Find the module boundaries the project actually has**, not the ones its directory names
   suggest.
   Accelerated: an architecture-overview or community-listing call (e.g.
   `get_architecture_overview`-shaped) clusters by real coupling. Baseline: read the top-level
   directory tree, then check it against the imports — a directory whose files import mostly
   from one other directory is one module wearing two names.

3. **Read the entry points first.** A `main`, an HTTP route table, a CLI command registry, a
   message consumer, a scheduled job. These are where the project states what it is for.
   Accelerated: a flow listing (e.g. `list_flows`-shaped) names the paths the project itself
   considers significant. Baseline: grep for the framework's entry-point markers — route
   decorators, `if __name__ == "__main__"`, `Main(`, `.MapGet(`, a `bin` or `scripts` entry in
   the manifest.

4. **Follow one real path end to end** before reading anything else. One request, one command,
   one message — from entry point to persistence and back.
   Accelerated: a flow query (e.g. `get_flow`-shaped) returns the whole path in one call.
   Baseline: read each hop in sequence. One traced path teaches more than ten skimmed files,
   because it shows which abstractions the project actually uses rather than which it declares.

5. **Locate the tests for that path.** They are the executable description of intended
   behaviour, and their absence is itself a finding worth recording.
   Accelerated: a tests-for query (e.g. `query_graph`-shaped with a tests-for pattern).
   Baseline: search for the symbol's name under the test directory, and check whether the
   project's test command names a coverage report you can read instead.

6. **Write down what you concluded and what you did not check.** An exploration that produces
   no artifact has to be repeated by the next person, including you next week.

## Red flags — STOP

- Reading files in search-result order and calling it an understanding
- Describing the architecture from directory names, without checking the imports against them
- Skipping step 4 — a traced path is the difference between knowing the layout and knowing the
  system
- Reporting "the codebase does X" from a region you did not open; say which parts you read
- Treating a generated or vendored directory as authored code — check whether it is in
  `.gitignore` or carries a generation banner before drawing conclusions from it

An orientation is not finished until it names the entry points, one traced path, where that
path's tests live, and — explicitly — the regions left unread.
