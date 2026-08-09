# Refinement feedback — Six corrections, eight decisions, three open items

<!-- refinement-form: refinement:2026-08-09-memory-comms-layer:v1 · saved 2026-08-09T11:11:51.071Z · answered 17/17 -->

Source document: `docs/work/2026-08-09-memory-as-a-communication-layer.md`

## C1 — No agent can set a TTL on a memory entry, because no tool exposes one

**Verdict:** APPROVE

**Notes:**

> Only new tool, we use it for memory degradation algorithm

---

## C2 — The sweep reaper does not exist — nothing schedules it

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## C3 — agentId is inert, and on memory_workspace_begin it is discarded entirely

**Verdict:** APPROVE

**Notes:**

> wire

---

## C4 — The tool registration list is hand-maintained in two places

**Verdict:** APPROVE

**Notes:**

> First, adjust your plan - other agent is refactoring stdio client into http proxy - so we will run everything through http. Check memories and adjust plan

---

## C5 — The response envelope is unbounded in project count

**Verdict:** APPROVE

**Notes:**

> 530 is almost nothing, but lets bound them to 36 or just return the waiting list for asking project, other project should be not intrested in the other projects waitng lists

---

## C6 — The advisory envelope notice is empirically ignored — we already ran this experiment

**Verdict:** APPROVE

**Notes:**

> Yeah do you read it? Do you notice what is in it?

---

## D1 — Ship T0 now: two PreToolUse hooks, no AiRaccoon change

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## D2 — Do not introduce an agent identity — use the git worktree path as the lane key

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## D3 — Do not put board state in the tool-response envelope

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## D4 — If a claim surface is built, build it on label: contexts before building a table

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## D5 — Do not build T2 (the full pub/sub board: topics, subscriptions, cursors, replies)

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## D6 — Record claude/channel (T3) as a rejected alternative and stop costing it

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## D7 — Route semantic coupling (incident 2) to code-review-graph, not to any board

**Verdict:** APPROVE

**Notes:**

> other agent is working on OLTP refactor right now

---

## D8 — The article and the build are separable — part one is publishable today

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## O1 — ADR-0020 exists only in another worktree, not in main

**Verdict:** APPROVE

**Notes:**

> it will be merged

---

## O2 — Is cross-machine coordination a real need or a speculative one?

**Verdict:** APPROVE

**Notes:**

> no, i have only one machine now - but could be it a real use case? yes.

---

## O3 — The prior LOW ranking rested on a premise that has expired

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## Not answered

_(none — every item has a verdict)_

<!-- end refinement feedback -->
