# Memory as a communication layer — exploration and ranking

Date: 2026-08-09. Status: exploration for a decision, not a plan of record. No code changed.
Companion owner-gate form: `docs/work/2026-08-09-memory-as-a-communication-layer-review.html`.

Source request: design an explicit agent communication layer over the memory server — an agent
message board with topics, subscriptions, bounded response state and time-limited cleanup — and
rank its usefulness against its cost. Prior material: `docs/work/2026-08-07-cross-session-agent-coordination.md`
(the article note), `docs/work/archive/2026-08-04-memory-model-gap-analysis.md` (the earlier
coordination-table proposal).

## The finding, first

The proposal is buildable and would work. The reason not to build it as specified is not
architectural — it is that **this repository has already run the experiment, and the result is on
the record.**

AiRaccoon ships an unconditional advisory notice on every one of its 22 tools:
`ToolGate.WrapAsync` (`src/AiRaccoon/Tools/ToolGate.cs:28-29`) attaches `PromotionMeta` to every
response, and that record's own doc comment states the design intent —
*"always present (zero is informative, never absent)"* (`src/AiRaccoon.Core/Memory/PromotionMeta.cs:3`).

That is the exact mechanism the request proposes for board state: *"We will return the state of
the board in each tool response."* It has been live for weeks. Measured from this session's own
`memory_search` responses:

```
"waitingPromotionsCount": 616, "oldestWaitSeconds": 59935
```

616 items waiting; the oldest has been waiting **16.6 hours**; the average wait is 7.5 hours. An
unmissable, per-call, always-present notice has been telling every agent across five projects that
a queue needs draining, for two-thirds of a day, and no agent has acted on it.

The one coordination mechanism in this repo that demonstrably *does* change agent behaviour is the
memory-first `PreToolUse` gate — and the article note credits it precisely (`:20-24`): it forced a
`memory_search` that returned ADR-0002 before either session wrote a line of exporter code. It
works because it **denies**, not because it informs.

**Advisory-everywhere is the failure mode; enforcing-narrowly is the mode with evidence behind it.**

## What the board would actually have caught

The article note's eight incidents are the only real test data available. Scoring them honestly:

| # | Incident | Board catches it? |
|---|---|---|
| 1 | ADR numbering collision | Weakly, and redundantly — `ls docs/adr/` is what caught it |
| 2 | `project_id` tag semantic conflict | **No** — see below |
| 3 | Plaintext vs cardinality distinction | No — produced by live disagreement |
| 4 | `dotnet test` corruption | No — already solved by `memory_write` |
| 5 | `serve` subcommand parse trap | No — already solved by `memory_write` |
| 6 | `EventId` collisions | **Yes** — this is a file claim, cleanly |
| 7 | Settings leak vs OTLP rationale | No — retroactive validation |
| 8 | Unscoped `pkill` | **No** — ran through Bash; no AiRaccoon call was involved |

Of eight incidents a board cleanly catches **one**. Two are already solved by memory today. The
two the request names as its motivating cases — "is someone modifying module A?" (incident 2) and
blast-radius damage (incident 8) — are both misses.

**Incident 2 is the decisive one and it deserves the detail.** Session B was going to strip
`project_id` tags from `PromotionQueueMetrics`; Session A was building an exporter that depends on
that dimension. The coupling, verified in the current tree, is a string-literal match across a file
boundary:

- `src/AiRaccoon/Observability/OtlpExport.cs:32` — `.AddMeter("AiRaccoon.PromotionQueue")`
- `src/AiRaccoon/Observability/PromotionQueueMetrics.cs:24` — `new Meter("AiRaccoon.PromotionQueue")`

Two different files, disjoint diffs, both test suites green. A file-scoped claims board never fires:
A never asks about a file it is not touching. A topic board fires only if A and B independently
choose the same free-text topic — which is the correlated-agreement failure the note itself names
as its sharpest lesson (`:293`): *"two independent expert opinions agreeing did not make them right."*

That coupling **is** a static edge, and this repo already runs the `code-review-graph` MCP server.
Incident 2 is a graph query, not a message. Routing it to the board is the proposal's weakest claim.

## The declared-but-unwired defect class

Verifying the pieces a TTL'd board would stand on turned up the same defect six times. This is the
dominant risk to any coordination feature, and it is why the ranking below weights "proven to fire"
above "designed correctly".

