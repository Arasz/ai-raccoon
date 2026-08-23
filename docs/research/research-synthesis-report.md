# Research Synthesis: Optimizing AiRaccoon's Agent Framework

**Date:** 2026-08-23
**Scope:** All research in `docs/research/` cross-referenced against `.ai-badger/` (invariants, personas, CLAUDE.md, skills, instructions)
**Method:** Evidence-graded synthesis (WS=well-supported, S=supported, C=contested, F=folklore)

---

## Part 1 — What Rules Are Implemented (and Whether Evidence Supports Them)

### 1.1 Invariants Audit (22 rules in CLAUDE.md)

| Invariant | Evidence Grade | Notes |
|---|---|---|
| Ask if a simpler shape would do | **WS** | Aligns with research: "premature abstraction is a cost with no buyer." Converges with Anthropic's "grow minimally from observed failure modes." |
| Check the source, not your own reasoning | **WS** | Directly addresses LLM hallucination/staleness. Research confirms: "facts are the exception — anything taken from documentation gets re-checked against its source." |
| Derive the list, or delete it | **WS** | Software engineering best practice. No research counter-evidence. |
| Guard clauses over hand-rolled null checks | **S** | .NET-specific best practice. Not studied in prompt research but reduces cognitive load for agents. |
| Measure only when the measurement pays | **WS** | Research confirms: "run your own benchmark when the time it costs is repaid by the decision it settles." |
| Minimal comments | **WS** | Research: verbosity causes multi-turn drift. Short comments = less context pollution. |
| No hand-rolled crypto | **WS** | Security best practice. No counter-evidence. |
| No hardcoded secrets | **WS** | Security best practice. No counter-evidence. |
| Run what you changed; pipeline runs the rest | **WS** | Aligns with research on fast feedback loops. |
| Plain names | **S** | Reduces agent confusion. Research on prompt sensitivity suggests clear naming reduces ambiguity. |
| One PR per task | **S** | Workflow discipline. Not studied in prompt research. |
| Done means proven | **WS** | Aligns with research: "define 'done' as an outcome predicate, not a prose vibe." |
| A check you have not seen fail is not a check | **WS** | Directly addresses the research finding that unverified checks are meaningless. |
| Screaming architecture | **S** | Domain-driven design. Helps agents navigate codebase structure. |
| Small commits, early draft PR | **S** | Workflow discipline. |
| Route state transitions through a state machine | **S** | Software engineering best practice. |
| TDD is mandatory | **WS** | Externally-grounded verification (tests) is the one form of self-correction that works per Huang et al. |
| Tests are designed before they are written | **WS** | Aligns with research: "a test list comes out of acceptance criteria before the first test is written." |
| Releases are traceable | **S** | Software engineering best practice. |
| Clean layering | **WS** | Separation of concerns. Helps agents reason about code boundaries. |
| High-performance logging | **S** | .NET-specific. Not studied in prompt research. |
| Static classes: extensions, constants, pure functions only | **S** | .NET-specific. Reduces agent confusion about stateful vs stateless. |
| MCP stays thin | **WS** | Aligns with research: tool schema quality dominates agent success. Thin MCP = clearer tool boundaries. |
| Pin actions to commit SHA | **WS** | Security best practice. No counter-evidence. |

**Verdict:** All 22 invariants are either well-supported or supported by evidence. None are folklore. However, the *number* of invariants is a problem (see Part 2).

### 1.2 Persona Audit

| Persona | Role Line | Description Length | Model | Evidence Alignment |
|---|---|---|---|---|
| architect | "Design and decomposition specialist" | ~60 words | opus | **Good**: task-naming role line, no biography. Model knob set. |
| code-reviewer | "Independent quality and security gate" | ~70 words | opus | **Good**: externally-grounded verification (two-pass with file/line evidence). |
| dotnet-engineer | "Default implementation engineer for .NET codebases" | ~80 words | sonnet | **Good**: TDD-first, grounded in project conventions. |
| test-engineer | "Testing specialist" | ~70 words | sonnet | **Good**: phased pipeline, red/green/refactor discipline. |
| delegator | Work-routing lead | ~50 words | opus | **Good**: orchestration role, not persona prose. |
| qa | Test-quality authority | ~40 words | opus | **Good**: focused on acceptance criteria. |
| qa-backend | QA for .NET server-side | ~60 words | opus | **Good**: stack-specific blind spots named. |

**Verdict:** Personas are well-designed per research. Role lines are task-relevant (not decorative). No biography, no "world-class expert." The `model:` field is a deterministic knob (research: "knobs over prose"). Description lengths are functional, not verbose.

**One concern:** The persona descriptions in the agent `.md` files are longer than the research recommends for system prompts. But these are *agent definitions* loaded as context, not inline system prompts — the research on persona length applies to the system prompt slot specifically.

