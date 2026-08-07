## 9. Access control at the tool boundary (per-project/global modes)

Verified while landing access-control work (FR-NM-2, ro/rw/full modes). When tools must be gated by
a per-project policy, keep the pattern split across layers:

- **Pure policy in the Core/domain project** (MCP-free): `enum AccessMode { Ro, Rw, Full }`,
  `enum AccessRequirement { Read, Write, Destructive }`, and a static policy type with
  `Resolve(AccessMode? global, AccessMode? perProject)` → `perProject ?? global ?? Rw`,
  `Allows(mode, requirement)` (Read always; Write needs Rw|Full; Destructive needs Full),
  `RequiredFor(requirement)` (Write→rw, Destructive→full), plus case-insensitive
  `Parse`/`Serialize` for settings values.
- **Enforcement impl lives in the server project** — it throws `McpException`, which Core
  must not reference. Injected `IMemoryAccessGuard` into the tools class:
  `EnsureAsync(projectId, requirement, toolName, ct)` throws
  `McpException("access-denied: <tool> requires mode <ro|rw> (current <mode>)")`. **Short-circuit
  `Read` before any settings lookup** — reads are allowed in every mode and a read tool shouldn't
  pay a settings round-trip. Register in DI: `AddSingleton<IMemoryAccessGuard>(sp => new
  MemoryAccessGuard(sp.GetRequiredService<IMemoryStore>()))`.
- **Settings storage**: bank settings table keys `access.mode.global` + `access.mode.project:<id>`
  (per-project overrides global; unset = rw). Seed the global once at bank open from an env var
  (`<APP>_ACCESS_MODE`) with `INSERT ... ON CONFLICT(key) DO NOTHING` so an operator-set row
  wins; the guard reads lazily per call. Don't read env in the guard itself — it makes unit tests
  environment-dependent.
- **Tool classification**: reads (search/list/stats/workspace_status/sweep dry_run=true) un-gated;
  writes (write/ingest/share/configure/embed_pending/workspace_begin/sync) rw+; destructive
  (delete/delete_context/workspace_consolidate/workspace_discard/sweep dry_run=false) full.
  For a `dryRun` bool tool, gate on `dryRun ? Read : Destructive`.
- **Gating tests use the REAL guard + a fake store with a `Settings` dictionary** — no fake guard
  needed; the whole store→resolve→enforce→McpException path is exercised. The fake store implements
  `GetSettingAsync`/`SetSettingAsync` from the dictionary.
- **Gate-filter naming**: `dotnet test --filter 'AccessMode'` is a bare-value filter = substring
  match on `FullyQualifiedName`. Name the test CLASSES with the filter token
  (`AccessModePolicyTests`, `AccessModeGuardTests`, `MemoryToolsAccessModeTests`) so the targeted
  gate actually runs them.
- **When gating lands on existing tools, existing tests break by design**: unit tests that called
  destructive tools (e.g. workspace consolidate) must seed the permissive mode in the fake first;
  E2E tests over the real server hit access-denied because the fresh bank defaults to rw — seed the
  bank by setting the env var in the test factory ctor (restore in Dispose), which also exercises
  the seed-at-open path.
