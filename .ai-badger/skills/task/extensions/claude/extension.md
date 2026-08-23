# task extension: claude

This is a **config-gated extension** of the base `task` skill (`skills/task/`), not a standalone skill. The base skill names delegation *roles* — "a high-reasoning agent", "a cheap model" — because it scaffolds for several coding agents.
This extension binds those roles to concrete Claude models, and records the subscription mechanics that motivate the binding.

**Activates when:** the project's `.ai-badger/config.json` has `"claude"` in its `agents` array.

## What the metering actually says

`config.json` carries no plan-tier signal, so this section states what is documented rather than assuming which plan you are on. **Check Settings → Usage for your actual plan and limits before leaning on any of it.**

On **Max plans, premium seats on Team plans, and premium seats on seat-based Enterprise plans**:

- Models included in the plan share a weekly pool. Anthropic on the top tier: *"Fable 5 draws from your plan's regular weekly usage limits and uses them faster than other Claude models."*
- Fable has a **billing threshold at 50%, not a ceiling**: *"once you use up to 50% of your weekly usage limits on Fable 5, you can continue in one of two ways: keep using Fable 5 with usage credits, or switch to another Claude model."*
  Past that point Fable stops being plan-funded and starts costing cash.
- There is also a second weekly limit **scoped to Sonnet models**: *"Max plans also have two weekly usage limits: one that applies across all models and another for Sonnet models only."*
  Anthropic does not document how the two interact — whether Sonnet usage is exempt from the all-model limit or is simply constrained twice. **Do not treat it as bonus capacity.** Sonnet is the cheap lane on per-token price and task fit,
  which is reason enough; it is not established that it spends an allowance the other lanes cannot.

On **Pro plans and standard seats on Team plans**: *"Fable 5 isn't included in your plan's usage limits. You can use Fable 5 with usage credits."* Fable does not touch the weekly pool there at all — it bills separately from the first call.

Whether Opus carries its own weekly limit is genuinely ambiguous: the Max-plan article describes the second limit as Sonnet-only, while the usage-limit best-practices article still refers to a reset *"for Opus only and all other models"*.
Note that if Opus does have its own limit it is a **cap, not a bonus** — the same reading that applies to the Sonnet one. Re-derive the lanes if Anthropic clarifies which article is current.

> Verified 2026-07-26 against `support.claude.com`. Limits, tiers and pricing change; re-check
> the "Usage and limits" collection rather than trusting these numbers indefinitely.

## Claude model lanes

- **Opus — planning and the quality gate.** Phase 1 decomposition and the Phase 3 correctness + architecture review. Also: adversarial review of another agent's claims, money or other derivation-heavy math, non-obvious root-cause debugging,
  and arbitration when two work packages disagree about a contract. Dispatch `model: "opus"` and prefix the call's
  `description` with `"Opus: "` so the lane is visible in the agent panel.
- **Sonnet — implementation, by default.** Everything that executes an already-decided spec:
  writing code, writing ADRs and docs where the decision is already recorded, mechanical fix-ups, and test backfills with pre-derived expected values. Pass `model: "sonnet"`
  explicitly rather than relying on the default, so the lane survives a change of session model.
- **Haiku — trivial mechanical work.** Comment and doc touch-ups, rote refactors, liveness probes. Dispatch `general-purpose` with `model: "haiku"`.
- **Fable — not a routine lane**, on either plan shape, but for different reasons. On Max and premium seats it draws the same pool as Opus and drains it faster, then bills cash past the 50% threshold — so for reasoning work Opus already
  handles it is strictly the worse trade. On Pro and standard Team seats it costs cash per call from the first call while Opus costs pool, so the trade is budget-versus-pool rather than pool-versus-pool; decide on which is actually scarce
  for you. Either way: reserve it for a problem Opus has been tried on and failed, and say why when you dispatch `model: "fable"`.

The orchestrating session must not assume it is already running the planning lane — the default model for new sessions changes. Get the reasoning by dispatching an explicit `Agent` call with the `model` override, not by doing the work
in-session because the session "is" Opus today.

## Reading a finished task's numbers

Measurements behind the delegation policy, and the two artefacts that carry them. Read this when interpreting `token-usage.json`, not on every dispatch — the base skill's policy is the part you act on.

### Judge a task by its model mix, not its cache efficiency

`token-usage.json` records both. `cacheEfficiency` (cache_read ÷ (cache_read + cache_creation))
turns out not to discriminate: **measured over 1250 real sessions it sits at 0.975–0.986 on every one**, including the most expensive. It is worth watching only for a *collapse*, which means the prefix is churning.

`modelMix` is the number that moves. Over those same sessions the expensive and mid tiers produced comparable output volume — **23.7M vs 20.6M tokens — at 3.1× the cost**. Which model did a task's output is the largest lever available, and
it is precisely what the lanes above steer.
`python3 .ai-badger/skills/task/scripts/task_tracker.py status` shows the dominant model and its share (`mix=opus-5:69%`);
`outputByModel` carries the full split.

Read it as the **delegation ratio** — the share produced by the mid and cheap tiers — over the main transcript *and* its subagents together. Those are different numbers and only the combined one means anything: on one real session the main
thread alone reads 2.3% while the true figure is 23.8%. `dispatches` carries the other half: how many ran, how many named no model (one that names none takes the `model:` lane in its persona's frontmatter, and the dispatch gate denies one
whose persona has no lane), and which agent types they went to. A run where most dispatches are
`general-purpose` is not routing to the personas the project scaffolds, whatever `personaRouting`
says.

No prices are recorded. They change, and a stale hardcoded rate would be a confidently wrong number; output tokens per model is the durable half, and a reader applies today's rates.

### Where subagent work actually lives

Not in the main transcript: **measured over 171 real transcripts, no record carries
`isSidechain: true`**. Claude Code writes each dispatch to
`<transcript-dir>/<session-id>/subagents/agent-<id>.jsonl`, with a paired `agent-<id>.meta.json`
naming its `agentType`, `description`, `spawnDepth` and `model`. `parse_transcript_usage` reads both, so a **per-dispatch** split is available — it needs no `parentUuid` chasing, and does not depend on the completion notification, which
exposes only `total_tokens`.

Treat `meta.json` as the undocumented CLI artefact it is: a format change must degrade to
`unknown`, never to zero, or it reads as a delegation collapse that never happened.

### The agent panel's model field can lie

**Do not misdiagnose this as a dispatch bug.** The live agent panel's per-task `model` field (and any custom status line reading it) can transiently show a stale value — e.g. the parent session's model for a subagent dispatched with a
different `model` override.

The panel field comes from an async live-status feed, a separate code path from the
`resolvedModel` Claude Code writes into the session transcript's tool-result metadata at call completion. **The transcript is ground truth; the panel is a snapshot that can lag it.** If a dispatch's actual model is in doubt, grep the
session's `.jsonl` for the `Agent` tool_use whose
`description` matches and check its paired `tool_result`'s `toolUseResult.resolvedModel`. Do not re-investigate this as a dispatch-code problem unless the transcript itself shows the wrong
`resolvedModel`.
