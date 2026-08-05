# Watch List Output Format — Refactor Plan

**Task:** `watch-list-format`
**Project:** AiRaccoon — C# .NET 10 MCP server over sqlite-memory
**Worktree:** `.ai-badger/worktrees/watch-list-format` (branch `task/watch-list-format`) — all work happens here
**Date:** 2026-08-05
**Type:** display-only refactor of the `ai-raccoon watch list` CLI output, extended 2026-08-05 with four post-brief requirements: (1) a self-explanatory header (the current `CLAUDE.md:` prefix reads as a bare filename), (2) watch-family command descriptions that state they CONFIGURE watching and never register watches, (3) a new read-only `watch registered` command for the registration table, (4) a Spectre.Console evaluation for CLI rendering (decision: reject, §6). Storage format, settings keys, and the MCP `memory_watch_status` surface do NOT change.

> **Revision note (2026-08-05):** requirements 1–4 below fold into the original WP structure: WP1/WP2 keep their shape with the new labeled format; a new **WP3** (descriptions + `watch registered`) is inserted before the former WP3 (`watch remove`), which becomes **WP4**; former WP4 (docs) becomes **WP5**.

---

## 1. Goal

Replace the current single dense line per target —

```
CLAUDE.md: enabled=true concurrency=4 scope=["/Users/arasz/RiderProjects"]
global: enabled=false concurrency=4 scope=["/Users/arasz/RiderProjects"]
```

— with a human-readable block format whose first part is self-explanatory, extracted into a pure, byte-pinned formatter; delete the duplicated effective-value resolution in `WatchListAsync` by consuming the already-tested `WatchConfig.Resolve`; decide what (if anything) the CLI does about shell-glob-polluted targets (a `CLAUDE.md`-named row written by an unquoted `*`). On top of the original brief:

1. **Self-explanatory header (requirement 1):** `CLAUDE.md:` does not say what `CLAUDE.md` is. The new format labels it (`target: CLAUDE.md …`) and labels the scalar fields (`enabled:`, `concurrency:`, `scope:`). The meaning of `enabled` is stated plainly in the format section, the `watch list` description, and the docs: *watching enabled for this target (config) — not a registered watch*.
2. **Description clarity (requirement 2):** the `watch` family descriptions say they CONFIGURE watching (enable/scope/concurrency) and do NOT register watches (that is the `memory_watch_add` MCP tool). The `memory_watch_add` description gains a pointer to the CLI config channel. The memory-usage prompt needs NO change (it already separates the channels, §2.9).
3. **`watch registered` (requirement 3):** a separate read-only command listing the actual mirror registrations (the persisted `watches` table — what `memory_watch_status` reports over MCP, minus the runtime-only fields), distinct from `watch list` (config). Justified against the CLI-only-config ruling in §3.3.
4. **Spectre.Console evaluation (requirement 4):** decision section §6 — rejected for this refactor; pure formatter stays in Core; a future verb-output workstream is the place to revisit.

The CLI is user-facing (agents use the MCP `memory_watch_status` surface instead), so human-friendliness is the priority; the settings rows and `WatchScopeList.ToJson` are display-only-untouched.

## 2. Structure review (read, not guessed)

All facts below are from `src/AiRaccoon/Setup/ConfigCommands.cs` (624 lines, private static verb handlers), `src/AiRaccoon/Setup/CliArgs.cs`, `src/AiRaccoon.Core/Watch/*`, `src/AiRaccoon.Infrastructure/Watch/*`, `src/AiRaccoon/Tools/WatchTools.cs`, `src/AiRaccoon/Prompts/MemoryPrompts.cs`, `tests/AiRaccoon.Tests/Unit/Setup/ConfigCommandsWatchTests.cs`, `tests/AiRaccoon.Tests/BDD/FileWatcherSteps.cs` in this worktree.

1. **`WatchListAsync` (ConfigCommands.cs L567–597) does three jobs inline:**
   - target discovery: `GetSettingsByPrefixAsync("watch.")` → `SortedSet<string>(StringComparer.Ordinal) { "global" }` (L570–579);
   - effective-value resolution: `enabled = project ?? global ?? "false"` (raw string), `concurrency = parse(project ?? global) ?? 4`, `scope = WatchScopeList.Parse(project ?? global)` (L583–592);
   - rendering: one interpolation `$"{target}: enabled={enabled} concurrency={concurrency} scope={WatchScopeList.ToJson(scope)}"` (L593).
