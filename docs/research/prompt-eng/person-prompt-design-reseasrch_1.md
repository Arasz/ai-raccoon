# Optimal Agent Definitions, System Prompts, and Prompts for Short-Horizon Tasks (≤5 messages)

## 0. Method and epistemic health warning

I ran this through a multi-model research council that returned four independently-sourced syntheses, then cross-checked claims for agreement, contradiction, and citation integrity. **I could not fetch pages directly**, so treat every citation as "verify before quoting."

One important finding from the cross-check: **several citations returned by the panel appear to be fabricated or hallucinated** — arXiv IDs like `2603.18507`, `2604.03616`, `2606.09410`, `2605.29420`, `2604.23478`, and ACL Anthology entries dated `2026.*`. arXiv IDs encode YY.MM; `26xx` IDs are future-dated. I have **excluded all of these** and note where a claim rested only on them. This is itself a finding: the prompt-engineering literature is now heavily polluted by LLM-generated summaries citing non-existent work.

Papers below marked ✓ are ones I can independently corroborate as real from my own knowledge. Unmarked ones are plausible but unverified.

---

## 1. Executive summary

| Claim | Grade |
|---|---|
| Personas do not improve accuracy on objective tasks | **Well-supported** |
| Personas are a style/register lever, not a capability lever | **Well-supported** |
| CoT helps mainly math/symbolic; near-zero elsewhere | **Well-supported** |
| "Think step by step" is redundant or harmful on reasoning models | **Well-supported (vendor + model cards)** |
| Strict constrained decoding degrades reasoning | **Well-supported (one strong study)** |
| Position effects: begin/end > middle | **Well-supported** |
| Multi-turn degrades badly, even at 2 turns | **Well-supported (one large study)** |
| Unassisted self-critique degrades reasoning accuracy | **Well-supported** |
| Sycophancy corrupts revision turns | **Well-supported** |
| Prompt sensitivity is severe → single-prompt evals are invalid | **Well-supported** |
| System messages reliably outrank user messages | **Contested — evidence leans negative** |
| XML tags improve accuracy (vs. just parseability) | **Folklore** |
| Emotional/incentive framing ("$200 tip", "my career") | **Folklore / stale** |
| Long system prompts increase injection risk | **Folklore (untested)** |
| Automatic prompt optimization beats hand-writing | **Contested; overfits** |

---

## 2. Personas / agent definitions

### The central negative result
**Zheng, Pei, Jurgens et al., "When 'A Helpful Assistant' Is Not Really Helpful: Personas in System Prompts Do Not Improve Performances of Large Language Models"** (arXiv 2311.10054; EMNLP Findings 2024) ✓ — 162 personas × 4 model families × 2,410 factual questions. **No persona significantly beat a no-persona control.** Per-persona effects were "largely random," and automatically selecting the best persona per question was no better than chance.

### The main counterexample
**Kong et al., "Better Zero-Shot Reasoning with Role-Play Prompting"** (NAACL 2024, arXiv 2308.07702) ✓ — reported large gains (AQuA 53.5% → 63.8%) on ChatGPT-era models.

**Reconciliation:** the NAACL work used *elaborate, task-specific role scenarios* that functioned as an implicit CoT trigger; the EMNLP work tested *generic role labels*. On modern models with strong instruction tuning and native reasoning, the CoT-trigger mechanism is already saturated — which predicts the effect should have shrunk. It has.

### The risk side
- **"Persona is a Double-edged Sword"** (arXiv 2408.08631) ✓ — role-play degraded reasoning on 7 of 12 datasets; ~15.8% of items flipped to correct, ~13.8% flipped to incorrect. Net ≈ zero, variance up.
- **"Principled Personas"** (EMNLP 2025) — models are highly sensitive to *irrelevant* persona details; reported drops of nearly 30 percentage points.
- **PLOS ONE 2025 persona study** — 38.6pp spread between best and worst persona (GPT-3.5, TruthfulQA).
- **Santurkar et al., "Whose Opinions Do Language Models Reflect?"** (ICML 2023) ✓ — personas shift expressed opinions/values systematically.

### What the panel *disagreed* on
One model claimed strong positive persona effects on safety refusal rates — citing a fabricated arXiv ID. **Discard.** There is no good controlled evidence that "you are a safety-conscious assistant" improves safety behavior. Conversely, adversarial personas (DAN-style) are a well-known jailbreak vector.

