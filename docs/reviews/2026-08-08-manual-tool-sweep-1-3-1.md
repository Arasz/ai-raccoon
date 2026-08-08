# Manual tool sweep — AiRaccoon 1.3.1

**Date:** 2026-08-08
**Target:** `1.3.1+51d678c835e8f520d86986a6fea38d935539a325` — the installed `~/.dotnet/tools/ai-raccoon`,
which is built from current `main` (`51d678c`).
**Method:** the server driven directly over stdio JSON-RPC, one live process per flow, so every
request and response below is a real wire exchange rather than a mocked call.
**Coverage:** all 22 exposed tools invoked at least once, against a throwaway project id
(`manual-13x-probe`) plus read-only calls against the live bank.

The task asked for 1.3.0. The repo and the installed binary are both 1.3.1, and 1.3.0 is two
releases behind `main`, so the sweep ran against what actually ships. Nothing below is
1.3.1-specific — every defect predates the bump.

## Result

18 of 22 tools behave as documented. Four defects, one of them breaking the documented happy path
of the workspace feature.

| # | Severity | Tool | Defect |
|---|---|---|---|
| D1 | High | `memory_workspace_consolidate` | The value its own description advertises crashes it |
| D2 | Medium | workspace family | Four different answers for the same unknown workspace |
| D3 | Low | `memory_share` / `memory_delete` | Unknown hash handled two different ways |
| D4 | Low | `memory_stats` | Returns every project's context list to a project-scoped caller |

## D1 — `memory_workspace_consolidate` rejects the value it documents

The tool description reads *"promotes the kept hashes (or `'all'`)"*, which reads as the scalar
string. The JSON schema disagrees: `keep` is `{"type":"array","items":{"type":"string"}}` with
`"Hashes to promote, or ['all'] to promote everything."`

```
keep: "all"    -> isError=true :: An error occurred invoking 'memory_workspace_consolidate'.
keep: ["all"]  -> {"promoted":1,"discarded":1}
```

The scalar form does not fail validation — it escapes as an unhandled exception:

```
fail: ModelContextProtocol.Server.McpServer[1433779783]
      "memory_workspace_consolidate" threw an unhandled exception.
      System.Text.Json.JsonException: The JSON value could not be converted to System.String[].
        at Microsoft.Extensions.AI.AIFunctionFactory...GetParameterMarshaller
        at AiRaccoon.Tools.ToolRefusals...Filter b__0  (src/AiRaccoon/Tools/ToolRefusals.cs:67)
```

Two defects in one. The description contradicts the schema, and argument-binding failures pass
straight through `ToolRefusals.Filter` — the component that exists so refusals read as refusals —
and reach the agent as `"An error occurred invoking 'X'."` with no parameter name, no expected
shape, nothing to act on.

The consequence is worse than a bad error message. An agent following the description calls
`consolidate` with `'all'`, gets an opaque error, and its workspace stays open with its entries
stranded in the outbox — confirmed: after the failed call the workspace still held its entry and
nothing had reached project scope. `CLAUDE.md` tells agents that workspaces "consolidate on
finish"; the documented way to finish one does not work.

## D2 — the workspace family gives four answers for the same bad input

Against a `workspaceId` that does not exist:

| tool | response | verdict |
|---|---|---|
| `memory_write` (`workspaceId` set) | `unknown-workspace: Workspace '…' does not exist for project '…'` | correct — this is the model |
| `memory_workspace_status` | `{"entries": [], "count": 0}` | silent success |
| `memory_workspace_discard` | `{"discarded": 0}` | silent success |
| `memory_workspace_consolidate` | `An error occurred invoking …` | untyped |

`UnknownWorkspaceException → "unknown-workspace"` is already mapped (`ToolRefusals.cs:26`) and the
exception type already exists. Only the write path throws it.

`status` is the one that bites. An empty outbox and a workspace that was already consolidated are
the same response, so an agent polling for completion cannot tell "finished" from "never existed"
— a silent failure inside a state machine, against a project whose invariants require state
transitions to be explicit.

## D3 — unknown hash, two contracts

- `memory_delete` with an unknown 64-hex hash → `{"deleted": 0}`
- `memory_share` with the same hash → `isError=true :: An error occurred invoking 'memory_share'.`

`memory_share`'s is another untyped leaked exception. Which of the two contracts is right is a
product question; that one of them leaks a raw exception is not.

## D4 — `memory_stats` is project-scoped but answers for every project

`memory_stats(projectId: "manual-13x-probe")` returns:

```json
"contexts": ["shared","project:ai-badger","project:ai-raccoon",
             "project:arasz-home-page","project:hermes-default","project:jsaa"]
```

`projectId` is a required parameter and the tool is documented as reporting "the bank's committed
contexts". One bank holds every project on the machine, so a caller scoped to one project learns
the names of all the others. Low severity — project ids are not secrets — but it is a scope leak
in a tool whose signature promises scoping.

## Confirmed-correct behaviour

Worth recording, because these are the paths most likely to rot:

- **Workspace isolation holds.** A workspace-scoped write is invisible to `scope=project` and
  visible to `scope=all` only when the workspace is named. After `discard`, the entry is gone from
  every scope.
- **Refusals are typed and legible** where they were designed to be: `watching-disabled`,
  `path-outside-scope` (both `memory_ingest_file` and `memory_ingest_directory`),
  `sync-not-configured` — the last carrying the exact CLI command to fix it.
- **`memory_share` → `scope=shared`** round-trips; the shared copy takes a new hash and a
  `shared/`-prefixed path.
- **Dedup is healthy**: zero exact-duplicate normalized values across all 288 queued candidates.
- **`memory_sweep(dryRun: true)`** lists without deleting; `memory_embed_pending` reports
  `{processed, pending}` honestly on an empty backlog.

## Ranking: a documented trap, not a defect

`memory_search` returns `ranking` values that cluster at the top — 1.0, 0.984, 0.968 for three
results. That is `ReciprocalRankFusion.Fuse` (`ReciprocalRankFusion.cs:46-51`) computing
`Ranking = score / max`: the top hit is **always exactly 1.0**, whatever it is. With RRF `k=60` and
the default `limit=20`, no result can ever fall below the default `minScore` of 0.7 — the
threshold would only start biting past rank 28.

ADR-0006 already records this: *"minScore is measured inert at the chosen point."* So the
behaviour is known and deliberate. What is stale is the **tool description**, which still sells
`minScore` as `"Minimum ranking threshold 0..1 (default 0.7)"` — a relevance control it is not.

This matters more than its severity suggests, because `CLAUDE.md` instructs agents to search
memory first and treat a hit as citable evidence. A live example from this sweep: a query for
`"minScore inert measured threshold"` returned an unrelated probe entry about schema write guards
at `ranking: 1.0`, tied with the genuinely relevant ADR-0006 chunk. An agent trusting the number
would cite the wrong one.

## Promotion queue — quality assessment

Measured across all 288 waiting candidates (`memory_promotion_list`, limit 1000).

**Distribution is flat where it should discriminate.** 288 rows carry only **59 distinct scores**.
**168 rows (58%) score exactly 2.500**; another 39 score exactly 3.500. **72% of the queue holds
one of two values.** Per project the spread is worse: jsaa has 9 distinct scores across 93 rows.
Since `memory_promotion_list` is documented as "ranked by score", most of the queue is not
actually ranked — review order inside the plateau is whatever the tiebreak happens to be.

**Two thirds of the queue is scored on metadata alone.** `('cross-project','recent')` accounts for
156 rows and `('accessed','cross-project','recent')` for 35 — **191 of 288 (66%) with no
content-evidence channel firing at all**. The content channels that promotion scoring v3 exists to
apply (`adr`, `rule-language`, `verified-contract`, `measured-values`) reach roughly 60 rows.

**`mid-sentence` labels but does not penalize.** It appears in the reason list of several of the
*highest* scorers (3.850). Those candidates are chunk fragments beginning mid-thought — e.g.
`"k=30 kills a1/a6; the max5x50 window starves a6's exact chunk…"` and `"its checkout share one
store (b12). backfill or annotate the 11 zero-token tasks."` A fragment that starts mid-sentence
is not a durable cross-project fact, and promoting one puts noise in the shared tier.

**The top-scoring candidate is install-local.** ai-raccoon's highest (3.900) is
`"[facts] airaccoon deployment (2026-08-06): one bank per install — ~/.ai-raccoon/memory.db holds
all projects…"` — a note about this machine's layout, scored above every ADR-derived candidate.

**The queue is accumulating, not draining.** 288 waiting, ages 0.91h–3.74h (median 3.21h), against
`reserved=200` per project with nothing `borrowing`. Per project: jsaa 93, ai-raccoon 72,
arasz-home-page 67, ai-badger 52, hermes-default 4. Nothing observed promotes candidates out.

**The queue cannot be reviewed in context.** Candidate values average 899 characters (median 924,
max 6,278) and are returned in full with no projection or summary mode.
`memory_promotion_list(limit: 1000)` returned **413,263 characters** in a single response; the
default `limit=50` returns roughly 45,000 — enough to crowd out the reasoning of the agent that is
supposed to be reviewing it. Fetching the queue at limit 60 through the MCP client exceeded the
client's own response ceiling and had to be spilled to a file.

**15 of 288 rows carry no `sourceFile`**, against a `CLAUDE.md` contract that entries carry source
paths so a hit can be cited.

## Method

Driver: `mcp_call.py`, a stdio JSON-RPC client holding one server process open per flow. Feeding
every frame and closing stdin makes the server exit before it flushes, so the driver keeps stdin
open and reads replies incrementally — worth knowing for anyone reproducing this.

One probe was invalid and is recorded so nobody repeats it: `memory_workspace_begin` takes
`projectId`, `agentId`, `name` and *generates* the workspace id. Passing `workspaceId` to it is
silently ignored. It was that mistake that exposed D2 — the write correctly refused the id that
`begin` never created, while `status` cheerfully reported an empty outbox for it.
