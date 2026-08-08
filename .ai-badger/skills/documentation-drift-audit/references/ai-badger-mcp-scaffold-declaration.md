# ai-badger: why a declared MCP server is absent from scaffolded projects

Session: integration review + docs refresh, 2026-08-05, ai-badger 0.79.0.
Symptom (user f:): "other projects don't see the project memory mcp". All facts
below verified against mcp_tools.py / stack-mcp.json / refresh.py that day;
re-verify before citing — framework code moves.

## The declaration pipeline (verified)

- Catalog: `features/common/stack-mcp.json` entry
  `{name, command, declare, availability: {command}}`.
- Gate: `McpTools._server_available` -> `shutil.which(availability.command)` at
  **scaffold time** (`AI_BADGER_MCP_AVAILABILITY=all|none` overrides for
  deterministic trees; the freshness guard uses `all`).
- Failure mode: **silent skip** — the server is simply not written to any
  config; no error, no note, no prerequisite hint.
- Writers: `.mcp.json` (claude/copilot readers, `expand_home=True` ->
  `${HOME}`-relative commands when the binary sits in a USER_TOOL_DIR),
  `.github/mcp.json` (copilot-only reader, `requires_reader=True`,
  `expand_home=False`, merge-only), `~/.claude/settings.json` (scope:user
  proposal only — never written).

## Root-cause chain observed

1. **Tool command names change between versions.** <pkg-id> 1.0.4
   installed the binary as `<tool>` (capital — evidence: a hand-written
   VS Code entry referencing `~/.dotnet/tools/the project`); 1.0.5's
   ToolCommandName is `the project` (lowercase shim). Every scaffold between
   #302 and the 1.0.5 install silently skipped the server — including this
   repo's own 0.79.0 scaffold (the shim's mtime postdates the scaffold).
2. **den-refresh only re-scaffolds on detected drift.** `frameworkVersion`
   equal -> report says `reScaffolded: false`, nothing changes. Version-equal
   projects need `refresh.py --force` (or re-run welcome-ai-badger).
3. **`.mcp.json` can be gitignored** (the framework repo's own is untracked) —
   the local fix is a local file, not a commit. Tracked files the forced
   refresh touches (.ai-badger/manifest.json stamps, .github/mcp.json) are
   often host-dependent and should be reverted, not committed.

## The .github/mcp.json #193 dedup trap

`.github/mcp.json` renders with **bare** commands (`expand_home=False`);
`.mcp.json` renders `${HOME}`-relative when USER_TOOL_DIRS resolves the
binary. `_declared_differently_in_mcp_json` then drops the server from
`.github/mcp.json` ("declared only in .mcp.json" — Copilot CLI reads both
files with no documented precedence). The committed tree is generated under
`AI_BADGER_MCP_AVAILABILITY=all` so it carries the full set; a host scaffold
that REMOVES entries from `.github/mcp.json` is expected runtime behavior,
not a regression — do not commit that diff.

## Fix recipe

1. Upgrade the tool so its command matches the catalog:
   `dotnet tool update -g <package>`; verify `which <command>` from the
   scaffold process's PATH (a GUI-launched agent may not inherit shell PATH
   additions — same class of issue as the hermes/the project conditional).
2. Re-scaffold each affected project: `den-refresh` (version drift triggers
   it) or `refresh.py --force` when versions match.
3. Verify: the project's `.mcp.json` carries the entry. Verified behavior: a
   same-name hand-written entry (e.g. a VS Code shape with
   `${workspaceFolder}` env) does NOT survive the merge — the generated shape
   replaces it and drops the user's env block (observed 2026-08-05 in
   the reference repo: <APP>_DATA_ROOT / <APP>_RID lost). After a
   refresh, re-add the env block by hand and keep the NEW command name (the
   pre-upgrade binary path is dead — the tool update removes the old shim).
4. Doc claims to keep honest: README "Declared only when X is on PATH" and
   `.hermes.md`'s conditional-declaration line are accurate — the operational
   answer is "re-scaffold after installing/upgrading", not a code change.

## The Hermes route — the one .mcp.json cannot serve

Hermes loads MCP servers ONLY from `~/.hermes/config.yaml` `mcp_servers:` — it has
no project route, so a server declared in the project's `.mcp.json` is invisible to
Hermes sessions (observed 2026-08-05: "other projects don't see the project" was
Hermes + the fact that the scaffold never writes the user config).
`features/hermes/adjustments/adjust_mcp.py` is **proposal-only by design**
(ADR-0014 decision 6: never write user-global agent config) — it prints the block
to merge, writes nothing. The fix is the native CLI:

```sh
hermes mcp add <name> --command <abs-path> [--env K=V ...]   # user-global; connection test runs
hermes mcp remove <name>                                     # then re-add to change env
```

- One entry serves ALL projects (no per-project scope) — `--env` in the shared
  entry pins that value for every project's sessions (a data-root pin aimed at one
  repo leaks into the others). Prefer no env (machine-wide default bank) unless the
  project genuinely needs its own root; ask the user which they want.
- A server that answers `initialize` then dies makes `hermes mcp add` report
  "Failed to connect: Connection closed" and prompt to save anyway — answer yes,
  then diagnose (next section). Saved-but-broken entries are re-testable later.
- A removed entry's watchdog (`hermes-agent/tools/mcp_stdio_watchdog.py`) can
  linger with the old server still alive — kill watchdog + server before re-adding
  if the new connection test fails.
- After `hermes mcp add`, tools appear in the NEXT session (the running session's
  toolset is fixed at startup).

## the project: single-instance port-5000 bind + the stdio probe

The server binds `http://127.0.0.1:5000` (Kestrel; `WebApplication.CreateBuilder`
in `src/the project/Program.cs`) on EVERY launch — stdio included. A second instance
responds to `initialize` then aborts: `Failed to bind to address
http://127.0.0.1:5000: address already in use`. To a client this looks exactly like
"server missing / disconnects after connect" — the config is fine; another
the project (a Hermes watchdog instance, a `--transport http` server, a stale dev
build) holds the port. macOS ControlCenter's `*:5000` coexists (SO_REUSEPORT) and
is NOT the blocker; only a second the project on 127.0.0.1:5000 is. Only one
the project instance can run per machine.

Probe recipe — distinguishes config problems from server crashes, no client needed:

```sh
(printf '%s\n' \
 '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"probe","version":"0"}}}' \
 '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}'; sleep 3) \
 | ENV_VARS /path/to/server 2>&1 | grep -E "serverInfo|tools|Failed to bind"
```

initialize answers but tools/list never arrives AND stderr shows the bind error =>
port conflict, not a config defect. Same probe shape works for any stdio MCP server.
