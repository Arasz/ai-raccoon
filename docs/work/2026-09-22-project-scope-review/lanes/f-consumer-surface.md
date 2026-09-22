# Lane F — Consumer surface: MCP tools & CLI contracts

Worktree: `/Users/arasz/RiderProjects/ai-raccoon/.ai-badger/worktrees/psr-f-consumer-surface` (base `5bca1900`).
Scratch: `docs/work/2026-09-22-project-scope-review/f-consumer-surface/`. Product driven as `ai-raccoon --data-root docs/work/2026-09-22-project-scope-review/f-consumer-surface/<root> …`
(installed `1.42.5+8a1f1dde` == HEAD product code, per GROUND-TRUTH). Scratch servers on explicit free ports
(7831, 7841, 7844–7847, 7861–7863, 7871); **port 7721 was never bound by this run.** 7721 was already held when this run started (PID 88735, started 14:53:59, i.e. before this run's first command) by an orphaned `serve` from an **earlier Lane F run on this same scratch path** — its bank and token had been deleted by this run's fresh-root setup, which is exactly why every no-`--port` settings command here hit a token mismatch. The user's live bank was neither read nor written. All of this run's scratch servers were stopped at the end (verified: no AiRaccoon listener remains, 7721 free).
MCP driven both over HTTP (`POST /mcp` + `X-AiRaccoon-Token`) and through the stdio proxy with live JSON-RPC.

Surface facts established first (context for the findings, not findings):
- Deriving the inventory from source (`[McpServerTool(Name = Tn…)]` + the `Tn*` consts in `src/AiRaccoon/Tools/*.cs`) yields **29 tools**;
  live `tools/list` over HTTP and over the proxy yields the same 29; `docs/reference/agent-memory-server.md` heading `## Tools (29)`
  and its 29 table rows match the registered set exactly (scripted set-diff: doc-only = `[]`, src-only = `[]`).
  `ToolInventoryTests` (6 tests, incl. the doc-heading and doc-table parity pairs) all pass. The surface is honest.
- 2 prompts listed (`memory-usage-guide`, `workspace-consolidation-guide`), matching the spec.
- `serve --port 0` prints the bound URL to stdout; `serve --restart` on a token-less root exits 11, on a foreign listener `serve` exits 3
  with the `--port 0` hint; `serve observability pid` exits 0/4 as documented; `doctor` exits 22 (no bank), 20 (v99 bank), 19 (dropped table),
  24 (open `model_migration` row) — all four measured; the corresponding MCP refusal `model-migration-in-progress: … (memory_write)` was
  reproduced live with `isError: true`; the query-guard refuse tier, unknown-hash, invalid-argument and `invalid-params` refusals all
  reproduce live with the documented prefixes and `isError: true` (the 1.42.0 checklist's "no isError flag over HTTP" does **not** reproduce
  on 1.42.5 — withdrawn as a lead, see Still open).

### F1 — `--quiet` writes `quiet.log` from two processes at once, producing torn, interleaved lines [MEASURED]
**Severity:** MEDIUM
**Evidence:** `docs/work/2026-09-22-project-scope-review/f-consumer-surface/data6/quiet.log:03,05,06` (30 lines, 3 malformed; reproduced in `data3` and `data4`, 3 malformed each); command `ai-raccoon --data-root …/dataN --port 784N --quiet settings retrieval list`; fragments observed: `f-consumer-surface/data6/mcp-token`, `2026-09-22T13:06:12026-09-22T13:06:17.9423060+00:00 [Debug] …`. Paths: `src/AiRaccoon/Hosting/Common/BackendLaunchArguments.cs:53` (the spawned backend inherits `--quiet`), `src/AiRaccoon/Setup/Logging/QuietLogging.cs:30` (one fixed path for every process), `QuietFileLoggerProvider.cs:33-34,61` (per-process lock only; two processes share the append handle).
A `--quiet` CLI invocation auto-starts `--quiet serve`, and **both** processes log to the same `quiet.log` with nothing but an in-process lock, so lines interleave mid-write. The reference makes this file the only diagnostic channel in quiet mode ("leaves no trace on the console — check `quiet.log` first"), and it is also the file the upgrade guidance tells you to read. Smallest fix: one writer per bank (serve owns the file), or an OS-level exclusive lock / per-PID file.

### F2 — the reference says every `serve --restart` failure exits `8`; the live code returns 10–14/16 [MEASURED]
**Severity:** MEDIUM
**Evidence:** `docs/reference/agent-memory-server.md:565` — "Every way the cycle can fail exits `8` with a line naming the port and the manual escape". `src/AiRaccoon/ExitCode.cs:19-23` — "8 is retired rather than narrowed"; live: `ai-raccoon --data-root …/data2 serve --restart --port 7831` → the "holds no token … may serve another data root" line, `EXIT=11` (`RestartNoToken`).
A consumer script written from the reference can never match the documented code, and the retirement's stated purpose ("a script that tested for it fails to match rather than matching the wrong case", ADR-0022) is defeated by the doc naming the retired value. The 10–16 codes all carry one-line XML docs ready to lift into the table.

### F3 — with no memory embedding engine (the fresh-bank default) `memory_search` returns **no** warning, while the code side returns one for the same kind of degradation [MEASURED]
**Severity:** MEDIUM
**Evidence:** `settings model show` on a fresh root → `provider: (none — FTS5-only search)`; on that never-configured bank a semantic-only query ("an antique navigation instrument reflecting evening light in a stargazing room" against one astrolabe/lamplight note) returns `results: 1` with `evidenceByHash … legs [{fts, rank 1}]`, `fusionStats.participatingLegs ["fts"]`, and `warning: (none)`; the identical call with `kind:code`/`kind:both` on the same bank → `warning: "code engine not configured — FTS5-only results; run 'ai-raccoon model code set default' …"`. Code: `SqliteCodeSearchService.cs:55` sets the code warning, `SearchWarnings.Compose` (src/AiRaccoon.Core/Memory/SearchWarnings.cs) composes only query-guard/length, `MemoryTools.cs:231` adds `dispatch.CodeWarning`.
On a fresh install the flagship tool silently answers keyword-only and nothing in the response says so; the agent is instructed (via `initialize`) to relay the code-engine variant verbatim, so the pattern and the fix already exist one corpus over. Smallest fix: the same warning string on the memory leg when `embedding.provider` is unset.

### F4 — `memory_write`'s documented return shape omits `stored`/`reason`, so a refused write looks like success to a doc-driven client [MEASURED]
**Severity:** LOW
**Evidence:** live `memory_write` of a noise-policy body → `{"data":{"hash":"","path":"","context":"","createdAt":1790082461,"stored":false,"reason":"rejected by noise policy 'HermesBackgroundProcessLog'"},"meta":{…}}`, **no `isError`**; the same call with clean content → `stored:true`. Doc row `docs/reference/agent-memory-server.md:43` says the return is `{hash, path, context, createdAt}`; the Error-shapes section (line 993+) says a refusal "comes back as a normal MCP tool error (`CallToolResult.IsError = true`)". Code: `src/AiRaccoon/Tools/MemoryTools.cs:465` (`WriteResult(…, bool Stored = true, string? Reason = null)`).
The tool's own description (the agent's prompt) documents `stored`; the reference does not, and its blanket "refusal ⇒ `isError`" rule points the other way for this one case. A client that checks `isError` and then reads `hash` counts a rejected write as stored (empty hash). One-line fix in the doc row plus a sentence in Error shapes.