2. **The resolution is duplicated — and Core already has the tested version.** `WatchConfig.Resolve(projectId, Func<string, string?> settings)` (`src/AiRaccoon.Core/Watch/WatchConfig.cs` L12–27) does exactly `project ?? global ?? default` for all three fields, with 14 unit tests in `tests/AiRaccoon.Tests/Unit/Watch/WatchConfigTests.cs` (project-beats-global, unparseable→default, `"[]"` project scope beats global, case-insensitive bool). `WatchListAsync` re-implements it with hand-rolled chains instead of calling it. Bonus identity that makes the swap trivial: `WatchConfigKeys.EnabledProject("global") == "watch.enabled.global" == EnabledGlobal` (same for scope/concurrency), so `Resolve(target, …)` is correct even for the `global` row itself — no special case.
3. **Ordering is ordinal, and "global" is *seeded, not first*.** The `SortedSet` guarantees `global` is always listed (even with zero rows), but ordinal placement puts `CLAUDE.md` before `global` (`'C'` 0x43 < `'g'` 0x67) — exactly what the user's real-run sample shows. The plan pins this behavior; it does not special-case "global first".
4. **The other four watch handlers duplicate the key-selection idiom** `target == "*" ? XGlobal : XProject(target)` (L489, 508, 521, 541, 561) — that is target semantics, not effective-value resolution, and it stays as-is. Note `watch scope list` (L537–548) and `watch concurrency` read only the target's own row with **no global fallback** — inconsistent with `watch list`'s resolution, but changing them is a behavior change beyond display; out of scope (recorded in §9).
5. **Stdout discipline is already guarded.** Verb results go to the `stdout` `TextWriter` passed into `ConfigCommands.RunAsync`; `CliArgs.Render` (help/parse errors/version) writes only to the stderr writer — `tests/AiRaccoon.Tests/Unit/Setup/CliOutputRoutingTests.cs` pins this (including "never writes to real stdout"). `RunAsync`'s catch writes `ai-raccoon: {message}` to stderr (L62). The refactor must keep `WatchListAsync` on the stdout channel and introduce no stderr writes. (The one existing stderr hint — `WatchSetEnabledAsync`'s empty-scope warning, L492–497 — is untouched.)
6. **Existing tests pin the old format.** Three tests in `ConfigCommandsWatchTests` assert the current line format (L233–281): `WatchList_ShowsResolvedValues_PerTarget` and `WatchList_ProjectRow_WinsOverGlobal` use `ShouldContain` on `"global: enabled=true concurrency=4 scope=[]"`-style lines; `WatchList_NoRows_PrintsOnlyGlobalDefaults` uses `stdout.Trim().ShouldBe("global: enabled=false concurrency=4 scope=[]")`. These three must be updated (format change); the file's other 15 tests (enable/disable/scope/concurrency, asserting settings rows and exit codes) stay green unmodified. **No BDD step pins `watch list` output** — the file-watcher feature's CLI-rule scenarios (file-watcher.feature L44–76) assert settings effects and the add-a-scope warning, never the list format.
7. **Pollution is a one-row ghost.** The sample's `CLAUDE.md` row shows `scope=["/Users/arasz/RiderProjects"]` — the *global* scope via fallback — consistent with exactly one polluted row: `watch.enabled.CLAUDE.md = "true"` written by an unquoted `watch enable * true` (shell expanded `*` to file names). Existing cleanup does **not** fully work: `watch disable CLAUDE.md` writes `enabled=false` but the row — and its `watch list` entry — persists forever; `watch scope remove` only clears scope rows. Making `watch disable` delete the row would be semantically wrong (project row deleted → falls back to global, which may be `true`). There is no verb that deletes a target's rows (WP4 adds one).
8. **No test pins the command descriptions (verified).** `CliArgsTests` asserts command paths only (e.g. `Parse_WatchList_ParsesCommandPath`); `CliOutputRoutingTests` pins the routing contract, the `"Usage"` marker, and the System.CommandLine 2.0.10 parse-error template — not description strings; the MCP inventory tests (`ToolInventoryTests`, `WatchToolsInventoryTests`) pin tool NAMES only. **Every description edit in WP3 is therefore test-neutral** — no existing test needs updating for them. (Deliberately: SCL help layout is 2.0.10-pinned and drifts on upgrade, per the CLI-args pitfalls — we do not add new pins on help text.)
9. **The memory-usage prompt already separates the channels.** `MemoryPrompts.cs` L23: "watching must be enabled with the path inside the scope allowlist — one-time per install via the CLI: `ai-raccoon watch scope add …` and `ai-raccoon watch enable …`. Then register the path with memory_watch_add …". `MemoryPromptsTests.MemoryUsageGuide_TeachesWatchUsage` pins the fragments `watch scope add`, `memory_watch_add`, `memory_watch_status` (substring asserts). **No prompt change needed**; if the line is ever edited, keep those fragments.
10. **Live watch state is runtime-only and NOT observable from the CLI.** `WatchStatus` (`Core/Watch/WatchState.cs`): `ProjectId, Path, State (scanning/healthy/retrying/stopped), LastError, LastSync` — doc comment: "runtime-only, not persisted". `WatchService.StatusAsync(projectId)` merges persisted registrations (`IWatchStore.ListWatchesAsync()` filtered per project) with the in-memory `WatchPipeline.GetStatuses(projectId)`; a registration with no runtime status renders as `Scanning` (WatchService.cs L46–64). The CLI is a one-shot process (`Program.cs` L18–29 runs the verb and exits; no pipeline, no IPC to a running server), so **State/LastError/LastSync cannot be truthfully reported by `watch registered`** — it reports the persisted registration facts only (§3.2). The MCP tool, which runs inside the server process next to the pipeline, stays the only live-state surface.
11. **The registration seam already exists — no new service method.** `IWatchStore.ListWatchesAsync()` (`Infrastructure/Watch/WatchStore.cs`) returns every `WatchRegistration(ProjectId, Path, CreatedAt, LastChangeTs)` across all projects; `WatchStore` implements it over the `watches` table. `FakeWatchStore` (`tests/AiRaccoon.Tests/Unit/Watch/WatchTestFakes.cs`, internal, in-memory, with call counters) is the established test double. `FileWatcherFeatureContext` exposes the real `WatchStore` for BDD wiring.
12. **The MCP status contract is BDD-covered and unchanged.** file-watcher.feature's status-rule scenarios drive `WatchTools.Status` (FileWatcherSteps ~L695, L1018–1117: scanning, retrying, stopped, healthy, error fields) — that surface is untouched by this plan; the CLI view gets its own coverage (WP3).

## 3. Target format (decision + justification)

### 3.1 Format: labeled header, one scope path per line

The user's complaint: *"it is not clear what the first part means"* — `CLAUDE.md:` reads as a bare filename, not a target. The first part gets an explicit label, and the two scalar fields move to the same labeled, colon style so the whole header reads as config fields, not `key=value` soup:

```
target: CLAUDE.md  enabled: true  concurrency: 4  scope:
  /Users/arasz/RiderProjects
target: acme  enabled: true  concurrency: 8  scope:
  /a
  /b
target: global  enabled: false  concurrency: 4  scope: (none)
```