### 1.3 CLAUDE.md Architecture

Current structure:
```
1. Project summary (2 lines)
2. Non-negotiable invariants (22 items, ~80 lines)
3. Commands (2 lines)
4. Path-specific instructions (5 rules)
5. Agent delegation (4 routing rules)
6. Prompt markers (6 markers)
7. MCP tools sections (5 sections)
8. Framework/skills section
```

**Research alignment:**
- **Position effects (WS):** Critical instructions should be at beginning and end. Currently, invariants are at the top (good) but the file ends with framework/skills boilerplate (bad — should end with the most critical operational rules).
- **Length:** ~180 lines / ~11.7K chars. Research says "if it's over a few hundred words, look for things to remove." At ~2000 words, this is over the recommended threshold.
- **Constraint count (WS):** 22 invariants × ~95% compliance per rule ≈ 33% overall compliance. This is the single biggest finding from the research.

---

## Part 2 — What We Are Missing

### 2.1 Critical Gaps (Well-Supported Evidence, Not Implemented)

**Gap 1: Constraint-count tax is unaddressed**
- Research (IFEval): per-constraint compliance compounds multiplicatively. 22 rules at 95% each = ~33% overall compliance.
- Current: 22 non-negotiable invariants, all presented with equal weight.
- Fix: Prioritize invariants into tiers (critical vs. important vs. nice-to-have). Reduce the always-loaded set to ≤7 critical rules. Load others contextually.

**Gap 2: No positive-framing mandate**
- Research (IFEval, SysBench): negative constraints ("do not X") have lower compliance than positive constraints ("always do Y").
- Current: Several invariants use negative framing ("No hand-rolled crypto", "No hardcoded secrets", "Never invent facts").
- Fix: Rewrite negative invariants as positive specifications where possible.

