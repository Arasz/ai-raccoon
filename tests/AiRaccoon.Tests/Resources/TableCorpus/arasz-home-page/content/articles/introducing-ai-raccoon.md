---
id: 12
title: "AiRaccoon: An MCP Memory Server Where Measurement Beat the Plan"
slug: introducing-ai-raccoon
publishedAt: "2026-08-05T10:00:00Z"
updatedAt: "2026-08-15T12:00:00Z"
author: "Rafał Araszkiewicz"
description: "AiRaccoon is a local-first MCP memory server for AI agents: encrypted SQLite, hybrid FTS5+vec0 search, project and workspace isolation, cloud sync. Measured on 2026-08-04: 60% top-3 match rate on the 35-query baseline, 6/6 section hits on the structure-aware harness."
tags: [AI, Agents, MCP, Open Source, .NET, SQLite, Developer Tools]
status: published
categories: [agent-memory, verification]
---

> **Updated:** Correction, 2026-08-15. Two things below have gone stale, and one of them is security-relevant. The access-control section describes mode `full` as adding delete, sweep and consolidate, with no caveat. That was incomplete in a way that mattered: at `full`, a delete could name another project's context, or the `shared` tier, and destroy it. ADR-0051, accepted 2026-08-14, records the exploit and the fix, which shipped in 1.13.0. That section carries a warning below. The second is plain drift. "What AiRaccoon is" says nineteen MCP tools (16 memory, 3 file-watcher). The server reference now lists 26, grouped more finely than the article's two buckets: 10 memory, 4 workspace, 3 watch, 2 promotion, 2 share, 2 sweep, 2 search-feedback, 1 sync. The watch tools are still 3, and the count of 2 agent prompts is still right. Everything else, including both benchmarks and the limits stated alongside them, is as published and stays unedited.

## The problem: agents forget everything

An agent that spends 200,000 tokens understanding your project remembers none of it tomorrow.

I run multi-agent workflows on .NET and Angular codebases, and the pattern is always the same: the second agent that touches a repository re-reads the same files, re-derives the same conclusions, and re-forgets them. My earlier framework, [ai-badger](https://github.com/Arasz/ai-badger), pushed conventions into project files that every agent loads. But the deep knowledge, the ADR that explains why, the invariant that forbids it, stayed in files nobody told the agent to read.

A memory server is the missing layer: a process your agent talks to over the Model Context Protocol to store notes and search them later, so the next agent starts where the last one stopped. AiRaccoon's promise is that the memory is local (nothing leaves your machine until you say so), scoped (one project's knowledge stays in its project), and searchable well enough that the agent actually finds the right piece.

The searchable part is where the numbers kept overturning my plans.

## What AiRaccoon is

<!-- IMAGE: ai-raccoon-namesake -->
<!-- src: /screenshots/ai-raccoon-namesake.webp -->
<!-- alt: Close-up of a raccoon face -->
<!-- caption: The namesake -->
<!-- width: 800 -->
<!-- height: 599 -->

AiRaccoon is an MCP server that gives agents persistent, project-scoped memory backed by a managed .NET SQLite store, local-first by default. A user-scope install keeps one bank under `~/.ai-raccoon` shared by every project; a project-scope install keeps its own bank under `<project>/.ai-raccoon`. Projects partition the bank by context (`project:<id>`), so several projects can share one install without sharing one memory.

It runs on the ModelContextProtocol C# SDK 2.1.0 on .NET 10, speaks stdio by default with opt-in Streamable HTTP, and ships as a [NuGet tool](https://www.nuget.org/packages/ai-raccoon): `dotnet tool install -g ai-raccoon`. Three launch flags define an install's identity: `--transport` (default `stdio`), `--data-root` (default `~/.ai-raccoon`), and `--install-scope` (`user` or `project`). The zero-config client entry is five lines:

```json
{
  "mcpServers": {
    "ai-raccoon": { "command": "ai-raccoon" }
  }
}
```

