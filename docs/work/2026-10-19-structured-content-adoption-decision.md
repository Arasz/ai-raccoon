# Research: structured MCP results for AiRaccoon

**Date:** 2026-10-19
**Decision:** Defer production adoption until a supported client needs the typed result channel.

## Question

Would MCP `structuredContent` improve AiRaccoon or its clients enough to justify adding it to the current tool responses?

## Findings

### The pinned SDK supports attributed POCO results and keeps the text form [MEASURED]

AiRaccoon pins `ModelContextProtocol` and `ModelContextProtocol.AspNetCore` to 2.2.0 in `Directory.Packages.props`. The restored 2.2.0 package metadata identifies SDK commit `6fa3825973949a9c4f0cd8af344e15a8db09dc35`. Its `McpServerToolAttribute.UseStructuredContent` defaults to false. With the option enabled on a POCO-returning method, the SDK infers an `outputSchema`, serializes the object to `CallToolResult.StructuredContent`, and retains the serialized JSON in a text content block.

The behavior was checked against the pinned SDK in `StructuredContentSdkBehaviorTests.AttributedPoco_UsesStructuredContent_AndKeepsJsonTextFallback`. The test failed when its probe attribute had `UseStructuredContent = false` and passed when enabled. It asserts the schema, the structured value, and the matching text fallback. Run: `dotnet test --project tests/AiRaccoon.Tests --filter FullyQualifiedName~StructuredContentSdkBehaviorTests.AttributedPoco_UsesStructuredContent_AndKeepsJsonTextFallback` (1 passed).

**Sources:** `Directory.Packages.props:12-13`; `tests/AiRaccoon.Tests/Unit/Mcp/StructuredContentSdkBehaviorTests.cs`; package XML `ModelContextProtocol.Core.xml` (`McpServerToolAttribute.UseStructuredContent`); SDK source at [AIFunctionMcpServerTool.cs, pinned commit](https://github.com/modelcontextprotocol/csharp-sdk/blob/6fa3825973949a9c4f0cd8af344e15a8db09dc35/src/ModelContextProtocol.Core/Server/AIFunctionMcpServerTool.cs). The source returns both the serialized text block and structured value for ordinary POCO results.

### First-party clients read only text today [READ]

The Hermes integration extracts the first text content block, parses its JSON, and unwraps AiRaccoon's `{data, meta}` envelope. The Python MCP client likewise parses JSON from `result.content`. Neither reads `structuredContent`. `McpToolContractTests.NoTool_DeclaresAnOutputSchema` currently pins the no-schema tool contract.

**Sources:** `integrations/hermes/ai-raccoon/client.py:305-324`; `scripts/src/mcp_client.py:151-165`; `tests/AiRaccoon.Tests/Unit/Mcp/McpToolContractTests.cs:108-122`.

### A typed MCP client can access the new field [READ]

The official .NET MCP client represents `CallToolResult.StructuredContent` as a nullable `JsonElement`. Its tools documentation shows clients listing and calling tools, and says clients should interpret structured values according to the advertised output schema. This provides a typed entry point for SDK consumers, though the caller still needs to deserialize the `JsonElement` into an application type. This review did not verify support across desktop agent hosts.

**Sources:** [C# SDK `CallToolResult`](https://github.com/modelcontextprotocol/csharp-sdk/blob/6fa3825973949a9c4f0cd8af344e15a8db09dc35/src/ModelContextProtocol.Core/Protocol/CallToolResult.cs); [C# SDK tools documentation](https://github.com/modelcontextprotocol/csharp-sdk/blob/6fa3825973949a9c4f0cd8af344e15a8db09dc35/docs/concepts/tools/tools.md). MCP recommends retaining serialized JSON in a text block for backward compatibility and says clients should validate structured results against a declared schema: [MCP tool specification](https://modelcontextprotocol.io/specification/2025-11-25/server/tools).

### A representative response nearly doubles in size [MEASURED]

A compact UTF-8 size comparison used an eight-row `memory_search` response shaped from the current `ApiEnvelope<SearchResultList>` and search DTOs. It included approximately 390-character snippets, result metadata, two retrieval-evidence legs per row, fusion statistics, and the promotion metadata envelope. With the same JSON in the text content block in both cases:

| MCP result shape | UTF-8 bytes |
| --- | ---: |
| Text JSON only | 7,807 |
| Text JSON plus `structuredContent` | 15,188 |
| Added by structured copy | 7,381 (+94.5%) |

The payload itself was 7,360 bytes. This is a DTO-shaped sample, not a captured live search response. Exact token cost was not measured: the available local tools had no compatible model tokenizer, and whether a host passes both representations to its model is host-specific. A host that does pass both would expose nearly the same JSON twice to the model.

**Sources:** `src/AiRaccoon/Tools/ApiEnvelope.cs`; `src/AiRaccoon/Tools/MemoryTools.cs:578-588`; `src/AiRaccoon.Core/Memory/MemorySearchResult.cs`; `src/AiRaccoon.Core/Memory/Fusion/RetrievalEvidence.cs`; `src/AiRaccoon.Core/Memory/Fusion/FusionStats.cs`; `src/AiRaccoon.Core/Memory/PromotionMeta.cs`. Measurement script used Python standard-library JSON serialization with compact separators and UTF-8 encoding; both result forms carried the same payload.

## Decision

Defer production `UseStructuredContent` opt-in. Current first-party clients do not consume it, no supported agent host was found that requires it, and retaining the compatibility text adds about 95% to this representative result's serialized MCP response. The .NET SDK gives custom consumers schema discovery and a structured JSON value, but that benefit alone does not justify changing every tool's result size without a known consumer.

Keep the existing compact text JSON envelope. Revisit when a supported client can consume the structured field and there is a concrete benefit to schema-aware handling. This task adds only an SDK behavior regression test and this decision record; it does not change any production tool's wire output.

## Simpler shape check

The existing compact JSON text already satisfies the current clients and callers. Keeping that shape is the smaller option until a consumer demonstrates a need for a second representation.