### F5 — `model code set default` downloads 194 MB **before** it discovers it cannot reach its settings server [MEASURED]
**Severity:** LOW
**Evidence:** `ai-raccoon --data-root …/data --quiet model code set default` (no `--port`; the only listener on 7721 was the stale scratch server described above, whose token does not match this root — the same shape as a machine whose primary bank owns 7721) → `downloaded faxenoff/code-daemon-embed-v1@main … (4 file(s))` then `the settings server at http://127.0.0.1:7721/mcp refused this credential`, `EXIT=17`; the same command with `--port 7841` → `EXIT=0`, engine activated. `du -sh data/models` = 194M. Order in code: `src/AiRaccoon/Setup/Cli/Commands/SettingsCommands.cs:~238` (`DownloadDefaultCodeModelAsync` first) then `ActivateCodeDirectoryAsync` (settings write through the HTTP server).
`model code set default` is the one command the `initialize` instructions tell every agent to have the user run ("it downloads and activates"). On a machine whose default port 7721 belongs to the user's primary bank — the normal multi-bank case — the user waits for a large download and then gets a credentials error; a re-run skips the download and fails identically until `--port` is added. Nothing is permanently lost (the model is on disk and `model code set local <dir>` activates it), hence LOW. Fix: resolve/print the settings-server target before the download.

