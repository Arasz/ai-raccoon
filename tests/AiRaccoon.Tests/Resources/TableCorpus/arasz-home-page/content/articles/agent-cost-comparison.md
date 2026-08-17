---
id: 3
title: "What AI Coding Agents Actually Cost: Real Usage Data Across 3 Agents"
slug: agent-cost-comparison
publishedAt: "2026-07-23T09:00:00Z"
updatedAt: "2026-08-15T12:00:00Z"
author: "Rafał Araszkiewicz"
description: "Data-driven comparison of Claude Code, Hermes Agent (via OpenRouter), and GitHub Copilot with real token usage, cost breakdowns, and the 80/20 strategy."
tags: [AI, Claude Code, Hermes, OpenRouter, Copilot, Cost, Agents]
status: published
categories: [agent-cost]
---

# What AI Coding Agents Actually Cost: Real Usage Data Across 3 Agents

> **Updated:** Correction, 2026-08-15. Two rows in "Cost Per Million Tokens (Actual API Rates)" misstate the rate that produced the number beside them. The first names the wrong model: it reads `claude-opus-4 (API) | $75.00`, but the usage it points at is `claude-opus-4-8`, a different model, priced at $5.00 input and $25.00 output per million tokens. The $75.00 rate belongs to `claude-opus-4`, which produced none of the tokens measured here. The second is subtler: `claude-sonnet-5 (API) | $15.00` quotes list price, while the $1,141.24 beside it was priced at the introductory $2/$10 rate. The dollar totals are unaffected either way, and so are the ROI multiples downstream. Those totals came out of ccusage, which prices each model at the rate it was actually served at, not out of these explanatory rows. The arithmetic settles it in both cases. At `claude-opus-4` rates, cache reads alone would cost $1,709 (1,139,582,023 tokens at $1.50 per million, the standard tenth-of-input cache rate), blowing past the $938.41 total before a single output token is counted; at `claude-opus-4-8` rates the same reads cost $570, output adds $149, and the remainder leaves room for input and cache writes. Sonnet fails the same test at list price, where reads and output together come to $1,351 against a $1,141.24 total, and passes at the introductory rate at $901. The labels are wrong; the totals are right, and the 7.6x return stands ($3,021 against $399, both in dollars). A third figure does not. The "~26x return on the subscription alone" below divides a dollar value by a euro cost: $3,021 over €114. Like for like it is 23x, using the $130 this article prints beside that €114. The mistake is mine and it runs against the rule stated further down, that every figure here is in USD unless noted.

## TL;DR

Tracked every token across Claude Code, Hermes Agent (via OpenRouter), and GitHub Copilot for two weeks of real software development. Claude Max delivered $3,021 in API-equivalent value for €350/mo (~$399) but hit weekly limits after ~2 heavy days. mimo-v2.5-pro via OpenRouter cost $0.10/MTok with no usage caps — 1/37th of Claude Sonnet's rate. GitHub Copilot's credit system is consumption-based with variable discounts, not flat-rate. The cost-effective strategy: mimo for 80% of daily work (~$8.60–65.70/day), Claude for high-stakes tasks, Copilot for code review. Total: ~$1,436/mo across all three agents.

---

## The Agents Tested

> **Note:** All costs below are **what I actually paid** — not universal API pricing. Claude is billed in EUR (includes 23% Polish VAT), everything else in USD. The per-token costs reflect my real spend divided by real token counts from the tracking tools.