Nineteen MCP tools (16 memory, 3 file-watcher) plus 2 agent prompts; every tool requires a `projectId`. The tools I reach for most:

| Tool | What it does |
|---|---|
| `memory_write` | Store a note, returns its content hash |
| `memory_search` | Hybrid FTS5 + vector search, scoped to project/shared/all |
| `memory_share` | Promote a hash into the cross-project `shared` tier |
| `memory_workspace_begin` / `memory_workspace_consolidate` | Open a sandbox, then promote the durable parts |
| `memory_sweep` | List or run degradation candidates (dry-run by default) |
| `memory_sync` | Push/pull snapshots to S3 or Azure Blob |
| `memory_watch_add` | Mirror a path into memory (opt-in) |

<!-- caption: The AiRaccoon MCP tools I use most -->

The full 19-tool contract lives in the [server reference](https://github.com/Arasz/ai-raccoon/blob/main/docs/reference/agent-memory-server.md).

## Try it out

The quickest way to see what an agent memory server buys you is to run one:

```
dotnet tool install -g ai-raccoon
```

The tool is on [NuGet](https://www.nuget.org/packages/ai-raccoon). After install, `ai-raccoon` is the whole interface: launch the server with `ai-raccoon` (stdio by default, bank under `~/.ai-raccoon`), configure it with the verb commands, and the zero-config `.mcp.json` entry from above is all an MCP client needs to connect. Since 1.0.2 the package also ships a schema-valid MCP registry descriptor, so VS Code can add the server straight from the registry instead of hand-writing config. `ai-raccoon --help` lists the verbs.

> **Why not the clean `ai-raccoon` id?** It is a reserved prefix on nuget.org that our account could not publish to: every push came back 409, which the dotnet CLI reports as "already exists", while no version of `ai-raccoon` was visible in any NuGet API. NuGet support was emailed on 2026-08-05 — and answered fast: the reservation was assigned to the account the next day. Since 1.0.9 the package publishes under the raw `ai-raccoon` id (the installed command stays `ai-raccoon`); `arasz.ai-raccoon` is deprecated. The two ids share the same command shim, so existing installs switch with an uninstall first — the bank under `~/.ai-raccoon` is untouched:

```
dotnet tool uninstall -g arasz.ai-raccoon
dotnet tool install -g ai-raccoon
```

## Isolation: projects, a shared tier, and workspace sandboxes

Memory that every project can read is noise; memory that no other project can read is amnesia. AiRaccoon has three isolation mechanisms for three different jobs.

`project:<id>` partitioning keeps one project's committed knowledge out of another's search results while sharing a single bank. The flat `shared` tier is the promotion path for cross-project knowledge: `memory_share` moves a hash in, and shared entries are exempt from degradation sweeps, so curated knowledge stays put. Workspaces are sandboxes for in-flight work: `memory_workspace_begin` mints a `workspace_id` whose notes stay in an outbox, invisible to normal searches, until `memory_workspace_consolidate` promotes the keepers and deletes the rest. Discard is free, which makes the workspace the right place for an agent's exploratory notes.

<!-- DIAGRAM: ai-raccoon-contexts -->
<!-- kind: flowchart -->
<!-- title: Context partitioning inside one memory bank -->
<!-- node: bank:One memory bank -->
<!-- node: shared:shared (curated) -->
<!-- node: project:project:<id> -->
<!-- node: workspace:workspace:<id> (outbox) -->
<!-- node: custom:custom (docs:api, ...) -->
<!-- edge: bank->shared:memory_share only -->
<!-- edge: bank->project:plain writes -->
<!-- edge: bank->workspace:workspace_begin -->
<!-- edge: bank->custom:context label -->

The four contexts have different lifecycles:

| Context | Scope | Synced? | Swept? |
|---|---|---|---|
| `shared` | curated, via `memory_share` only | yes | exempt |
| `project:<id>` | committed project memory | yes | yes |
| `workspace:<id>` | sandbox outbox | never | no |
| custom (`docs:api`, …) | user-defined labels | yes | never |

<!-- caption: Context partitioning: what each partition is for, and whether sync and degradation sweeps touch it -->

A CHECK constraint keeps a row in exactly one world: a committed entry has a scope and no workspace id; a workspace entry has a workspace id and no scope. The lifecycle is crash-safe, too: `memory_workspace_begin` inserts an `Active` row that survives a crash, so an abandoned sandbox stays traceable instead of leaking silently.

## Search: hybrid, structure-aware, and measured

The baseline said my search was wrong 40% of the time. That was the easy part of the story.

The status quo was hybrid search: FTS5 keyword ranking and vec0 semantic similarity fused with reciprocal rank fusion (rrfK=60, 1:1 weights). On 2026-08-04 I ran a 35-query retrieval baseline against the job-search assistant's docs corpus, seeded with 681 chunks from 166 files. Of the 10 queries with a known expected source, 6 matched at rank ≤3: 60%. The breakdown showed where it hurt:

| Category | Queries | With expected source | Matched top-3 | Rate |
|---|---:|---:|---:|---:|
| Architecture Decision Records | 7 | 7 | 3 | 43% |
| Invariants & Conventions | 6 | 3 | 3 | 100% |

<!-- caption: Baseline match at rank ≤3 by category, measured 2026-08-04 (35-query set, 681 chunks / 166 files, jsaa docs corpus; rates are over expected-source queries only) -->

All ten expected-source queries are ADR or invariant queries; the other 25 have no expected source and only check that results are relevant. Invariants are short, keyword-dense files; they matched at rank 1. ADRs are long documents chunked by heading, and a query aimed at one section lost to sibling sections of the same ADR and similar sections of other ADRs. Section-targeting was the failure mode.

The fix that worked was a second embedding. Each chunk already had a content embedding; I added a heading-path embedding, the chunk's `Decision > D1` chain, and fused the two: `score = α·content + (1−α)·structure`. On the 7-query A1–A7 set, whose expected sources are all `#decision` fragments, measured the same day on the 6,675-chunk harness corpus:

| Arm | File hit@5 | Section hit@5 | MRR (file) | MRR (section) |
|---|---:|---:|---:|---:|
| Content-only (α=1.0) | 7/7 | 4/6 | 0.560 | 0.369 |
| Structure-only (α=0.0) | 5/7 | 1/6 | 0.493 | 0.143 |
| **Fixed α=0.5** | **7/7** | **6/6** | **0.671** | **0.457** |
| Sigmoid α, T=0.1 / 0.5 | 7/7 | 6/6 | 0.671 | 0.457 / 0.493 |
| FTS status quo (F1) | 6/7 | 1/6 | 0.750 | 0.143 |
| F2: specced query fixes | 3/7 | 1/6 | 0.429 | 0.029 |
| F3: F2 + consolidation | 3/7 | 0/6 | 0.429 | 0.000 |

<!-- caption: Search arms on the 7-query A1–A7 set (6 of 7 target a section), measured 2026-08-04 (6,675-chunk corpus, 71% docs/work pollution) -->

The structure signal is the only mechanism that delivers section-targeted retrieval: 6/6, against 1/6 for FTS and 4/6 for content-only, and MRR(section) rises from 0.37 to 0.46–0.56 across the fusion arms. Two caveats. First, structure is a fuse-able signal, not a standalone ranker; structure-only lands 1/6, because heading paths alone cannot rank documents. Second, these are 7-query numbers on a corpus that is 71% `docs/work` pollution. Every number here is corpus-conditional, and my development plan requires a clean-corpus re-run before anything ships on top of them.

The cost is real but acceptable: 2× vector storage (content plus structure) and 81.5 seconds at ~0.8 GB RSS to embed 6,675 chunks in-process.

<!-- DIAGRAM: ai-raccoon-search-pipeline -->
<!-- kind: flowchart -->
<!-- title: Structure-aware search pipeline -->
<!-- node: query:Query -->
<!-- node: fts:FTS5 bm25 -->
<!-- node: vc:vec0 content embedding -->
<!-- node: vs:vec0 structure embedding -->
<!-- node: rrf:RRF fusion, fixed α=0.5 -->
<!-- node: results:Ranked results -->
<!-- edge: query->fts -->
<!-- edge: query->vc -->
<!-- edge: query->vs -->
<!-- edge: fts->rrf -->
<!-- edge: vc->rrf -->
<!-- edge: vs->rrf -->
<!-- edge: rrf->results -->

<!-- CHART: ai-raccoon-section-hits -->
<!-- kind: bar-horizontal -->
<!-- title: Section-level hit@5 by search arm (7 queries) -->
<!-- data: FTS status quo 1, Structure-only 1, Content-only 4, Fixed α=0.5 6 -->
<!-- description: The fixed-α fusion is the only arm that puts the right section in the top five for every query that has one. -->

## What measurement overturned

My plan predicted the cheap fixes would win. The measurement disagreed, and it was right.

Two dead ends, both falsified by the harness:

**Per-query α collapsed.** The clever version picked α per query by sigmoid confidence voting: α = sigmoid(confidence × T), where confidence is the spread of structure similarities. On this corpus the confidence signal is query-invariant (max−mean of structure sims ≈ 0.39–0.49), so per-query α came out ≈0.58 for every query, and the sigmoid arms at T=0.1 and T=0.5 match fixed α=0.5 on every hit-count metric and on file MRR; the only difference is section MRR, 0.457 vs 0.493. The machinery does not earn its place. What shipped is a fixed-weight fusion with a tunable constant: `ai-raccoon retrieval alpha set {0..1}`, default 0.5.

**The plan's FTS query-construction fixes regressed the status quo.** The plan's Wave 1 specced stopword stripping, AND-for-short queries, identifier-AND, and bigrams. As specced, it dropped file hit@5 from 6/7 to 3/7 and MRR(file) from 0.750 to 0.429. AND-for-short zero-matches whenever any non-stopword is absent from the target (11 of 35 queries zero-matched), and identifier-AND loses the A7 case because cross-referencing ADRs hold more `adr` + `0070` occurrences than ADR-0070's own chunks. The plan's own prediction, that stopword removal alone fixes ADR-0070, was falsified on the corpus.

| What the plan predicted | What measured | What shipped |
|---|---|---|
| Per-query α via confidence voting | Query-invariant; sigmoid ≡ fixed blend | Fixed α=0.5, tunable via CLI |
| Stopwords + AND-for-short + bigrams | Regressed 6/7 → 3/7 file hits | AND with OR-fallback on under-match |
| Source column deferred | Identifier queries need path matching | FTS source column at 8× bm25 weight |

<!-- caption: Plan prediction vs measurement vs shipped design, from the 2026-08-04 findings -->

What shipped instead tracks the data: the fixed α=0.5 blend, AND-primary query construction with OR fallback on under-match, and an FTS index that carries `source_file` and `section` as weighted columns (bm25 weights 1.0/8.0/16.0), so identifier tokens match paths, not just bodies: the structural fix, not the lexical one. The pre-registered win rule kept the comparison honest: an arm beats content-only only with ≥2 section flips or an MRR(file) delta ≥ 0.1; everything else is a tie.

<!-- DETAILS: how the numbers were measured -->
Two harnesses, two corpora, one scorer. The baseline (2026-08-04) ran 35 queries from `scripts/baseline-queries.json` against a bank seeded with 681 chunks from 166 files of the job-search assistant's docs; the metric was expected-source match at rank ≤3, with a known source for 10 of the 35 queries. The dual-vector harness ran the same 35 queries against a 6,675-chunk bank (71% of it `docs/work`), computed its own embeddings per arm, and scored the primary 7-query set A1–A7 by file-level and section-level hit@5 and MRR, where a section is a heading-path segment match (Decision sections carry sub-headings, so fragment-stripped last-segment matching would miss them). A shared comparison script scored every ranked list; every run sat under a 6 GB RSS cap.
<!-- /DETAILS -->

Seven queries is small; the pre-registered rule and per-query tables mitigate that, but it is the honest limit of this comparison.

## Embedding models: the 21 MB local model wins

The smallest embedding model is good enough for most uses. I measured that claim instead of trusting it.

On 2026-08-03 I benchmarked three embedding options on 174 real documents with 68 judged queries, same retrieval path, only the embedder differs:

| Model | Size | MRR | Per-query latency | nDCG@10 |
|---|---:|---:|---:|---:|
| all-MiniLM-L6-v2 (local, in-process) | ~21 MB | 0.836 | ~9 ms | 0.607 |
| EmbeddingGemma-300m (served) | ~334 MB | 0.858 | ~37 ms | 0.704 |
| Qwen3-Embedding-0.6b (served) | ~639 MB | 0.854 | ~90 ms | 0.606 |

<!-- caption: Embedding benchmark, measured 2026-08-03 (174 docs, 68 judged queries) -->

MRR is how high the first relevant hit ranks, the number that decides whether the agent finds the right memory at all. nDCG@10 is how well the whole top-10 is ordered. The served models win only the second: 0.70 vs 0.61. The 21 MB bundled model finds the right memory first essentially as often as models 15–30× its size, in 4–10× less time, offline.

<!-- CHART: ai-raccoon-embedding-tradeoff -->
<!-- kind: scatter -->
<!-- title: Embedding models: MRR vs latency -->
<!-- data: Local (9 ms) 0.836, EmbeddingGemma (37 ms) 0.858, Qwen3-0.6b (90 ms) 0.854 -->
<!-- xLabel: Embedding model (per-query latency) -->
<!-- description: The local model matches the served models on MRR while the others pay 4–10x the latency. -->

The recommendation is boring on purpose: configure the bundled local model first and move to a served model only if retrieval quality on your own corpus proves insufficient. Out of the box there is no embedding engine, so search is FTS5-only until you run `ai-raccoon model set local`; the bundled model is offline, ~21 MB, and in-process. You trade 4–10× latency and 15–30× disk for an ordering gain in the top-10, not for finding the right memory first.

## Security: the shipped half and the researched half

The security story has a shipped half and a researched half. This article only claims the shipped half.

Encryption at rest is opt-in, and the default key source is one environment variable: `AIRACCOON_DB_PASSPHRASE` turns on AES-256-CBC page-level encryption of the SQLite bank via e_sqlite3mc, and FTS5 and vec0 work unchanged. Without the passphrase the bank is plaintext. API keys and cloud credentials (OpenAI key, S3 keys, Azure connection string) live in the settings table, encrypted at rest when a passphrase is set. S3 keys and the Azure connection string are entered through interactive prompts, never on the command line; an empty answer aborts and persists nothing. The OpenAI key is passed to the `model set openai --api-key` config verb. Or you use the machine `--cli` credential chains (`DefaultAzureCredential`, the AWS default chain), which store only non-secret markers. `sync show` redacts secrets.

The threat model in [SECURITY.md](https://github.com/Arasz/ai-raccoon/blob/main/SECURITY.md) is worth quoting: the dangerous direction is the client that launches the process. A stdio MCP server inherits the privileges of whatever starts it, so the ro/rw/full access modes are a defence-in-depth layer, not a moat. The repo caveats: no CI secret scanning yet, one maintainer. This is a tool for people who read the docs, not a product with a support SLA.

## Access control: ro, rw, full

> **Warning:** The table below understates what `full` allowed. Until 1.13.0, a caller at `full` could delete another project's memory. The delete path kept its own copy of the context-to-rows mapping, a thousand lines away in another file, and that copy had no scope check. `memory_delete_context` with `context: "project:victim"` bound the project id from the caller-supplied string instead of the caller's own, so it deleted the victim's rows; `context: "shared"` carried no project predicate at all and wiped the cross-project tier. A 2026-08-14 adversarial review ran both against a real server. The precondition held in production, because the deployed bank's global mode is `full` and `memory_sweep` requires it. The gate itself was never bypassed: at the default `rw` the same calls were correctly refused. What was broken is that the rule had been ratified and tested on the write side only. AiRaccoon 1.13.0 fixes it. One function, `ContextScope.RequireWithinProject`, decides whether a context stays inside the caller's project, and every path that accepts an untrusted context now calls it, so `memory_delete_context` refuses a foreign `project:` context and refuses `shared` at every access mode. Read [ADR-0051](https://github.com/Arasz/ai-raccoon/blob/main/docs/adr/0051-a-context-never-names-another-project.md) for the exploit trace and for what the fix deliberately does not cover: access mode still resolves the mode of the project the caller names, with no caller identity anywhere, so it is not an authorization boundary against a caller free to name any project it likes.

Not every tool that can reach the bank should be able to delete it.

| Tier | What it allows | Example |
|---|---|---|
| `ro` | Read only | A reviewer agent that must never mutate memory |
| `rw` (default) | Read + write | The coding agent doing the work |
| `full` | Adds destructive operations: delete, sweep, consolidate | A maintenance agent trusted to run sweeps |

<!-- caption: Access tiers, enforced at the tool boundary by MemoryAccessGuard -->

The global default is `rw`; per-project overrides beat it, and a project-specific row beats the `*` wildcard, more specific wins. A nuance: the background file-watcher mirror runs regardless of tier.

## Cloud sync: S3 and Azure Blob

Sync is the correlation point between your machine-wide memory and a project's, and it is off until you turn it on.

`memory_sync` pushes and pulls VACUUM INTO snapshots of the bank's committed contexts (shared plus every project) to one cloud object store. Conflict handling is If-Match, not blind last-writer-wins: a push only lands if the remote has not changed since your last pull, and when it has, the sync re-pulls and re-merges the remote rows and tries again. Workspace scratch never syncs; workspace rows are stripped before the snapshot leaves the bank.

Four authentication modes, two per backend:

| Method | Configure with | Stored in settings | Auth at sync time |
|---|---|---|---|
| S3 access/secret keys | `sync add s3 …` (keys prompted) | keys, encrypted at rest with passphrase | BasicAWSCredentials |
| S3 AWS chain | `sync add s3 … --cli` | non-secret `s3Chain` marker | AWS default credential chain |
| Azure connection string | `sync add azure <container>` (prompted) | connection string, encrypted at rest | BlobServiceClient |
| Azure az CLI | `sync add azure <container> --cli --account <name>` | non-secret account name | DefaultAzureCredential |

<!-- caption: Sync credential modes: prompted secrets vs machine CLI chains -->

Two footguns, both documented and both real: `sync add azure` does not create the container, so create it first or the first sync fails, and `sync show` redacts secrets precisely because the settings table is where they live.

## Configuration and operations

The server reads exactly one environment variable. Everything else goes through the CLI.

`ai-raccoon` is both the server and its own config tool: launch flags start it, verb commands change the bank's settings table, and the running server hot-reloads the rows, so `ai-raccoon model set local` or `ai-raccoon retrieval alpha set 0.4` applies without a restart.

```
ai-raccoon access default set {ro|rw|full}    ai-raccoon access default show
ai-raccoon model set local [path]             ai-raccoon model set openai {model-id} --base-url {url}
ai-raccoon retrieval alpha set {0..1}         ai-raccoon retrieval alpha show
ai-raccoon sweep threshold set {0..1}         ai-raccoon sweep show
ai-raccoon sync add s3 {url} --bucket {name}  ai-raccoon sync add azure {container}
ai-raccoon watch enable {project-id|*} {true|false}
```

Defaults are the boring, sane ones: rw access, no embedding engine until configured (FTS5-only search; `model set local` enables the bundled model), fixed α=0.5, RRF 1:1 with k=60, sweep threshold 0.3 with `shared` exempt, watcher concurrency 4, watchers disabled until enabled. Every MCP tool records OpenTelemetry metrics (`ai_raccoon_tool_invocations`, `ai_raccoon_tool_duration_ms`, ActivitySource `AiRaccoon.MemoryTools`), watchable live with `dotnet-counters`. No OTLP export yet: the instrumentation is local-only, and `project_id` rides as a plaintext tag until that changes.

On tests: the README's "185+" is stale. A `dotnet test` run on 2026-08-05 reports 1,092 test cases, 1,049 passing and 43 skipped (xunit.v3 counts theory data rows individually; a grep of `[Fact]`/`[Theory]` attributes counts 885).

## Design choices worth copying

Four decisions I'd make again, and one I'd measure first.

**Rating with half-life decay and an access-count multiplier.** Every search hit raises an entry's rating (`rating = baseScore × 0.5^(age/halfLife) × (1 + accessCount × multiplier)`), and degradation sweeps remove old, low-rated project entries while `shared` stays exempt. Memory that earns attention survives; memory nobody reads fades. The global sweep-TTL knob is gone; candidates are entries with an explicit per-entry TTL whose rating sits below the threshold and whose age exceeds that TTL. The wrinkle: no shipped tool or CLI verb sets a per-entry TTL yet, so a sweep lists nothing until that path exists.

**Content-hash dedup.** Writes are keyed by content hash, so the same note stored twice is one row. It sounds trivial; it removes an entire class of agent-duplicated-memory noise, and the same hash-keying later fixed the OOM.

**A thin MCP layer over a pure domain.** The tools map parameters and format results; the business logic lives in a domain layer with no infrastructure dependencies, warnings are errors, package versions are centralized. The MCP contract is a facade, not the architecture, which is why the CLI-config refactor could reshape the tool set without touching the core.

**Measurement-first discipline, including the failures.** The harness with pre-registered win rules is why the α machinery died and the FTS regression never shipped. The memory-safety story is the negative lesson: a prototype run OOM-killed my machine at 50 GB, and the fix, keying embeddings by hash and bounding batch sizes, took the same workload to ~0.8 GB. The 50 GB version is the number I remember.

One more caveat: the current release starts with a clean bank. Existing-bank migration is deferred, so "upgrading" means re-seeding, not migrating.

## Verdict: use it if you are tired of re-reading

**Yes, if you:**
- Run multi-agent workflows on a project you care about
- Want memory that is local, scoped per project, and searchable by section
- Prefer a benchmark over a feature list, and trust numbers that admit their own limits

**Skip it if you:**
- Need a hosted, multi-user service; this is a local process with an opt-in localhost HTTP endpoint
- Expect a young ecosystem; one maintainer, no CI secret scanning yet
- Want to migrate an existing memory bank; the current release starts clean

The caveats stand: corpus-conditional numbers from both harnesses, a 7-query primary set, one-maintainer support. The project is [open source (MIT)](https://github.com/Arasz/ai-raccoon), built with TDD against 1,092 test cases (1,049 passing, 43 skipped, 2026-08-05), installs as a [NuGet tool](https://www.nuget.org/packages/ai-raccoon) with `dotnet tool install -g ai-raccoon`, and the full contract is in the [server reference](https://github.com/Arasz/ai-raccoon/blob/main/docs/reference/agent-memory-server.md).

Memory is only useful if the agent finds the right piece of it. That part, I measured.