### F6 — the only tool-parity BDD scenario is `@ignore`d, names a removed tool and the wrong count, and would **pass** if re-enabled [READ]
**Severity:** LOW
**Evidence:** `docs/work/features-native-memory/native-memory.feature:200-206` (`Scenario: All 17 tools are still listed`, listing `memory_configure`); `docs/work/features-native-memory/native-memory.feature.cs:1756` (`Skip="Ignored"`); `tests/AiRaccoon.Tests/BDD/NativeMemorySteps.cs:903-928` — the `When` step is a no-op and the `Then` step checks **16** names (all present in the live 29), deliberately dropping `memory_configure`. The reference says `memory_configure` "was removed by the CLI-config refactor" (`agent-memory-server.md:37`) and live `tools/list` shows 29.
Dropping the tag would produce a green scenario whose text still asserts a 17-tool surface with a tool that does not exist — a stale spec that *reports success*. `gh issue list --search "tool inventory"` returns no open item for it. Fix: rewrite the scenario text to the real surface (or delete it in favour of the passing `ToolInventoryTests`, which already guard the count against the doc).

### F7 — a merge-conflict marker is committed in the middle of a sentence in the agent-facing reference [MEASURED]
**Severity:** LOW
**Evidence:** `docs/reference/agent-memory-server.md:212` — the line is exactly `>>>>>>> origin/main`, between "…on its own on-demand" (line 211) and "cadence, rather than re-embedding inline itself" (line 213); `git blame` attributes it to `2a55334a`. A repo-wide grep for `^<<<<<<<|^=======$|^>>>>>>>` finds this line and nothing else.
The file is the contract every integrator reads; the paragraph currently reads "on its own on-demand >>>>>>> origin/main cadence", and a future conflict resolution in this file has a decoy marker to key off. Fix: delete line 212.

### F8 — `docs/reference/README.md`'s contents list omits `search-parameters.md` [MEASURED]
**Severity:** LOW
**Evidence:** `ls docs/reference/` includes `search-parameters.md`; `docs/reference/README.md:8-18` lists only `agent-memory-server.md`, `embedding-benchmark.md`, `logging-event-ids.md`, `whats-new-history.md`. The omitted file is the only place the per-call search knobs `sourceLambda`, `consolidationThreshold`, `docScoreFormula` and `candidateWindow` are defined, and `agent-memory-server.md`'s tool table does not list them (`grep -rn "search-parameters" docs/` shows no link from the server reference either).
So four live `memory_search` parameters have a documented contract that is unreachable from both the reference index and the tool table. Fix: one line in the reference README (and ideally a cross-link from the tool row).

### F9 — the embedding how-to calls the bundled local engine "the Default"; a fresh bank has **no** memory engine [MEASURED]
**Severity:** LOW
**Evidence:** `docs/how-to/configure-embedding-engines.md:13` (`Local ONNX Engine (Default)`), `:31` (`| **Local (Default)** | all-MiniLM-L6-v2 (int8) |`), `:39` (`Recipe 1: Use local bundled ONNX model (Default)`); live on a never-touched root: `ai-raccoon --data-root …/fresh --port 7871 --quiet settings model show` → `provider: (none — FTS5-only search)`. The product itself calls no-engine "the default" (`CliCommandTree.cs:228`, `SettingsCommands.cs:283`), and the first-run tutorial's verification step (`docs/tutorials/get-started-with-ai-raccoon.md:105-106`) writes and searches without ever configuring an engine.
A reader is told semantic search works out of the box; it does not, and the tutorial's keyword-overlapping verification query passes anyway. Fix: label recipe 1 "the recommended engine", and add the one-line `model embedding set local` step to the first-run flow (pairs with F3).

### F10 — `doctor` crashes with a raw .NET parameter message on an out-of-range `model_migration.started_at` [MEASURED]
**Severity:** LOW
**Evidence:** scratch bank copy with `UPDATE model_migration SET started_at = 1758538800000 WHERE id = 1` → `ai-raccoon --data-root …/doctor-tests/mig doctor` prints `ai-raccoon: Valid values are between -62135596800 and 253402300799, inclusive. (Parameter 'seconds') Actual value was 1758538800000.` and `EXIT=15`; `src/AiRaccoon/Setup/Cli/Commands/DoctorCommands.cs:317-319` (`DateTimeOffset.FromUnixTimeSeconds(unixSeconds)` under `FormatTimestamp`) is unguarded. The same function deliberately degrades other malformed migration reads to `model migration: unreadable` + `status: HEALTHY, EXIT=0` (observed with a TEXT `started_at`).
`doctor` is the tool an operator runs *because* a bank looks wrong; on corrupted data it replaces the diagnosis with an opaque parameter-range message. Fix: format defensively (or print `started_at` raw) in the same spirit as the guard-tripped arm.