| Agent              | Models                               | Cost / 1M Total Tokens     | Availability                                                                                                                          |
|--------------------|--------------------------------------|----------------------------|---------------------------------------------------------------------------------------------------------------------------------------|
| **Claude Code**    | Sonnet 5, Opus 4, Fable 5, Haiku 4.5 | $0.06 (cache-heavy)        | Limited — 5h rolling sessions, weekly caps ([docs](https://support.claude.com/en/articles/11049741-what-is-the-max-plan))             |
| **Hermes Agent**   | mimo-v2.5-pro, deepseek-v4-pro       | $0.10                      | **24/7 continuous, no limits**                                                                                                        |
| **GitHub Copilot** | 12 models (Claude, GPT-5.x)          | $3–8 (varies by discounts) | Credit-based, 20K credits/mo on Max ([docs](https://docs.github.com/en/copilot/concepts/billing/usage-based-billing-for-individuals)) |

### Tracking Tools Used

- **ccusage** (`npx ccusage daily --json`) — the primary tracker. Auto-detects Claude Code, Hermes, Copilot CLI, and many others from their local data files. Provides per-day, per-session, per-model, per-agent breakdowns with cost calculations.
- **OpenRouter Explorer** — built-in dashboard with per-model token counts, spend, and request counts. Exported as CSV and PDF for this analysis.
- **OpenRouter Activity API** — granular per-call data including provider routing, cache hits, generation time, and cost breakdowns. 4,454 API calls analyzed from a 2-day period (Jul 22–23).
- **GitHub Copilot AI Usage Report** — billing export with per-model, per-day credit consumption, discount rates, and quota information. Available from GitHub's billing settings. This is the most granular Copilot data available for individual accounts.
- **GitHub Copilot OTEL** — file-based telemetry export (newly configured). Currently tracking MCP server connections; token-level data will accumulate over time.

---

## The Continuous Work Problem

<!-- CHART: cost-per-token-bar -->
<!-- kind: bar-horizontal -->
<!-- title: Cost per 1M Total Tokens (USD) -->
<!-- data: Claude Max $0.06, mimo-v2.5-pro $0.10, DeepSeek V4 $0.09, Copilot $4.00 -->
<!-- lowerIsBetter: true -->
<!-- xLabel: $/MTok -->
<!-- Horizontal bar chart comparing cost per 1M total tokens across agents: -->
<!-- Claude Max $0.06, mimo-v2.5-pro $0.10, DeepSeek $0.09, Copilot $3-8. -->
<!-- Lower is better. Highlight mimo's sweet spot near Claude's rate. -->

The most important difference between these agents isn't cost per token — it's **availability**.

**Claude Max** hits its weekly token limit after ~2 days of heavy use. Sessions terminate mid-task. There's no auto-resume. You either buy additional tokens (€250/mo) or wait for the weekly reset. When you're in flow state on a complex feature, this disrupts workflow.

**mimo-v2.5-pro via Hermes** has no session limits, no weekly caps, no interruptions. You can work 24 hours straight, 7 days a week. The model is always available. At ~$65.70/day on heavy work days (ccusage, Jul 23), it's a coding partner that never says "you've reached your limit." The 7-day OpenRouter average is lower (~$8.62/day for mimo alone), reflecting lighter usage on some days.

**The core trade-off:** cost per token matters less than consistent availability.

|                       | mimo-v2.5-pro         | Claude Max 5x                                                                                                              |
|-----------------------|-----------------------|----------------------------------------------------------------------------------------------------------------------------|
| Session limits        | **None**              | 5-hour rolling session ([docs](https://support.claude.com/en/articles/11145838-use-claude-code-with-your-pro-or-max-plan)) |
| Weekly caps           | **None**              | Two limits: all models + Sonnet-only ([docs](https://support.claude.com/en/articles/11049741-what-is-the-max-plan))        |
| Auto-resume           | **Always running**    | Must work around with skills                                                                                               |
| Weekend/night work    | **Unlimited**         | Same limits apply                                                                                                          |
| Flow state protection | **Never interrupted** | Interrupted when session/weekly cap hit                                                                                    |
| Daily cost            | ~$8.60–65.70 (varies) | ~$13.30 (avg, total spend)                                                                                                 |
| Cost for 2 heavy days | **~$131**             | ~$131 (subscription + overages prorated)                                                                                   |

**The fair comparison:** Claude Max 5x ($100/mo) lets you work roughly 2 heavy days per week before hitting limits. At that usage level, mimo costs about the same (~$131 for 2 days). But mimo doesn't stop there — you can keep going for the remaining 5 days at ~$65.70/day, totaling ~$460/week for uninterrupted 7-day work. Claude would need additional token purchases (€250/mo) and still can't match the continuity.

> **Note:** Anthropic [no longer publishes fixed hours-per-week figures](https://www.morphllm.com/claude-code-usage-limits). The "2 heavy days" is based on observed usage patterns, not official numbers. Your actual limits depend on conversation complexity, model choice, and session activity.

**The trade-off:** mimo costs more per month for continuous use but provides uninterrupted access. Claude is cheaper per-token but subject to session and weekly caps that can interrupt work mid-task.

<!-- CHART: continuous-work-timeline -->
<!-- kind: timeline -->
<!-- title: Availability Comparison (7 Days) -->
<!-- data: mimo-v2.5-pro 7, Claude Max 5x 2 -->
<!-- description: mimo runs continuously. Claude hits limits after ~2 heavy days. -->
<!-- xLabel: Days available per week -->
<!-- Two horizontal timelines (7 days): mimo is a solid green bar (always on). -->
<!-- Claude shows 2 blue days, then red "LIMIT" zone, grey "wait/buy", then blue again. -->
<!-- Below: checkmarks for mimo (no interruptions, predictable cost, 24/7) -->
<!-- and X marks for Claude (sessions end, no auto-resume, limits hit mid-work). -->

**The recommended pattern:** Use mimo for the 80% of daily work where continuity matters (implementation, tests, docs, iteration). Use Claude for the 20% where peak quality matters and you can plan around the limits (architecture decisions, complex refactors, code review).

---

## The Data: 16 Days of Real Usage

### Claude Code — The Subscription Advantage

Claude Code runs on the **Claude Max subscription** (€100/mo, billed in EUR from the EU, includes 23% Polish VAT). The ccusage tool calculates per-token API costs to show what the usage *would have cost* at pay-per-token rates — but the actual out-of-pocket cost is the subscription plus overages.

**API-equivalent value consumed (Jul 8–21, 10 days):**

<!-- CHART: claude-model-breakdown -->
<!-- kind: bar-horizontal -->
<!-- title: Claude Model Cost Breakdown (API-Equivalent) -->
<!-- data: Sonnet 5 $1141, Opus 4 $938, Fable 5 $913, Haiku 4.5 $28 -->
<!-- description: Total API-equivalent value: $3,021. Actual cost: ~$399/mo. -->
<!-- xLabel: USD -->
<!-- Stacked horizontal bar showing Claude model cost breakdown: -->
<!-- Sonnet 5 $1,141 (37.8%), Opus 4 $938 (31.1%), Fable 5 $913 (30.2%), Haiku $28 (0.9%). -->
<!-- Bar widths proportional to cost. Total: $3,021 API-equivalent. -->
<!-- Note: Actual cost was $399/mo (subscription + overages). | -->

| Model | API-Equivalent Cost | Output Tokens | Cache Read Tokens |
|---|---|---|---|
| claude-sonnet-5 | $1,141.24 | 15,294,898 | 3,738,126,393 |
| claude-opus-4-8 | $938.41 | 5,955,019 | 1,139,582,023 |
| claude-fable-5 | $912.73 | 3,653,646 | 439,609,992 |
| claude-haiku-4-5 | $28.41 | 792,839 | 153,025,656 |
| **Total** | **$3,020.79** | **25,696,402** | **5,470,344,064** |

**What this means:** The Max plan delivered **$3,021 worth of API tokens for €114 (~$130 incl. VAT)** — a ~26x return on the subscription alone. Cache reads are 98.6% of all input tokens, meaning the sessions are heavily context-reuse driven.

**Additional token costs:** The Max plan's weekly allowance wasn't enough for the workload. An additional **€250 (~$285 at €1 = $1.14) in token purchases** were needed to cover overages when the weekly limit was hit mid-session. **Total actual Claude spend: ~$399/mo** ($114 subscription + $285 additional tokens). This is still a massive discount vs. pure API pricing ($3,021), but it's important to note the subscription alone doesn't cover heavy usage.

**Currency & VAT note:** Claude's pricing is in **EUR** (billed from the EU). All other services (OpenRouter, Copilot) are billed in **USD**. The EUR amounts include **23% Polish VAT**, which inflates the real cost. The most cost-effective strategy is to **buy additional tokens in bulk** — larger purchases have a lower per-unit cost after VAT, so topping up once when you hit the limit is cheaper than multiple small purchases throughout the month.

**Daily API-equivalent cost variance:**

<!-- CHART: claude-daily-cost-bar -->
<!-- kind: bar-vertical -->
<!-- title: Claude Daily API-Equivalent Cost -->
<!-- data: Jul 8 $31.81, Jul 12 $469.08, Jul 14 $470.81, Jul 15 $24.16, Jul 19 $742.42, Jul 21 $98.31 -->
<!-- description: Massive variance from $24 to $742 in the same week. -->
<!-- xLabel: Date -->
<!-- yLabel: USD -->
<!-- Vertical bar chart showing Claude's daily API-equivalent cost (Jul 8-21): -->
<!-- Jul 8 $32, Jul 11 $39, Jul 12 $469, Jul 13 $454, Jul 14 $471, Jul 15 $24, -->
<!-- Jul 18 $214, Jul 19 $742, Jul 20 $454, Jul 21 $98. -->
<!-- Color-code: light blue for normal days, dark blue for heavy days. -->
<!-- Show the massive variance — from $24 to $742 in the same week. -->

| Date   | API-Equivalent Cost | Models Used                | Notes              |
|--------|---------------------|----------------------------|--------------------|
| Jul 8  | $31.81              | Fable, Opus                | Light usage        |
| Jul 12 | $469.08             | Fable, Sonnet, Haiku       | Heavy session      |
| Jul 14 | $470.81             | Sonnet, Fable, Opus        | Sonnet dominated   |
| Jul 19 | **$742.42**         | Opus, Sonnet, Fable, Haiku | Most expensive day |
| Jul 15 | $24.16              | Sonnet only                | Light day          |

**Strengths:**

- Access to the best models (Fable, Opus) for complex reasoning
- Hooks system for workflow automation
- 98.6% cache hit ratio keeps sessions efficient

**Weaknesses:**

- Sessions are too short — you hit the limit mid-work
- No auto-resume; must work around it with skills
- Cron needs agent tokens (auto-resume on limit is locked)
- No API access in plans lower than MAX

---

### Hermes Agent via OpenRouter — The Real Spend

This is where the actual money goes. OpenRouter charges per-token, and the costs are real.

**OpenRouter Explorer data (Jul 16–23, 7 days):**

| Model             | Spend       | Tokens          | Requests  |
|-------------------|-------------|-----------------|-----------|
| MiMo-V2.5-Pro     | $60.31      | 575,305,562     | 5,351     |
| DeepSeek V4 Pro   | $35.94      | 398,506,366     | 2,009     |
| DeepSeek V4 Flash | $0.05       | 470,559         | 438       |
| GLM 5.2           | $0.02       | 24,049          | 2         |
| Laguna XS 2.1     | $0.00       | 725,180         | 517       |
| **Total**         | **$101.88** | **987,988,991** | **8,503** |

**Token distribution by model (OpenRouter, Jul 16–23):**

<!-- CHART: openrouter-token-donut -->
<!-- kind: donut -->
<!-- title: Token Distribution by Model (Jul 16-23) -->
<!-- data: mimo-v2.5-pro 58.2, DeepSeek V4 Pro 40.3, Others 1.5 -->
<!-- description: 988M total tokens over 7 days. DeepSeek had high rework overhead. -->
<!-- Donut/pie chart showing token distribution: -->
<!-- mimo-v2.5-pro 58.2% (green), DeepSeek V4 Pro 40.3% (red), Others 1.5% (grey). -->
<!-- Center text: "988M tokens". Legend with cost per slice. -->
<!-- Note: DeepSeek's 40.3% had high rework overhead — poor value despite low per-token cost. -->

| Model                             | % of Total Tokens |
|-----------------------------------|-------------------|
| MiMo-V2.5-Pro                     | 58.2%             |
| DeepSeek V4 Pro                   | 40.3%             |
| Others (Qwen, GLM, Flash, Laguna) | 1.5%              |

**Provider routing (from activity data, 4,454 calls on Jul 23):**

<!-- CHART: provider-routing-bar -->
<!-- kind: bar-horizontal -->
<!-- title: OpenRouter Provider Routing (mimo-v2.5-pro) -->
<!-- data: Xiaomi $8.49, Novita $3.45, DeepInfra $42.70, AtlasCloud $2.64, StreamLake $4.29 -->
<!-- description: Same model, 33x cost difference by provider. -->
<!-- Horizontal bar chart showing cost per provider for mimo-v2.5-pro: -->
<!-- Xiaomi 2,632 calls $8.49 (green, cheapest), Novita 647 $3.45, -->
<!-- DeepInfra 433 $42.70 (red, most expensive), AtlasCloud 389 $2.64, -->
<!-- StreamLake 351 $4.29. Call out: Xiaomi $0.003/call vs DeepInfra $0.099/call (33x). -->

| Provider   | Calls | Cost   | Notes                 |
|------------|-------|--------|-----------------------|
| Xiaomi     | 2,632 | $8.49  | Direct Xiaomi API     |
| Novita     | 647   | $3.45  | Third-party provider  |
| DeepInfra  | 433   | $42.70 | Highest per-call cost |
| AtlasCloud | 389   | $2.64  | Budget provider       |
| StreamLake | 351   | $4.29  | DeepSeek routing      |
| GMICloud   | 2     | $0.00  | Fallback              |

**Key metrics (from activity CSV, Jul 22–23):**

- Total API calls: 4,454
- Average cost per call: $0.014
- Cache efficiency: 88.9% (540M cached / 608M total prompt tokens)
- Most calls: mimo-v2.5-pro via Xiaomi ($0.003/call) or Novita ($0.005/call)
- Most expensive calls: mimo-v2.5-pro via DeepInfra ($0.099/call)

**Why mimo-v2.5-pro dominates:** It's the workhorse — 93% of all Hermes cost. The model handles multi-step coding tasks reliably, follows project conventions (reads CLAUDE.md, respects invariants), and manages context windows well. Costs roughly 1/37th of Claude Sonnet per output token.

**DeepSeek V4 Pro — underperformed:** Despite consuming 40.3% of total tokens (399M tokens, $35.94), DeepSeek V4 Pro's outputs required significantly more rework. The model missed project conventions and produced less reliable code. The per-token cost was low, but the rework overhead made it poor value in this testing.

**Xiaomi's cost advantage:** mimo-v2.5-pro via Xiaomi's direct API was the cheapest routing option at **$0.003/call** (2,632 calls, $8.49 total). The same model via DeepInfra cost **$0.099/call** — 33x more expensive. OpenRouter's automatic provider routing sometimes picked expensive providers, resulting in up to 33x cost variation for the same model.

**Strengths:**

- OpenRouter has huge model selection from all providers
- mimo-v2.5-pro is a real workhorse — performance on par with Fable/Opus most of the time, cheaper than Haiku
- Best hooks/skills implementation with token-efficient focus
- Can use any API provider and multiple providers simultaneously
- Agent-free cron supported
- Auto-improvement: modifies skills without prompting if it thinks they can be enriched
- Channel connectors (WhatsApp, SMS, email)

**Weaknesses:**

- Some terminal rendering bugs
- Has its own data structure convention — doesn't play well with Claude Code and other CLIs (ai-badger helps bridge this)
- More setup required upfront
- Skill authoring has a learning curve

---

### GitHub Copilot — AI Credits, 12 Models, Heavy Discounts

<!-- CHART: copilot-credits-bar -->
<!-- kind: bar-horizontal -->
<!-- title: Copilot Credit Consumption by Model (Net Cost) -->
<!-- data: Code Review $30.23, Sonnet 5 $18.80, Fable 5 $9.93, Sonnet 4.6 $7.85, GPT-5.6 Terra $7.35, Others $5.84 -->
<!-- description: Total: $80 net (63% discount from $218 gross). -->
<!-- Horizontal bar chart showing Copilot credit consumption by model (net cost): -->
<!-- Code Review $30.23, Sonnet 5 $18.80, Fable 5 $9.93, Sonnet 4.6 $7.85, -->
<!-- GPT-5.6 Terra $7.35, Opus 4.8 $2.20, Luna $2.09, others < $1. -->
<!-- Total: $80 net (63% discount from $218 gross). -->

GitHub Copilot's pricing is more complex than it looks. It uses an **AI credits system** with two SKU types:

- **`coding_agent_ai_credit`** — agent mode (like Claude Code, autonomous coding)
- **`copilot_ai_credit`** — chat, inline suggestions, and code review

Each model consumes credits at different rates, and discounts are applied based on your plan tier and monthly quota.

**AI Usage Report (Jul 11–14, 4 days):**

| Metric                       | Value                  |
|------------------------------|------------------------|
| Total credits consumed       | 21,805                 |
| Gross amount                 | $218.05                |
| Discount applied             | $138.05 (63%)          |
| **Net amount (actual cost)** | **$80.00**             |
| Monthly quotas seen          | 1,500 / 7,000 / 20,000 |
| Models used                  | 12                     |

**By model (sorted by net cost):**

| Model               | Credits | Gross   | Discount % | Net Cost  |
|---------------------|---------|---------|------------|-----------|
| Code Review model   | 12,354  | $123.54 | 76%        | $30.23    |
| Claude Sonnet 5     | 4,151   | $41.51  | 55%        | $18.80    |
| Claude Fable 5      | 1,111   | $11.11  | 11%        | $9.93     |
| Claude Sonnet 4.6   | 1,264   | $12.64  | 38%        | $7.85     |
| GPT-5.6 Terra       | 1,060   | $10.60  | 31%        | $7.35     |
| Claude Opus 4.8     | 732     | $7.32   | 70%        | $2.20     |
| GPT-5.6 Luna        | 404     | $4.04   | 48%        | $2.09     |
| Claude Sonnet 4.5   | 224     | $2.24   | 55%        | $1.00     |
| Auto: GPT-5.3-Codex | 34      | $0.34   | 0%         | $0.34     |
| Claude Haiku 4.5    | 88      | $0.88   | 77%        | $0.20     |
| GPT-5.4             | 299     | $2.99   | **100%**   | **$0.00** |
| GPT-5.3-Codex       | 84      | $0.84   | **100%**   | **$0.00** |

**By usage type:**

| SKU                                      | Credits | Net Cost | Top Models                  |
|------------------------------------------|---------|----------|-----------------------------|
| `copilot_ai_credit` (chat/inline/review) | 18,345  | $54.97   | Code Review, Sonnet 5, Luna |
| `coding_agent_ai_credit` (agent mode)    | 3,460   | $25.03   | Fable 5, Sonnet 4.6, Terra  |

**Key findings:**

- **Code Review model is the biggest consumer** — 12,354 credits ($30.23 net) across 4 days. This is Copilot's automated PR review, and it's heavily used.
- **Plan upgrades trigger temporary discounts** — the data shows you upgraded from Pro ($10/mo, 1,500 credits) to Pro+ ($39/mo, 7,000 credits) on Jul 12, then to Max ($100/mo, 20,000 credits) on Jul 14. Each upgrade triggered massive 100% discounts on many models that day, which reverted to normal rates the next day. This is likely an upgrade bonus, not a permanent feature. Credits reset monthly (1st of each month at 00:00 UTC), not weekly.
- **Discount rates vary wildly** — from 0% to 100%, with an average of 63%. The high discounts are tied to plan upgrades, not permanent features.
- **12 models available** — far more than expected. Includes Claude Fable 5, Claude Opus 4.8, GPT-5.6 Terra/Luna, and a dedicated Code Review model.
- **Agent mode vs chat mode** — agent mode (coding_agent_ai_credit) consumed 3,460 credits ($25.03), while chat/inline/review consumed 18,345 credits ($54.97). Code review dominates.

**Projected monthly cost:** At $80/4 days = ~$600/month net — but this is unstable. The discount structure depends on plan tier, upgrade timing, and model selection. On a stable Max plan without mid-cycle upgrades, expect the full 20,000 credits at face value (~$200/month) plus any overages. The $80/4 days figure includes upgrade bonuses that won't recur monthly.

**Practical lessons from 4 days of heavy Copilot use:**

- **Run demanding tasks at the end of the month** — credits reset on the 1st at 00:00 UTC. If you burn through your allowance early, you're stuck waiting. Front-loading heavy work into the first week means sitting idle for 3 weeks.

- **Fable has a separate counter** — different models draw from different pools. If you exhaust one model's limit, you can't redirect unused credits from another. This happened during testing: Opus was set as the default model, its limit was hit, and 50% of the Fable allowance remained unused.

- **Manage model selection consciously** — the best strategy is to exhaust premium model credits (Fable) first for high-value work, then fall back to cheaper models for the rest of the cycle. But this requires constant attention to which model is active and how much allowance remains.

- **Delegation burns limits fast** — you can't just delegate work freely. Every subagent call, every code review, every chat message consumes credits. The moment you stop paying attention to model selection, you waste allowance on the wrong model. This is the biggest operational overhead of Copilot vs. flat-rate alternatives.

---

## The Real Cost Comparison

<!-- CHART: monthly-cost-comparison -->
<!-- kind: bar-vertical -->
<!-- title: Estimated Monthly Cost by Agent -->
<!-- data: Claude Code $399, Hermes Agent $437, GitHub Copilot $600 -->
<!-- description: Hermes delivers the most tokens per dollar. Copilot projection includes upgrade bonuses. -->
<!-- xLabel: USD -->
<!-- Grouped bar chart comparing monthly costs across agents: -->
<!-- Claude $399 (blue), Hermes $437 (green), Copilot $600 (purple). -->
<!-- Total line at $1,436. Add token count labels on each bar. -->
<!-- Show that Hermes delivers the most tokens per dollar. -->

Here's the honest comparison — what actually came out of pocket:

| Agent                            | Estimated Monthly Cost | Actual Cost            | Period  | What You Got                                         |
|----------------------------------|------------------------|------------------------|---------|------------------------------------------------------|
| **Claude Code** (Max + overages) | ~$399/mo               | €100 sub + €250 tokens | 10 days | ~19.4B tokens (6.5B measured), $3,021 API-equivalent |
| **Hermes Agent** (OpenRouter)    | ~$437/mo               | $102/week              | 7 days  | 988M tokens, 8,503 API calls                         |
| **GitHub Copilot**               | ~$600/mo               | $80/4 days             | 4 days  | ~20M tokens (est.), 21,805 credits, 12 models        |
| **Total**                        | **~$1,436/mo**         |                        |         | **~23.8B tokens/month**                              |

> **Token estimates:** Claude tokens from ccusage (includes 98.6% cache reads at Anthropic's discounted rate). Copilot tokens estimated from $80 net spend ÷ blended ~$4/MTok for the Claude/GPT model mix. Hermes tokens are actual counts from OpenRouter Explorer and ccusage. Copilot's $600/mo projection is based on the test period which included plan upgrade bonuses — on a stable Max plan without upgrades, expect ~$100/mo subscription plus overages at $0.01/credit for heavy use.

### Cost Per Million Tokens (Actual API Rates)

| Model                 | Output $/MTok | Effective Cost | Notes                                               |
|-----------------------|---------------|----------------|-----------------------------------------------------|
| claude-sonnet-5 (API) | $15.00        | $1,141/10 days | Only relevant if paying per-token                   |
| claude-opus-4 (API)   | $75.00        | $938/10 days   | Only relevant if paying per-token                   |
| xiaomi/mimo-v2.5-pro  | $0.40         | $60/week       | **Real spend via OpenRouter**                       |
| deepseek-v4-pro       | $1.10         | $36/week       | **Underperformed — rework overhead offset savings** |

**The subscription arbitrage:** Claude Max gives you $3,021 worth of API tokens for €114 ($130, incl. 23% VAT) — but heavy users will hit the weekly limit and need additional purchases (€250/mo = $285). At $399/mo total, you're still getting a 7.6x return vs. pure API pricing. The most cost-effective approach is to **buy additional tokens in bulk** — one large top-up has a lower per-unit cost than multiple small purchases. The question is whether you *need* Claude for everything — and the answer is no. mimo-v2.5-pro handles 80% of implementation work at 1/37th the cost.

---

## The 80/20 Strategy

<!-- CHART: quality-vs-cost-scatter -->
<!-- kind: scatter -->
<!-- title: Quality vs Cost - The Real Picture -->
<!-- data: mimo $0.40, Claude $15.00, DeepSeek $1.10, Copilot $4.00 -->
<!-- description: mimo sits in the sweet spot: near-Claude quality at a fraction of the cost. -->
<!-- xLabel: Cost per 1M output tokens (USD) -->
<!-- lowerIsBetter: true -->
<!-- Scatter plot: X = cost per 1M output tokens, Y = perceived quality. -->
<!-- mimo (low cost, high quality — sweet spot), Claude (medium cost, highest quality), -->
<!-- DeepSeek (very low cost, low quality), Copilot (low cost, medium quality). -->
<!-- Circle mimo as the sweet spot. Label DeepSeek and Qwen as "cheap but poor value". -->

| Work Type                    | Agent           | Model                  | Why                                             |
|------------------------------|-----------------|------------------------|-------------------------------------------------|
| Implementation (80% of work) | Hermes Agent    | mimo-v2.5-pro          | Reliable, 1/37th the cost of Claude             |
| Architecture decisions       | Claude Code     | Opus/Fable             | Complex reasoning needs the best model          |
| Code review                  | GitHub Copilot  | Code Review model      | Dedicated model, 76% discount, heavily used     |
| First-time setup             | Claude Code     | Sonnet                 | New patterns need strong guidance               |
| Quick questions              | Copilot (Rider) | GPT-5.4 / Sonnet 5     | Some models 100% discounted                     |
| PR review automation         | GitHub Copilot  | Code Review + Sonnet 5 | Biggest credit consumer, but heavily discounted |

**Monthly cost with this split:**

- Claude Max + overages: ~$399/mo (€100 subscription + €250 additional tokens, EUR prices include 23% VAT; €1 = $1.14)
- OpenRouter (Hermes, mimo-v2.5-pro only): ~$40–60/mo (USD)
- Copilot: ~$200/mo net (USD, consumption-based with 63% average discount)
- **Total: ~$620–660/mo**
- **Savings vs pure API pricing:** $3,021 API-equivalent for $399 = **7.6x return** on Claude alone

---

## Cost Optimization Strategies

1. **Model routing**: Use cheap models (mimo-v2.5-pro) for implementation, expensive ones (Opus/Fable) for planning and review. Hermes's persona routing makes this automatic.

2. **Cache awareness**: Keep context byte-stable across turns. Claude's cache reads cost 1/10th of writes. OpenRouter cache efficiency is 88.9% — rewriting state files mid-session kills this.

3. **Provider selection on OpenRouter**: The same model costs vastly different amounts depending on the provider. mimo-v2.5-pro via Xiaomi is $0.003/call; via DeepInfra it's $0.099/call. OpenRouter routes automatically, but you can influence it.

4. **Skill loading**: Load skills on-demand, not every turn. Reduces context token overhead significantly.

5. **Task decomposition**: Break work into focused subagents. Smaller context = fewer tokens per agent.

6. **Quality over price**: DeepSeek looked cheap on paper but cost more in rework. mimo-v2.5-pro at $0.40/MTok output delivered better results than models costing 15x more. Always factor in retry costs when comparing models.

---

## How to Track Your Own Usage

```bash
# OpenRouter — built-in Explorer dashboard
# Visit: https://openrouter.ai/activity
# Export as CSV/PDF for analysis

# Claude Code — ccusage (auto-detected)
npx ccusage daily
npx ccusage claude daily --json

# Hermes Agent — ccusage or built-in CLI
npx ccusage hermes daily --json
hermes usage --by-model --since "7 days ago"

# GitHub Copilot — AI Usage Report (billing export)
# Visit: https://github.com/settings/billing/summary
# Download "AI Usage Report" CSV for per-model credit consumption
# Also set up OTEL for CLI-level tracking:
export COPILOT_OTEL_ENABLED=true
export COPILOT_OTEL_EXPORTER_TYPE=file
export COPILOT_OTEL_FILE_EXPORTER_PATH="$HOME/.copilot/otel/copilot-otel-$(date +%Y%m%d-%H%M%S).jsonl"
npx ccusage copilot daily --json

# All agents combined
npx ccusage monthly --json
npx ccusage session --json
```

---

## Code Quality Case Study

A common concern with AI-generated code is quality. Here's evidence from the largest project — job-search-ai-assistant (private repo) — a .NET/C# backend + React 19 frontend built primarily with Claude Code and Hermes Agent. **All code was hand-reviewed and refactored by a human developer before merging.**

### Backend

| Metric          | Result                                                   |
|-----------------|----------------------------------------------------------|
| Build           | ✅ 0 warnings, 0 errors                                   |
| Tests           | ✅ 2,302 passed, 0 failed (1 skipped — visual PDF review) |
| Critical issues | **None found**                                           |
| Overall grade   | **Strong** — production-quality                          |

**Strong points:**

- Domain purity enforced by ArchUnit tests
- Guard clauses used consistently (`CommunityToolkit.Diagnostics.Guard`)
- Immutable records as the norm
- High-performance logging (`[LoggerMessage]` on nested `static partial class Log`) adopted uniformly
- Thorough take-home calculator (UoP + B2B/JDG, progressive/flat/ryczałt, ZUS schemes, KUP caps)
- Clean signal model with conflict-aware merge closing a documented TOCTOU gap

**5 minor findings:** one UUID version inconsistency, one guard clause style mismatch, one missing range validation, one duplicated helper, one frontend/backend type divergence. No architectural issues.

### Frontend

| Metric        | Result                                                                       |
|---------------|------------------------------------------------------------------------------|
| Overall grade | **A-**                                                                       |
| TypeScript    | Zero `any` types in application code                                         |
| Testing       | 85 test files + axe accessibility tests + Playwright E2E                     |
| Design system | Cohesive console aesthetic (JetBrains Mono, bracket motifs, blinking carets) |
| Accessibility | Skip links, ARIA, focus-visible, prefers-reduced-motion built-in             |

**Strong points:**

- Monochrome palette with hue reserved exclusively for domain state chips
- Screaming Architecture followed consistently (domain-named folders)
- All data fetching via TanStack React Query (no raw `fetch`)
- Tailwind CSS v4 `@theme` configuration rated "exemplary"
- React 19 ref-as-prop pattern correctly adopted (no deprecated `forwardRef`)

**Note:** This code was built by AI agents (Claude Code + Hermes) but **hand-reviewed and refactored by a human developer**. The reviews above are independent assessments of the final merged code, not the raw AI output. The combination of AI speed + human judgment produces code that passes professional code review standards.

---

## Methodology Notes

- All data collected from July 8–23, 2026 (16 active days)
- Tasks were real production work across **three projects**:
  - arasz-home-page (this repo) — Angular + Azure Functions + Terraform (this blog, infrastructure)
  - job-search-ai-assistant (private repo) — .NET/C# + React 19 + Cosmos DB (the main product)
  - ai-badger — AI agent marketplace/catalog framework ([GitHub](https://github.com/Arasz/ai-badger))
- **Claude Code**: Claude Max subscription (€100/mo) + €250 in additional token purchases. Billed in EUR, includes 23% Polish VAT. Converted to USD at €1 = $1.14. API-equivalent costs calculated by ccusage for comparison only.
- **Hermes Agent**: OpenRouter API (pay-per-token). Actual spend tracked via OpenRouter Explorer and ccusage.
- **GitHub Copilot**: Business plan. AI credit system with per-model consumption tracked via GitHub's AI Usage Report (billing export). Used primarily via Rider IDE integration, but billing and usage data come from GitHub, not JetBrains.
- **Currency**: Claude Code billed in EUR; all other services billed in USD. All figures in this article are in USD unless noted.
- ccusage detected agents automatically from their local data stores (no manual configuration)

---

## Tools, Agents, and Resources

**Agents tested:**

- [Claude Code](https://docs.anthropic.com/en/docs/claude-code) — Anthropic's terminal-based coding agent
- [Hermes Agent](https://hermes-agent.nousresearch.com/docs) — open-source CLI agent by Nous Research
- [GitHub Copilot](https://github.com/features/copilot) — GitHub's AI coding assistant

**Model providers:**

- [OpenRouter](https://openrouter.ai/) — unified API for 200+ AI models (used for mimo-v2.5-pro, DeepSeek, Qwen)
- [Anthropic](https://www.anthropic.com/) — Claude models (Sonnet, Opus, Fable, Haiku)
- [Xiaomi MiMo](https://huggingface.co/XiaomiMiMo) — mimo-v2.5-pro model

**Tracking tools:**

- [ccusage](https://github.com/ccusage/ccusage) — CLI tool for tracking AI coding agent usage across Claude Code, Hermes, Copilot, and more
- [OpenRouter Explorer](https://openrouter.ai/activity) — built-in usage dashboard with per-model token counts and spend
- [GitHub Copilot AI Usage Report](https://docs.github.com/en/copilot/concepts/billing/usage-based-billing-for-individuals) — billing export with per-model credit consumption

**Documentation referenced:**

- [Claude Max plan](https://support.claude.com/en/articles/11049741-what-is-the-max-plan) — pricing, usage limits, weekly caps
- [Claude Code with Pro/Max](https://support.claude.com/en/articles/11145838-use-claude-code-with-your-pro-or-max-plan) — 5-hour rolling sessions, shared limits
- [GitHub Copilot usage-based billing](https://docs.github.com/en/copilot/concepts/billing/usage-based-billing-for-individuals) — AI credits, plan tiers, monthly quotas
- [Claude Code usage limits](https://www.morphllm.com/claude-code-usage-limits) — third-party analysis of session/weekly caps

---

*Last updated: 2026-07-23*
