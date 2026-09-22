# Lane G — Product design: the agent-facing experience

Base `5bca1900`, worktree read-only (`git status` clean at finish). Every product run used
`--data-root docs/work/2026-09-22-project-scope-review/g-product-design/data`; the scratch HTTP server ran on port 7811 and
was stopped afterwards. The user's live server on 7721 was never queried for data — it appears only
as the refused-credential target in F4 (one 401, nothing read or written). Method: a real first-run
transcript (README → `--help` → `doctor` → `serve` → `initialize`/`tools/list` → write/search → the
tutorial's exact Step 4), live empty/thin/refused/denied/capability states, ≥6 tool-description vs
live-schema vs docs pairs, and a prior-ruling check per finding. 29 live tools; 2 prompts.

### F1 — A fresh install runs memory search keyword-only, and nothing on the agent-facing path says so [MEASURED]
**Severity:** HIGH
**Evidence:** `ai-raccoon --data-root docs/work/2026-09-22-project-scope-review/g-product-design/data doctor` → `memory engine: not configured — run 'ai-raccoon model embedding set local' to enable semantic memory search`; `memory_search kind=memory` on that bank → `evidenceByHash{…legs:[{legName:"fts",rank:1}]}`, `fusionStats.participatingLegs:["fts"]`, no `warning`. README.md:132 `| **Local (Default)** | Bundled all-MiniLM-L6-v2 (int8) |`; docs/how-to/configure-embedding-engines.md:13,31 the same "Default"; tutorial Step 4 (docs/tutorials/get-started-with-ai-raccoon.md:105-106) never activates an engine; src/AiRaccoon/Setup/McpServerInstructions.cs:14-25 promises "hybrid keyword + semantic search" and warns only about the code corpus.
Running product. A brand-new bank serves FTS-only memory results while the README, the how-to and
the server's own `instructions` all present semantic memory as the default; the only surfaces naming
the state are `doctor` and `settings model show`, neither on the agent path. Cost: the advertised
core capability is silently absent until a CLI step nobody is told to run, and the agent cannot tell
"no vector engine" from a legitimately thin keyword result. Prior-ruling check: "default = no engine"
is intended in docs/work/archive/2026-08-04-cli-config-findings.md:26 and was exercised as a manual
step by the 2026-09-09 checklist (docs/work/checklist/2026-09-09-1.42.0-full.json, item 4); no
ruling keeps it off the onboarding and warning surfaces. Smallest fix: add the activation step to the
Quick Start/tutorial and emit a memory-side warning on search while `embedding.provider` is absent.

### F2 — The only first-run search warning blames the code corpus and prescribes a code-only fix [MEASURED]
**Severity:** MEDIUM
**Evidence:** live `kind=both` search (default) on the fresh bank → `warning: "code engine not configured — FTS5-only results; run 'ai-raccoon model code set default' to download and activate the default code embedding model"`; src/AiRaccoon.Core/Memory/Code/CodeSearchWarnings.cs:15-16; docs/reference/agent-memory-server.md:1028 ("kind=memory is unaffected, since the memory and code engines are independent settings rows").
Running product. On the fresh bank the memory leg is FTS-only *as well*, yet the warning's
"FTS5-only results" reads as the whole response and the only remedy it names fixes the secondary
corpus. The memory leg carries no analogous warning (F1), so the one signal a caller sees points
away from the actual gap. Cost: an agent following the server instruction to relay the remedy
verbatim tells its human the problem is code-only and leaves the primary corpus keyword-only. Prior-
ruling check: the code warning is specified in docs/work/2026-08-21-code-search-implementation-plan.md
§3.6; no ruling found on the phrase's scope. Smallest fix: say "the code section is FTS5-only" in the
warning string.

### F3 — The tutorial's final verification call fails on the current version [MEASURED]
**Severity:** MEDIUM
**Evidence:** the tutorial's exact Step 4 call, `memory_search {"projectId":"get-started","query":"install verification"}`, → `invalid-argument: The arguments dictionary is missing a value for the required parameter 'sessionId'. (Parameter 'arguments')`; docs/tutorials/get-started-with-ai-raccoon.md:106; `sessionId` became required in 1.38.0 (README.md:37, commit 5f76197f 2026-09-03) and the tutorial was edited after that (aa4eb110, 2026-09-09) without it.
Running product. The documented install-verification step cannot succeed as written, and the
tutorial's own failure advice ("If it comes back empty, re-check Step 3's `.mcp.json`") sends the
reader to debug the connection instead of adding a parameter. Cost: the first-run success criterion
is unreachable as documented. Prior-ruling check: none found beyond the README breaking-change line;
the tutorial records no update task. Smallest fix: add `sessionId` to the Step 4 call.

### F4 — With another bank's server on 7721, the documented activation command exits 17 with no remedy named [MEASURED]
**Severity:** MEDIUM
**Evidence:** `ai-raccoon --data-root docs/work/2026-09-22-project-scope-review/g-product-design/data model embedding set local` → `ai-raccoon: the settings server at http://127.0.0.1:7721/mcp refused this credential — it may serve another data root`, exit 17; the same command with `--port 7811` → `embedding engine set to local (bundled ONNX model); re-embedding in the background`, exit 0. `lsof -iTCP:7721` showed the user's AiRaccoon process holding the port; src/AiRaccoon/Settings/ServerSettingsStore.cs:231, src/AiRaccoon/Settings/CliSettingsBackend.cs:44-75.
Running product. Settings verbs always acquire the default port unless `--port` precedes the verb, so
any second bank (another project, a reviewer's scratch root) hits a credential refusal whose text
names what happened but not what to do. Cost: the first-run activation step of F1 is blocked in a
common multi-bank setup and the fix is in neither the message nor the tutorial. Prior-ruling check:
the manual-checklist skill's "Never bind the default port" rule (why maintainers always pass `--port`)
is the closest thing found; no doc pairs `--data-root` with `--port` for settings verbs. Smallest
fix: append "pass `--port <n>` to reach this data root's server" to the refusal.

### F5 — The canonical agent contract reference has a committed merge marker [MEASURED]
**Severity:** LOW
**Evidence:** `git show HEAD:docs/reference/agent-memory-server.md | grep -n '>>>>>>>'` → `212:>>>>>>> origin/main`; introduced by 2a55334a (2026-08-25, PR #583); flagged as OPS-17 SHOULD in docs/work/2026-08-26-doctor-parity-moe-r3-ops-review.md:326.
Read at HEAD, not a runtime state. The marker sits inside the code-engine paragraph that the F2
warning quotes, in the doc docs/reference/README.md:8 calls "the MCP server's complete agent-facing
contract". Cost: the primary mid-task document looks untrustworthy and the tracked fix has missed
four weeks of releases. Prior-ruling check: the 2026-08-26 ops review already prescribed fixing it in
its own commit. Smallest fix: delete the line.

### F6 — A query with no lexical or semantic overlap returns every entry as a confident top hit [MEASURED]
**Severity:** MEDIUM
**Evidence:** live `memory_search kind=memory` for "quantum chromodynamics lattice gauge" against 4 unrelated entries → 4/4 returned, top `ranking: 1`, `warning: null`, `fusionStats{topMargin:0.0161, maxPossible:0.0164, participatingLegs:["vector"]}`, top `fusionStrength: 1.0`, cosines 0.028/0.022/−0.016/−0.034; a genuinely matching query on the same bank measured `topMargin: 0.508`.
Running product. There is no absolute relevance floor (the 0.6 `minRelativeScore` is relative to the
top hit), so a nonsense query normalizes to `ranking: 1.0`; the Stage-1 signals that decode the state
are optional fields, and `fusionStrength: 1.0` (single vector leg at rank 1) points the opposite way
from the flat margin. Cost: an agent that does not inspect `fusionStats` reports relevant memories
for a query that matched nothing. Prior-ruling check: signals-only without verdicts is deliberate
(docs/plans/2026-09-03-search-signal-preservation-plan.md §3 and G7); no ruling covers whether the
flat-margin single-leg state should carry a `warning`. Smallest fix: warn when
`participatingLegs.length == 1` and `topMargin` is below a small threshold.

### F7 — The refusals that gate the degradation lifecycle name no remedy, while sibling refusals do [MEASURED]
**Severity:** MEDIUM
**Evidence:** live on a default (`rw`) bank — `memory_set_ttl`/`memory_sweep{dryRun:false}`/`memory_delete` → `access-denied: <tool> requires mode full (current rw)`; `memory_ingest_file` → `path-outside-scope: Path '…' is outside the ingest scope.`; `memory_watch_add` → `watching-disabled: Watching is disabled for project 'lane-g'.` Contrast the sibling refusals: `invalid-params: projectId is required (… pass projectId explicitly, or register this directory with memory_watch_add / settings ingest scope add)` and `… unless allProjects=true; pass projectId … or allProjects=true …`. src/AiRaccoon.Core/Access/AccessModePolicy.cs:26 (default `Rw`); src/AiRaccoon/Access/MemoryAccessGuard.cs (message has tool + modes only).
Running product. On a default bank the whole TTL/sweep/delete surface refuses, and the message names
neither the setting nor the CLI verb that changes it; the same is true of the scope and watch
refusals. Cost: an agent cannot tell its human what to run, on exactly the tier where the lifecycle
is unreachable by default. Prior-ruling check: the three-tier access model is deliberate (FR-NM-2;
docs/reference/agent-memory-server.md:430-442) but no ruling was found on the refusal text. Smallest
fix: append the `settings access` remedy to the access-denied message (and the CLI pointer to the
other two).

### F8 — The explanation of deferred embeddings misstates fresh-bank search and names retired commands [MEASURED]
**Severity:** LOW
**Evidence:** docs/explanation/agent-memory-architecture.md:101-102 — "search only returns embedded content, so a fresh bank needs an engine configured via the CLI (`ai-raccoon model set local` or `model set openai …`) plus `memory_embed_pending` to become searchable". Live: a bank with `pending: 1` and no engine returned the pending entry through FTS; `ai-raccoon model set local` → `Unrecognized command or argument 'set'` (exit 15), and the 1.35.0 rename to `model embedding set` is recorded at README.md:38.
Running product. The one page that explains deferred embeddings tells a reader the bank is not
searchable before the engine and pending drain — false for keyword search — and points at commands
that no longer parse. Cost: a reader debugging "why didn't my write come back" is sent the wrong way
and then to a CLI error. Prior-ruling check: the current verbs are documented at
docs/reference/agent-memory-server.md:178,730; this page was not updated by the rename. Smallest fix:
correct the sentence and the two command names.

### F9 — `memory_performance` hand-lists 6 search phases while the live report returns 10 plus 3 fusion series [MEASURED]
**Severity:** LOW
**Evidence:** live call → 42 series, including `search.open`, `search.embed`, `search.adjustment`, `search.total` and `search.fusion.top_strength/legs_fired/top_margin`; src/AiRaccoon/Tools/PerformanceTools.cs:17 names only `search.fts, search.vector, search.fusion, search.affinity, search.snippets, search.bump`; the derived lists are `SearchResults.SeriesNames` (src/AiRaccoon.Core/Memory/SearchResults.cs) and `FusionStats.MetricNames` (src/AiRaccoon.Core/Memory/Fusion/FusionStats.cs).
Running product and read the source. The description also promises "a tool or phase never recorded
still appears, at count 0", which makes its six-name list read as exhaustive. Cost: an agent mapping
the response finds seven unexplained series, and this is the project's own derive-or-delete-the-list
invariant applied to a tool description. Prior-ruling check: none; no test pins the description
string. Smallest fix: drop the enumeration or quote the derived constants.

### F10 — `memory_stats` says it reports "the bank's committed contexts" but reports only the project's [MEASURED]
**Severity:** NIT
**Evidence:** src/AiRaccoon/Tools/MemoryTools.cs:361; live `memory_stats {"projectId":"lane-g"}` → `contexts:["project:lane-g"]`, and for a project with no rows → `contexts:[]` although the bank holds another project's entries; docs/explanation/agent-memory-architecture.md:94 states stats count only the caller's project context.
Running product. The wording over-claims scope in the direction of cross-project visibility; there
is no functional leak. Smallest fix: say "this project's committed contexts".

### F11 — `memory_sweep` documents a `dry_run` parameter that does not exist, and the spelling is silently ignored [MEASURED]
**Severity:** NIT
**Evidence:** src/AiRaccoon/Tools/SweepTools.cs:25 "(dry_run, default)"; live schema property is `dryRun`; live call `{"projectId":"lane-g","dry_run":false}` returned the dry-run shape (`candidates:[], deleted:[]`) with no error.
Running product. A caller copying the description's spelling gets a listing while asking for a
deletion; the fail direction is safe but silent. Smallest fix: rename to `dryRun` in the description.

### F12 — README's test count is ~1,500 below this base's discovery count [READ]
**Severity:** NIT
**Evidence:** README.md:160 `# xunit.v3 test suite (~3700 tests)`; docs/work/2026-09-22-project-scope-review/GROUND-TRUTH.md Phase-0 measurement: `dotnet test --list-tests` → `Discovered 5210 tests.`
Read, not re-measured: the full suite was in flight and the review's rules forbid re-running it, so
the 5210 figure is this review's own Phase-0 number. Cost: a credibility nit on the README's
architecture map; nothing else depends on it.

## Still open

- I did not run the full suite or a build in this worktree (review ground rules); build/test status
  is taken from GROUND-TRUTH.
- I did not activate the **code** engine (`model code set default`, a 187 MB download), so the
  post-activation code-search journey and the two-corpus warning state are read from the plan and
  docs, not seen.
- Busy-bank, migration-in-progress, and access-mode `ro` states were not exercised live; only the
  default `rw` denials were.
- `memory_share_extract` propose returned all-empty for a 1-entry bank and I did not determine
  whether worthiness scoring or rating excluded it — Lane C owns promotion eligibility.
- `chunkIndex: -1, totalChunks: 0` appears for entries written without `sourceFile`; the sentinel's
  documented meaning was not found, so I filed nothing.
- Stage-1 arithmetic verified incidentally and matches the plan: `maxPossible` measured 1/61 for one
  leg and 2/61 for two (k=60, w=1), and `ranking` values are the `(k+1)/(k+rank)` normalization —
  Lane B owns the deeper ranking checks.
- F12 is the only READ finding; the README count may legitimately mean something narrower than
  discovered tests, so I did not raise its severity.

Grade mix: 12 findings — 11 MEASURED, 1 READ, 0 INFERRED, 0 UNVERIFIED.

## Owner questions

- Should the first-run path (Quick Start/tutorial/server instructions, or a `memory_search` warning)
  name memory-engine activation, or are `doctor` and `settings model show` the intended discovery
  surface?
- Should the code-corpus warning say "the code section is FTS5-only", given both corpora are
  keyword-only on a fresh bank?
- Should a flat-margin single-leg response carry a `warning`, or is signals-only final until the
  Stage-2 relevance map ships?
- Should `access-denied` (and `path-outside-scope` / `watching-disabled`) repeat the CLI remedy the
  sibling refusals already give?
- Is the explanation's "search only returns embedded content" sentence to be corrected, or does it
  mean something narrower than it reads?
- Should a docs-lint or gate reject committed merge markers in reference documents, so OPS-17 cannot
  recur as a silent four-week miss?