### F11 — the stdio proxy exits 0 with empty stdout when the client closes stdin right after its batch [MEASURED]
**Severity:** LOW
**Evidence:** `printf …initialize…\n…tools/list…\n` piped into `ai-raccoon --data-root …/data --port 7841` → `EXIT 0`, stdout `''`; the same messages with stdin held open (Python `Popen` + `readline()` before EOF) return the initialize result and `tools/list` = 29 tools. Code: `src/AiRaccoon/Hosting/Proxy/ProxyRunner.cs:40-41` (`await server.RunAsync(ctx)`), EOF ends the stdio transport while the HTTP forward is still in flight.
`echo … | ai-raccoon` is the natural first probe for an integrator; it reports success with silence. Interactive MCP clients keep the pipe open, so real clients are unaffected — hence LOW. Fix: drain in-flight responses before honouring EOF, or emit one stderr line on early EOF.

### F12 — project scope splits the bank and the log into `<data-root>/.ai-raccoon/` but leaves the token at `<data-root>/`, unignored and outside the "state" directory [MEASURED]
**Severity:** LOW
**Evidence:** fresh root, `ai-raccoon --data-root …/scope-test --install-scope project --port 7864 --quiet settings retrieval list` → bank `scope-test/.ai-raccoon/memory.db`, log `scope-test/.ai-raccoon/quiet.log`, token **`scope-test/mcp-token`** (0600). Code: `SqliteConnectionFactory.cs:167-176` (`<dataRoot>/.ai-raccoon` for project scope) vs `McpTokenFile.cs:41-44` (`Path.Combine(dataRoot, "mcp-token")`). The repo's `.gitignore` has no `mcp-token` entry, and the README recommends exactly this layout ("Local banks in `~/.ai-raccoon` or `<project>/.ai-raccoon`").
A user who points `--data-root` at a repository gets an unignored secret file in the working tree (one `git add -A` from being committed), and a backup of `<project>/.ai-raccoon/` silently omits the credential — the reference warns about the `.mcp.json` version of this accident but not the token. Fix: mint the token in the bank directory, or document and ignore the path.

## Still open
- **Withdrawn lead**: the 1.42.0 checklist's "refused query over HTTP has no `isError` flag" does **not** reproduce on 1.42.5 — the query-guard refusal, unknown-hash and invalid-argument all came back `isError: true` over raw HTTP. I could not re-run the stdio-proxy variant of that exact observation for every prefix, so the transport-envelope question is answered only for HTTP plus one proxy `tools/call`.
- Two `--install-scope` details I did not resolve: whether `<data-root>/.ai-raccoon/mcp-token` would break `serve --restart`/proxy token handoff if the token were moved (I only proved where each artefact lands), and whether the project scope is ever used with a data root that is not a repo (no evidence either way).
- The busy-port `serve` run also prints a ~30-line fail-level framework stack trace and does a `Bank WAL checkpoint complete` before failing to bind (observed in full, lines 00–39 of stderr); the documented exit 3 + `--port 0` hint do appear, so I left it as an observation rather than a finding — it is Lane E's logging territory and may already be known.
- I did not exercise `memory_sync` against a real remote, `model embedding set openai`, encryption verbs, or the code-engine-unloadable/missing-manifest refusals (they need an external endpoint or a mutated install). The `initialize` instructions/protocol-version contract is not covered by any doc I could find; I measured the strings but did not decide whether that warrants a finding (owner question Q4).
- Inventory/refusal parity was checked against the installed 1.42.5 binary, which GROUND-TRUTH pins as identical to HEAD product code; I did not rebuild the worktree to re-derive it from source at HEAD (the prebuilt test DLL in the worktree was used for the two filtered test runs).
- Grade mix: 11 MEASURED, 1 READ, 0 INFERRED, 0 UNVERIFIED.
**Procedural note (self-reported):** one early command (`ai-raccoon doctor` with no `--data-root`) read the default root (`/Users/arasz/.ai-raccoon`) before I caught it; `doctor` is read-only and opened nothing read-write, and every subsequent command carried an explicit scratch `--data-root`.

## Owner questions
1. **`--quiet` `quiet.log` ownership**: single writer (serve owns it) or an OS-level lock? F1 is a real corruption of the only quiet-mode trace and the fix choice changes who can log.
2. **Memory-engine warning**: should a fresh bank's `kind=memory` search carry the same `warning` the code corpus gets (F3), and if the silence is deliberate, which ADR says so?
3. **`--install-scope project` layout**: token beside the bank (`<data-root>/.ai-raccoon/mcp-token`) or keep `<data-root>/mcp-token` and document + ship an ignore entry (F12)?
4. **MCP `initialize` contract**: the `instructions` string and protocol-version negotiation appear in no reference doc — is that deliberate (client-layer text, not contract) or should `agent-memory-server.md` document them?
5. **Reference exit-code table**: `docs/reference/agent-memory-server.md` names retired exit `8` and has no full exit-code table (F2); should the new 10–16/17–25 codes get one table owned by a test, the way the refusal prefixes do?
