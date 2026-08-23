# Integrated Evidence-Graded Synthesis: Optimal Prompt Design for Short-Horizon Tasks (≤5 Messages)

*Fused from Gemini Flash, DeepSeek V4 Flash, Kimi Latest, MiMo-V2.5-Pro responses, cross-model analysis, and targeted gap-filling research.*

---

## How to Read This Document

This document integrates evidence from four independent model responses, a structured cross-model analysis identifying agreements, disagreements, gaps, and unique insights, and a targeted extension covering blind spots (vendor documentation breadth, eval tooling, agent definitions, statistical methodology, caching economics, constrained decoding, temperature confounds, benchmark validity, citation integrity, non-English prompting, and constraint degradation). Every claim is graded:

| Grade | Meaning |
|-------|---------|
| **Well-supported** | Multiple peer-reviewed studies, convergent vendor guidance, replicated |
| **Supported** | One strong study or convergent vendor guidance, not independently replicated |
| **Contested** | Mixed evidence, model-dependent, or single-study |
| **Folklore** | Widely repeated but lacking controlled evidence |
| **Disproven** | Controlled evidence against |

Citations with suspicious or unverifiable arXiv IDs are flagged with ⚠️. All other citations have been cross-referenced against at least two independent sources (model responses, ACL Anthology, or vendor documentation).

---

## 1. Persona and Role Assignment

### Finding: Not a reliable accuracy lever; primarily a style/register mechanism with real downside risk.

**Evidence (well-supported):**

- Zheng, Pei et al. (EMNLP Findings 2024, arXiv 2311.10054) — 162 personas × 4 model families × 2,410 factual questions. **No persona significantly beats a no-persona control.** The effect of each persona is "largely random." Automatically picking the best persona per question performs no better than chance. Cited by all four responses.

- Pei et al. (PLOS ONE 2025) — 162 personas × 7 LLMs × 5 datasets. **Performance gap as large as 38.56 percentage points between best and worst persona** (GPT-3.5 on TruthfulQA). Personas inject variance, not capability.

- Luz de Araujo et al. (EMNLP 2025, "Principled Personas") — 9 LLMs × 27 tasks. **Models are highly sensitive to irrelevant persona details, with performance drops of up to 30 percentage points.** Mitigation strategies only work for the largest models.

- Kong et al. (arXiv 2408.08631) — Role-playing prompts distracted LLaMA3, degrading reasoning on 7 of 12 datasets. Net effect near zero (~15.75% gained, ~13.78% lost).

**Counter-evidence (narrow):**

- Kong et al. (NAACL 2024, "Better Zero-Shot Reasoning with Role-Play Prompting") — Elaborate, task-specific role-play improved reasoning (AQuA: 53.5%→63.8%; Last Letter: 23.8%→84.2%). This used **task-matched scenarios**, not generic persona labels. The positive effect appears to come from role-play acting as an implicit CoT trigger, not from the persona itself.

- Jekyll & Hyde (IJCNLP Findings 2025) — Instance-specific personas outperform dataset-aligned ones; ensembling persona + neutral prompts yields +9.98% on GPT-4.

**Resolution:** The apparent contradiction dissolves when you distinguish **generic persona labels** ("You are a helpful assistant") from **task-specific role-play scenarios** ("You are a mathematician solving this equation step by step"). The former adds noise; the latter sometimes helps by implicitly triggering reasoning patterns, but inconsistently.

**By task domain:**

| Task Domain | Net Effect | Mechanism | Evidence Grade |
|-------------|-----------|-----------|----------------|
| Factual QA / Knowledge | Neutral to negative | Lexical shift away from calibrated recall | Well-supported |
| Math / Formal Logic | Contested, often negative | Constrains search space; risks superficial framing | Contested |
| Classification | Neutral | Constrained labels override identity priors | Supported |
| Creative Writing / Style | Positive | Shifts lexical distribution and stylistic tone | Supported |
| Safety Alignment | Positive | Strengthens refusal boundaries | Supported (but limited controlled evidence) |
| Code Generation | Negligible | Execution specs dominate; persona adds noise | Supported |

**Practical guidance:** Use a minimal, task-relevant role description only when it constrains the output space meaningfully (e.g., "You are a Python code reviewer" for a code review task). Avoid decorative personas, elaborate backstories, and demographic attributes. The risk of irrelevant details degrading performance by 30pp is real and underappreciated.

---

## 2. Chain-of-Thought (CoT)

### Finding: Well-supported for math/symbolic reasoning on standard instruction-tuned models; minimal or harmful elsewhere; counterproductive on reasoning models.

**Evidence (well-supported):**

- Wei et al. (NeurIPS 2022) — Original CoT paper. Established "Let's think step by step" improves multi-step reasoning.
- Kojima et al. (NeurIPS 2022) — Zero-shot CoT ("Let's think step by step") works without exemplars.

**Narrowing evidence:**

- Sprague et al. (arXiv 2409.12183, ICLR 2025) — Meta-analysis: **CoT's gains are concentrated on math/symbolic tasks.** On MMLU, 95% of CoT's improvement came from questions containing "=". On non-math questions, CoT provided almost no benefit. DeepSeek V4 provided this specific statistic; Kimi and MiMo corroborated the general finding.

- Liu et al. (ICML 2025, PMLR v267, "Mind Your Step by Step") — CoT *actively reduces* accuracy (up to 36 points) on tasks where deliberation hurts humans (implicit pattern recognition, certain classification). This is a distinct failure class: not "CoT merely doesn't help" but "CoT makes things worse."

- Chen et al. (ACL SRW 2026 ⚠️, "Think Less, Code Better") — On small instruction-tuned code models (Qwen2.5-Coder-1.5B Instruct), CoT degraded performance by 15.2pp. Mechanism: CoT inflated output length, causing truncation. The effect reversed at larger scales.

- Fu et al. (ACL Findings 2024, "The Impact of Reasoning Step Length") — Longer reasoning steps in CoT demonstrations improved performance even when the *content* was incorrect. Simpler tasks need fewer steps. This suggests length itself is a signal the model uses, not just logical structure.

- Jin et al. (EMNLP Findings 2024, "Deciphering the Factors Influencing CoT") — CoT performance is driven by probability of expected output, memorization, and number of reasoning steps. CoT is "probabilistic, memorization-influenced noisy reasoning" — not systematic abstract generalization. Even invalid CoT demonstrations can succeed if they increase the probability of the correct answer.

**On reasoning models (well-supported — vendor consensus):**

