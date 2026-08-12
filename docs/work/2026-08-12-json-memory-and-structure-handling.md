# Research: JSON Memory and Code Source Handling

**Date:** 2026-08-12 **Question:** How are *.json files handled as memory and code source files in AiRaccoon, do we extract document structure from them, and can code-review-graph be used to extract JSON structure?

## Findings

### F1 — AiRaccoon file ingestion ignores *.json files completely by default [READ]

FileIngestor restricts indexable file extensions strictly to markdown and plain text files. Any attempt to ingest `.json` files via `memory_ingest_file` or `memory_ingest_directory` results in the file being skipped.

**Evidence:** `/Users/arasz/RiderProjects/ai-raccoon/src/AiRaccoon.Infrastructure/Ingestion/FileIngestor.cs:24-25, 237` (`IndexableExtensions` set and `IsIndexableFile` check).

### F2 — Ingestion of *.json files via memory_ingest_file produces zero chunks [MEASURED]

Executing the AiRaccoon test suite confirms that `IsIndexableFile` filters out `.json` extensions prior to file reading or chunking, returning 0 inserted chunks.

**Evidence:** Executed `dotnet test tests/AiRaccoon.Tests/AiRaccoon.Tests.csproj --filter "FullyQualifiedName~Ingest|FullyQualifiedName~Chunk"` on macOS 26.6.1 (.NET 10.0.100), 144 passing tests.

### F3 — AiRaccoon has no structural chunking or section extraction for JSON content [READ]

Structure extraction in AiRaccoon relies exclusively on Markdown heading hierarchy (`HeadingPathParser.cs`). When text is written directly (e.g., via `memory_write`), `MarkdownChunker.cs` breaks text into line-granular units with code-fence
awareness. No JSON key hierarchy, JSONPath, or AST sectioning is performed. Furthermore, `SourcePathQuery` regex enforces `.md`, `.markdown`, or `.txt` for `file#section` queries.

**Evidence:** `/Users/arasz/RiderProjects/ai-raccoon/src/AiRaccoon.Core/Chunking/HeadingPathParser.cs:1-120`, `/Users/arasz/RiderProjects/ai-raccoon/src/AiRaccoon.Core/Chunking/MarkdownChunker.cs:11-46`,
`/Users/arasz/RiderProjects/ai-raccoon/src/AiRaccoon.Infrastructure/Sqlite/SourcePathQuery.cs:51`.

### F4 — code-review-graph does not extract structure from generic *.json files [READ]

`code-review-graph` relies on tree-sitter language parsers. Its `EXTENSION_TO_LANGUAGE` dictionary maps code files (`.py`, `.ts`, `.cs`, `.go`, `.yaml`, etc.) to language parsers but explicitly excludes `.json`. Specific JSON files
(`composer.json`, `tsconfig.json`, `.ipynb`) are only used internally for PHP module mapping, TS path alias resolution, or Jupyter notebook cell extraction. Generic JSON files generate no graph nodes or structural relationships.

**Evidence:** `/opt/homebrew/lib/python3.14/site-packages/code_review_graph/parser.py` (`EXTENSION_TO_LANGUAGE` definition and `CodeParser` methods).

### F5 — Dedicated JSON chunking or tree-sitter-json integration is required for structured JSON memory [INFERRED]

To support JSON files with structural sectioning (e.g., mapping object keys or JSONPath nodes to `section` and `heading_path` in memory.db), AiRaccoon would need a custom JSON chunker (using `System.Text.Json` DOM or `JsonDocument`) or an
extension to `code-review-graph`'s language parser.

Reasoning from: Findings F1, F3, and F4 above.

### F6 — Performance impact of minified or deeply nested JSON in raw memory_write was not benchmarked [UNVERIFIED]

No memory or tokenization benchmarks were run for minified single-line JSON or deeply nested JSON schemas written directly via `memory_write`.

## Still open

- Whether AiRaccoon should add `.json` to `IndexableExtensions` using a JSON object-key chunker vs. flattening JSON to Markdown / YAML blocks before ingestion.
- Whether `code-review-graph` should add a `json` tree-sitter parser or custom language configuration for config/data graph nodes.
