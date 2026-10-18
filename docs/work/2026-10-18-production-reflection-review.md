# Research: production reflection and generator options

**Date:** 2026-10-18 **Question:** Which production reflection uses exist in AiRaccoon, and should they be replaced with direct calls or source generation?

## Findings

### F1 — Production reflection is limited to assembly metadata, enum schema attributes, and MCP tool inventory [READ]

The production scan found reflection in `AssemblyBuildStamp`, `ServerInfo`, `KebabCaseEnumJsonConverter<TEnum>`, and `McpToolInventory`. Calls named `JsonElement.GetProperty`, delegate `Invoke`, and `object.GetType` are not reflection and
should not be included in a reflection-removal pass. SQLite provider initialization already uses direct `raw.SetProvider` and `raw.FreezeProvider()` calls.

**Evidence:** `src/AiRaccoon.Core/Observability/AssemblyBuildStamp.cs:13-21`; `src/AiRaccoon/Observability/ServerInfo.cs:18-21`; `src/AiRaccoon.Infrastructure/Embedding/Manifest/EmbeddingManifestSerializer.cs:109-114`;
`src/AiRaccoon/Tools/McpToolInventory.cs:15-24`; `src/AiRaccoon.Infrastructure/Sqlite/SqliteEncryptionInit.cs:21-36`.

### F2 — Assembly metadata reflection is a small startup read with no meaningful speed case for generation [INFERRED]

`AssemblyBuildStamp` reads the SDK-emitted informational version and the `CommitTimestamp` metadata that `GitStamp.targets` adds during build. `ServerInfo` reads the informational version once into a static field. Since both read
build-specific metadata and run outside hot paths, replacing these with a custom generator is unlikely to improve runtime performance materially. If eliminating reflection is a concrete trimming or AOT requirement, expose those values as
MSBuild-generated constants or source; first confirm that requirement.

This recommendation is inferred from the cited one-time reads and the build target that supplies the timestamp. No benchmark was run.

### F3 — Enum schema-name reflection can be replaced with direct mappings if trimming becomes a requirement [READ]

`KebabCaseEnumJsonConverter<TEnum>` reflects over enum fields to read `SchemaNameAttribute`; only `TokenizerFamily` and `PoolingMode` currently use that attribute. A small explicit mapping per enum (or a switch) is simpler than adding a
generator for two enum types. Keep the generic converter's kebab-case fallback for unannotated values. A source generator is reasonable only if this pattern expands enough that maintaining explicit mappings becomes a real burden.

**Evidence:** `src/AiRaccoon.Infrastructure/Embedding/Manifest/EmbeddingManifest.cs:7-27`; `src/AiRaccoon.Infrastructure/Embedding/Manifest/EmbeddingManifestSerializer.cs:82-129`.

### F4 — MCP inventory is the strongest source-generation candidate, though direct declarations may be enough [READ]

`McpToolInventory.Names()` discovers attributed methods across the tool assembly each time it is called. There are 30 `[McpServerTool]` declarations. The inventory feeds `PerformanceTools`, and the test compares it with a separately
maintained registration list. A generated inventory would derive names from the attributes at compile time and avoid runtime reflection and the product-side list. A direct list of names would be cheaper to implement, but would duplicate
metadata already present on methods; the existing parity test can catch drift if retained. No MCP source-generator package was found in the central package version file, so a project-specific generator would add a dependency and maintenance
surface.

**Evidence:** `src/AiRaccoon/Tools/McpToolInventory.cs:13-24`; `src/AiRaccoon/Tools/PerformanceTools.cs:39`; `tests/AiRaccoon.Tests/Unit/Mcp/McpToolInventoryTests.cs:17-28`; `tests/AiRaccoon.Tests/TestHelpers/RegisteredTools.cs:36`;
`Directory.Packages.props` (no MCP or source-generator package entry); 30 matches from `rg -n '\[McpServerTool\(' src/AiRaccoon/Tools --glob '*.cs'`.

### F5 — Source generation is unlikely to make this code meaningfully faster [INFERRED]

The reflection sites are metadata reads and inventory discovery, not repeated computational work in a request loop (the MCP inventory is built when performance data is requested). Replacing them can improve trimming compatibility,
compile-time checking, or cold-start work, but a measurable throughput gain is not established. A benchmark would only be useful if startup or inventory latency is an identified problem.

This inference follows from the cited call sites. No timing was measured.

## Still open

- Is trimmed or Native AOT publishing an actual target? The current project settings and deployment requirements do not establish one.
- SDK inventory check (resolved): the installed MCP SDK (2.2.0) does not expose a generated registry or compile-time inventory hook. Its `WithTools<T>()` and `WithToolsFromAssembly()` APIs discover attributed methods at runtime; it also
  accepts an explicit `IEnumerable<McpServerTool>`, which is a manual registration hook rather than generated inventory. The package contains no analyzer or source-generator assembly. The SDK's official repository has an open request for
  source-generator-driven registration, consistent with that gap.

**Evidence:** `ModelContextProtocol` 2.2.0 package XML docs for `McpServerBuilderExtensions.WithTools` and `WithToolsFromAssembly`; package contents under `~/.nuget/packages/modelcontextprotocol/2.2.0` contain only target-framework library
assemblies; [SDK source-generator registration request](https://github.com/modelcontextprotocol/csharp-sdk/issues/317).

- The direct-list option versus a small dedicated generator should be decided based on how often tool declarations change and whether maintaining one extra list is an acceptable tradeoff.
