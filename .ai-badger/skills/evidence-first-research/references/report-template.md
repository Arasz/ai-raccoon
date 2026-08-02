# Research: <the question, as a noun phrase>

**Date:** YYYY-MM-DD
**Question:** <the one sentence you wrote down before you looked anything up>

<!--
Optional. Any of the five kinds; `provenance` is drawn automatically and is not declared here.
Prefer `range` over `bars` for timings — a single bar asserts a precision three runs will not
support, and the spread is usually the interesting part.

```chart:range
title: push seconds by change shape
docs-only: 12..14..19
full: 68..75..91
```
-->

## Findings

### F1 — <claim, present tense, one line> [MEASURED]

<What it means and why it matters. Two short paragraphs at most.>

**Evidence:** <the command, the machine, the conditions. Reproducible by someone else.>

### F2 — <claim> [READ]

<Body.>

**Evidence:** `path/to/file.py:120-160`, or the spec section, or the doc URL.

### F3 — <claim> [INFERRED]

<Body — and say what this reasons *from*. Reasoning is not a source; the inputs are.>

### F4 — <claim> [UNVERIFIED]

<Why it was not checked. No evidence line is required, and none should be invented.>

## Still open

- <What you could not settle, and what would settle it.>
- <A question the investigation raised that it did not answer.>

<!--
Rules the renderer enforces, so a clean render is itself a check:

  - every finding ends its claim with one of MEASURED / READ / INFERRED / UNVERIFIED in brackets
  - an unknown grade is refused, not passed through as a fifth grade nobody defined
  - MEASURED and READ are refused without an **Evidence:** line
  - UNVERIFIED needs nothing — admission must stay cheaper than silence

Render with:
  python3 .ai-badger/skills/evidence-first-research/scripts/render_report.py <this file>

It writes the HTML to a temp directory and prints the path. It refuses any target inside the
repository: the markdown record is the artefact, and a committed view becomes a second source of
truth nobody can date.
-->