**Gap 3: No reasoning-model-aware prompting**
- Research (WS across all vendors): CoT scaffolding, few-shot examples, and prescriptive step plans are counterproductive on reasoning models (o-series, Claude extended thinking, DeepSeek-R1).
- Current: Persona descriptions include prescriptive steps (e.g., test-engineer's 7-step pipeline). The task skill dispatches to "opus" for architect/code-reviewer without adjusting prompt style.
- Fix: Add a reasoning-model detection layer. When dispatching to a reasoning model, strip CoT scaffolding and prescriptive steps; state goals and success criteria only.

**Gap 4: No multi-turn degradation mitigation in skills**
- Research (Laban et al., WS): ~39% performance drop at 2 turns. Consolidated restart recovers ~95%.
- Current: The task skill already handles this well (stateless regeneration pattern). But individual skills that involve multi-turn interaction (review-tests, design-tests, code-review-checklist) don't explicitly encode the "restart after 2 failed revisions" rule.
- Fix: Add escalation rules to multi-turn skills.

**Gap 5: No tool schema quality audit process**
- Research (DocsChisel, Trace-Free+, WS): tool schema quality is 40-50% of agent success. Single documentation field changes shift success by ~6pp.
- Current: MCP tool descriptions are auto-generated or hand-written with no quality gate.
- Fix: Establish a tool schema review process. Audit MCP tool descriptions for specificity, parameter documentation, and when-to-use/when-not-to-use guidance.

**Gap 6: No eval harness for prompt changes**
- Research (WS): prompt sensitivity is severe (up to 76-point swings). Single-template evaluation is unreliable.
- Current: No systematic evaluation of prompt/persona changes.
- Fix: Build a lightweight eval set for key agent tasks (code review, test generation, architecture decisions).

### 2.2 Moderate Gaps (Supported Evidence)

**Gap 7: System prompt structure doesn't follow evidence-based ordering**
- Research: Identity → Objective → Constraints → Output contract → Edge cases. Critical at head and tail.
- Current: Invariants → Commands → Instructions → Delegation → MCP tools. The most critical operational rules (TDD, done-means-proven) are buried in the middle of a 22-item list.

**Gap 8: No sycophancy mitigation in review personas**
- Research (Sharma et al., WS): models flip correct answers under user pushback. Third-person framing reduces sycophancy by up to 63.8%.
- Current: code-reviewer has "if a finding is pushed back on, re-verify" (good) but no explicit third-person framing mandate.

**Gap 9: Humanization rules not integrated into documentation skills**
- Research: AI text detection relies on burstiness, perplexity, vocabulary signatures. Nine levers identified.
- Current: `creative:humanizer` skill exists but documentation instructions don't reference humanization principles.

**Gap 10: No cache-aware prompt layout**
- Research (WS): stable prefixes enable 0.1× caching. Append-only conversations are the designed happy path.
- Current: CLAUDE.md is loaded as a stable prefix (good). But skills and persona files that change mid-task break cache locality.

---

## Part 3 — Unproven Rules We Are Following

| Rule We Follow | Evidence Grade | Risk |
|---|---|---|
| XML/Markdown delimiters improve accuracy | **F** | No controlled study shows accuracy benefit. Useful for parsing only. We use XML-style sections in prompts — this is fine for organization but don't expect accuracy gains. |
| "Think step by step" helps | **C→F for reasoning models** | We don't explicitly use this, but some persona descriptions imply step-by-step reasoning. On reasoning models (opus), this is counterproductive. |
| Longer persona descriptions = better agents | **F** | Research: irrelevant persona details cause up to 30pp degradation. Our persona descriptions are functional but could be shorter. |
| System prompt overrides user prompt | **C** | Research: Instruction Hierarchy is a training intervention, not architectural. SysBench shows near-identical attention. We rely on CLAUDE.md as a system-level authority — this is reasonable but not guaranteed. |
| More rules = better behavior | **Disproven** | IFEval: compliance degrades multiplicatively. Our 22 invariants likely achieve ~33% combined compliance. |
| Self-critique improves quality | **Disproven for intrinsic** | Huang et al.: intrinsic self-correction lowers accuracy. Our code-reviewer uses externally-grounded critique (good), but some skills may use unassisted "review your work" patterns (bad). |
| Emotional framing / "world-class expert" | **F** | We don't use this (good). No action needed. |

---

## Part 4 — Top 10 Most Impactful Changes

Ranked by (evidence strength × expected impact × implementation effort):

### Change 1: Tier invariants — reduce always-loaded constraint count
**Impact: HIGH | Evidence: WS | Effort: MEDIUM**
- Problem: 22 invariants × 95% compliance ≈ 33% overall compliance (IFEval).
- Fix: Split into 3 tiers:
  - **Tier 1 (always loaded, ≤7):** TDD, done-means-proven, check-sources, no-hardcoded-secrets, clean-layering, plain-names, ask-if-simpler.
  - **Tier 2 (contextual, loaded by path):** guard-clauses, high-performance-logging, static-classes, mcp-thin, pin-actions, state-machine, screaming-architecture.
  - **Tier 3 (reference only):** minimal-comments, measure-when-it-pays, derive-or-delete, pr-per-task, small-commits, pipeline-runs-the-rest, traceable-releases, no-hand-rolled-crypto, prove-the-check-fails, tests-are-designed-and-reviewed.
- Acceptance: CLAUDE.md invariant section ≤7 items. Tier 2/3 loaded via path-specific instructions or skill references.

### Change 2: Rewrite negative constraints as positive specifications
**Impact: HIGH | Evidence: WS | Effort: LOW**
- Problem: "No hand-rolled crypto" → lower compliance than "Use the platform's built-in crypto APIs."
- Fix: Rewrite all negative-framed invariants as positive specifications.
- Acceptance: Zero invariants starting with "No" or "Never" in Tier 1.

### Change 3: Add reasoning-model-aware dispatch
**Impact: HIGH | Evidence: WS | Effort: MEDIUM**
- Problem: Persona descriptions include prescriptive steps that constrain reasoning models.
- Fix: In delegation.md, add a note: "When dispatching to a reasoning model (opus, o-series), state goals and success criteria only — strip prescriptive steps and CoT scaffolding."
- Acceptance: delegation.md includes reasoning-model guidance. Persona descriptions have a "reasoning-model variant" section.

### Change 4: Audit and improve MCP tool descriptions
**Impact: HIGH | Evidence: WS | Effort: MEDIUM**
- Problem: Tool schema quality is 40-50% of agent success. No current audit process.
- Fix: Review all MCP tool descriptions (ai-raccoon, code-review-graph, semantica, playwright). Ensure each has: purpose, when-to-use, when-NOT-to-use, parameter docs (type, default, nullability), error semantics.
- Acceptance: Every MCP tool has a when-NOT-to-use section. Parameter descriptions cover all fields.

### Change 5: Reorder CLAUDE.md for position effects
**Impact: MEDIUM | Evidence: WS | Effort: LOW**
- Problem: Critical rules buried in middle of 22-item list. File ends with boilerplate.
- Fix: Move Tier 1 invariants to top. Move the most critical operational rule (TDD or done-means-proven) to the bottom as a closing reminder. Move MCP tools / framework sections above the invariant list (they're context, not constraints).
- Acceptance: First 3 items and last item of CLAUDE.md are Tier 1 invariants.

### Change 6: Add escalation rules to multi-turn skills
**Impact: MEDIUM | Evidence: WS | Effort: LOW**
- Problem: Skills like review-tests, design-tests don't encode "restart after 2 failed revisions."
- Fix: Add to each multi-turn skill: "If 2 revision turns haven't converged, restart with a consolidated prompt containing the original spec plus accumulated corrections."
- Acceptance: All skills with multi-turn interaction patterns have explicit escalation rules.

### Change 7: Add third-person framing to review personas
**Impact: MEDIUM | Evidence: WS | Effort: LOW**
- Problem: Sycophancy in revision turns. Third-person framing reduces it by up to 63.8%.
- Fix: In code-reviewer.md, add: "Frame findings as objective criteria ('the spec requires X') rather than personal opinion ('I think X should change')."
- Acceptance: code-reviewer.md includes third-person framing guidance.

### Change 8: Build a lightweight eval set for key agent tasks
**Impact: MEDIUM | Evidence: WS | Effort: HIGH**
- Problem: No systematic evaluation of prompt/persona changes. Prompt sensitivity means casual A/B testing is worthless.
- Fix: Create 20-30 representative tasks (code review, test generation, architecture decision, bug fix) with checkable outcomes. Use for regression testing prompt changes.
- Acceptance: Eval set exists in `docs/eval/`. At least 20 tasks with ground truth.

### Change 9: Integrate humanization principles into documentation instructions
**Impact: LOW-MEDIUM | Evidence: S | Effort: LOW**
- Problem: Documentation output sounds AI-generated. Humanization research identifies 9 levers.
- Fix: Add to `documentation.instructions.md`: burstiness requirement (vary sentence lengths), banned AI vocabulary list, active voice preference.
- Acceptance: documentation.instructions.md includes humanization rules.

### Change 10: Add cache-aware layout guidance to task skill
**Impact: LOW-MEDIUM | Evidence: WS | Effort: LOW**
- Problem: Mid-task file rewrites break prompt caching.
- Fix: In task skill, add: "Never rewrite always-loaded context files (CLAUDE.md, state.json) mid-task. Rewrite only between tasks."
- Acceptance: Task skill Phase 2 includes cache-awareness note. (Note: this may already be present — verify first.)

---

## Part 5 — Implementation Plan

### Wave 1: Quick wins (Changes 2, 5, 6, 7, 9) — all LOW effort
- Rewrite negative invariants as positive specs
- Reorder CLAUDE.md for position effects
- Add escalation rules to multi-turn skills
- Add third-person framing to code-reviewer
- Add humanization rules to documentation instructions

### Wave 2: Structural changes (Changes 1, 3, 10) — MEDIUM effort
- Tier invariants (requires CLAUDE.md restructuring + path-specific instruction updates)
- Add reasoning-model-aware dispatch to delegation.md
- Verify/add cache-aware guidance to task skill

### Wave 3: Quality infrastructure (Changes 4, 8) — HIGH effort
- MCP tool schema audit
- Build eval set for prompt regression testing

### TDD Approach
Each change follows:
1. Write a test that verifies the current state (RED — should fail if change is needed)
2. Apply the change
3. Verify the test passes (GREEN)
4. Refactor if needed

For prompt/context file changes, "tests" are:
- Grep-based checks (e.g., "no invariant starts with 'No'")
- Line-count checks (e.g., "CLAUDE.md invariant section ≤7 items")
- Structural checks (e.g., "first 3 items are Tier 1 invariants")

---

## Appendix — Research Sources

| Source | ArXiv/Venue | Grade |
|---|---|---|
| Zheng et al. "When 'A Helpful Assistant' Is Not Really Helpful" | arXiv 2311.10054, EMNLP Findings 2024 | WS |
| Laban et al. "LLMs Get Lost in Multi-Turn Conversation" | arXiv 2505.06120 | WS |
| Huang et al. "LLMs Cannot Self-Correct Reasoning Yet" | ICLR 2024, arXiv 2310.01798 | WS |
| Sharma et al. "Understanding Sycophancy in Language Models" | ICLR 2024, arXiv 2310.13548 | WS |
| Liu et al. "Lost in the Middle" | TACL 2024, arXiv 2307.03172 | WS |
| Tam et al. "Let Me Speak Freely?" | EMNLP Industry 2024, arXiv 2408.02442 | WS |
| Sclar et al. "Quantifying Language Models' Sensitivity to Spurious Features" | ICLR 2024, arXiv 2310.11324 | WS |
| IFEval (Zhou et al.) | arXiv 2311.07911 | WS |
| Sprague et al. "To CoT or not to CoT?" | arXiv 2409.12183 | WS |
| Wallace et al. "The Instruction Hierarchy" | arXiv 2404.13208 | WS |
| DocsChisel (tool schema quality) | arXiv 2608.10037 | S |
| Trace-Free+ (tool description rewriting) | arXiv 2602.20426 | S |
| SYCON Bench (sycophancy + third-person) | EMNLP Findings 2025 | S |
| Truth Decay (sycophancy compounding) | arXiv 2503.11656 | S |