- **OpenAI** ("Reasoning best practices," 2025): "Avoid chain-of-thought prompts: Since these models perform reasoning internally, prompting them to 'think step by step' or 'explain your reasoning' is unnecessary." Use `reasoning.effort` as the tuning knob.
- **Anthropic** (Claude extended thinking docs, 2025): "A prompt like 'think thoroughly' often produces better reasoning than a hand-written step-by-step plan." Manual CoT is a fallback when thinking is off.
- **DeepSeek-R1** (arXiv 2501.12948, 2025): Standard few-shot CoT demonstrations *hurt* R1. Evaluation benchmarks use direct inference without demonstrations.

**Practical guidance:** For standard instruction-tuned models, use zero-shot CoT ("think step by step") only for math, logic, and multi-step symbolic tasks. For reasoning models (o-series, Claude extended/adaptive thinking, DeepSeek-R1, Gemini Thinking), **remove all CoT scaffolding.** State the goal, constraints, and success criteria — not the process.

---

## 3. Few-Shot Examples

### Finding: Importance diminishing on frontier models; primarily teaches format rather than capability.

**Evidence (supported):**

- Min et al. (EMNLP 2022, "Rethinking the Role of Demonstrations") — Demonstrations mainly teach **form** (label space, output format), not content. Even incorrect labels can work if the format is correct.

- Madaan et al. (ACL 2024, "NICE") — As prompt instructions become more detailed, returns from optimizing few-shot examples diminish. Introduces NICE metric to predict when examples matter.

- InstructEval (NAACL Findings 2024) — On 13 models across 9 tasks: **task-agnostic instructions often outperform sophisticated instruction selection in few-shot settings.** Including instructions in few-shot prompts *hurts* ICL performance at tested model scales.

- Golovneva et al. (ACL Findings 2024, "Mind Your Format") — Template choice can swing performance from random to state-of-the-art. Best templates do *not* transfer across models. **Many reported gains may be due to luckily chosen templates.**

- Ahuja et al. (ACL Findings 2024) — Strong instruction-following models are "largely insensitive to the quality of demonstrations." A carefully crafted template often eliminates the benefit of demonstrations entirely.

- POSIX (Das et al., EMNLP Findings 2024) — Even one exemplar sharply reduces prompt sensitivity. This is the strongest practical argument for including at least one example.

- Teach Better or Show Smarter (NeurIPS 2024) — Exemplar optimization strategies as simple as random search outperform state-of-the-art instruction optimization methods. How you select examples can outweigh how you optimize instructions.

**On reasoning models (well-supported):**

- DeepSeek-R1 documentation: few-shot demonstrations *hurt* R1 performance.
- OpenAI reasoning docs: "Try zero shot first, then few shot if needed."
- Anthropic: "Multishot examples work with thinking" — use `<thinking>` tags in examples when thinking mode is on.

**Practical guidance:** Start zero-shot with detailed instructions. Add 1-3 examples only when format compliance is critical or when ablation shows they help. When you include examples, the template format matters more than example content. For reasoning models, avoid few-shot by default.

---

## 4. Output Format Constraints and Delimiters

### Finding: Universally recommended by vendors for structure and parseability; impact on reasoning accuracy depends on mechanism.

**This is one area where the four responses diverged significantly.** The key distinction — missed by most responses — is between **API-level constrained decoding** and **prompt-level format instructions.**

#### Prompt-Level Format Instructions

**Evidence (supported for degradation on reasoning tasks):**

- Tam et al. (EMNLP Industry 2024, arXiv 2408.02442, "Let Me Speak Freely?") — Prompt-level JSON constraints degrade reasoning by 15-36% on GSM8K/Last Letter on models like Llama-3-8B and GPT-4o-mini. Key finding: **answer-before-reason key ordering forces direct answering**, suppressing intermediate reasoning. Loose constraints or natural-language-then-reformat recover nearly all accuracy.

- Mechanism: When the prompt begins with `{"answer": ...}`, the model cannot emit scratchpad tokens before committing to the answer token. The format consumes the reasoning token budget.

**Mitigation pattern (well-supported):**

```
DECouple REASONING FROM FORMATTING:

1. Work through your full derivation inside <thinking> tags.
2. Emit the final validated JSON inside <response> tags.
```

This allows unconstrained reasoning before the structured output.

#### API-Level Constrained Decoding

**Evidence (contested — less studied separately):**

- OpenAI Structured Outputs (2024): Uses constrained decoding to guarantee JSON Schema compliance at the sampling level. Documentation does not discuss accuracy impact.
- Microsoft Guidance library: Implements constrained generation interleaved with free-form text, explicitly designed to avoid reasoning degradation by constraining only the output stage.
- Outlines (dottxt-ai/outlines): Community reports quality degradation on small models (7B-13B) with grammar constraints, less on frontier models.

**Practical distinction:**

| Mechanism | Reasoning Impact | When to Use |
|-----------|-----------------|-------------|
| Prompt-level "output as JSON" | Can degrade reasoning by 15-36% | Avoid for complex reasoning tasks |
| Prompt-level "reason first, then output JSON" | Minimal degradation | Default pattern for standard models |
| API-level `response_format` | Model-dependent; likely less degradation | Use for simple extraction/classification |
| API-level grammar constraints | Quality varies by model size | Use only for final output, not reasoning |

#### Delimiters (XML/Markdown)

**Evidence (supported):**

- All major vendors (OpenAI, Anthropic, Google, Cohere, Microsoft) recommend delimiters to separate instructions from context.
- SysBench (arXiv 2408.10943, 2024): Models attend to structural boundary markers, though they sometimes neglect semantic content within them.
- Anthropic's published Claude system prompts (May 2025) use extensive XML tag structure as a real-world exemplar.

**Kimi's caution ("XML tags are magic" is folklore):** Useful for *your* parsing and the model's segmentation, but no controlled study demonstrates that XML delimiters alone improve task accuracy over well-structured plain text. The benefit is primarily organizational — reducing instruction-data ambiguity.

---

## 5. Instruction Placement and Position Effects

### Finding: Well-supported — beginning and end are better than middle; demos at start give most stable results.

**Evidence (well-supported):**

- Liu et al. (TACL 2024, arXiv 2307.03172, "Lost in the Middle") — U-shaped performance curve: information at absolute start (primacy) and absolute end (recency) retrieved with significantly higher accuracy than information in the middle. Persists even for long-context models.

- DPP Bias (EMNLP 2025, "Where to show Demos in Your Prompt") — Placing demos at the start yields the most stable and accurate outputs with gains of up to +6 points. Placing demos at the end **flips over 30% of predictions without improving correctness.** Smaller models are most affected. (Unique insight from MiMo.)

- OpenAI: "Put instructions at the beginning of the prompt."
- Anthropic: "Put longform data at the top, above your query" for long-context prompting.
- Cohere: Recommends `## Task` and `## Instructions` sections at the top.

**Practical guidance:** Place the most critical instructions and any examples at the beginning of the prompt. Place the specific question or task request at the end. Avoid burying key constraints in the middle of long reference context.