Rules:
- **Header** `target: {target}  enabled: {enabled}  concurrency: {concurrency}  scope:` — the target is always labeled `target:`; `enabled`/`concurrency` render their canonical values (`true`/`false` via `WatchConfig.Enabled`, `4` via the resolved int); fields are separated by two spaces.
- **Scope entries** each on their own line, two-space indent, in stored order (already ordinal-sorted by `WatchScopeList.Add`).
- **Empty scope** renders `(none)` inline on the header — no dangling `scope:`.
- **No blank lines between targets** — the indent carries the block structure.
- **Ordering unchanged**: ordinal by target name; `global` seeded but not forced first (§2.3).

**What `enabled` means here — stated plainly:** `enabled: true` = *watching is enabled for this target in config* — the gate that `memory_watch_add` checks before registering. It is **not** a registered watch. The two are different surfaces: this output is the *configuration* view; the *registration* view is `watch registered` (§3.2) or, for agents, `memory_watch_status`. This sentence is the doc/description contract for the header; it appears verbatim-ish in WP3's `watch list` description and WP5's docs.

Justification vs. the alternatives considered:
- `target:` label beats `[CLAUDE.md]` brackets: brackets signal an INI-style section convention the reader must know; `target:` is a plain label that reads without convention, and it survives odd target names (dots, uppercase — `CLAUDE.md`) without ambiguity. Rejected: `[target]`.
- An em-dash prose header (`CLAUDE.md — watch config:`) reads nicely but is prose, not fields; a human can scan it, but it does not label the *fields* and is weaker for byte-pinning clarity. Rejected.
- A full table (columns target/enabled/concurrency/scope) was already rejected in the original brief: absolute paths are unbounded, so aligned columns are fragile — the last column absorbs arbitrary width and padding logic would itself need testing. The labeled header keeps the zero-layout-logic property (§3 justification of the original plan still holds).
- `key=value` (`enabled=true`) on the header was the old style; the colon style (`enabled: true`) is consistent with the new `target:`/`scope:` labels and reads as a config field list, which is exactly what the block is.
- One path per line matches the sibling `watch scope list` output and fixes the quoted-vs-unquoted inconsistency (raw JSON `["/a"]` → plain `/a`); `(none)` follows existing conventions in this file: `provider: (none — FTS5-only search)` (L209), `(unset)` (L214–216).
- Zero layout logic: the formatter is header + optional indented lines; byte-pinnable in tests. No parsing contract to preserve: agents use `memory_watch_status` (JSON) — nothing parses the CLI text.

### 3.2 New read view: `watch registered` (requirement 3)

**Name:** `watch registered` — *not* `watch status`.

Justification:
- `watch status` would promise live state (`scanning`/`healthy`/`retrying`/`stopped`, `lastError`, `lastSync`) that a one-shot CLI process cannot observe (§2.10): those fields live in `WatchPipeline`'s memory inside the MCP server process, and there is no IPC between the CLI verb process and a running server. A `watch status` that printed `scanning` for everything (the empty-runtime fallback) would be actively misleading.
- `registered` names exactly what the command shows: the persisted registration rows (`IWatchStore.ListWatchesAsync()`), and pairs with `watch list` as the two read views — *configuration* vs *registrations*.
- `memory_watch_status` keeps its name and role: the only live-state surface, used by agents.

**Argument shape:** optional `{project-id}` positional (`HelpName = "project-id"`, `Arity = ZeroOrOne`); no argument = every project. The all-projects view is the CLI's differentiator over the per-project MCP tool; the optional filter mirrors the sibling `watch scope list {target}` shape. User-run commands get no access-tier checks (same as every CLI verb, `ConfigCommands` doc comment).

**Output shape** (one line per watch, byte-pinned):

```
project: proj-a  path: /Users/arasz/ws/repo  registered: 2023-11-14T22:13:20Z  lastChange: never
project: proj-a  path: /Users/arasz/ws/repo/docs  registered: 2023-11-14T22:13:20Z  lastChange: 2023-11-15T08:00:00Z
```

- Fields: `project:` (ProjectId), `path:` (normalized watch path), `registered:` (CreatedAt → UTC ISO-8601, `DateTimeOffset.FromUnixTimeSeconds(...).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)`), `lastChange:` (LastChangeTs, the catch-up watermark; `0` = never synced → `never`).
- Ordering: `OrderBy(ProjectId, StringComparer.Ordinal).ThenBy(Path, WatchPath.PathComparer)` — mirrors the per-project path ordering in `WatchService.StatusAsync` (L51).
- Empty result: `no registered watches` (follows the `sync not configured` convention in `SyncShowAsync`, L450).
- The MCP `WatchStatus` fields `State`/`LastError`/`LastSync` are intentionally absent — runtime-only (§2.10); the plan says so in the command description and docs so no user expects them.

**Service seam:** no new service method. The handler calls the existing `IWatchStore.ListWatchesAsync()`; the only new production code is the CLI handler + wiring (WP3). `ConfigCommands.RunAsync` gains a trailing optional parameter `IWatchStore? watchStore = null` (precedent: the encryption work added `bank`, `bws`, `env` the same way; all existing call sites compile unchanged). `Program.cs` passes `watchStore: new WatchStore(bank)` (plus `using AiRaccoon.Infrastructure.Watch;`). The handler guards the dependency with `Guard.IsNotNull(watchStore)` (CommunityToolkit.Diagnostics, already referenced) so a mis-wired call fails loudly at the boundary.

**Consistency with the CLI-only-config ruling** (ai-raccoon-pitfalls, "Config-change MCP tools REMOVED" + "Watch CONFIG is CLI-only"): the ruling bans *config writes* outside the CLI — `memory_configure`/`memory_set_structure_alpha` were deleted and env/args config channels removed; it explicitly keeps "tools as operations" (`memory_sync`/`memory_sweep`/`memory_embed_pending` stay) and the CLI already has read verbs (`watch list`, `access list`, `model show`, `sync show`, `encryption show`). A CLI read-only view of registrations is a read operation, not a config channel: it creates **no second write surface** (registrations are still created only via `memory_watch_add`, which remains rw-gated), and it changes nothing about how the server reads config. It IS a new surface (registrations were CLI-invisible, MCP-read-only), so the plan flags it in the PR description per the "new surface — justify it" rule; justification lives in this paragraph and §2.10.

