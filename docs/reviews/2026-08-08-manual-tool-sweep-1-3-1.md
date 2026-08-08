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

Six defects across seven tools. Two are High: one breaks the documented happy path of the
workspace feature, the other lets a small scope tier capture every search. The remaining fifteen
tools behave as documented.

| # | Severity | Tool | Defect |
|---|---|---|---|
| D1 | High | `memory_workspace_consolidate` | The value its own description advertises crashes it |
| D2 | Medium | workspace family | Four different answers for the same unknown workspace |
| D3 | Low | `memory_share` / `memory_delete` | Unknown hash handled two different ways |
| D4 | Low | `memory_stats` | Returns every project's context list to a project-scoped caller |
| D5 | High | `memory_search` (`scope=all`) | A one-entry scope tier ranks 1.0 for every query, whatever the topic |
| D6 | Low | `memory_workspace_consolidate` | Reports every promoted entry as also discarded |

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

## D6 — `consolidate` reports every promoted entry as also discarded

Low severity, but it reads as data loss. `memory_workspace_consolidate` returns a `discarded`
count that includes the entries it just promoted:

| entries written | `keep` | response |
|---|---|---|
| 1 | `["all"]` | `{"promoted": 1, "discarded": 1}` |
| 2 | `["all"]` | `{"promoted": 2, "discarded": 2}` |
| 3 | `["all"]` | `{"promoted": 3, "discarded": 3}` |
| 3 | one hash | `{"promoted": 1, "discarded": 3}` |

`discarded` is counting *outbox rows removed*, which is all of them, since consolidating clears the
workspace either way. The plain reading of `{"promoted": 3, "discarded": 3}` is that three entries
were promoted and three were thrown away — on a workspace where nothing was dropped. An agent
checking whether its notes survived gets an answer that says they did not.

The useful number is how many were *not* kept: 0 in the `["all"]` case, 2 in the selective case.

Selective keep itself is correct: promoting one hash of three leaves that hash findable in project
scope and the other two absent, confirmed by search.

## A small scope tier captures every `scope=all` search — D5, High

Discovered by accident, then reproduced deliberately. It is the same normalization as above, in its
damaging form.

`scope=all` collects a result list per context and fuses them. The fusion scores **by rank position
only**, so a context's rank-1 entry scores `weight / (k + 1)` no matter how many candidates that
context ranked. At `k=60`:

| list | rank-1 score | displayed |
|---|---|---|
| shared tier, **1** entry | 1/61 = 0.016393 | **1.0000** |
| project tier, **2,400** entries | 1/61 = 0.016393 | **1.0000** |
| project tier, rank 2 | 1/62 = 0.016129 | 0.9839 |

The one-entry tier and the 2,400-entry tier **tie**, and the tie is broken by
`ThenBy(Path, Ordinal)` — effectively a coin flip on the filename.

Max-normalization is not the cause, though it is what makes the symptom so visible: dividing every
score by a positive constant preserves order, so the two rank-1 entries tie with or without it.
Normalization only inflates the displayed number to a confident `1.0000`.

The sweep's `memory_share` test put exactly one entry into a previously empty shared tier. Every
subsequent `scope=all` query returned it at `ranking: 1.0`:

| query | top-3 contained the shared entry at |
|---|---|
| `"banana pancake recipe"` | **1.0000** |
| `"how do I bake sourdough bread"` | **1.0000** |
| `"kubernetes ingress controller TLS termination"` | **1.0000** |
| `"promotion scoring tournament winner"` | **1.0000** |

The entry was about SQLite schema write guards. A parallel review of the bank measured it surfacing
at 0.83–1.0 in roughly 20 of 44 unrelated queries.

`scope=project` was unaffected, which localises it to the cross-context fusion rather than to
ranking generally. Removing the entry restored normal results — confirmed by re-running
`"banana pancake recipe"` afterwards.

This is not a quirk of an empty bank. **The shared tier is *designed* to be small** — CLAUDE.md
calls it the curated, cross-project, sweep-exempt tier that "nothing is shared without explicit
promotion" into. The smaller and more curated it is, the harder it captures every search. A
ten-entry shared tier against a 2,400-entry project tier still puts its best match in the top few
for any query at all.

The sweep's entry has been deleted and the shared tier is empty again; the bank is back to 2,401
entries across the five real projects.

**Fixed.** Contexts partition storage, not relevance — the per-context loop exists because the vec0
index is partitioned by context key. Both modalities already produce absolutely comparable
cross-context scores: `bm25` from one shared `entries_fts` index with global corpus statistics, and
cosine from one embedding space. So the fix collects per-context candidates, orders each modality
globally by absolute score, and fuses **once**. Length-weighting was rejected because it penalizes
exactly the curated tier the product wants trusted; raw scores and fixed-reference normalization
were rejected because fused scores are ~0.016, and the shipped `minScore` default of 0.7 would then
have started filtering everything ADR-0006 measured as unfiltered.

Single-context parity is exact rather than approximate: nDCG@5 0.674, MRR 0.881, recall@5 0.564 and
exact-chunk@3 4/11 are unchanged, and regenerating the 96-point RRF grid and the 33-point affinity
grid leaves `git status docs/work/` empty — byte-identical. That holds by construction, since every
baseline query searches a single context where the new path is a no-op.

