---
name: parallel-expert-review
description: "Use when reviewing code or plans with parallel architect+engineer subagents: PR review with architecture concerns, pre-refactor assessment, MoE plan review before implementation (ground-truth the plan FIRST — trust nothing in it), wave-gated dispatch, integrate findings, route owner questions through owner-gate."
version: 1.0.0
author: ai-badger
license: MIT
platforms: [linux, macos, windows]
scope: optIn
metadata:
  hermes:
    tags: [review, subagents, moe, architecture]
    related_skills: [code-review-checklist, multi-lane-report-assembly]
---

# Parallel Expert Review

Review a code change by dispatching two expert subagents in parallel — an architect
(structure, composition, naming, separation) and a domain engineer (idiomatic patterns,
correctness, edge cases) — then integrate their findings. When findings conflict or a
recommendation is unclear, have both agents defend their positions before settling.

## When to use

- PR review with architecture concerns
- Pre-refactor assessment ("how can we improve and simplify?")
- Codebase audit across multiple concerns
- Any review where structure AND implementation both matter

## MoE plan review — before implementation (variant)

The user's explicit flow for refactor work: "do a MoE review, all experts do the review,
you integrate their findings, questions that need answering from me use owner gate skill,
prepare before and after architecture diagrams for the changed module, after review we
will start work." Use this when a PLAN document (not code) is the review target and
implementation comes later.

### 0. Ground-truth the plan BEFORE dispatch — non-negotiable

Plan documents go stale between writing and review. Before any expert sees it, verify
every factual claim against current code: file paths (files move between refactors),
line counts, "N unused copies" claims, existence of named classes, ctor signatures.
Trust NOTHING in the plan without checking. This session found: paths moved (Core →
separate `src/<Proj>.Core/` project), "4 unused OpenSshKeyBuilder copies" was actually
1 USED copy (9+ call sites), and the stale plan would have sent experts reviewing ghosts.
Feed experts the verified ground truth explicitly: "VERIFIED GROUND TRUTH (trust these
over the plan's stale claims): ...". Experts reviewing stale plans produce stale reviews.

### 1. Dispatch ALL personas, wave the gate

- personaRouting maps architect / test-engineer / code-reviewer / dotnet-engineer — send
  EVERY relevant persona, each with its own lens (structure / coverage+test-strategy /
  quality / feasibility).
- Respect the delegation cap (max 3 concurrent children): dispatch 3 in wave 1, then the
  code-reviewer in wave 2 reviewing the INTEGRATED result. Review the join, not just the parts.
- Each expert prompt ends with: numbered findings with SEVERITY (must-fix / should-fix /
  consider / nit), a verdict, and **decision-ready owner questions** (one line each, so the
  orchestrator can route them through owner-gate without reformatting).
- READ-ONLY: plan reviews edit nothing. Explicitly tell subagents not to modify files.

### 2. Integrate, then owner-gate

- Merge findings into a unified report; group by theme; dedupe.
- Every open question needing the user → `owner-gate-review` skill (decision cards with
  claim / detail / why-this-matters, verdict controls). Never resolve a user question yourself.
- Collect owner questions from ALL expert reports (each was asked to produce them) —
  one question answered twice beats one never asked.
- **Generate the owner-gate form programmatically, don't hand-edit the 30 KB template.**
  Load `owner-gate-review/references/form-template.html` (read when generating the owner-gate form), replace the `var CONFIG = {...};`
  and `var DECISIONS = [...]` blocks with `json.dumps()` output built from your decisions
  array, and sanity-check the result: storageKey unique to this review (never the template's
  example `refinement:2026-01-15-import-pipeline:v1`), `outName`/`expectedDir` matching the
  watch path, all ids present. **HTML-escape angle brackets in detail/why strings**
  (`I&lt;Verb&gt;Commands`, not `I<Verb>Commands`) — the fields accept inline HTML and a
  bare `<` gets parsed as a tag. This session generated a 9-card form in one pass with
  json.dumps + a few assertions; hand-editing the template would have taken several
  fragile patches.

### 3. Before/after architecture diagrams as review deliverables