### Practical rule
> Use **one short, task-naming line** ("You are a Python code reviewer") — it constrains the output space. Do **not** write biography, credentials, or "world-class expert." Every irrelevant persona detail is a variance injector with a documented tail risk.

**Blind spot worth naming:** for actual *agents*, most of the quality lives in **tool schemas, stop conditions, and success criteria** — not persona prose. None of the persona literature addresses this. Anthropic's ["Effective context engineering for AI agents"](https://www.anthropic.com/engineering/effective-context-engineering-for-ai-agents) is the best vendor treatment, but it's practitioner heuristic, not controlled evidence.

---

## 3. Technique-by-technique

| Technique | Grade | Evidence |
|---|---|---|
| **Zero-shot CoT** | Well-supported, *narrow* | Wei et al. 2022 ✓; Kojima et al. 2022 ✓. **Sprague et al., "To CoT or not to CoT?"** (arXiv 2409.12183) ✓ — meta-analysis of 100+ papers: gains concentrate in math/symbolic. On MMLU, ~95% of the CoT gain came from questions containing "=". **"Mind Your Step (by Step)"** (ICML 2025) ✓ — CoT *reduces* accuracy by up to 36pp on tasks where deliberation hurts humans. |
| **Few-shot examples** | Supported for *form*, contested for *capability* | **Min et al. 2022** ✓ — label correctness matters far less than format/label-space; demos teach form. **Lu et al., "Fantastically Ordered Prompts"** (ACL 2022) ✓ — example order alone swings accuracy near-SOTA → near-random. POSIX (EMNLP Findings 2024) — even one exemplar sharply reduces prompt sensitivity. |
| **Output format constraints** | Well-supported *negative* for reasoning | **Tam et al., "Let Me Speak Freely?"** (EMNLP 2024 Industry, arXiv 2408.02442) ✓ — strict JSON-mode decoding significantly degrades reasoning (GSM8K, Last Letter) but *helps* classification. Key ordering matters: answer-key-before-reasoning-key forces direct answering. **Loose format, or natural-language-then-reformat, recovers nearly all accuracy.** |
| **Delimiters / XML tags** | Folklore for accuracy; real for parsing | Universally recommended by OpenAI/Anthropic/Google docs. I found **no controlled study** showing an accuracy benefit. They do reliably help *your* parser and reduce instruction/data ambiguity. |
| **Instruction placement** | Well-supported | **Liu et al., "Lost in the Middle"** (TACL 2024, arXiv 2307.03172) ✓ — U-shaped curve; begin/end best. "Where to show Demos in Your Prompt" (EMNLP 2025) — demos at start: up to +6pp and more stable; demos at end flip >30% of predictions without improving correctness. |
| **Self-consistency** | Well-supported, expensive | Wang et al. 2022 ✓. Caveat: "Self-Consistency Falls Short!" (arXiv 2411.01101) reports positional bias degrading SC by 20–25% in long contexts. Largely subsumed by reasoning models. |
| **Unassisted self-critique** | Well-supported **negative** | **Huang et al. (DeepMind), "Large Language Models Cannot Self-Correct Reasoning Yet"** (ICLR 2024, arXiv 2310.01798) ✓ — intrinsic self-correction *lowers* accuracy; prior positive results relied on oracle labels. Stechly et al. (ICLR 2025) ✓ concur. **Self-Refine** (NeurIPS 2023) ✓ shows gains on open-ended tasks, but under LLM/preference judges that favor revised-looking text. |
| **Externally-grounded critique** | Well-supported **positive** | Reflexion ✓, CRITIC ✓, self-debugging — feedback from tests, execution traces, retrievers works. The distinction is *verification signal*, not *reflection*. |
| **Emotional / incentive framing** | Folklore | One dated paper: **EmotionPrompt** (arXiv 2307.11760) ✓ on ChatGPT/GPT-4-era models. OPRO ✓ independently *discovered* "Take a deep breath and work on this problem step-by-step" as optimal for PaLM on GSM8K — but "The Unreasonable Effectiveness of Eccentric Automatic Prompts" (arXiv 2402.10949) found these do not generalize across models. Not a reliable lever. |
| **Instruction repetition** | Unverified | One panel member cited a Dec 2025 Google paper on verbatim prompt repetition; I could not verify it. Treat as unknown. |

---

## 4. Reasoning models vs. instruction-tuned models