### 3.3 Command descriptions (requirement 2) — exact new texts

`CliArgs.WatchCommand()` (L269–289) today → proposed:

| Command | Today | Proposed |
|---|---|---|
| `watch` | "File-watcher configuration (CLI-only channel)" | "Watch configuration (CLI-only channel): enable/disable, scope allowlist and concurrency per target. This family CONFIGURES watching — it does not register watches; registrations are created by agents via the memory_watch_add MCP tool." |
| `watch enable` | "Enables or disables watching for a target" | "Enables or disables watching for a target (configuration only — does not register a watch; use memory_watch_add to register)" |
| `watch disable` | "Alias for enable … false" | unchanged |
| `watch scope` | "Scope allowlist (absolute paths, covers dir + subdirs)" | "Scope allowlist (absolute paths, covers dir + subdirs) — the paths a registered watch must sit under" |
| `watch scope add` / `remove` / `list` | "Adds a scope path (normalized absolute, deduped, re-sorted)" / "Removes a scope path" / "Lists a target's scope allowlist" | unchanged |
| `watch concurrency` | "Sets the watcher concurrency (1..16, default 4)" | unchanged |
| `watch list` | "Lists every target's enabled flag, concurrency and scopes" | "Lists each target's watch CONFIGURATION (enabled, concurrency, scope) — not registered watches; use 'watch registered' for those" |
| `watch registered` (new) | — | "Lists every REGISTERED watch (project, path, registered at, last change) from the watches table. Registrations are created via memory_watch_add; live state (scanning/healthy/…) is reported by memory_watch_status, not the CLI." |

**MCP side — `memory_watch_add` description** (`Tools/WatchTools.cs` L35–36): add the config-channel pointer, because agents hitting `watching-disabled`/`path-outside-scope` need to know the config channel is the CLI:

> "Registers a file or directory to be mirrored into the project's memory. Watching must be enabled and the path inside the scope allowlist — both configured via the CLI ('ai-raccoon watch enable' / 'watch scope add'). Already-watched paths are a no-op. Returns immediately — the initial scan runs in the background (status reports scanning)."

**Memory-usage prompt (`MemoryPrompts.cs`):** no change (§2.9 already separates CLI config from `memory_watch_add` registration).

**Tests pinning these strings — named:** none. `CliArgsTests` pins parse paths only; `CliOutputRoutingTests` pins routing + `"Usage"` + the SCL 2.0.10 parse-error template; `ToolInventoryTests`/`WatchToolsInventoryTests` pin tool names only; `MemoryPromptsTests` pins prompt fragments (unchanged, prompt unchanged); BDD steps pin no help text. WP3 adds exactly one new parse test for `watch registered` (§5). No description-related test updates are required or added.

## 4. Hot points and the seams around them

| # | Hot point | Today | Seam after refactor |
|---|---|---|---|
| (a) | Effective-value resolution (`project ?? global ?? default`) | Inline chains in `WatchListAsync` L583–592; canonical version exists untested-by-CLI in `WatchConfig.Resolve` | `WatchListAsync` calls `WatchConfig.Resolve(target, key => rows.GetValueOrDefault(key))`; the CLI-side duplication disappears |
| (b) | Per-target line rendering | Interpolation at L593 | New pure `WatchListFormat.Render(target, WatchConfig)` in `Core/Watch` (labeled header, §3.1), byte-pinned by direct unit tests |
| (c) | Ordering | `SortedSet` ordinal, `global` seeded (present, not first) | Unchanged; pinned by a new test so it can't silently drift |
| (d) | Stdout discipline | Verb results → `stdout` writer; help/parse errors → stderr via `CliArgs.Render` | Unchanged; `WatchListAsync` and `WatchRegisteredAsync` keep writing blocks to the stdout writer; no new stderr writes |
| (e) | Live state unobservable from a one-shot CLI process | `WatchStatus.State/LastError/LastSync` runtime-only in `WatchPipeline`; `IWatchStore.ListWatchesAsync()` returns persisted `WatchRegistration` rows only | `watch registered` reports the persisted fields (project, path, registered, lastChange); live state stays on `memory_watch_status` (§2.10, §3.2) |
| (f) | Command descriptions | Unpinned by tests; `watch` family does not say it configures; `list` does not say it is config-only | WP3 replaces the texts (§3.3); `memory_watch_add` description gains the CLI pointer; no test updates required (§2.8) |
| (g) | `IWatchStore` injection into `ConfigCommands.RunAsync` | No watch-store dependency today; `WatchStore` exists and is BDD-exposed (`FileWatcherFeatureContext.WatchStore`) | Trailing optional `IWatchStore? watchStore = null` (precedent: `bank`/`bws`/`env`); `Program.cs` passes `new WatchStore(bank)`; handler guards with `Guard.IsNotNull` |

## 5. Work packages (TDD: failing test first, build green at every step)

Gates: `dotnet build` (TreatWarningsAsErrors, 0 warnings) and `dotnet test` from the worktree root; targeted `--filter` per package. No changes to `Program.cs`'s server path, the settings keys, `WatchScopeList`, `WatchConfig.Resolve`, `WatchService`, `WatchStore`, `IWatchService`, `IWatchStore`, or the MCP watch tools' behavior.

### WP1 — Pure formatter (RED → GREEN)