**What this fix does not claim.** No retrieval measurement anywhere in the repo exercises
`scope=all` with two populated tiers — every graded query and harness fixture is single-context. So
cross-context *ranking quality* has no ground truth here. The claim is: defect removed,
single-context parity preserved, semantics now well-defined ("rank the union as if it were one
bank"). It is not a measured cross-context improvement, and that absence is why nothing was retuned.
Adding a cross-context stratum to `scripts/baseline-queries.json` with a shared-tier fixture is the
open corpus-scope question.

## Promotion queue — quality assessment

Measured across all 288 waiting candidates (`memory_promotion_list`, limit 1000).

**Distribution is flat where it should discriminate.** 288 rows carry only **59 distinct scores**.
**168 rows (58%) score exactly 2.500**; another 39 score exactly 3.500. **72% of the queue holds
one of two values.**

**The cause is not what this reads like, and the correction matters.** My first reading was that
promotion scoring v3 collapses. A dedicated review of the scoring path established otherwise:
**those rows were never scored by v3 at all.** The reason vocabulary gives it away —
`cross-project`, `recent`, `accessed`, `organic-write` exist nowhere in the v3 code; they are the
retired v1 incumbent's, and 2.500 is exactly v1's `+2 cross-project +0.5 recent`. ADR-0018 names
this very distribution as the defect v2/v3 was built to fix.

Restricted to the genuinely-v3 rows, scoring spreads fine: **60 distinct scores over 84 rows,
1.90–3.90.**

The mechanism is a re-scoring hole. A propose pass emits at most `limit` candidates
(`SharedExtractionService.cs:74-76`, hosted loop passes `DefaultCandidateLimit = 20`), and scoring
is deterministic — so every pass produces the *same* top 20. Rows ranked 21 and below are never
re-emitted, never re-upserted, never re-scored. Every project with any v3 rows has **exactly 20**.
The queue is a 20-row churning head on 204 rows of frozen v1 sediment, and the default `limit=50`
review window is 100% v3 rows, which is why the sediment went unnoticed.

**Two thirds of the queue is scored on metadata alone** — `('cross-project','recent')` on 156 rows,
`('accessed','cross-project','recent')` on 35, **191 of 288 (66%)**. That is the same finding seen
from the other side: those are the v1-scored rows, and v1 scored on metadata.

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

## Retrieval quality, measured over 44 real queries

A separate pass used the bank the way an agent is told to — `memory_search`, `projectId`
`ai-raccoon`, `scope=all`, 2-3 formulations per question — across 16 information needs, and
recorded what came back rather than what should have.

**It mostly works.** 14 of 16 topics got a decisive hit in the top 3. Effectively every result
carried a `sourceFile`.

Three things degrade it:

- **Two thirds of snippets open with `…` and cut into a sentence mid-stream**, unusable without
  fetching more. Whether a snippet starts cleanly tracks where the match keyword sits inside the
  chunk, not the query — re-phrasing does not reposition it usefully.
- **There is no way to fetch a whole entry.** Answers had to be reassembled from overlapping
  truncated snippets by repeated rephrasing. One entry — the docs-cleanup LANDMINE note — could not
  be recovered in full by any formulation. This inverts the value of the "2-3 formulations" rule in
  CLAUDE.md: rephrasing rarely surfaced a *different document*, but it was the only way to move the
  snippet window and read the sentence actually wanted.
- **Manually-written entries cite poorly.** Ingested document chunks carry real paths; the LANDMINE
  note's `sourceFile` is a bare directory with no filename, and a promoted entry's is its own
  storage blob rather than the document it describes.

**One entry in the bank is actively wrong.** An entry sourced to
`src/AiRaccoon.Infrastructure/Sqlite/MemorySchema.cs` states *"There is no schema version marker in
the memory bank: MemorySchema.cs has no PRAGMA…"*. That describes the pre-WI-5 state;
`MemorySchema.cs:339,344` reads and writes `PRAGMA user_version` today. Read on its own it would
send an agent the wrong way. **Left in place** — it is project data, not this sweep's to delete —
but it should go.

**A nuance on `minScore` worth recording.** ADR-0006's Decision section locks in `k=60`, 1:1
weights and **`minScore = 0.0`** as the grid optimum. The shipped tool default is **0.7**
(`SearchQuery.cs`, `MemoryTools.cs`). A prior doc audit reviewed that gap and marked it as holding,
on the grounds that the parameter is measured inert at both values. That is true today and stops
being true the moment the normalization changes — at which point a 0.7 default starts silently
filtering results that ADR-0006 decided should not be filtered.

## Method

Driver: `mcp_call.py`, a stdio JSON-RPC client holding one server process open per flow. Feeding
every frame and closing stdin makes the server exit before it flushes, so the driver keeps stdin
open and reads replies incrementally — worth knowing for anyone reproducing this.

One probe was invalid and is recorded so nobody repeats it: `memory_workspace_begin` takes
`projectId`, `agentId`, `name` and *generates* the workspace id. Passing `workspaceId` to it is
silently ignored. It was that mistake that exposed D2 — the write correctly refused the id that
`begin` never created, while `status` cheerfully reported an empty outbox for it.