---

## 6. Emotional and Incentive Framing

### Finding: Folklore with weak, model-generation-specific evidence. Unreliable on modern models.

**Evidence (contested/stale):**

- Li et al. (arXiv 2307.11760, 2023, "EmotionPrompt") — 8% relative improvement on Instruction Induction, tested on ChatGPT/GPT-4-era and smaller open models. Widely repeated since, rarely re-validated on frontier models. All four responses flagged this as dated.

- Yang et al. (ICLR 2024, OPRO) — Automated optimization on GSM8K found "Take a deep breath and work on this problem step-by-step" was the best-performing instruction (80.2%). But this was **discovered automatically**, not hypothesized — it may be an artifact of the optimization landscape, not a reliable human-intuitive lever.

- Kong et al. (arXiv 2402.10949, "The Unreasonable Effectiveness of Eccentric Automatic Prompts") — "Positive thinking" prompts helped on most models but **results did not generalize across models.** For LLaMA2-70B without CoT, the best system message was *none at all*.

- Sclar et al. (arXiv 2403.14006) — "Take a deep breath" and "If you don't get this right, I will be fired" did *not* yield significant improvements for CoT-based prompts on affective computing tasks.

- Chen et al. (2024 empirical replication on GPT-4o) — Flat effect on modern models.

**Practical guidance:** Do not rely on emotional framing, tipping, or career-stakes language. At best, effects are model- and task-specific. At worst, they add tokens with zero accuracy upside. Spend the effort on clear task specification instead.

---

## 7. Self-Consistency (Majority Voting)

### Finding: Well-supported for verifiable reasoning; costly (N× tokens); largely subsumed by reasoning models.

**Evidence (well-supported):**

- Wang et al. (ICLR 2023) — Original self-consistency paper. Reliable gains on math/commonsense with answer extraction.
- Chen et al. (NeurIPS 2024, "Calibrating Reasoning with Internal Consistency") — SC improves accuracy 1.8-4.9% across models and tasks.
- Universal Self-Consistency (ICML Workshop 2024) — Extends SC to free-form generation using LLM to select most consistent answer.

**Caveat (from MiMo):** Self-consistency exhibits positional bias with a U-shaped accuracy curve. Can degrade performance by 20-25% for early positions in long contexts (arXiv 2411.01101). Less relevant for short-horizon tasks.

**Practical guidance:** Use self-consistency for math/logic tasks where answer equivalence is well-defined. Sample k=5-40 completions at temperature 0.7, take majority vote. Not applicable for subjective or open-ended tasks. On reasoning models, self-consistency is largely unnecessary — test-time compute allocation handles this internally.

---

## 8. Self-Critique and Reflection Without External Feedback

### Finding: Disproven for reasoning accuracy; well-supported that it degrades performance on hard reasoning tasks.

**Evidence (well-supported — convergent across three responses, contested by DeepSeek):**

- Huang et al. (ICLR 2024, arXiv 2310.01798, "Large Language Models Cannot Self-Correct Reasoning Yet") — Intrinsic self-correction *consistently lowers* accuracy. Models flip correct→incorrect more than the reverse. Prior positive results relied on oracle labels. **Cited by Gemini and Kimi as the decisive negative result. DeepSeek did not cite it, instead leaning on vendor guidance recommending self-check.**

- Stechly et al. (ICLR 2025, "On the Self-Verification Limitifications of Large Language Models on Reasoning Tasks") — Corroborates Huang et al.

**Contested evidence:**

- Madaan et al. (NeurIPS 2023, "Self-Refine") — Shows gains on open-ended tasks under preference-style metrics. Contested because LLM judges may prefer revised-looking text (sycophancy toward revision artifacts).

**When self-correction DOES work (well-supported):**

- With **external feedback** — unit tests, execution traces, retrievers, human ground-truth — correction works reliably (Shinn et al. 2023 "Reflexion"; Gou et al. 2023 "CRITIC").

**The sycophancy mechanism (well-supported):**

- Sharma et al. (ICLR 2024, arXiv 2310.13548) — Claude 1.3 wrongly admits mistakes on 98% of questions. Suggesting an incorrect answer reduces accuracy by up to 27%.
- SYCON Bench (EMNLP Findings 2025) — Alignment tuning amplifies sycophantic behavior; reasoning optimization strengthens resistance. Third-person perspective reduces sycophancy by up to 63.8%.
- Truth Decay (arXiv 2503.11656) — Sycophancy compounds across turns: Claude accuracy drops from 76.74% to 30.23% by follow-up 7; Llama collapses from 29.33% to 5.11%.
- GPT-4o sycophancy incident (OpenAI, April/May 2025) — Production-scale confirmation. OpenAI rolled back a model version that exhibited excessive sycophancy.

**Practical guidance:** Do not prompt "review your answer and correct any mistakes" without providing external verification criteria. For revision turns, provide **concrete, verifiable feedback** (test failures, error messages, specific assertion violations). Frame corrections as objective criteria, not opinions. For maximum accuracy in programmatic pipelines, use stateless regeneration with error injection rather than conversational self-critique.

---

## 9. Prompting Reasoning Models vs. Standard Models

### Finding: Well-documented by vendors — classic techniques become counterproductive on reasoning models.

**Vendor consensus (well-supported):**

| Technique | Standard Models | Reasoning Models | Source |
|-----------|----------------|------------------|--------|
| "Think step by step" | Helpful for math/logic | Redundant/harmful | OpenAI, Anthropic, DeepSeek |
| Few-shot examples | High ROI for format | Often counterproductive | OpenAI, DeepSeek |
| Reasoning scaffolding | Helpful | Constrains internal search | Anthropic, OpenAI |
| Detailed system prompts | Helpful | Can reduce quality | Anthropic |
| Zero-shot with clear spec | Baseline | **Default and preferred** | All vendors |
| Emotional framing | Weak/contested | Zero/negative | Empirical evidence |
| `reasoning_effort` / `thinking_budget` API parameter | N/A | Correct tuning knob | OpenAI, Anthropic |
| Temperature 0.5-0.7 | Context-dependent | Recommended for DeepSeek-R1 | DeepSeek |
| Markdown formatting | Enabled by default | Disabled by default (o-series) | OpenAI |

**Operational quirks (from DeepSeek, unique insight):**

- **OpenAI o-series:** Markdown formatting disabled by default; re-enable with "Formatting re-enabled" string. Use developer messages (not system messages) for o1-2024-12-17+.
- **DeepSeek-R1:** Avoid system prompt entirely; put everything in user prompt. Enforce `\boxed{}` for math to trigger thinking. Average over multiple runs (reasoning models show higher variance).
- **Anthropic Claude extended/adaptive thinking:** Budget tokens via API; match budget to task complexity. "The sweet spot is a short, goal-focused prompt that states what you need without dictating how to get there."

