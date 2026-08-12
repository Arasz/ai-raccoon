# Implementation Plan: Extensible FileType Handlers and JSON Chunking (MoE Refined)

**Goal:** Refactor AiRaccoon's file ingestion architecture to support extensible file types via `IFileTypeHandler` and `IFileTypeMatcher`, add native `.json` file support with a structured `JsonFileTypeChunker`, and record an ADR covering present and future file type additions (XML, YAML, HTML).

---

## 1. Architectural Design & Contracts

### 1.1 Core Interfaces (`AiRaccoon.Core/Ingestion/` & `AiRaccoon.Core/Chunking/`)

- Re-use domain interface `IChunker` (`AiRaccoon.Core.Chunking.IChunker`):
  ```csharp
  namespace AiRaccoon.Core.Chunking;

  public interface IChunker
  {
      IReadOnlyList<string> Chunk(string text, int maxTokens, int overlayTokens = 0);
  }
  ```

- **`IFileTypeHandler`** (`AiRaccoon.Core.Ingestion.IFileTypeHandler`):
  ```csharp
  namespace AiRaccoon.Core.Ingestion;

  public interface IFileTypeHandler
  {
      string Name { get; }
      IReadOnlySet<string> Extensions { get; }
      IChunker Chunker { get; }
  }
  ```

- **`IFileTypeMatcher`** (`AiRaccoon.Core.Ingestion.IFileTypeMatcher`):
  ```csharp
  namespace AiRaccoon.Core.Ingestion;

  public interface IFileTypeMatcher
  {
      bool TryGetHandler(string path, [NotNullWhen(true)] out IFileTypeHandler? handler);
      bool IsSupported(string path);
      IReadOnlySet<string> SupportedExtensions { get; }
  }
  ```

### 1.2 Default Implementations & Extensions

1. **`FileTypeMatcher`** (`AiRaccoon.Infrastructure.Ingestion.FileTypeMatcher`):
   - Accepts `IEnumerable<IFileTypeHandler>` in constructor.
   - Normalizes extensions (leading dot, lowercase).
   - Throws `InvalidOperationException` on startup if duplicate extensions are registered across handlers.
   - Uses `FrozenDictionary<string, IFileTypeHandler>` or `Dictionary<string, IFileTypeHandler>(StringComparer.OrdinalIgnoreCase)` for O(1) lookup.

2. **`MarkdownFileTypeHandler`** (`AiRaccoon.Infrastructure.Ingestion.MarkdownFileTypeHandler`):
   - `Name`: `"Markdown"`.
   - `Extensions`: `{ ".md", ".markdown", ".txt" }`.
   - `Chunker`: `TokenizerChunker` (or delegates to `MarkdownChunker`).

3. **`JsonFileTypeChunker` & `JsonFileTypeHandler`**:
   - `Name`: `"Json"`.
   - `Extensions`: `{ ".json" }`.
   - `JsonFileTypeChunker` implements `IChunker`:
     - Parses input string with `JsonDocument`.
     - Structures JSON elements (top-level properties, object entries, array items) into token-bounded JSON chunks.
     - Respects `maxTokens` and `overlayTokens`.
     - **Exception Safety:** If `JsonException` or format error occurs, falls back to `MarkdownChunker.Split` without throwing.

### 1.3 FileIngestor Refactoring (`AiRaccoon.Infrastructure/Ingestion/FileIngestor.cs`)

- Inject `IFileTypeMatcher` into `FileIngestor`.
- Replace static `IsIndexableFile` / `IndexableExtensions` check with `_matcher.TryGetHandler(path, out var handler)`.
- Call `handler.Chunker.Chunk(content, chunkMaxTokens, chunkOverlayTokens)`.

### 1.4 DI Wire-Up (`Dependencies.cs` & `CliCommandRunner.cs`)

- Register `IFileTypeHandler` implementations: `MarkdownFileTypeHandler`, `JsonFileTypeHandler`.
- Register `IFileTypeMatcher` -> `FileTypeMatcher`.
- Register `FileIngestor` resolving `IFileTypeMatcher`.

---

## 2. TDD & Testing Strategy

1. **`FileTypeMatcherTests`**:
   - Matches `.json`, `.md`, `.markdown`, `.txt`.
   - Matches case-insensitively (`.JSON`, `.MD`).
   - Handles paths with or without full directory strings (`"path/to/file.JSON"`).
   - Returns `false` for unsupported extensions (`.bin`, `.exe`, `.pdf`).
   - Throws on duplicate extension registration across multiple handlers.

2. **`JsonFileTypeChunkerTests`**:
   - Valid JSON object -> formatted JSON chunks.
   - Large JSON payload exceeding `maxTokens` -> split across multiple token-bounded chunks.
   - Malformed JSON -> falls back to plain text line chunking without throwing exception.
   - Empty JSON `{}` / `[]` -> empty or single minimal chunk.
   - Primitive JSON / scalar string -> single chunk.

3. **`FileIngestorTests` & Integration**:
   - `IngestFileAsync` with `.json` file -> successfully inserts chunks into bank.
   - `IngestDirectoryAsync` with mixed directory (`.md`, `.json`, `.png`) -> ingests `.md` and `.json`, ignores `.png`.
