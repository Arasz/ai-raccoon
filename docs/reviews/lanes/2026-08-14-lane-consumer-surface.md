# Lane report — consumer surface (MCP tools and CLI)

Campaign: project-scope review, base `1d1889d517baf840df0b839f547091bd7f46808b`.
Model: sonnet · read-only; the built CLI was run to observe behaviour. Lane verified the base SHA.

---

### F1 — A mistyped or unrecognised CLI token silently falls through to launching the MCP proxy/server instead of failing [MEASURED]
**Severity:** HIGH
**Evidence:** ran `dotnet src/AiRaccoon/bin/Release/net10.0/AiRaccoon.dll notacommand` (stdin from
`/dev/null`). Output: `Unrecognized command or argument 'notacommand'.` on stderr, immediately
followed by live `System.Net.Http.HttpClient.BackendSessionClient` request/response logging
(`POST http://127.0.0.1:7721/mcp` → 200), and **`EXITCODE=0`**. Root cause:
`src/AiRaccoon/Setup/Cli/CliArgs.cs:25-29` — when the full-tree parse produces errors and
`ContainsVerb(args)` is false, it silently re-parses with `BuildLaunchRootCommand()` (launch-flags-only
root, no verbs), discarding the unrecognised-token errors from that second parse's result path; and
`AppRunner.Run` (`AppRunner.cs:40-51`) then falls into `RunProxy`/`DirectRunAsync` because
`IsCommandInput` is false.

**Why it matters:** a human who fat-fingers a subcommand (`ai-raccoon wach status`,
`ai-raccoon acess set …`) gets a one-line warning buried before a wall of HTTP proxy logging, then the
process either hangs waiting on stdio protocol frames or silently attaches to whatever backend is
listening on port 7721 — and a script checking `$?` sees **success**. This is the opposite of
"destructive verbs confirm": here a *non-command* silently does something.

**Fix:** when `ContainsVerb(args)` is false but the full-tree parse reported unrecognised-token errors
(as opposed to zero args), return `ExitCode.FailedToParseCliArgs` rather than silently re-parsing as a
bare launch. Reserve the fallback-to-launch path for genuinely verb-less invocations.

---