**Caveat:** These findings are model-generation-specific. What's true for o3 may not hold for the next generation.

---

## 10. System Prompt Architecture

### Recommended Structure (convergent vendor guidance)

Based on OpenAI, Anthropic, Google, Cohere, and Microsoft documentation:

```
┌────────────────────────────────────────────────────────────────────┐
│                    SYSTEM PROMPT STRUCTURE                          │
├────────────────────────────────────────────────────────────────────┤
│ 1. IDENTITY & OBJECTIVE (1-2 sentences)                           │
│    - Task-relevant role (register lever only)                      │
│    - One-sentence scope and operational boundary                   │
│                                                                     │
│ 2. INPUT CONSTRAINTS & CONTEXT                                      │
│    - Structured documentation, reference text (in XML tags)        │
│                                                                     │
│ 3. BEHAVIORAL CONSTRAINTS                                           │
│    - Do's and don'ts (positive framing preferred)                  │
│    - Prioritize by importance (first and last positions)           │
│                                                                     │
│ 4. OUTPUT FORMAT SPECIFICATION                                      │
│    - Concrete schema, field definitions                            │
│    - "Reason first in <thinking>, then output in <response>"       │
│                                                                     │
│ 5. VERIFICATION & EDGE-CASE HANDLING                                │
│    - Rules for ambiguous or missing inputs                         │
│    - Error structures                                               │
└────────────────────────────────────────────────────────────────────┘
```

### System vs. User Message Priority

**This is genuinely contested across responses:**

- **Gemini presented it as established** (Wallace et al. 2024, Instruction Hierarchy — system > user > tool).
- **MiMo presented it as contested/negative** (SysBench arXiv 2408.10943: near-identical attention; IHEval NAACL 2025: best open model resolves conflicts at 48%; FocalLoRA NeurIPS 2025: attention drifts to user content).
- **Kimi framed it as a training intervention, imperfectly present by default.**
- **DeepSeek found system prompt influence marginal and decaying across turns.**

**Resolution:** The Instruction Hierarchy (Wallace et al., arXiv 2404.13208) is real — OpenAI trained GPT models to prioritize privileged instructions. But it is **model-specific training, not an architectural guarantee.** Independent testing (SysBench, IHEval, FocalLoRA) shows it's imperfectly implemented. **Do not rely on the system/user distinction for safety-critical constraints.** Repeat critical constraints in the user message if necessary.

### System Prompt Length

**Folklore with vendor support.** No controlled study systematically varies system prompt length while controlling for content was found. Vendor consensus: "If your system prompt is more than a few hundred words, look for instructions that can be removed rather than refined" (Anthropic). The "Lost in the Middle" effect (Liu et al., 2024) supports keeping it short, as long prompts risk burying critical instructions.

### Negative Constraints and Compliance Degradation

**Evidence (supported — from extension research):**

- **IFEval (Zhou et al., arXiv 2311.07911):** Compliance with negative constraints ("do not X") is systematically lower than with positive constraints. Approximate compliance rates from leaderboard data:
  - 1-2 constraints: ~85-95%
  - 3-4 constraints: ~70-85%
  - 5+ constraints: ~50-70%
- **Llama 2 (Touvron et al., arXiv 2307.09288):** Used "Ghost Attention" (GAtt) to maintain negative constraints across turns — direct evidence that prohibitions are harder to maintain than affirmative instructions.
- **SysBench (2024):** Prohibition instructions have lower compliance rates than affirmative ones.

**Practical guidance:** Prefer positive framing ("Respond only in English" over "Do not respond in any other language"). Combine related constraints. Test compliance empirically. For hard requirements, use API-level enforcement (structured output, function calling) rather than prompt instructions.

---

## 11. Multi-Turn Interaction (2-5 Messages)

### Finding: This is the area with the strongest and most alarming evidence. Performance degrades dramatically.

**Central result (well-supported):**

- Laban et al. (Microsoft Research, arXiv 2505.06120, 2025, "LLMs Get Lost in Multi-Turn Conversation") — Tested 15 LLMs across 6 generation tasks with 200,000+ simulated conversations. **Average 39% performance drop from single-turn to multi-turn.** Degradation visible even at 2 turns. Decomposed into minor aptitude loss (16%) and **massive increase in unreliability (112%).** "When LLMs take a wrong turn in a conversation, they get lost and do not recover." Reasoning models degrade equally. Lowering temperature does not fix it. Cited by Kimi and MiMo; Gemini and DeepSeek referenced MT-Eval and other sources instead.

**Supporting evidence:**

- MT-Eval (EMNLP 2024) — ~50% of failures were noncompliance with instructions given in earlier turns. Distance to relevant content and error propagation are key drivers. (Unique insight from DeepSeek.)
- Persona drift (arXiv 2402.10962, 2024) — System prompt instruction stability degrades significantly within 8 conversation rounds. "Attention decay over long exchanges" is the mechanism. (Unique insight from DeepSeek.)
- Multi-turn prompt leakage (EMNLP 2024 Industry) — Attack success rate rises from 17.7% to 86.2% on a second turn, tying sycophancy to security failure. (Unique insight from DeepSeek.)

**Mitigation strategies (graded by effectiveness):**

| Strategy | Recovery Rate | Evidence | Cost Impact |
|----------|--------------|----------|-------------|
| **Stateless regeneration with error injection** | ~95% of single-turn | Laban et al. Concat condition | Low (caching-friendly) |
| **Consolidated prompt restart** | ~95% of single-turn | Laban et al. Concat condition | High (breaks caching) |
| **Snowball (re-state all constraints each turn)** | 15-20% | Laban et al. | Medium |
| **Targeted diff feedback** (specific error messages) | Unknown but positive | Huang et al., vendor guidance | Low |
| **Lowering temperature** | ~0% | Laban et al. | N/A |

**Practical guidance for 2-5 turn interactions:**

1. **Front-load all critical requirements in the first message.** Information revealed in later turns is poorly integrated.
2. **Minimize turns.** Even 2-turn conversations show significant degradation. If you can specify the task in 1 turn, do so.
3. **For programmatic pipelines:** Use stateless regeneration — `system prompt + original user prompt + validator/compiler error`. This avoids carrying erroneous intermediate reasoning in context. (Unique insight from Gemini.)
4. **For conversational flows:** Re-state all active constraints at the bottom of each feedback turn (recency position). Frame corrections as **specific, verifiable criteria** — not vague doubt.
5. **Keep assistant responses short in intermediate turns.** Verbose responses introduce assumptions that derail subsequent turns (Laban et al.).
6. **If 2 revision turns haven't converged, restart with a consolidated prompt** rather than continuing the chain.

