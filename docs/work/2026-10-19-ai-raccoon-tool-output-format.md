# Research: AiRaccoon tool output format

**Date:** 2026-10-19 **Question:** What output format do AiRaccoon tools return, and if it is not minimized serialized JSON, what is it and why?

## Findings

### F1 — Successful tools return compact JSON text inside an MCP tool result [READ]

At the MCP protocol boundary, a tool call returns a `CallToolResult` with content blocks. AiRaccoon clients receive a text content block whose `Text` is serialized JSON. This is distinct from returning a bare JSON-RPC object directly to the
caller.

The JSON text is compact (without formatting whitespace). For example, the wire-surface test checks the serialized substring `"path":"..."`, parses tool text as JSON, and reads its `data` fields.

**Evidence:** `tests/AiRaccoon.Tests/E2E/McpServerToolSurfaceE2ETests.cs:68-72,128-141,176-180`; C# SDK tools documentation, “Structured content”
section: https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/tools/tools.md#structured-content.

### F2 — The serialized value is AiRaccoon’s `ApiEnvelope<TData>` [READ]

Each successful tool returns the shared envelope with `data` and `meta` members. `ToolGate.WrapAsync` creates it around the tool payload and promotion metadata. JSON property names are shown lowercase in the round-trip tests (`data`,
`hash`, `deleted`).

**Evidence:** `src/AiRaccoon/Tools/ApiEnvelope.cs:5-10`; `src/AiRaccoon/Tools/ToolGate.cs:90-95`; `tests/AiRaccoon.Tests/E2E/McpServerToolSurfaceE2ETests.cs:68-72`.

### F3 — AiRaccoon uses text content rather than MCP `structuredContent` [READ]

The E2E client extracts `TextContentBlock` values from `CallToolResult.Content`, and the tests parse those strings as JSON. AiRaccoon source has no `UseStructuredContent` setting; its pinned ModelContextProtocol package is 2.2.0. The SDK
documents structured output as an explicit opt-in (`UseStructuredContent = true`), placing that value in `CallToolResult.StructuredContent` and advertising an output schema.

That explains the observed shape: the application returns a typed envelope, while the SDK’s ordinary result path exposes it as tool text. The code’s established envelope also keeps the payload and promotion metadata together for tools.

**Evidence:** `tests/AiRaccoon.Tests/E2E/McpServerToolSurfaceE2ETests.cs:176-180`; `Directory.Packages.props:12-13`; source search for `UseStructuredContent` in `src/AiRaccoon` returned no matches; C# SDK tools documentation, “Structured
content” section: https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/tools/tools.md#structured-content.

### F4 — The focused tool round-trip passed [MEASURED]

The end-to-end test successfully called tools over the MCP client and parsed the response text as JSON.

**Evidence:** `dotnet test --project tests/AiRaccoon.Tests --filter FullyQualifiedName~McpServerToolSurfaceE2ETests.UncoveredTools_RoundTripOverTheWire --no-restore`, run on the local macOS arm64 machine against .NET 10; 1 passed, 0 failed,
duration 3.295 seconds.

## Still open

- Whether any downstream MCP host specifically requires `structuredContent` rather than JSON encoded in text was not checked. Inspecting the supported client integrations would settle whether opting in would improve compatibility; changing
  to it would also be a wire-shape change for existing clients.
