# 0140. An unknown tool call is answered with the tool to call

Date: 2026-10-22

Status: Accepted

## Context

2.0.0 renamed all thirty MCP tools and shipped **no aliases** (`docs/changelog/2.0.0-mcp-tool-surface-renamed.md`).
A client that still speaks the old names gets nothing back. Measured against the live 2.0.0 server,
every one of the thirty pre-rename names is refused the same way:

```json
{"_rpc_error": {"code": -32602, "message": "Unknown tool: memory_search"}}
```

That is the SDK's own answer, and it is a dead end. The caller learns that the tool does not
exist and nothing else — not that it was renamed, not what to call instead. A client that was
connected across the upgrade keeps a catalogue of thirty dead names and cannot see the new ones,
so its *next* call is the old one; the error arrives as
`Error: Request failed (remote): Unknown tool: memory_search` and stops there.

The MCP protocol says a tool error belongs in the result, not in a JSON-RPC error, and says why:
*"This allows language models to receive detailed error feedback and potentially self-correct in
subsequent requests."* The SDK's own `CallToolResult.IsError` documentation says the same. This
server already answers every other expected refusal that way — `ToolRefusals.Filter` turns an
invalid argument, a disabled feature or a path outside scope into `CallToolResult.IsError = true`
with a wire prefix, and logs it at `Information` rather than `Error` (ADR-0061). An unknown tool
name was the one expected failure still escaping as a protocol error.

## Decision

**A call naming a tool this server does not have comes back as a normal tool error, and the
message names the tool to call.**

```csharp
catch (McpProtocolException) when (IsUnknownTool(request))
{
    return UnknownTool(request);
}
```

- **The wire prefix is `unknown-tool`**, beside the other prefixes in `ToolRefusals`. A renamed
  name reads `unknown-tool: 'memory_search' was renamed to 'query' in 2.0.0 — call 'query' instead.`;
  any other name reads `unknown-tool: 'no_such_tool' is not a tool on this server. This server
  exposes 30 tools; call tools/list to see them.`
- **The surface decides what exists.** `McpToolInventory.Names()` is generated from the
  `[McpServerTool]` declarations (the same pass that gathers the scopes, ADR-0139), so the check
  cannot drift from the tools the server has.
- **The rename map is the 2.0.0 changelog's table**, written once in `RenamedToolNames` and
  compared with the changelog by a test, so the map and the record of the rename cannot part
  company. A map key that came back onto the surface, or a target that left it, fails the same
  test.
- **It is a refusal, logged at `Information`**, like every other expected failure: a client
  naming a tool that does not exist is the client's mistake, not a server fault.

**It does not add aliases.** The old names stay dead. An alias would make two names for one
tool, and the project has no alias machinery to keep in step with the surface (the same reason
the rename shipped without one).

**It does not answer with a JSON-RPC error.** The protocol error is the SDK's default, and it is
the thing that leaves the caller with nothing to act on.

**It does not turn a protocol error for a tool the server *does* have into a refusal.** The
condition is the name, not the exception: a protocol error for a known tool stays a protocol
error and is rethrown as before.

## Consequences

- A client that calls a pre-2.0.0 name learns what to call instead, in one line, in the place
  the MCP protocol says a model can read it.
- The refusal joins the documented error-shape table, and the drift test that keeps that table
  and the code in step now covers it.
- The rename map is a hand-written list of facts about a past release, so it is guarded from both
  directions against the live surface and against the changelog that records the rename.
- A client connected across the upgrade still has a stale catalogue; this makes its next call
  recoverable, and the release notes tell it to reconnect.

## Evidence

`tests/AiRaccoon.Tests/Integration/Mcp/UnknownToolCallTests.cs` — a real MCP client against a
real server (`RealMcpServerFixture`), asserting the shape of the answer and that no Error-level
server log record was produced.

Watched red by disabling the new catch clause and re-running:

```
Xunit.MicrosoftTestingPlatform.XunitException: ModelContextProtocol.McpProtocolException
  : Request failed (remote): Unknown tool: 'memory_search'
```

— the dead end, reproduced on demand. The unit cases in
`tests/AiRaccoon.Tests/Unit/Mcp/UnknownToolGuidanceTests.cs` hold the map against the live
surface and against the 2.0.0 changelog's table.