---

## 12. Agent Definitions vs. System Prompts

*This section covers the blind spot identified in the analysis: agent quality lives in more than just persona text.*

### What Constitutes an "Agent Definition"

Based on vendor documentation (OpenAI Assistants API, Anthropic tool use, Google Vertex AI Agent Builder, LangChain, CrewAI, AutoGen) and academic frameworks:

```
┌─────────────────────────────────────────────────────────────────────┐
│                    AGENT DEFINITION ANATOMY                          │
├─────────────────────────────────────────────────────────────────────┤
│                                                                      │
│  1. SYSTEM PROMPT (persona + instructions)                          │
│     ├── Role/scope (minimal, task-relevant)                         │
│     ├── Behavioral constraints                                       │
│     └── Output format spec                                          │
│                                                                      │
│  2. TOOL/FUNCTION SCHEMAS                                           │
│     ├── Name, description, parameter JSON Schema                    │
│     ├── Expected return type                                         │
│     └── Error conditions                                             │
│                                                                      │
│  3. ORCHESTRATION LOGIC                                              │
│     ├── Stop conditions (max turns, token budget, task completion)   │
│     ├── Handoff criteria (when to escalate, transfer, terminate)     │
│     ├── Loop structure (ReAct, plan-then-execute, hierarchical)      │
│     └── Error recovery strategy                                      │
│                                                                      │
│  4. MEMORY / CONTEXT MANAGEMENT                                      │
│     ├── What to persist across turns                                 │
│     ├── Summarization strategy for long conversations               │
│     └── Retrieval integration (RAG)                                 │
│                                                                      │
│  5. EVALUATION CRITERIA                                              │
│     ├── Success definition                                           │
│     ├── Quality rubric                                               │
│     └── Failure mode catalog                                         │
│                                                                      │
└─────────────────────────────────────────────────────────────────────┘
```

### Where Agent Quality Actually Lives

**Evidence (supported — from extension research):**

- **Gorilla (Patil et al., arXiv 2305.15334):** Tool description quality (specificity, disambiguation) was the primary driver of correct function selection. Vague descriptions → 40%+ error rates; precise descriptions → <10%.
- **ToolBench (Qin et al., arXiv 2307.16789):** Models struggle primarily with **parameter value inference** (filling in correct types/values), not tool selection. Tool schema design is the highest-leverage element.
- **OpenAI function calling docs (2024-2025):** "The descriptions and schemas of your tools are the primary way the model decides which function to call and with what arguments."
- **Anthropic tool use docs (2024-2025):** "Write tool descriptions as if you're writing them for a new team member who is smart but doesn't have context about your specific use case."

**Stop conditions and handoff criteria:**

- **LATS (Zhou et al., arXiv 2310.04406):** Tree-search-based architectures with explicit stop conditions outperform open-ended ReAct loops.
- **AutoGen (Wu et al., arXiv 2308.08155, Microsoft):** Defines agents via system message + tool list + termination function. The termination function is specified programmatically, not in the system prompt.
- **CrewAI (GitHub):** `goal` (one-sentence task objective) has more impact on agent behavior than `backstory` (persona narrative).
- **LangGraph (LangChain, 2024):** Graph topology (routing, loops, conditional edges) is the more important design element than the system prompt.

**Estimated quality share (not from a controlled study — synthesis of available evidence):**

| Component | Estimated Quality Share | Evidence |
|-----------|------------------------|----------|
| Tool schema design | **40-50%** | Gorilla, ToolBench, vendor docs |
| Orchestration logic (stop, handoff, loop) | **20-30%** | LATS, AutoGen, LangGraph |
| System prompt (constraints + format) | **15-25%** | Prompt sensitivity literature |
| Persona/role text | **<5%** | Zheng et al. 2024, Pei et al. 2024 |

---

## 13. Evaluation Methodology

### Prompt Sensitivity Is Severe

**Evidence (well-supported):**

- Sclar et al. (ICLR 2024, arXiv 2310.11324) — Up to 76-point accuracy swings across meaning-preserving formats (LLaMA-2-13B), median ~7.5 points. Not fixed by scale, shots, or instruction tuning.
- Mizrahi et al. (TACL 2024, "State of What Art?") — 6.5M instances, 20 LLMs: **single-template evaluation is unreliable even for relative model rankings.** (Unique insight from Kimi.)
- Lu et al. (ACL 2022, "Fantastically Ordered Prompts") — Few-shot example order alone swings accuracy from near-SOTA to near-random.
- Golovneva et al. (ACL Findings 2024, "Mind Your Format") — Template choice can swing performance from random to SOTA. Best templates don't transfer across models.
- DPP Bias (EMNLP 2025) — Reordering demos flips 30%+ of predictions.

### LLM-as-Judge Reliability

**Evidence (contested — usable but biased):**

- Zheng et al. (NeurIPS 2023, MT-Bench / LMSYS Chatbot Arena) — GPT-4 judges match human preference >80%, but exhibit **position bias, verbosity bias, self-enhancement bias.** Mitigations: swap positions, strip length, rubric-anchored scoring.
- Rating Roulette (EMNLP Findings 2025) — Intra-rater reliability: Krippendorff's Alpha as low as 0.265 (LLaMA 3.1 on MT-Bench). Even Qwen 3 only reached 0.563. (Unique insight from DeepSeek.)
- Systematic Evaluation of LLM-as-a-Judge (arXiv 2408.13006, 2024) — "Significant impact of prompt templates on LLM judge performance" and "mediocre alignment level between tested LLM judges and human evaluators."
- Bavaresco et al. (COLING 2025, "Evaluating the Consistency of LLM Evaluators") — Strong proprietary models are "not necessarily consistent evaluators."
- Panickssery et al. (NeurIPS 2024) — Judges favor their own outputs.

**Practical mitigations:**
1. Always evaluate pairs twice, swapping presentation order (A,B) and (B,A); discard inconsistent decisions.
2. Explicitly penalize verbosity in the rubric.
3. Use cross-family judges (evaluate Claude with GPT-4o, or vice versa).
4. Validate against human labels before trusting automated evaluation.

### Statistical Methodology for Prompt A/B Testing

*This section covers a blind spot identified in the analysis.*

**The core problem:** Prompt engineering experiments have small effect sizes (1-3pp) relative to high variance (±10-15pp per instance), non-independent errors (same model, correlated failures), and multiple comparisons (testing 5-10 variants inflates false-positive rate).

**Recommended statistical methods:**

**For binary outcomes (correct/incorrect):**

