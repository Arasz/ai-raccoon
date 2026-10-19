# Research: MCP structured content for AiRaccoon clients

**Date:** 2026-10-19 **Question:** Would using MCP `structuredContent` improve anything for AiRaccoon and its clients?

## Findings

### F1 — Structured content adds a typed, schema-described result channel [READ]

MCP lets a tool declare an `outputSchema` and return a corresponding `structuredContent` value. Clients can validate it and use its types directly. For backward compatibility, the spec recommends keeping the serialized JSON in a text
content block too.

**Evidence:** MCP specification 2025-11-25, Tools / Structured Content and Output Schema: https://modelcontextprotocol.io/specification/2025-11-25/server/tools (structured content and output schema sections); ModelContextProtocol C# SDK
2.2.0 is pinned in `Directory.Packages.props:12-13`.

### F2 — Current AiRaccoon clients read the text channel [READ]

The Hermes integration extracts the first text block, parses it as JSON, and unwraps the `{data, meta}` envelope. The Python MCP client also reads `result.content` and parses the text. The C# contract test explicitly asserts that no
AiRaccoon tool declares an output schema.

**Evidence:** `integrations/hermes/ai-raccoon/client.py:305-324`; `scripts/src/mcp_client.py:151-165`; `tests/AiRaccoon.Tests/Unit/Mcp/McpToolContractTests.cs:108-122`.

### F3 — Existing first-party clients gain little unless they are updated to consume structured content [INFERRED]

This follows from F1 and F2: structured output can remove client-side JSON parsing and enable schema validation, but AiRaccoon’s first-party clients currently parse text and do not inspect the structured field. Adding the field while
retaining JSON text should preserve their current path, but would not make those clients use the typed value.

### F4 — Keeping compatibility text duplicates the result representation [INFERRED]

This follows from the MCP backward-compatibility recommendation in F1. When both forms carry the same envelope, the tool result contains JSON in text and a structured copy, so wire size grows. Some hosts may also pass the text to the model,
where duplicated content could add tokens. No AiRaccoon measurement of the size or token impact was made.

## Still open

- Which supported external MCP hosts or SDK clients consume `structuredContent` and validate `outputSchema` for AiRaccoon tools? A client-by-client compatibility check would show whether this unlocks a concrete integration.
- Does the pinned .NET SDK 2.2.0 emit both the existing text JSON and structured data for AiRaccoon’s attributed POCO-returning tools? Verify against the pinned package before enabling it, then measure response size and token use on
  representative large search results.