- The user reviews the plan's effect on a module visually. Produce TWO diagrams: the
  changed module BEFORE (current state) and AFTER (proposed). Match the repo's existing
  diagram conventions (this repo uses mermaid with Evidence blocks in docs/explanation/).
- Build the BEFORE diagram from verified code facts (read the actual files); build AFTER
  from the integrated expert findings — the after-shape depends on what the review decided.
- Validate mermaid blocks with the mermaid-github-compat validator before presenting.

### 4. Stop at the gate

The workflow ends after review + diagrams + owner answers — implementation is a separate
phase. Do not start coding because the review finished; the user said "after review we
will start work" — wait for the go.

## Flow

### 1. Gather context

Read the changed files. Understand the problem the code is solving. Identify the architectural
boundaries and composition points. Run the build to establish a baseline.

### 2. Dispatch parallel reviews

**Architect subagent** — focus on:
- Architecture and composition (does the split make sense?)
- Component separation (are concerns cleanly divided?)
- Naming clarity (do names tell you what things do?)
- Alternatives (what simpler shapes could work?)
- Adherence to project invariants (screaming architecture, clean layering, etc.)

**Domain engineer subagent** — focus on:
- Idiomatic patterns (DI, guard clauses, nullable handling, logging)
- Correctness (edge cases, error paths, contracts)
- Test coverage and honesty
- Adherence to project code-style invariants

Each subagent gets: the worktree path, file list, project invariants (CLAUDE.md), and
specific focus areas. Prompts must be self-contained — subagents know nothing about this
conversation.

### 3. Integrate findings

When both subagents return:
- Merge findings into a unified report
- Group by theme (architecture, naming, patterns, tests)
- Flag conflicting recommendations
- For each conflict, identify whether it needs adversarial resolution

### 4. Adversarial resolution

When two agents disagree or a finding is ambiguous:
- Present each agent's position to the other
- Ask each to defend or concede
- The orchestrator decides based on the evidence, not the loudest argument
- Default to the simpler shape when evidence is equal

### 5. Plan from findings

After the review is settled, produce a refactoring plan with:
- Specific changes, ordered by dependency
- Acceptance criteria per change
- Which changes can run in parallel

### 6. Execute with TDD

Follow the project's task skill for implementation: write failing tests first, implement,
review, QA gate.

## Gotchas
- **Worktree alignment**: `task_tracker.py start` may create the worktree from the wrong
  branch. After creation, verify with `git branch --show-current` and
  `git log --oneline -3`. Use `git fetch origin <branch> && git reset --hard origin/<branch>`
  to align.
- **Mid-review PR merge — the merged squash can differ from the reviewed head.** If the
  user merges the PR during review, do NOT assume "the review is still valid" on the old
  head. The author may have pushed updates (reworks, docs, test fixes) between your dispatch
  and the merge — the squash then contains a different shape than the lanes reviewed. Do:
  `git fetch origin main && git reset --hard origin/main`, diff the reviewed head against
  the merged commit (`git diff <reviewed-head> <merge-commit>`), RE-RUN the gates on the
  merged state, and re-verify EVERY lane finding against the merged file
  (`git show <merge>:<path>`) before accepting it. Real case (2026-08, PR #55): the merged
  service used a PeriodicTimer where the reviewed head used Task.Delay; one lane claimed
  the merged code lacked the refactor (wrong — it had it), another claimed a port test ran
  real sqlite (wrong — it used a fake). Both settled only by reading the merged source.
  Lane findings cite line numbers against a shape that may no longer exist.
- **Reconcile lane disputes from source, never by lane count.** When two lanes disagree on
  a fact (e.g. "cross-project dedup is broken" vs "it's idempotent"), read the actual
  query/merge path and settle it — the minority lane was right in this session's dedup
  dispute. A lane that did more probing can be wrong about state while right about
  substance; verify each claim at the merged commit independently.
- **Subagent context**: Each subagent needs the worktree path, not the main checkout path.
  The worktree IS the review target.

## References

- `references/owner-gate-form-lifecycle.md` — generating and running the owner-gate decision form for MoE reviews; read when running the owner-gate form.