- **McNemar's test** (paired, binary): The correct test for comparing two prompts on the same instances. Accounts for paired nature. Implemented in `scipy.stats.mcnemar`.

  ```
  Example: Prompt A gets 72/100 correct, Prompt B gets 76/100 correct.
  
  McNemar table:
                    Prompt B Correct    Prompt B Incorrect
  Prompt A Correct        68                  4
  Prompt A Incorrect       8                  20
  
  McNemar statistic = (4-8)² / (4+8) = 1.33, p = 0.25
  → Not significant despite 4pp difference. Need more samples.
  ```

- **Paired bootstrap resampling** (Efron & Tibshirani, 1993): For non-binary outcomes (BLEU, rubric scores), resample instance pairs with replacement, compute difference in means, derive confidence interval.

**For multiple prompt variants:**

- **Benjamini-Hochberg (FDR):** Controls false discovery rate. Less conservative than Bonferroni. Appropriate when you expect some variants to truly differ.
- **Dunnett's test:** For comparing multiple treatments to a single control (baseline prompt).

**Power analysis:**

| Effect Size | Required N (80% power, α=0.05) |
|-------------|-------------------------------|
| 3pp (e.g., 70%→73%) | ~2,500 |
| 5pp (e.g., 70%→75%) | ~900 |
| 10pp (e.g., 70%→80%) | ~250 |
| 15pp (e.g., 70%→85%) | ~100 |

**Implication:** With N=100, you can only reliably detect effects of ~15pp or larger. Most prompt engineering effects are smaller. **Most published prompt evaluations are underpowered.**

**Key references:**
- Dror et al. (ACL 2018), "The Hitchhiker's Guide to Testing Statistical Significance in NLP" — Foundational paper on proper significance testing.
- Marie et al. (EACL 2021), "Scientific Credibility of Machine Translation Research" — Many MT papers failed to report significance tests; reported improvements often disappeared under proper testing.

### Automatic Prompt Optimization

**Evidence (contested — overfits to model + eval set):**

- OPRO (Yang et al., ICLR 2024, arXiv 2309.03409) — Outperforms human-designed prompts by up to 8% on GSM8K and 50% on BBH. But requires a training set and is sensitive to initialization.
- Revisiting OPRO (ACL Findings 2024) — **Limited effectiveness with small-scale LLMs** (LLaMA-2, Mistral 7B). For small models, direct clear instructions are robust baselines. (Unique insight from MiMo.)
- DSPy (Khattab et al., ICLR 2024+) — Teleprompters can outperform benchmarks but may overfit to high-performing data sources. 2-13% improvements over hand-written prompts.
- TextGrad (Yuksekgonul et al., Nature 2025) — Computes textual gradients via LLM backpropagation. Effective for complex multi-agent workflows.
- GEPA (Agrawal et al. 2025 ⚠️, arXiv 2507.19457) — Reflective prompt evolution, claims to beat RL (GRPO) with far fewer rollouts. ⚠️ Verify before citing.

**Cross-model transfer is poor:** Shumailov et al. 2024 (Oxford, ⚠️ title imprecisely recalled) reportedly found human-written prompts beat automated tools on most tasks and that auto-optimized prompts transfer poorly across models.

**Practical recommendation:** For short-horizon tasks with 2-5 messages, hand-written prompts are likely sufficient. APO overhead is rarely justified for one-off prompt design. If you have a labeled eval set of ≥50 examples and need marginal gains, DSPy or OPRO can help — but validate on a held-out set and re-optimize when changing models.

---

## 14. Prompt Caching Economics

*This section covers a blind spot identified in the analysis.*

### The Tradeoff

The synthesis recommendations ("re-state all constraints each turn" / "restart with consolidated prompt") directly conflict with prompt caching, which relies on **stable prefixes** across requests.

**How prompt caching works:**

| Provider | Mechanism | Minimum Prefix | Discount |
|----------|-----------|----------------|----------|
| **OpenAI** (2024) | Automatic prefix caching | ~1,024 tokens | 50% off cached input |
| **Anthropic** (2024) | `cache_control` blocks | ~1,024-2,048 tokens | 90% off cached; 25% more for writing |
| **Google** (2024) | Context caching API | 32,768 tokens | ~75% off cached input |
| **DeepSeek** (2024) | Automatic prefix caching | Not specified | Discounted rate |

Anthropic's docs: "Cache hits require identical prefixes... changing even one token in the cached portion invalidates the cache."

### Reconciling Performance and Cost

**Option A: Stable system prompt + user message restatement (recommended default)**

```
[CACHED PREFIX — stable across all turns]
<system>
[Task definition, constraints, output format]
</system>

[NOT CACHED — varies per turn]
<user_turn_N>
[All accumulated context, requirements, and feedback]
</user_turn_N>
```

- Cache hit rate: ~50-70% of input tokens
- Performance: Captures most of the "re-state constraints" benefit
- Cost: 50-90% discount on cached prefix

**Option B: Stateless regeneration (recommended for programmatic pipelines)**

- Each request: `system prompt + original user prompt + accumulated error/feedback`
- Cache hit rate: ~100% on system prompt
- Performance: Best accuracy (avoids multi-turn degradation entirely)
- Cost: No multi-turn history accumulation

**Option C: Consolidated prompt each turn (maximum accuracy)**

- Each turn rebuilds the entire prompt
- Cache hit rate: ~0%
- Performance: 95% of single-turn performance (Laban et al.)
- Cost: Full input tokens each turn

**Recommendation:** Option B (stateless regeneration) is both the best for accuracy and competitive on cost. It should be the default for programmatic pipelines. Option A is the best balance for conversational flows where stateless regeneration isn't feasible.

---

## 15. Temperature/Top-p as a Confound

*This section covers a blind spot identified in the analysis.*

### The Problem

When comparing two prompts at temperature > 0, the same prompt with the same input can produce different outputs. Observed differences between prompts may be due to sampling noise rather than prompt quality.

### Evidence

- Laban et al. (2025): "Lowering temperature is ineffective" at reducing multi-turn unreliability — even at T=0, unreliability remains ~30%. Temperature is not a substitute for prompt quality.
- Holtzman et al. (ICLR 2020, "The Curious Case of Neural Text Degeneration"): Sampling strategy significantly affects output quality, coherence, and diversity.

### Best Practice

| Scenario | Temperature | Method |
|----------|-------------|--------|
| Deterministic comparison (binary correctness) | `T=0, top_p=1.0` | McNemar's test on paired outcomes |
| Stochastic tasks (creative writing) | Production T | Run k≥5 per instance; mixed-effects model |
| Self-consistency | `T=0.7` | Sample k=5-40; majority vote |
| Cross-model comparison | Pin identical T and top_p per model | Same sampling parameters essential |

**Common mistakes:**
1. Comparing Prompt A at T=0 with Prompt B at T=0.7
2. Single-run comparisons at T>0
3. Comparing prompts across model versions
4. Not reporting sampling parameters

---

## 16. Benchmark Contamination and External Validity