| # | Thing | State | Evidence |
|---|---|---|---|
| 1 | `entries.agent_id` | Written, never read back | `MemorySql.cs:89` inserts it; no SELECT projects it; `MemoryEntry` has no agent field |
| 2 | `agentId` + `name` on `memory_workspace_begin` | Declared on the tool, discarded entirely | `WorkspaceTools.cs:31,34` declare them; `:42` calls `BeginAsync(projectId, ct)`. Columns `MemorySchema.cs:22-23` never written |
| 3 | `SetEntryTtlAsync` | Plumbed to the store, unreachable by any agent | `IMemoryStore.cs:85` → `SqliteMemoryStore.cs:679` → `ForgettingPolicyService.cs:36`. **No MCP tool exposes `ttlDays`** — grep of `src/AiRaccoon/Tools/` returns nothing |
| 4 | `SweepService` | Implemented, no scheduled caller | `SweepAsync` has exactly one caller: `SweepTools.cs:40`, the manual tool, `dryRun` defaulting true. Three hosted services exist (`Dependencies.cs:76,118,144`); none sweeps |
| 5 | `ai_raccoon_queue_evicted_score` | Instrument with no writer | Recorded in the article note's verification section |
| 6 | `PromotionMeta` | Delivered on all 22 tools, acted on by nobody | 616 waiting, oldest 16.6h (above) |

Items 3 and 4 together are load-bearing: **the TTL mechanism every "TTL'd claims" design depends on
is dead at both ends.** No agent can set a TTL, and nothing would reap it if they could. Any board
with `expires_at` would be the seventh entry in this table unless the reaper is put on a timer and
watched to fire against a real row first (`prove-the-check-fails`).

The same shape is visible outside the codebase: `.ai-badger/task-tracking/current-session.json`
names one session, and that PID is dead, while `/tmp/cc-socks/` correctly listed three live
sessions (5202, 36774, 46971). A registry nobody garbage-collects answers confidently and wrongly.

## Answers to the four direct questions

### Do we have an agent id? Do we need one?

We have one nominally and none in practice — items 1 and 2 above. It was never meant to be
identity: the spec says *"`agent_id` is provenance only"*
(`docs/work/features-agent-memory/spec-issue-1.md`). Only the Hermes integration ever sets it
(`integrations/hermes/ai-raccoon/__init__.py:285,354`), to a coarse `hermes-<identity>` label.

There is also no session or connection identity to borrow: the HTTP transport is
`options.Stateless = true` (`src/AiRaccoon/Setup/McpServerSetup.cs:196`), and nothing in `src/`
reads `ClientInfo`.

**Recommendation: do not introduce an agent identity. Use the git worktree path as the lane key.**
It is *observed* rather than claimed — computed by a hook from `cwd` — which matters because the
note's own stated limit (`:313`) is that "everything relied on accurate self-report." A
self-declared `agentId` fails open: two careless agents pick `"claude"` and silently impersonate
each other. A worktree path survives restart, distinguishes lanes, and is already this project's
unit of isolation (31 worktrees today).

Independently of any tier: **fix or delete the three dead parameters.** A declared, documented,
discarded parameter is a hand-maintained surface that has already drifted from its implementation.

### `_meta` versus a communication section

