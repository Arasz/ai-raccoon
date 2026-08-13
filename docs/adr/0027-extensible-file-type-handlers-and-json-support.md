# 0027 — Extensible FileType Handlers and JSON Support

Date: 2026-08-12

Status: Accepted.

## Context

Prior to this decision, `FileIngestor` hardcoded indexable file types using a static `HashSet<string> IndexableExtensions = { ".md", ".markdown", ".txt" }` and routed all ingested text directly to `TokenizerChunker` / `MarkdownChunker`.

This created two problems:
1. Adding support for new file formats (such as `.json`, `.xml`, `.yaml`, or `.html`) required modifying `FileIngestor` directly and lacked a clean extension point.
2. Ingesting `.json` files returned 0 chunks because `FileIngestor` dropped unsupported extensions at the outer threshold. When structured data like JSON was written directly via `memory_write`, it was chunked line-by-line as raw text without preserving JSON object key structures.

## Decision 1 — Core FileType Handler Abstractions

We introduce three focused interfaces in `AiRaccoon.Core`:

1. **`IFileTypeChunker`**:
   `IReadOnlyList<string> Chunk(string content, int maxTokens, int overlayTokens = 0)`
   Encapsulates content chunking algorithms for a specific format family.

2. **`IFileTypeHandler`**:
   Exposes `string Name`, `IReadOnlySet<string> Extensions`, and `IFileTypeChunker Chunker`.

3. **`IFileTypeMatcher`**:
   Exposes `bool TryGetHandler(string path, [NotNullWhen(true)] out IFileTypeHandler? handler)` and `bool IsSupported(string path)`.

`FileTypeMatcher` receives `IEnumerable<IFileTypeHandler>` via dependency injection, builds an immutable extension lookup dictionary using case-insensitive comparison (`StringComparer.OrdinalIgnoreCase`), and fails fast on startup if duplicate extension handlers are registered.

## Decision 2 — Markdown & Text Backward Compatibility

`MarkdownFileTypeHandler` encapsulates `.md`, `.markdown`, and `.txt` extensions, delegating chunking to the existing `TokenizerChunker` / `MarkdownChunker`. Existing markdown and plain-text ingestion behavior is 100% preserved.

## Decision 3 — Native JSON Support via JsonFileTypeChunker

`JsonFileTypeChunker` parses JSON using `System.Text.Json` (`JsonDocument` / `JsonElement`) and breaks JSON content into key-aware structural chunks (e.g. `{"key": value}`) that respect `maxTokens` boundaries.

If the input content is malformed or invalid JSON, `JsonFileTypeChunker` gracefully falls back to line-based chunking without throwing exceptions.

## Decision 4 — Extensible Roadmap for XML, YAML, and HTML

The `IFileTypeHandler` pattern establishes a clear blueprint for upcoming file format additions:

- **`.xml`**: `XmlFileTypeChunker` (`XmlFileTypeHandler` with `{ ".xml" }`) using `XmlReader` / element-tree chunking.
- **`.yaml` / `.yml`**: `YamlFileTypeChunker` (`YamlFileTypeHandler` with `{ ".yaml", ".yml" }`) preserving YAML key hierarchies.
- **`.html` / `.htm`**: `HtmlFileTypeChunker` (`HtmlFileTypeHandler` with `{ ".html", ".htm" }`) extracting text content by DOM block elements.

## Costs and Trade-offs

- `FileTypeMatcher` construction evaluates extension uniqueness at startup; duplicate registrations throw `InvalidOperationException`.
- JSON parsing allocates temporary `JsonDocument` instances during chunking; fallback path guarantees no ingestion failures on malformed JSON files.

## Amendment (2026-08-13) — chunker split and layer placement

The dependencies-refactor review (`docs/reviews/2026-08-13-architecture-review-ingestion-and-deps-refactor.md`)
reshaped the abstractions above:

- `IFileTypeChunker` was never introduced. The existing `IChunker` (Core) is retained as the base contract, with two format-specific marker interfaces added: `IMarkdownChunker : IChunker` and
  `IJsonChunker : IChunker` (both `AiRaccoon.Core.Chunking`). Handlers take the specific interface, so the DI graph cannot mis-wire them (`MarkdownFileTypeHandler(IMarkdownChunker)`,
  `JsonFileTypeHandler(IJsonChunker)`).
- `MarkdownChunker` (Core) is now an injectable `IMarkdownChunker` (constructor `TokenCount`); the former
  `TokenizerChunker` was reduced to the o200k token counter `O200kTokenizer` (Infrastructure) and registered as the `TokenCount` delegate. Markdown chunking is fully in the pure layer; only the tokenizer and the JSON chunker remain in
  Infrastructure.
- `IFileTypeHandler` and `IFileTypeMatcher` live in `AiRaccoon.Core.Ingestion` (Decision 1's placement holds).
- `IFileIngestor` is an Infrastructure seam whose signature names `SqliteConnection`: the caller opens the bank once and hands the open connection in so ingestion can join an existing write transaction (`SqliteMemoryStore.ReplaceFileAsync`
  re-ingests with `embedInline: false`). It is intentionally not a Core abstraction.