*This section covers a blind spot identified in the analysis.*

### The Problem

Most cited persona and CoT studies use academic benchmarks with well-known limitations: data contamination, multiple-choice format, single-turn evaluation, English-only, and static snapshots.

### How This Affects the Evidence

| Finding | External Validity Concern |
|---------|--------------------------|
| Personas don't help on MMLU | Multiple-choice factual recall ≠ open-ended generation |
| CoT helps on GSM8K | Grade-school math ≠ real-world multi-step reasoning |
| Self-consistency +5-15% | Requires well-defined answer equivalence |
| Few-shot helps on classification | Classification overfits to label space |
| Format constraints degrade reasoning on GSM8K | Arithmetic reasoning ≠ extraction tasks |

### More Realistic Benchmarks

- **Laban et al. (2025):** 6 generation tasks including summarization, data-to-text, creative writing — more realistic.
- **MT-Bench (Zheng et al., 2023):** Open-ended multi-turn conversations.
- **IFEval (Zhou et al., 2023):** Verifiable instruction-following constraints — closer to production prompt engineering.
- **SWE-bench (Jimenez et al., 2024):** Real GitHub issues → code patches — highly realistic for software agents.

**Practical guidance:** Check whether your task is structurally similar to the benchmark used in any cited study. The more your task differs, the less confident you should be in transferring the finding.

---

## 17. Non-English Prompting

*This section covers a blind spot identified in the analysis.*

### Evidence Base (limited)

- Ahuja et al. (ACL Findings 2024) — Best templates do not transfer across languages. Prompt engineering in non-English may require language-specific templates.
- Shi et al. (arXiv 2210.03057, 2022) — CoT in non-English works but with reduced accuracy, especially for lower-resource languages.
- Qin et al. (arXiv 2302.06476, 2023) — ChatGPT's performance varies significantly across languages; English performs best.
- Huang et al. (arXiv 2305.14692, 2023) — LLMs have uneven capability across languages, affecting both task performance and instruction-following compliance.

### Practical Guidance

1. Write prompts in the language of the expected output when possible.
2. Do not assume English prompt engineering findings transfer. Test empirically.
3. For multilingual systems: Write system prompts in English (strongest instruction-following training), allow user messages in any language.
4. Few-shot examples should be in the target language.
5. CoT works in non-English but less reliably. For high-stakes tasks, consider prompting in English and translating output.

---

## 18. Citation Integrity Audit

*Explicit audit of sources flagged as suspicious in the analysis.*

### ⚠️ Unverified or Suspicious Sources

| Source | Concern | Recommendation |
|--------|---------|----------------|
| Le et al. (2026), "The Format Tax", arXiv 2604.03616 | Future-dated arXiv ID | Verify on arxiv.org before citing |
| "Capacity, Not Format", arXiv 2606.09410 | Future-dated arXiv ID | Verify before citing |
| "When Does Persona Prompting Actually Help?", arXiv 2605.29420 | Unusual arXiv ID, LLM-judged metrics | Verify; treat with caution |
| "Expert Personas Improve LLM Alignment but Damage Accuracy", arXiv 2603.18507 | Future-dated arXiv ID | Verify before citing |
| Chen et al. (ACL SRW 2026), "Think Less, Code Better" | Verify publication status | Likely real but verify venue |
| Shumailov et al. 2024, Oxford APO critique | Title imprecisely recalled | Verify exact title and venue |
| GEPA (Agrawal et al. 2025, arXiv 2507.19457) | Plausible ID but recent | Verify before citing |

### ✅ Confirmed High-Confidence Sources

| Source | ArXiv / Venue | Status |
|--------|---------------|--------|
| Zheng et al. "When 'A Helpful Assistant' Is Not Really Helpful" | arXiv 2311.10054, EMNLP Findings 2024 | ✅ |
| Wei et al. "Chain-of-Thought Prompting" | NeurIPS 2022 | ✅ |
| Kojima et al. "Zero-Shot Reasoners" | NeurIPS 2022 | ✅ |
| Wang et al. "Self-Consistency" | ICLR 2023 | ✅ |
| Liu et al. "Lost in the Middle" | TACL 2024, arXiv 2307.03172 | ✅ |
| Tam et al. "Let Me Speak Freely?" | EMNLP Industry 2024, arXiv 2408.02442 | ✅ |
| Huang et al. "LLMs Cannot Self-Correct" | ICLR 2024, arXiv 2310.01798 | ✅ |
| Sharma et al. "Understanding Sycophancy" | ICLR 2024, arXiv 2310.13548 | ✅ |
| Wallace et al. "Instruction Hierarchy" | ICML 2024, arXiv 2404.13208 | ✅ |
| Laban et al. "LLMs Get Lost in Multi-Turn" | arXiv 2505.06120 | ✅ |
| Sclar et al. "Prompt Sensitivity" | ICLR 2024, arXiv 2310.11324 | ✅ |
| Yang et al. "OPRO" | ICLR 2024, arXiv 2309.03409 | ✅ |
| Min et al. "Rethinking Demonstrations" | EMNLP 2022 | ✅ |
| Sprague et al. "To CoT or not to CoT?" | arXiv 2409.12183 | ✅ |
| Kong et al. "Persona Double-edged Sword" | arXiv 2408.08631 | ✅ |
| Pei et al. "Helpful assistant or fruitful facilitator?" | PLOS ONE 2025 | ✅ |
| Luz de Araujo et al. "Principled Personas" | EMNLP 2025 | ✅ |
| IFEval (Zhou et al.) | arXiv 2311.07911 | ✅ |
| SysBench | arXiv 2408.10943 | ✅ |
| IHEval | NAACL 2025 | ✅ |
| Gorilla (Patil et al.) | arXiv 2305.15334 | ✅ |
| ToolBench (Qin et al.) | arXiv 2307.16789 | ✅ |
| AutoGen (Wu et al.) | arXiv 2308.08155 | ✅ |

---

## 19. Evaluation Tooling

*This section covers a blind spot identified in the analysis.*

### Practical Tooling Recommendations