Settled by evidence rather than spec reading. The MCP `_meta` field on `CallToolResult` is
spec-sanctioned and, in practice, invisible — the spec says implementations *"MUST NOT make
assumptions about values at these keys"*, and no shipping client routes it to the model. Claude
Code silently ignores `ResourceUpdatedNotification` entirely (issue #47823, closed `NOT_PLANNED`).

`ApiEnvelope.Meta` is visible, because it is **payload, not protocol metadata** — it is serialized
into a text content block and lands verbatim in the model's context. Both `memory_search` responses
in this session prove it.

So the channel the request asks for already exists. The question is not *can we*, it is *should
we*, and the 616-item measurement answers that: **no.** Adding board state to the envelope would
spend ~53 tokens × 22 tools × every call to reproduce a mechanism this repo has already shown
agents ignore.

### Is there an event mechanism in MCP?

Not a usable one. The latest ratified revision (`2026-07-28`) **removed server-initiated requests
entirely** — sampling and elicitation may now only be emitted as an `InputRequiredResult` in
response to an in-flight client request (the MRTR pattern). No core-spec notification is defined to
reach the model's context; they all maintain client-side state.

The one path that does reach the model is Claude Code's proprietary `claude/channel` capability,
and it is out of reach here for three independent reasons, any one of them fatal:

1. `options.Stateless = true` — the C# SDK documents that unsolicited notifications are dropped on
   stateless Streamable HTTP because there is no session-wide channel.
2. `claude/channel` is **stdio only**, and ADR-0020 (accepted 2026-08-09; currently living only in
   `.ai-badger/worktrees/mcp-server/`, not in `main`) makes stdio a *proxy*. Backend→client push
   would need relay machinery ADR-0020 does not specify.
3. It needs `--dangerously-load-development-channels` plus a full-screen warning per client per
   machine — reimposing exactly the per-client config cost ADR-0020 exists to avoid.

It is also a research preview, fire-and-forget with silent drops, zero-portability, and documented
by Anthropic as a prompt-injection vector. A channel any local process can write to, injecting
verbatim text into another agent's context, is a security surface.

**Recommendation: record it as a rejected alternative so nobody re-derives this.**

### Bounding growth, and "only new activity"

Both are solvable and neither is the hard part.

- **Response size.** The envelope is already the problem: `PromotionMeta.WaitingByProject` and
  `CapacityByProject` are unbounded in project count. Measured at 5 projects: **527 bytes ≈ 155
  tokens**, growing ~86 bytes per project. At 20 projects that is ~530 tokens on every call. This
  wants a top-N cap with an overflow count regardless of any board decision.
- **"New" without identity.** A monotonic `seq` in the envelope plus a caller-supplied `sinceSeq`
  on the fetch tool. The agent's own transcript is the cursor — the previous envelope is already in
  its context, so a changed number is a visible delta. Zero server state, works under
  `Stateless = true`, and the failure is self-limiting: an agent that lies about `sinceSeq` sees
  nothing and harms only itself. Server-side per-subscriber cursors have the opposite property — a
  stale or mis-attributed cursor silently starves the wrong reader, which is the
  `current-session.json` failure with extra steps.

## The tiers

| | T0 — hook + observation | T1 — claims with TTL | T2 — full pub/sub board | T3 — + `claude/channel` |
|---|---|---|---|---|
| Incidents caught | 1, 6, **8** | 1, 6 (+intent, +TTL) | +2 only by topic-naming luck | same as T2 |
| Cost | 1 work package, hours | 5 WPs, 1.5–2 agent-days | 12–15 WPs, 1.5–2 weeks | Blocked, not scopeable |
| Per-call token tax (×22) | **0** | 0 quiet / ≤53 active | ~40–90 always | same |
| New tables / tools | 0 / 0 | 1 / 2 | 4 / 6 | 4 / 6 |
| GC burden | none | one reaper | four uncollected registries | same |
| Reversibility | total | high | low | n/a |
| Precedent | the one mechanism the note credits | matches `promotion_queue` | **no MCP server or mainstream framework does topic pub/sub for resource contention** | research preview |

**T0** extends the `PreToolUse` surface already wired in `.claude/settings.json`: a dirty-file check
on `Edit|Write|MultiEdit` (run `git worktree list` + per-tree `git status --porcelain` — measured
at 0.51s across 31 worktrees, and it found a live three-way collision), and a **blast-radius deny**
on `Bash` matching `pkill|killall|build-server shutdown` when more than one lane is live. That deny
is the only mechanism in any tier that would have prevented incident 8 — the one incident that
actually landed.

**T1** is a `notices` table (`seq`, `project_id`, `lane`, `kind`, `subject`, `note`,
`created_at`, `expires_at NOT NULL`), two tools, a schema-ladder step to v4, and a reaper in
`BankMaintenanceHostedService`. Its honest pitch is not "it prevents collisions" — it prevents one
class that T0 also partly covers. Its real value is making "state what you are not touching" —
which the note lists as discipline that nothing enforced — into something with a shape.

**A cheaper T1 exists and should be priced before the table is.** The board is already expressible
in the current tool surface: `memory_write(context: "board/promotion-metrics")` posts,
`memory_search(contextLabel: ...)` reads topic-scoped, and `ContextNaming.LabelContext`
(`src/AiRaccoon.Core/Memory/ContextNaming.cs:23-27`) already namespaces it. On that path the
marginal build is (a) put `SweepService` on a `PeriodicTimer` beside the three services that
already have one, (b) expose `ttlDays` on `memory_write`, (c) a topic naming convention, (d) tests
proving expiry fires. That fixes defect-class items 3 and 4 as a side effect, and those are owed
anyway.

**T2** adds four tables and six tools for one incremental incident it catches only by luck. Two of
its four registries (`board_cursors`, `board_subscriptions`) are the `current-session.json` failure
reproduced in SQL. Not recommended.

## Two prerequisites owed regardless

1. **De-duplicate the tool list.** `WithTools<…>` is hand-written twice — `McpServerSetup.cs:57-64`
   and `:143-150` — with nothing comparing them. This is the `derive-or-delete-the-list` invariant
   in its purest form, and any new tool means editing both.
2. **Cap `PromotionMeta`.** Top-N projects with an overflow count. Without it, "the envelope is
   bounded" is false no matter what a board does.

## Where the prior ranking expired

The 2026-08-04 gap analysis rated a coordination table **LOW**, dead last, with one stated reason:
*"AiRaccoon's primary use case is currently single-agent."* That premise was false within three
days. Today: 31 worktrees, 17 live `claude` processes, 3 cross-session sockets, 4 tasks started
today, and the owner's own persisted note *"Several Claude sessions share this repo."*

A LOW rating whose only justification is a now-false premise carries no weight. That is an argument
for re-deriving the ranking — which this document does — not for inheriting either verdict.

## Evidence handling notes

Two citations circulating in the research were wrong and are corrected here rather than repeated:

- **MAST (arXiv 2503.13657)** does not find that communication protocols fail. It finds that
  *improved prompting and orchestration* are insufficient (Wilcoxon p=0.4 on one case study). A
  standardized communication protocol appears in MAST as **its own proposed future-work remedy**.
  What survives, and is the strongest single argument against an advisory board, is that its
  inter-agent-misalignment failure modes are dominated by agents **misusing an available channel**
  — which argues for enforcement, not for no channel.
- **The Google scaling study** numbers (180 configurations, 39–70% degradation, +80.9%) are from
  **v1** of arXiv 2512.08296; the current v3 says 260 configurations. More importantly the
  degradation is measured on *sequential* planning where later actions depend on earlier
  observations; the +80.9% is on *parallelizable* work. This repo runs 31 independent branches —
  the shape that gained. Citing the degradation number alone inverts the finding.

## Recommendation

**Ship T0 now, independently of every other decision.** Hours of work, zero token tax, totally
reversible, and the only tier that addresses the incident that actually landed.

**Then fix the defect class** — the reaper on a timer, `ttlDays` reachable, the dead parameters
gone, the tool list derived, `PromotionMeta` capped. All owed regardless, and all prerequisites for
any coordination feature that claims a TTL.

**Then, if a durable claim surface is still wanted, build it on `label:` contexts and prove the
reaper fires before building a table.** Do not put board state in the envelope. Do not build T2.
Record T3 as rejected.

**Route incident 2 to `code-review-graph`, and do not let any board claim it.**

### The strongest argument against this recommendation

It optimizes for the observed past. The eight incidents come from one afternoon with two sessions.
Collision probability scales roughly with the square of concurrent lanes, and this repo now runs
31 worktrees — including four separate lanes converging on the same transport layer. Eight
incidents at N=2 could be eighty at N=10, and the mix would shift toward exactly the semantic class
T0 cannot see, because file-level isolation is already exhausted.

The honest counter to that counter: **T1 does not catch incident 2 either.** So growth in the
semantic class is an argument that T1 is *insufficient*, not that T2 is *right* — since T2 catches
it only by naming luck. If lane count is the real worry, the next move after T0 is a semantic
signal from the code graph, not a bigger board.

## On the article

The article note and the build are separable and should be decided separately. The note is
publishable today at zero engineering cost.

Its thesis is that coordination emerged *without* a mechanism and worked because both sides chose
to disclose. Shipping the mechanism does not illustrate that thesis — but the note also names its
own missing mechanism twice (`:302-307`: "No lock, no registry, no arbiter"; "contact came after
both sides had already committed work"). A part two that says "here is what we built, here is what
the evidence said about advisory notices, and here is the one thing that changed behaviour" is a
stronger piece than part one alone, whose honest ending is that incident 8 shows what unlucky looks
like. That is a narrative argument, not an engineering one, and it should not carry a vote.