**RED:** new file `tests/AiRaccoon.Tests/Unit/Watch/WatchListFormatTests.cs` (`[Trait]` Unit/Fast, xunit + Shouldly, no CLI plumbing — construct `WatchConfig` records directly):
- `Render_EmptyScope_RendersNoneInline` → `Render("global", new WatchConfig(false, [], 4))` `ShouldBe` `"target: global  enabled: false  concurrency: 4  scope: (none)"`
- `Render_ScopePaths_OneIndentedLinePerPath` → two paths → `"target: acme  enabled: true  concurrency: 8  scope:\n  /a\n  /b"`
- `Render_EnabledAndConcurrency_RenderCanonicalValues` → `true` → `"true"`, `4` → `"4"`

**GREEN:** new file `src/AiRaccoon.Core/Watch/WatchListFormat.cs` — `public static string Render(string target, WatchConfig config)`; empty scope → header with `(none)`, else header + `string.Join('\n', scope.Select(p => "  " + p))`. Pure string building; no IO, no storage types. Placement unaffected by the Spectre decision (§6 rejects Spectre; Core stays framework-free).

**Gate:** `dotnet test --filter "FullyQualifiedName~WatchListFormatTests"`

### WP2 — Wire `WatchListAsync` (RED → GREEN)

**RED:** edit `tests/AiRaccoon.Tests/Unit/Setup/ConfigCommandsWatchTests.cs`:
- Update the three `WatchList_*` tests to the new format; `WatchList_ShowsResolvedValues_PerTarget` becomes the **full-output byte pin** (change `ShouldContain`×2 to `ShouldBe` on the whole string):
  `"target: acme  enabled: true  concurrency: 2  scope:\n  /a\ntarget: global  enabled: true  concurrency: 4  scope: (none)"`
  (acme sorts before global ordinally; acme's own concurrency=2 and scope `/a`; global falls back to default 4 and no scope).
- Update `WatchList_ProjectRow_WinsOverGlobal` → `"target: acme  enabled: true  concurrency: 8  scope: (none)\ntarget: global  enabled: false  concurrency: 16  scope: (none)"`.
- Update `WatchList_NoRows_PrintsOnlyGlobalDefaults` → `stdout.Trim().ShouldBe("target: global  enabled: false  concurrency: 4  scope: (none)")`.
- Add `WatchList_Ordering_IsOrdinalByTargetName` — rows for `acme`, `CLAUDE.md`, `global` → full output starts `"target: CLAUDE.md  …\ntarget: acme  …\ntarget: global  …"` (pins seeded-but-ordinal, §2.3).
- Add `WatchList_OnlyEnabledRow_ShowsResolvedGlobalScope` — the ghost case: `watch.enabled.CLAUDE.md = "true"` + `watch.scope.global = ["/x"]` → `"target: CLAUDE.md  enabled: true  concurrency: 4  scope:\n  /x"` and `"target: global  enabled: false  concurrency: 4  scope:\n  /x"` (pins pollution visibility and global scope fallback).

All six fail against current code (old format / no such behavior).

**GREEN:** rewrite `WatchListAsync` (ConfigCommands.cs L567–597): keep the prefix fetch and the seeded `SortedSet`; per target replace the three inline `??` chains (L583–592) with

```csharp
var config = WatchConfig.Resolve(target, key => rows.GetValueOrDefault(key));
await stdout.WriteLineAsync(WatchListFormat.Render(target, config));
```

One short comment where `Resolve` is called: `// target "global" maps to the global keys by construction (Project("global") == Global).` Delete the now-unused inline resolution. Nothing else in the file changes.

**Gate:** `dotnet test --filter "FullyQualifiedName~ConfigCommandsWatchTests|FullyQualifiedName~WatchListFormatTests"`

### WP3 — Descriptions + `watch registered` (RED → GREEN; requirement 2 + 3)

One WP because the `watch list` description references `watch registered` — the help text must not ship before the command exists. All description edits are test-neutral (§2.8); the command gets parse + handler + BDD coverage.

**RED:**
- `tests/AiRaccoon.Tests/Unit/Setup/CliArgsTests.cs`: add `Parse_WatchRegistered_ParsesCommandPath` → `CliArgs.Parse(["watch", "registered"]).CommandPath.ShouldBe(["watch", "registered"])`; add `Parse_WatchRegistered_AcceptsOptionalProjectFilter` → `["watch", "registered", "acme"]` parses with the argument value `"acme"`.
- `tests/AiRaccoon.Tests/Unit/Setup/ConfigCommandsWatchTests.cs` (reuse `FakeWatchStore` from `Unit/Watch/WatchTestFakes.cs` — same assembly; seed its `Watches` dict directly; the `Run` helper gains a `watchStore` argument passed as the new trailing `RunAsync` parameter):
  - `WatchRegistered_ListsAllRegistrations_SortedByProjectThenPath` — seed `acme`/`/a` (created 1_700_000_000, lastChange 2_000_000_000), `acme`/`/b` (created 1_700_000_000, lastChange 0), `zeta`/`/z` (created 1_700_000_000, lastChange 0) → full-output `ShouldBe`:
    `"project: acme  path: /a  registered: 2023-11-14T22:13:20Z  lastChange: 2033-05-18T03:33:20Z\nproject: acme  path: /b  registered: 2023-11-14T22:13:20Z  lastChange: never\nproject: zeta  path: /z  registered: 2023-11-14T22:13:20Z  lastChange: never"`
  - `WatchRegistered_ProjectFilter_LimitsToProject` — `["watch", "registered", "acme"]` → only the two `acme` lines.
  - `WatchRegistered_NoRows_PrintsNoRegisteredWatches` — empty store → `stdout.Trim().ShouldBe("no registered watches")`.
  - `WatchRegistered_ReadsOnlyTheWatchesTable` — settings rows present, no registrations → `no registered watches` (proves no settings fallback).
- BDD (integration coverage of the status contract — the MCP side is already covered by the feature's status scenarios, §2.12; this covers the CLI view end-to-end over the real store): add one scenario to file-watcher.feature's "Watch configuration is CLI-only and user-facing" rule:
  `Scenario: watch registered lists the registrations made by memory_watch_add` — Given a watch for "proj-a" on path "/repo" (existing helper), When the user runs `watch registered`, Then the CLI output lists the registered watch for "proj-a" at the mapped path. Implement via `FileWatcherSteps`: `RunCliAsync` passes `watchStore: Ctx.WatchStore` (the real store, already exposed by `FileWatcherFeatureContext`); new step bindings `[When("^the user runs watch registered$")]` and `[Then("^the CLI output lists the registered watch for \"([^\"]*)\" at \"([^\"]*)\"$")]` asserting `_lastCliMessage` contains the `project: …  path: …` line. (The mapped path is `Map("/repo")`, which is what the service normalized into the store.)

All of the above fail against current code: no `registered` command (parse error), no handler, no step.

**GREEN:**
- `src/AiRaccoon/Setup/CliArgs.cs` — replace the description strings per §3.3; add to `WatchCommand()`:
  `new Command("registered", "Lists every REGISTERED watch (project, path, registered at, last change) from the watches table. Registrations are created via memory_watch_add; live state (scanning/healthy/…) is reported by memory_watch_status, not the CLI.") { new Argument<string?>("project-id") { HelpName = "project-id", Arity = ArgumentArity.ZeroOrOne } }`.
- `src/AiRaccoon/Setup/ConfigCommands.cs` — dispatch row `["watch", "registered"] => await WatchRegisteredAsync(parseResult, Guard.IsNotNull(watchStore), stdout, cancellationToken),`; new handler: optional project filter → `store.ListWatchesAsync()` → filter/sort (`OrderBy(ProjectId, StringComparer.Ordinal).ThenBy(Path, WatchPath.PathComparer)`) → render the §3.2 line (UTC ISO-8601, `0` → `never`) → `no registered watches` when empty; all to the stdout writer, exit 0. `RunAsync` signature gains the trailing `IWatchStore? watchStore = null`.
- `src/AiRaccoon/Program.cs` — `watchStore: new WatchStore(bank)` in the `RunAsync` call (L27–28) + `using AiRaccoon.Infrastructure.Watch;`. (CLI verb path only; server path untouched.)
- `src/AiRaccoon/Tools/WatchTools.cs` — `memory_watch_add` description per §3.3 (name/behavior unchanged — inventory tests stay green).
- BDD: `FileWatcherSteps.cs` — `RunCliAsync` watchStore wiring + the two step bindings.

**Gate:** `dotnet test --filter "FullyQualifiedName~ConfigCommandsWatchTests|FullyQualifiedName~CliArgsTests|FullyQualifiedName~FileWatcherSteps"`

### WP4 — Cleanup affordance: `watch remove <target>` (recommended; owner-gate-able) [was WP3]

**Why it earns its existence:** the polluted target cannot be fully removed today — `watch disable` leaves the row listed forever (§2.7). A heuristic marker is rejected (cannot distinguish a file-name row from a legitimate project id containing dots; project ids are directory names). `watch remove` is the missing inverse of enable/scope/concurrency: ~20 lines, and the only way to delete a ghost row.

**RED:** add to `ConfigCommandsWatchTests.cs`:
- `WatchRemove_DeletesEnabledScopeAndConcurrencyRows_ForTarget` — rows for `acme` gone; a second target's rows untouched.
- `WatchRemove_Star_DeletesGlobalRows` — the three `watch.*.global` rows gone.
- `WatchRemove_NoRows_IsExitZeroNoOp` — exit 0, stdout contains `removed`, store unchanged.
Add to `tests/AiRaccoon.Tests/Unit/Setup/CliArgsTests.cs`: `Parse_WatchRemove_ParsesTarget` → `CommandPath ["watch","remove"]`, target value parses.

**GREEN:** `CliArgs.cs` `WatchCommand()` (L269–289) += `new Command("remove", "Removes all watch config rows for a target") { new Argument<string>("target") { HelpName = "project-id|*" } }`; `ConfigCommands.RunAsync` switch (L48–53) += `["watch", "remove"] => await WatchRemoveAsync(parseResult, store, stdout, cancellationToken),`; new handler deletes the target's enabled/scope/concurrency keys (star → the three global keys) via `DeleteSettingAsync`, prints `removed watch config for {target}` to stdout, returns 0. No `Program.cs` change.

**Gate:** `dotnet test --filter "FullyQualifiedName~ConfigCommandsWatchTests|FullyQualifiedName~CliArgsTests"`

**Fallback if the owner drops WP4:** WP5's doc note becomes the only mitigation (`watch enable <name> false` neutralizes the row's effect; the ghost stays listed — accepted).

### WP5 — Docs [was WP4]

- `README.md` and `src/AiRaccoon/README.md` command tables: add `ai-raccoon watch remove {project-id|*}` and `ai-raccoon watch registered [{project-id}]`; note the `watch list` block format (labeled `target:` header, scope one path per line, `(none)` when empty); note the quoting pitfall: *quote the wildcard — `ai-raccoon watch enable '*' true`; an unquoted `*` expands to file names and writes rows named after files; remove such rows with `ai-raccoon watch remove <name>`*.
- `docs/reference/agent-memory-server.md` (File watching bullet, L96–106): mention `watch list` (block format: labeled `target:` header with enabled/concurrency, scope one path per line, `(none)` when empty), `watch registered` (persisted registration view: project/path/registered/lastChange — live state stays on `memory_watch_status`; `enabled` in `watch list` means *watching enabled for this target (config), not a registered watch*), and `watch remove`.
- No test; gate = doc read-through at review.

## 6. Spectre.Console evaluation (requirement 4 — decision)

**Question:** should the CLI adopt Spectre.Console (the *rendering* library — NOT Spectre.Console.Cli, which was rejected for ARG PARSING in `docs/work/2026-08-04-cli-args-exploration.md`; that decision stands and is untouched) to render `watch list` / `watch registered` output?

**What it offers for this output:**
- Tables (auto-width columns, borders, headers), panels, colors/styles, markup, progress/live widgets (irrelevant for one-shot verbs), and built-in terminal capability detection.
- **Redirect/TTY behavior (verified 2026-08-05):** since 0.55.0, "ANSI output is now disabled when stdout or stderr is redirected" (official 0.55.0 release notes, spectreconsole.net); capability detection also honors `NO_COLOR`/`TERM`/platform and output redirection (`Profile.cs`/`AnsiConsoleFactory`). So captured test output gets **no ANSI escapes by default** — the redirect hazard the stdio discipline worries about is handled by the library, not the caller. Unverified: the exact *layout* of a table rendered to a redirected writer (border glyphs remain plain text; column widths are content-driven by default) — a spike would be required before any adoption, and byte pins would still couple to Spectre's layout algorithm.
- For THIS output specifically, a table's value over the §3 block format is low: paths are unbounded (the original reason aligned columns were rejected, §3.1), so the scope column dominates the table; the genuine gain is color, not structure.

**Costs (measured 2026-08-05 from NuGet):**
- Latest stable 0.57.2; TFMs net8.0/net9.0/**net10.0**/netstandard2.0 — the net10.0 group exists, so TFM compat is fine. Dependency on net8.0+: `Spectre.Console.Ansi` 0.57.2 only. Package ~2.2 MB; net10.0 `Spectre.Console.dll` ~830 KB (+ ~840 KB XML docs).
- Still a 0.x version line (pre-1.0 API churn was a stated reason the repo rejected Spectre.Console.Cli; the rendering lib is more stable in practice, but the same caveat applies).
- Repo precedent: `Directory.Packages.props` / `AiRaccoon.csproj` contain no `Spectre.*`; the CLI stack is System.CommandLine 2.0.10 only, added deliberately as the official GA parser. The repo's posture is minimal, purpose-driven dependencies.
- **Byte-pinned tests:** WP1/WP2 pin full-output `ShouldBe` strings. A Spectre-rendered table's exact bytes are Spectre's layout algorithm output (padding, borders, width computation); pins would drift on Spectre upgrades. The alternative — asserting semantic content per row — weakens exactly the byte-pin contract this refactor establishes.
- **Layering (clean-layering invariant):** Core must stay framework-free, so `WatchListFormat` cannot reference Spectre. The legal shape if adopted: Core keeps the pure plain-text formatter as the canonical, byte-pinned contract, and a Setup-layer renderer (new file in `src/AiRaccoon/Setup/`, e.g. `WatchListSpectreRenderer`) takes `IReadOnlyList<(string Target, WatchConfig Config)>` + `IAnsiConsole` and renders a table only when `Profile.Capabilities.Interactive`, else delegates to `WatchListFormat`. That is two renderers to maintain and test for one output — the repo's "ask if a simpler shape would do" invariant pushes against it.

**Recommendation: REJECT for this refactor.** Keep the pure formatter in Core and the byte-pinned plain-text output. The user-facing gain (color, borders) does not pay for a new ~2.2 MB dependency, a second renderer, and pin drift risk on output whose current value is "reads clearly and is byte-stable". If Spectre polish is wanted later, do it as a **separate "verb output" workstream across ALL verbs** (`model show`, `sync show`, `access list`, `watch list`/`registered` — one coherent surface, one dependency decision), with three preconditions: (1) rendering in the Setup layer only, Core stays pure (shape named above); (2) tests pin content, not layout bytes; (3) a spike first verifying redirected-output rendering and pinning the version in `Directory.Packages.props`. This refactor ships zero new dependencies.

## 7. File map

| File | Action | WP |
|---|---|---|
| `src/AiRaccoon.Core/Watch/WatchListFormat.cs` | **NEW** — pure formatter (labeled header, §3.1) | WP1 |
| `tests/AiRaccoon.Tests/Unit/Watch/WatchListFormatTests.cs` | **NEW** — byte-pinned formatter tests | WP1 |
| `src/AiRaccoon/Setup/ConfigCommands.cs` | EDIT — `WatchListAsync` body; WP3: dispatch + `WatchRegisteredAsync` + trailing `IWatchStore? watchStore`; WP4: dispatch + `WatchRemoveAsync` | WP2, WP3, WP4 |
| `tests/AiRaccoon.Tests/Unit/Setup/ConfigCommandsWatchTests.cs` | EDIT — 3 `WatchList_*` updated, 2 new; WP3: 4 `WatchRegistered_*` (via `FakeWatchStore`); WP4: 3 new | WP2, WP3, WP4 |
| `src/AiRaccoon/Setup/CliArgs.cs` | EDIT — WP3: watch-family descriptions + `registered` command; WP4: `remove` command | WP3, WP4 |
| `tests/AiRaccoon.Tests/Unit/Setup/CliArgsTests.cs` | EDIT — WP3: 2 parse tests; WP4: 1 parse test | WP3, WP4 |
| `src/AiRaccoon/Program.cs` | EDIT (WP3 only) — `watchStore: new WatchStore(bank)` + using | WP3 |
| `src/AiRaccoon/Tools/WatchTools.cs` | EDIT (WP3 only) — `memory_watch_add` Description text | WP3 |
| `docs/features/file-watcher/file-watcher.feature` | EDIT (WP3) — one CLI scenario | WP3 |
| `tests/AiRaccoon.Tests/BDD/FileWatcherSteps.cs` | EDIT (WP3) — `RunCliAsync` watchStore wiring + 2 step bindings | WP3 |
| `README.md`, `src/AiRaccoon/README.md`, `docs/reference/agent-memory-server.md` | EDIT (WP5) — command tables, block format, `watch registered`, pitfall note | WP5 |
| `docs/plans/watch-list-format-refactor.md` | **NEW** — this document | — |

**Explicitly NOT touched:** `Program.cs` server path, `CliArgs.Render`, `WatchConfigKeys.cs`, `WatchScopeList.cs`, `WatchConfig.cs`, `WatchService.cs`, `WatchStore.cs`, `WatchPipeline`, `IWatchService`, `IWatchStore`, `WatchStatus`/`WatchState`, `Tools/WatchTools.cs` behavior (description text only, WP3), `MemoryPrompts.cs`, `docs/work/*` provenance, settings keys.

## 8. Acceptance criteria

1. **Stay green unmodified:** all 15 non-`WatchList_*` tests in `ConfigCommandsWatchTests`; all of `CliOutputRoutingTests`, `CliArgsTests` (minus the added parse tests), `WatchConfigTests` (14), `Unit/Watch/*`, `Unit/Setup/*`, `Unit/Mcp/*` (incl. `ToolInventoryTests`/`WatchToolsInventoryTests` — tool names unchanged); full suite.
2. **Updated:** the three `WatchList_*` tests (new labeled format; `WatchList_ShowsResolvedValues_PerTarget` now a full-multi-target `ShouldBe` byte pin); the watch-family descriptions in `CliArgs.cs` and the `memory_watch_add` description in `WatchTools.cs` (no test updates required — verified unpinned, §2.8).
3. **New:** `WatchListFormatTests` (3 byte-for-byte `ShouldBe`s), 2 `WatchList_*` behavior tests (ordering incl. `global`-not-first; ghost-target visibility), WP3's 4 handler tests + 2 parse tests + 1 BDD scenario, WP4's 3 + 1 parse tests.
4. **`watch registered` semantics:** reads only the `watches` table via `IWatchStore.ListWatchesAsync()`; reports persisted fields (project, path, registered, lastChange) and never fabricates live state; `no registered watches` on empty; sorting (projectId, path) pinned.
5. **Display-only invariant:** settings keys, `WatchScopeList.ToJson`/`Parse`, and stored values unchanged (asserted implicitly: all enable/scope/concurrency row-writing tests untouched); `WatchConfig.Resolve` unchanged and now exercised through the CLI; MCP `memory_watch_status`/`memory_watch_remove` behavior unchanged.
6. **Stdout discipline:** `WatchListAsync` and `WatchRegisteredAsync` write only to the stdout writer; no new stderr writes (guarded by the existing routing tests plus WP2/WP3 harness assertions).
7. **Zero new dependencies:** the Spectre.Console evaluation (§6) ends in rejection; no package changes in this task.
8. **Full gate:** `dotnet build` exit 0 (0 warnings) and `dotnet test` exit 0 from the worktree root.

## 9. Out of scope

- MCP `memory_watch_status` / `memory_watch_add` / `memory_watch_remove` and `WatchService` behavior — the agent channel, unchanged (only `memory_watch_add`'s description text changes).
- Live watch state (State/LastError/LastSync) via the CLI — runtime-only, unobservable from a one-shot process; no IPC surface is added (§2.10, §3.2); `memory_watch_status` remains the live-state surface.
- `watch scope list` output (one path per line) and the `watch scope list`/`watch concurrency` no-global-fallback inconsistency — different verbs, behavior change beyond display; follow-up candidate.
- Spectre.Console adoption — deferred to a separate all-verb workstream with preconditions (§6).
- Storage format, settings keys, `WatchScopeList`/`WatchConfigKeys` helpers, `WatchConfig.Resolve` semantics.
- `Program.cs` server path, `CliArgs.Render`/parse-error routing.
- Heuristic pollution markers in `watch list` — rejected (false positives on legitimate dotted project ids).
- Aligned-column rendering — rejected (§3.1, §6).

## 10. Risks

| # | Risk | Mitigation |
|---|---|---|
| R1 | Any script parsing `watch list` text breaks | Accepted: CLI is human-facing; agents use `memory_watch_status` (JSON). Flagged in the PR description. |
| R2 | `WatchConfig.Resolve` canonicalizes: `enabled` prints `true`/`false` regardless of stored case; scope paths normalize via `WatchPath.Normalize` (idempotent on CLI-written rows; changes display only for hand-edited rows) | Display-only deltas; called out in WP2's commit/PR so review sees them. |
| R3 | WP3/WP4 touch `CliArgs.cs` (descriptions + two commands) | Small diffs; existing per-verb parse tests don't snapshot the full tree (verified: `CliArgsTests` asserts specific commands only); new parse tests are added. |
| R4 | Ordering nuance ("global" not first for uppercase/punctuation targets) surprises | Pinned by `WatchList_Ordering_IsOrdinalByTargetName`; documented in this plan. |
| R5 | WP4 dropped late → ghost rows stay listed with only a doc-note mitigation | Explicit fallback stated in WP4; decision needed at the owner gate. |
| R6 | `watch registered` cannot show live state → user expects `scanning/healthy/…` | Named `registered`, not `status`; the gap is stated in the command description and docs (§3.2, §3.3); `memory_watch_status` remains the live surface. |
| R7 | `RunAsync` signature grows (trailing `IWatchStore?`) | Trailing optional parameter; precedent `bank`/`bws`/`env` (encryption work); all existing call sites compile unchanged (verified: positional call sites pass ≤7 args). |
| R8 | WP3's BDD scenario touches the shared `RunCliAsync` helper | Additive `watchStore:` argument only; the other CLI-rule scenarios' assertions are unchanged. |
| R9 | Spectre.Console looks attractive in review and gets re-proposed mid-refactor | Decision recorded in §6 with measured costs and the named alternative workstream; keep the refactor zero-dependency. |

## 11. Definition of done

- WP1–WP2 merged green (formatter + wired `WatchListAsync`, byte-pinned on both sides); WP3 per requirement 2+3 (descriptions + `watch registered` with parse/handler/BDD coverage); WP4 per owner decision (`watch remove`); WP5 docs updated.
- Full `dotnet build` + `dotnet test` green from the worktree root.
- Each behavior change demonstrably red-before-implementation (test-first commits per WP).
- Owner-gate review offered with the diff and gate output.
