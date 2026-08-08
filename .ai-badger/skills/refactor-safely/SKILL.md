---
name: refactor-safely
description: >-
  Use when renaming, moving, extracting, or removing code and every affected location must be
  known before the first edit — a rename that spans call sites, an extraction that changes a
  signature, or a removal that might delete something still in use. Trigger phrases: "refactor
  this safely", "rename X everywhere", "is this code still used", "find everything that calls this
  before I change it", "preview this refactor". Not for reconciling a feature whose implementation
  has drifted from its intended design — that is `differential-feature-refactor`, a design
  question to answer first; this skill is the mechanical preview-apply-verify discipline once the
  refactor's scope is already known.
version: 1.0.0
author: ai-badger, after the code-review-graph skill templates
license: MIT
platforms: [linux, macos, windows]
scope: optIn
metadata:
  hermes:
    tags: [refactoring, dependency-analysis, safety]
    related_skills: [debug-issue, review-changes, differential-feature-refactor]
---

# Refactor safely

Always preview before applying. The discipline: enumerate every affected location before changing
one, confirm no critical path runs through the target unexamined, then verify with tests after —
never trust that a rename or removal was complete just because it compiled.

The workflow derives from the skill templates the `code-review-graph` project auto-installs
(MIT, © 2026 Tirth Kanani), rewritten here to be tool-agnostic: every step carries a baseline
that needs no graph server.

## Steps

1. **Enumerate every affected location before changing one.**
   Accelerated: a code-graph refactor tool in rename-preview mode (e.g. `refactor_tool` with
   `mode="rename"`; discover the real tool name from the server's own listing) returns the full
   edit list before anything is applied. Baseline: the IDE's rename-refactoring (atomic,
   reference-aware) where available; otherwise `grep -rn` every reference and read each hit to
   rule out false positives (shadowed names, string literals, comments).

2. **Check blast radius and whether a critical path runs through the target.**
   Accelerated: impact-radius / affected-flows calls (e.g. `get_impact_radius`-shaped,
   `get_affected_flows`-shaped). Baseline: grep callers and walk outward toward entry points, same
   as the tracing step in `debug-issue`. Flag any hit in auth, payment, data-write, or public API
   surface.

3. **For a removal, confirm zero *reachable* callers before deleting** — not zero grep hits.
   Accelerated: a dead-code detection mode (e.g. `refactor_tool` with `mode="dead_code"`).
   Baseline: grep the whole tree including test files and dynamic-dispatch registration (DI
   container, route table, reflection lookup, config-driven plugin list) — a zero-hit grep is not
   proof if the language or framework allows dynamic dispatch.

4. **Apply using the safest available mechanism**, always using the edit list from step 1 rather
   than re-deriving it. Accelerated: apply the previewed refactor by its id (e.g.
   `apply_refactor_tool` with the `refactor_id` from step 1). Baseline: the IDE's apply-refactor
   action, or scripted find-replace only after step 1's list is complete and has been read in
   full.

5. **Bracket the apply with test runs** — once before, so a pre-existing failure cannot be
   mistaken for one you caused, and once after, before calling the refactor done. Use the
   configured test command (`.ai-badger/config.json`'s `commands.test`, or the project's
   documented equivalent). Accelerated: a post-refactor change-detection call (e.g.
   `detect_changes`-shaped) to confirm no impact beyond what step 1 previewed. Baseline: run the
   test command directly and read the result — a green exit code from a command you didn't
   actually invoke does not count.

## Gotchas

No environment-specific gotchas known.

## Red flags — STOP

- Applying an edit before step 1 produced a complete list
- "It compiles" treated as proof a rename or removal was complete
- A zero-hit grep treated as proof of dead code, with no check for dynamic dispatch
- An apply with no test run on either side of it

A refactor whose apply is not bracketed by test runs is not a safe refactor, regardless of how
confident the preview looked. Without the run before, a failure after is unattributable.