### F2 — Any MCP-backend autostart failure is undiagnosable: the child process's stdout/stderr is unconditionally discarded [READ + MEASURED]
**Severity:** HIGH
**Evidence:** `src/AiRaccoon/Hosting/Proxy/BackendLauncher.cs:151-152` starts the backend with
`RedirectStandardOutput/Error = true` and immediately hands both pipes to `DrainAsync` (`:156-170`),
which reads and discards every byte ("Discarded: the backend's own output is not the proxy's to
relay"). The only surviving signal is a bare integer exit code, surfaced as `(serve exit N)` in
`BackendSessions.cs:33`.

Reproduced: `dotnet …/AiRaccoon.dll --data-root <fresh> --port 19721 </dev/null` → `EXITCODE=6`,
stderr: `the backend at http://127.0.0.1:19721/mcp did not answer within 30s (serve exit 1)`. Root
cause isolated: `BackendSessions.Executable()` (`BackendSessions.cs:141-143`) uses
`Environment.ProcessPath` to re-invoke itself; when the app runs via `dotnet AiRaccoon.dll`,
`ProcessPath` resolves to the `dotnet` muxer, so the spawned command is literally
`dotnet --data-root <path> --install-scope user serve --port 19721`, which `dotnet` rejects. Confirmed
directly: that command → `Could not execute because the specified command or file was not found.` /
`EXIT=1` — the exact code the proxy reported. This does **not** reproduce through the packaged
apphost: `tests/AiRaccoon.Tests/Integration/Setup/Serve/BackendLauncherTests.cs:40` deliberately
launches `Path.Combine(AppContext.BaseDirectory, "AiRaccoon")`, the native apphost binary — the test
author was aware `ProcessPath` matters and worked around it in the harness without fixing the runtime
path.

**Why it matters:** two problems compound. (a) The self-relaunch is fragile outside the packaged
global-tool apphost — anyone on a framework-dependent build (contributors, CI, a Docker image invoking
`dotnet AiRaccoon.dll`) hits a silent 30-second hang ending in a bare exit code. (b) Even for genuine
unrelated failures (bad passphrase, corrupt bank, port race), the operator gets zero diagnostic text.

**Fix:** capture (do not discard) the spawned backend's stderr up to a bounded byte cap and include its
last lines in the `BackendUnavailable` message alongside the exit code; and resolve the exit-code
number to its `ExitCode` constant name (e.g. `serve exit 1 (FailedToResolveEncryptionKey)`).

---

### F3 — `model set openai --api-key <key>` puts a secret on the command line, contradicting the codebase's own stated rule and its sibling commands' pattern [READ]
**Severity:** MEDIUM
**Evidence:** `src/AiRaccoon/Setup/Cli/CliArgs.cs:11` states in the type's own doc comment: *"Secrets
are never options."* Yet `src/AiRaccoon/Setup/Cli/CliCommandTree.cs:134-138` defines
`new Option<string>("--api-key") { Description = "API key persisted in the settings table" }` on
`model set openai`, and `SettingsCommands.cs:111,115-117` reads it off `ParseResult` and persists it —
no prompt, no env-var fallback. Contrast `src/AiRaccoon/Setup/Cli/Commands/SyncCommands.cs:28,36,114-115`,
which prompts interactively via `streams.ReadLineAsync` for the S3 access/secret key and the Azure
connection string — the same class of secret, handled the safe way in the same file family.

**Why it matters:** a plaintext `--api-key` on argv lands in shell history, is visible via `ps`/`/proc`,
and leaks into scrollback and CI logs. `EncryptionCommands.cs` even documents an env-var fallback
(`BWS_ACCESS_TOKEN`) for its own token option; `model set openai` has neither that nor a prompt.

**Fix:** make `--api-key` optional and prompt via `streams.ReadLineAsync` when omitted, keeping the
option as a documented non-interactive escape hatch.

---

### F4 — `encryption unset` and `encryption bitwarden` rekey the live bank with no confirmation gate [READ]
**Severity:** MEDIUM
**Evidence:** `EncryptionCommands.cs:234-264` (`UnsetAsync`): when a bank exists and an env passphrase
is available, it proceeds straight to `Log.RekeyingBank` → `_bankConnectionFactory.RekeyBankAsync(…)`
with no prompt, no `--force`/`--yes`, no dry-run. `encryption bitwarden`'s own help text says outright
"rekeys the bank" (`CliCommandTree.cs:267-268`). Compare the MCP side, where the same codebase gates an
analogous operation explicitly: `memory_share_extract`'s `autoPromote` requires `confirm=true` or
throws `confirm-required` (`ShareTools.cs:75-79`).

**Fix:** require an explicit `--force` (or interactive y/N confirmation reading the bank path back to
the operator) before `RekeyBankAsync` runs.

---

### F5 — Two live, untested docs report a stale tool/class count against the actual 26 tools / 10 methods / 8 classes [MEASURED]
**Severity:** MEDIUM
**Evidence:** `docs/reference/README.md:9` — *"the MCP server's complete agent-facing contract: 22
tools, 2 prompts…"*. `docs/explanation/architecture.md:596-598` — *"Tools/MemoryTools.cs 9
[McpServerTool] methods, no business logic (22 tools in all, across the seven Tools/*.cs classes)"*.
Measured: `MemoryTools.cs` now declares **10** `[McpServerTool]` methods; there are **8** tool classes
(QualityTools is new since that doc); the total is **26**, verified by
`tests/AiRaccoon.Tests/Unit/Mcp/ToolInventoryTests.cs:33-66`. The two guard tests at
`ToolInventoryTests.cs:124,135` only cover `docs/reference/agent-memory-server.md` (which is accurate —
`## Tools (26)` at `:19`); nothing covers `docs/reference/README.md` or `docs/explanation/architecture.md`.

**Why it matters:** the project's own "derive the list, or delete it" invariant, violated one file over
from where it is actually enforced.

**Fix:** drop the specific counts from those two files' prose (point to the tested table instead) or
extend the existing reflection-based test to cover them.

---

### F6 — `docs/reference/agent-memory-server.md`'s parameter table for `memory_promotion_list` omits `includeFullValue` [READ]
**Severity:** LOW
**Evidence:** `PromotionTools.cs:20-30` — `List(string? projectId, int limit = 50, bool
includeFullValue = false, …)`. `docs/reference/agent-memory-server.md:58` —
`| memory_promotion_list | projectId?, limit=50 | {rows: [PromotionQueueRow]} |`. The two drift tests
check tool *names* and the heading count, not per-row parameter completeness.

**Why it matters:** an agent reading only the reference table will not learn that queue previews can be
expanded to full text, and may resort to a `memory_get` per hash instead of the one flag that does it.

---

### F7 — `memory_search`'s own description and the onboarding prompt never forward-reference `memory_get`, even though search snippets are short by construction [READ]
**Severity:** MEDIUM
**Evidence:** `MemoryTools.cs:89-91` (search's `[Description]`) explains scope semantics only, no
mention of `memory_get`. `MemorySearchResult` carries a `Snippet`, not full content; the FTS5 snippet
is called with a 12-token window (`src/AiRaccoon.Infrastructure/Sqlite/MemorySql.cs:126`:
`snippet(entries_fts, 0, '', '', '…', 12)`). `memory_get`'s description *does* point backward ("as
returned by memory_write or memory_search", `MemoryTools.cs:70-71`), but the reference is
one-directional. The main onboarding document, the `memory-usage-guide` MCP prompt
(`src/AiRaccoon/Prompts/MemoryPrompts.cs:19-32`), walks search → cite → write → promote → sweep →
ingest/sync and **never mentions `memory_get`**.

**Why it matters:** an agent that has only read tool and prompt descriptions has a real chance of never
discovering that full content is one call away — it sees a short snippet, treats it as "the answer",
and under-cites or re-derives. The prior review's blocker was fixed at the tool-existence level and not
at the discoverability level.

**Fix:** one clause in `memory_search`'s description ("results carry a short snippet; call
`memory_get(hash)` for the full entry") and one line in the `memory-usage-guide` prompt.

---

### F8 — `ToolInventoryTests.ToolsNamespace_ExposesAll24SpecTools` asserts 26 under a name that still says 24 [READ]
**Severity:** LOW
**Evidence:** `tests/AiRaccoon.Tests/Unit/Mcp/ToolInventoryTests.cs:33` method name
`ToolsNamespace_ExposesAll24SpecTools`; body at `:39` asserts `tools.Count.ShouldBe(26)`.

---

### F9 — The `@ignore`d "All 17 tools are still listed" scenario's explanatory comment cites a count that has itself gone stale [READ]
**Severity:** LOW
**Evidence:** `docs/work/features-native-memory/native-memory.feature:197-201` — the comment says the
scenario's 17-tool list "is stale against the real 22-tool surface"; the real surface is now **26**. The
same comment (`:199`) points to `src/AiRaccoon/README.md` for "memory_configure is deliberately NOT an
MCP tool"; **that file does not exist** (only a top-level `README.md`).

**Why it matters:** the comment written to document drift has drifted a second time, and its citation is
a dead link.

---

## Still open

- Whether `dotnet tool install -g ai-raccoon`'s installed shim is a native apphost (unaffected by F2's `ProcessPath` bug) — inferred from `RuntimeIdentifiers`+`PackAsTool` packaging convention and the test harness's deliberate use of the apphost binary, but not measured against a real global-tool install.
- Whether an unmapped/genuine exception (the "it failed" case in `ToolRefusals.Filter`, `ToolRefusals.cs:76-102`) reaches the MCP client with more diagnostic text than a mapped refusal, or becomes a generic protocol error — not traced end to end.
- Whether `--quiet` mode suppresses F1's "Unrecognized command" warning entirely, making the silent-fallthrough case even more silent — plausible from `AppRunner.cs:187-200`, not measured.
- Full audit of every CLI subcommand's `--help` text — root, `access`, `access set` were sampled and every verb's registration read; leaf `--help` was not exhaustively invoked.

## Grade mix

MEASURED 3 (F1, F2, F5) · READ 6 (F3, F4, F6, F7, F8, F9) · INFERRED 0 · UNVERIFIED 0.

## Owner questions

1. Is the `dotnet AiRaccoon.dll` (framework-dependent) invocation a supported way to run this server, or purely a dev/test artefact — if the latter, should `BackendSessions.Executable()` fail fast with a clear message instead of mis-launching `dotnet`?
2. Is silently launching the server on any unrecognised token (F1) intentional forward-compatibility, or should unknown tokens always be a hard parse error?
3. Should `model set openai --api-key` move to interactive prompting (matching `sync add`), or is CLI-only intentional for non-interactive provisioning scripts?

## Healthy

- **`ApiEnvelope<T>`** is genuinely uniform across all 26 tools — every tool wraps its payload the same way via `ToolGate.WrapAsync`, with no per-class variance across the eight tool classes.
- **Idempotent no-op reporting is consistent and documented:** `memory_delete`, `memory_delete_context`, `memory_promotion_discard`, `memory_workspace_discard`, `memory_watch_add`/`remove` all report a count (0 on no-op) rather than erroring, and each `[Description]` says so.
- **Honest write outcomes (ADR-0032):** `memory_write` returns `stored`/`reason` rather than lying about a policy refusal (`MemoryTools.cs:38-67`).
- **`ToolRefusals`** is a single derived exception→prefix table with a matching log-level split (`WarningPrefixes`) so expected refusals do not pollute Error-level alerting while genuine faults still do.
- **`ExitCode.cs`** — every distinct CLI failure mode has its own stable documented constant, including the retired-and-not-reused `8` and the split of the old catch-all `10` into `10-16`.
- **The System.CommandLine `--` prefix trap** (options keep the prefix at `GetResult`/`GetValue`, arguments do not) was audited at every call site — `CliArgs.cs:98-99`, `EncryptionCommands.cs:86`, `SettingsCommands.cs:97,110,111`, `ObservabilityRunner.cs:34,93` — all consistent, **no mismatch anywhere**.
- **First-run experience measured clean:** a fresh `--data-root` with no prior bank — `access default show` auto-creates `memory.db` (233 KB, WAL) and returns `rw` with no error; `model show` cleanly reports `provider: (none — FTS5-only search)`.
- **Recognised-verb validation is solid:** `access set myproj badmode` → single-line stderr
  `ai-raccoon: invalid access mode 'badmode' (expected ro, rw or full)`, exit 15 — clean and scriptable, in sharp contrast to F1.
- **CLI help text:** every verb registered in `CliCommandTree.cs` carries a substantive, specific description, and `--help` renders correctly at every level sampled.

## Disconfirmed

- **The brief's flagged `minScore: 0.7` default/normalisation trap is already fixed and correctly documented**, not a live defect. `MemoryTools.cs:100-106` shows the parameter renamed to `minRelativeScore`, defaulting to `0.0` (off), with the description explicitly stating the "not an absolute quality bar" trap and citing ADR-0047. `README.md:34` documents the breaking rename accurately (1.12.0). The mismatch initially observed came from the **live** `mcp__ai-raccoon__memory_search` tool schema in this session, which still advertises `minScore`/0.7/ADR-0006 — a stale running server from an older build, not a defect at this commit. (This independently corroborates the retrieval lane's F6.)
- **The System.CommandLine option-prefix trap** flagged in the brief as something to check was checked exhaustively and **not found** — every option/argument read site already gets it right.