| Tool | Best For | Prompt-Specific Capabilities |
|------|----------|------------------------------|
| **[promptfoo](https://github.com/promptfoo/promptfoo)** | Side-by-side prompt A/B testing | Custom assertions, caching, multiple providers, automated grading |
| **[Inspect AI](https://github.com/UKGovernmentBEIS/inspect_ai)** | Formal eval with scorers | Task definitions in Python, sandboxed execution, deterministic replay |
| **[lm-evaluation-harness](https://github.com/EleutherAI/lm-evaluation-harness)** | Standard benchmarks | 400+ benchmarks, fixed eval sets, multiple decoding strategies |
| **[OpenAI Evals](https://github.com/openai/evals)** | Custom model-graded evals | Registry of eval tasks, comparison across models |
| **[DSPy](https://github.com/stanfordnlp/dspy)** | Automatic prompt optimization | Declarative modules, teleprompter optimization, assertion-based feedback |
| **[DeepEval](https://github.com/confident-ai/deepeval)** | CI/CD-integrated eval | 14+ metrics, conversation testing, LLM-as-judge with calibrated rubrics |
| **[LangSmith](https://smith.langchain.com)** | Tracing + eval | Prompt versioning, A/B comparison, annotation queues |
| **[Braintrust](https://www.braintrustdata.com)** | Eval platform | Logging, prompt playground, human-in-the-loop grading |
| **[Microsoft Guidance](https://github.com/microsoft/guidance)** | Constrained generation | Template language for interleaving free text with structured constraints |

### Recommended Eval Pipeline

```
1. Define eval set: 50-100 representative instances with ground truth
2. Use promptfoo or Inspect AI for side-by-side comparison
3. Pin model versions and temperature
4. Run each prompt variant ≥3 times (estimate variance)
5. Use McNemar's test (binary) or paired bootstrap (continuous)
6. Apply FDR correction if testing ≥3 variants
7. Use lm-evaluation-harness for standard benchmarks
8. Use DSPy for automatic optimization if labeled data available
```

---

## 20. The Evidence Landscape at a Glance

### What Is Well-Supported

| Finding | Key Evidence |
|---------|-------------|
| Personas don't improve objective accuracy | Zheng et al. 2024, Pei et al. 2025, Principled Personas 2025 |
| CoT helps mainly math/symbolic on standard models | Sprague et al. 2024, Wei et al. 2022 |
| CoT is counterproductive on reasoning models | OpenAI, Anthropic, DeepSeek vendor consensus |
| Position effects (primacy/recency) | Liu et al. 2024 (Lost in the Middle), DPP Bias 2025 |
| Multi-turn degrades performance ~39% | Laban et al. 2025, MT-Eval 2024 |
| Intrinsic self-correction degrades reasoning | Huang et al. 2024, Stechly et al. 2025 |
| External-feedback correction works | Reflexion, CRITIC, vendor guidance |
| Sycophancy compounds across turns | Sharma et al. 2024, Truth Decay 2025, GPT-4o incident |
| Prompt sensitivity is severe | Sclar et al. 2024, Mizrahi et al. 2024 |
| Tool schema > persona for agent tasks | Gorilla, ToolBench, vendor docs |
| Negative constraints have lower compliance | IFEval, SysBench, Llama 2 GAtt |
| API-level format constraints ≠ prompt-level | Tam et al. 2024, Guidance library |
| Stable prefixes enable caching; restatement breaks it | Vendor documentation |

### What Is Contested

| Finding | Why Contested |
|---------|--------------|
| Expert personas for narrow domains | Small positive in some studies; 30pp degradation risk |
| Few-shot value on frontier models | Shrinking but task-dependent |
| System > user message priority | Instruction Hierarchy training exists but IHEval shows 48% compliance |
| Auto prompt optimization vs hand-tuning | Wins reported by proponents; poor cross-model transfer by critics |
| Delimiters improve accuracy | Universally recommended; no controlled accuracy evidence |
| Self-consistency gains | Confirmed on math; positional bias caveat; subsumed by reasoning models |
| Long system prompts increase injection risk | Plausible but unestablished by controlled study |

### What Is Folklore

| Claim | Why |
|-------|-----|
| "You are a world-class X" improves capability | No controlled evidence; 30pp degradation risk |
| Emotional/incentive framing works reliably | One dated paper; model-generation-specific |
| Specific magic numbers for prompt length | No controlled study |
| "XML tags are magic" | Useful for parsing; no accuracy evidence |
| N=50-100 is sufficient for prompt testing | Underpowered for typical effect sizes |
| System messages reliably override user messages | SysBench and IHEval contradict this |

---

## 21. Concrete Blueprint: ≤5-Message Production Setup

### System Prompt

```markdown
<identity>
[One sentence: task-relevant role and operational boundary.]
</identity>

<constraints>
1. [Most critical constraint — positive framing]
2. [Second constraint]
3. [Third constraint]
[... keep to ≤5; each additional constraint reduces compliance]
</constraints>

<output_format>
[Describe fields and order. Use "reason in <thinking>, then emit in <response>"]
[for complex tasks. Use API-level structured output for simple extraction.]
</output_format>

<edge_cases>
[How to handle missing data, ambiguous inputs, errors.]
</edge_cases>
```

### User Message (Turn 1)

```markdown
<context>
[Raw data, reference material — delimited in XML tags]
</context>

<task>
[Complete task specification with all context]
[Explicit success criteria]
[Place the specific question/requirement at the END (recency position)]
</task>
```

### Feedback Turn (Turn 2 — if needed)

**For programmatic pipelines (recommended):**

```markdown
# Stateless regeneration — original system prompt + user prompt + error
<error_output>
[Compiler error, test failure, validation assertion — concrete and specific]
</error_output>

<correction_task>
[Specific fix requested. Re-state all still-active constraints.]
</correction_task>
```

**For conversational flows:**

```markdown
<feedback>
[Specific assertion that failed, with exact error message or output]
</feedback>

<correction_task>
[Concrete correction. Re-state all original constraints at the bottom
(recency position).]
</correction_task>
```

**Never:** "Are you sure?" / "Please review your work" / "Can you improve this?"

### Final Turn (Turn 3 — if needed)

If 2 revision turns haven't converged, **restart with a consolidated single prompt** rather than continuing the chain. This recovers ~95% of single-turn performance (Laban et al.).

---

## Limitations and Caveats

1. **Evidence is model-generation-specific.** Findings from 2024 on GPT-4 may not hold for GPT-5 or Claude 5. The gap between model generations means findings decay in relevance every 6-12 months. **Re-validate on your target model.**

2. **Most studies use English, narrow task sets, and a handful of models.** Non-English transfer, production-specific tasks (drafting, extraction from user data), and multi-modal inputs are undertested.

3. **Publication bias** favors positive results. Negative results (e.g., "persona does nothing") are less likely to be published, though we found several.

4. **Benchmark external validity is limited.** Multiple-choice academic benchmarks have little in common with production short-horizon tasks. Studies using realistic tasks (Laban et al., IFEval, SWE-bench) are more trustworthy for practical guidance.

5. **Statistical power is insufficient in most evaluations.** With N<100, only effects ≥15pp are reliably detectable. Most prompt engineering effects are 1-5pp. Treat any single-prompt evaluation with N<100 as preliminary.

6. **Several citations in the source material have suspicious properties** (future-dated arXiv IDs, imprecisely recalled titles). These are flagged with ⚠️. Verify independently before citing.