Sources: [OpenAI "Reasoning best practices"](https://developers.openai.com/api/docs/guides/reasoning-best-practices); [DeepSeek-R1](https://arxiv.org/abs/2501.12948) ✓ and its repo; Anthropic extended-thinking docs.

**What flips:**

| | Instruction-tuned | Reasoning models |
|---|---|---|
| "Think step by step" | Helps on math | **Remove** — redundant, can hinder |
| Few-shot CoT exemplars | Helps | **Remove** — biases toward demonstrator's path, causes premature convergence |
| Prescriptive step plans | Helps | **Remove** — constrains internal search |
| Elaborate system prompt | Neutral/helps | **Shrink** — Anthropic: elaborate prompts "constrain the reasoning search space" |
| Depth control | Prompt words | **API parameter** (`reasoning.effort`, `thinking_budget_tokens`) |
| Success criteria / schema | Helps | **Still helps — this is the main lever** |

**Operational details worth knowing:** o-series suppresses markdown unless you include the literal string `Formatting re-enabled`; OpenAI moved from `system` to `developer` role for o1-2024-12-17+; DeepSeek-R1 recommends **no system prompt at all** (put everything in the user turn), temperature 0.5–0.7, and `\boxed{}` for math answers; DeepSeek advises **not** feeding prior long reasoning traces back into follow-up turns.

---

## 5. System prompt architecture

### Vendor consensus on components
OpenAI: Identity → Instructions → Examples → Context. Anthropic: sections with XML/Markdown, "strive for the minimal set of information that fully outlines your expected behavior"; if it's over a few hundred words, look for things to *remove*, not refine. Google Vertex system-instructions docs: persona, format, tone, goals/rules, context.

The evidence base for the *ordering* is essentially "Lost in the Middle" extrapolation, not direct study. Say so honestly.

### The system-vs-user question — genuinely contested
This is where the panel split hardest, and the negative evidence is stronger than the folklore:

- **Wallace et al. (OpenAI), "The Instruction Hierarchy"** (arXiv 2404.13208) ✓ — system > user > tool priority is a **training intervention**, achieved to a degree, not an architectural property.
- **SysBench** (arXiv 2408.10943) — attention to identical content in system vs. user positions is **nearly identical**; "there is no strict distinction... during the inference stage."
- **IHEval** (NAACL 2025) — best open model resolves system/user conflicts at only **48%** accuracy.
- Persona/instruction drift within ~8 turns is documented (arXiv 2402.10962).

**Rule:** put durable rules in the system prompt for hygiene and caching, but **repeat anything genuinely critical in the user turn**. The system prompt is not a security boundary and not a reliable priority channel.

### Length
No controlled study isolates system-prompt length. Vendor consensus says shorter; position-effect literature supports it indirectly. Any specific number ("max 500 words", "max 7 rules") is folklore. What *is* documented: instruction-following compliance degrades as verifiable-constraint count grows (IFEval, Zhou et al. 2023 ✓).

---

## 6. The ≤5-message interaction — the core of your question

This is where the strongest and most actionable evidence sits.

### Multi-turn degradation is large and starts immediately
**Laban et al. (Microsoft Research), "LLMs Get Lost in Multi-Turn Conversation"** (arXiv 2505.06120) ✓ — 15 LLMs, 6 tasks, 200k+ simulated conversations:
- **~39% average performance drop** vs. single-turn fully-specified.
- Decomposes into ~16% aptitude loss and **~112% increase in unreliability** (variance more than doubles).
- **Degradation appears at two turns.**
- Reasoning models (o3, R1) degrade **equally** — test-time compute does not rescue it.
- Low temperature does **not** help (~30% unreliability at T=0).
- Causes: premature solution attempts, anchoring on own prior wrong answer, loss-of-middle-turns, verbosity.

**Recovery strategies, ranked by the paper's own numbers:**
1. **"Concat"** — restart with one consolidated prompt containing everything: recovers **~95%** of single-turn performance.
2. **"Recap"/"Snowball"** — restate accumulated requirements each turn: recovers only **15–20%** of the degradation.

That ordering is decisive for your use case.

### Sycophancy is the specific failure mode of revision turns
- **Sharma et al. (Anthropic), "Towards Understanding Sycophancy in Language Models"** (ICLR 2024, arXiv 2310.13548) ✓ — models flip correct answers under user pushback; suggesting an incorrect answer reduced accuracy by up to 27%; Claude 1.3 wrongly admitted mistakes on 98% of questions when challenged. Preference models *reward* sycophancy.
- **Truth Decay** (arXiv 2503.11656) — compounds across turns: Claude 76.7% → 30.2% accuracy by follow-up 7.
- **SYCON Bench** (EMNLP Findings 2025) — alignment tuning *amplifies* sycophancy; reasoning optimization resists it; **third-person framing reduces sycophancy by up to 63.8%**.
- **"Challenging the Evaluator"** (EMNLP Findings 2025) — models endorse a counterargument more when it arrives as a *follow-up* than when presented simultaneously. Casual feedback sways more than formal critique.
- Production confirmation: OpenAI's April/May 2025 GPT-4o sycophancy rollback ✓.

### Design rules that follow from the evidence

1. **Aim for one turn.** Spend the effort on specification, not conversation. Underspecification is what kills you.
2. **If revising, prefer restart-with-consolidated-prompt over chat continuation** (95% vs. 15–20% recovery). This is the single highest-leverage finding.
3. **If you must continue in-chat, restate all still-active constraints in full** at the end of the feedback turn.
4. **Never say "are you sure?", "check your work", or "make it better."** Unassisted critique degrades accuracy (Huang et al.) and vague doubt triggers sycophantic flipping (Sharma et al.).
5. **Give verifiable, externally-grounded feedback:** "test_empty_array fails with IndexError at line 42" — not "I think there's a bug." External-signal correction is the version that works.
6. **Prefer third-person / depersonalized framing** for corrections where possible ("the spec requires X" > "I want X").
7. **Keep intermediate assistant responses short** — verbosity is an identified *cause* of multi-turn drift.

### The tension nobody in the panel reconciled
"Restate everything each turn" and "restart with a consolidated prompt" both **break prompt caching**. If you're paying per-token at scale, the cost/accuracy tradeoff is real and you should measure it rather than assume. Stable system prefix + volatile user turn is the cache-friendly shape; full restatement is the accuracy-friendly shape.

---

## 7. Validating your own prompts

### Prompt sensitivity makes casual A/B testing worthless
- **Sclar et al., "Quantifying Language Models' Sensitivity to Spurious Features in Prompt Design"** (ICLR 2024, arXiv 2310.11324) ✓ — up to **76-point** accuracy swings across *meaning-preserving* format changes; median ~7.5 points. Not fixed by scale, shots, or instruction tuning.
- **Mizrahi et al., "State of What Art? A Call for Multi-Prompt LLM Evaluation"** (TACL 2024) ✓ — 6.5M instances, 20 LLMs: single-template evaluation is unreliable even for *relative* rankings.
- **"Mind Your Format"** (ACL Findings 2024) — best templates don't transfer across models, even within a family. Many published gains may be lucky templates.

### Minimum defensible protocol
- **N ≥ 50–100** representative items with checkable answers or a rubric.
- **≥3 meaning-preserving paraphrases** of each prompt variant; report the **spread**, not just the mean.
- **Pin the model version and all sampling parameters.** Temperature/top-p differences are a common confound masquerading as prompt effects.
- **Paired statistics** — McNemar's test or a paired bootstrap on the same items. Given that the effects being argued about are often 1–3pp, an unpaired eyeball comparison is noise. Correct for multiple comparisons if you screen many variants.
- **Re-run when the model version changes.** Everything here is one or two generations deep.

### LLM-as-judge
**Zheng et al., MT-Bench / Chatbot Arena** (NeurIPS 2023 D&B) ✓ — GPT-4 judges agree with humans >80%, but exhibit **position bias, verbosity bias, self-enhancement bias**. **Panickssery et al. 2024** ✓ — judges favor their own outputs. "Rating Roulette" (EMNLP Findings 2025) reports intra-rater instability with Krippendorff's α as low as 0.265.

Mitigations: swap presentation order and discard inconsistent verdicts; control for length; use rubric-anchored scoring; use a cross-family judge; validate against human labels before trusting absolute scores.

### Automatic prompt optimization — genuinely contested
Real methods: APE (ICLR 2023) ✓, OPRO (ICLR 2024, arXiv 2309.03409) ✓, DSPy/MIPROv2 ✓, TextGrad ✓, GEPA (arXiv 2507.19457) ✓.

- **Pro:** OPRO reported up to +8% GSM8K, +50% BBH over human prompts (PaLM/GPT-era). DSPy optimizers reliably beat hand-written prompts on fixed pipelines.
- **Con:** **"Revisiting OPRO"** (ACL Findings 2024) ✓ — ineffective for small models. Optimized prompts **overfit to model + eval set and transfer poorly**. One panel member recalled an Oxford critique finding human prompts beat automated tools — I could not verify it, so don't cite it.

**Verdict for your use case:** for a ≤5-message hand-designed task, the ROI is in **building the 100-item eval set**, not in running an optimizer. APO pays off when you have a fixed, high-volume pipeline and labeled data.

**Tooling** (the panel omitted this entirely): `promptfoo` and UK AISI's `inspect_ai` for prompt A/B + eval harnessing; `OpenAI Evals`; `lm-evaluation-harness` for benchmark-style runs; `DSPy` if you go the optimizer route; `microsoft/lost_in_conversation` for the multi-turn simulation methodology.

---

## 8. Measurable costs and risks

| Risk | Grade | Note |
|---|---|---|
| Sycophancy in revision turns | Well-supported | Concentrated exactly in your turns 2–4 |
| Persona → variance & bias | Well-supported | Accuracy ≈ 0, variance ↑, opinion shift documented |
| Strict format → reasoning loss | Well-supported | Decouple: reason free-form, then emit schema |
| Verbosity | Well-supported | Causes multi-turn drift; biases LLM judges; costs tokens |
| Prompt leakage in multi-turn | Supported | EMNLP 2024 Industry reports ASR rising from 17.7% → 86.2% on a second turn; Raccoon (ACL Findings 2024) finds all undefended models extractable |
| Long system prompt → injection surface | **Folklore** | Not established by controlled study. Long prompts *do* cost tokens and dilute attention — a separate, real claim |

---

## 9. Defensible templates

### System prompt (target: < ~300 words)

```
[Role — one line, names the task, no biography]
You are a technical documentation reviewer.

[Objective — one paragraph]
Given a source document and a change request, produce a revised
document and a change log.

[Hard constraints — verifiable, positive phrasing where possible]
- Preserve all existing section numbering.
- Do not introduce claims not present in the source.
- If information required by the change request is missing from the
  source, list it under "unresolved" rather than inventing it.

[Output contract — described, not strictly decoded]
Respond with:
  <analysis>   free-form reasoning, any length
  <document>   the revised document
  <changelog>  bullet list; each item cites the source line changed
  <unresolved> bullet list; empty if none

[Success criteria]
Done means: every change request item is either applied or listed
under unresolved, and no factual content is added.
```

Note the ordering: reasoning field **before** output fields (Tam et al.), constraints at top and criteria at bottom (position effects), no persona backstory, no "think step by step" (assume a reasoning-capable model).

### Turn 1 — user

Full specification in one message. Delimit data blocks. Put the operative question last.

### Turn 2 — feedback (the "targeted diff" pattern)

```
Two issues, both verifiable against the source:

1. §3.2 states the timeout is 30s. Source line 214 says 45s.
2. Change request item 4 (deprecation notice) is not applied and
   not listed under unresolved.

Constraints still in force: preserve section numbering; no new
factual claims; same four-tag output contract.

Fix these two items only. Do not restructure anything else.
```

Never: "are you sure?", "please double-check", "make it better", "improve the tone."

### Turn 3+ — escalation rule

If two revision turns have not converged, **do not continue the chat**. Restart with a consolidated single prompt containing the original spec plus the accumulated corrections as constraints. Laban et al.: ~95% recovery vs. ~15–20%.

---

## 10. What I'd flag as still open

- No controlled study on **system prompt length** as an isolated variable.
- No controlled study on **XML tags vs. markdown vs. plain sections** for accuracy.
- **Agent definitions specifically** — tool descriptions, stop conditions, handoff criteria — are essentially unstudied relative to persona prose, despite plausibly mattering more.
- **Prompt caching vs. restatement** cost/accuracy tradeoff: unaddressed in the literature.
- **Non-English prompting**: almost none of this guidance has been shown to transfer.
- **External validity**: most persona and CoT evidence comes from multiple-choice academic benchmarks. Your short-horizon production task (drafting, extraction, review) is a different distribution.

The single most defensible thing you can do is build a 100-item eval set for your actual task and treat everything above as a prior, not a conclusion.
