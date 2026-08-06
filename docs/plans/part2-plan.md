# Part 2 Plan — AiRaccoon Encryption/Config Subsystem

## What was reviewed

PR #31 "Complete encryption refactor: injected interfaces + logging in EncryptionCommands"
was reviewed for architecture, composition, component separation, and naming.
Two subagents (architect + dotnet-engineer) produced 10 findings; a plan review
gated the refactoring plan.

## Changes made (PR #32)

- **Extract `IEncryptionCommands` injectable class** — `EncryptionCommands` was a static
  partial class with 5 nullable deps threaded through `ConfigCommands.RunAsync`. Now an
  injectable component with constructor injection.
- **Shared `EncryptionKeyResolver.Create()` factory** — eliminates duplicated provider chain
  wiring between `CliCommandRunner` and `Dependencies.cs`.
- **`EncryptionState` → `EncryptionSidecar`** — "State" was misleading (stateless sidecar file
  I/O adapter). Interface renamed `IEncryptionSidecar`, added `FilePath` property.
- **`ConfigVerbRunner` → `CliCommandRunner`** — reflects responsibility (runs all CLI verbs,
  not just config verbs).
- **Deduplicated `FirstStderrLine`** → `BwsResult.FirstErrorLine` computed property.
- **Guard clauses in `EncryptionCommands` ctor** — 5 `Guard.IsNotNull` checks.
- **OpenSshKeyBuilder consolidated** — 5 duplicate copies replaced with shared
  `TestOpenSshKeyBuilder` in TestHelpers/ with proper PEM encoding.
- **Null suppression replaced** — `encryptionCommands!` → `?? ThrowHelper.ThrowArgumentNullException`.
- **CancellationToken last** — per .NET convention.
- **Naming consistency** — `_encryptionState` → `_sidecar`, `encryptionState` → `sidecar`.

## Deferred to Part 2

### 1. Extract remaining ConfigCommands verb families to injectable components

The new rule (static classes only for extensions/constants; logic in injectable components)
applies to all of `ConfigCommands`, not just the encryption handlers. Remaining handlers:

| Verb family | Lines | Complexity |
|-------------|-------|------------|
| access (default set/show, set, unset, list) | ~70 | Low — settings reads/writes only |
| model (set local/openai, reset, show) | ~70 | Low |
| retrieval (alpha set/show) | ~25 | Low |
| sweep (threshold set/show) | ~25 | Low |
| sync (add s3/azure, remove, show) | ~200 | Medium — interactive secrets, dual-backend |
| watch (enable/disable, scope add/remove/list, concurrency, list, registered, remove) | ~150 | Medium — WatchStore dependency |

Each family becomes `I<Verb>Commands` + `<Verb>Commands` with ctor injection.
`ConfigCommands.RunAsync` dispatches through the interfaces instead of calling
private static methods.

### 2. Convert remaining static utility classes to injectable components

| Class | Location | Logic | Action |
|-------|----------|-------|--------|
| `CliArgs` | `Setup/Cli/` | Parsing, command tree dispatch | Extract to `ICliArgsParser` |
| `CliCommandTree` | `Setup/Cli/` | Tree building | Convert to constants/config or builder |
| `CliRendering` | `Setup/Cli/` | Help/error rendering | Extract to `ICliRenderer` |
| `McpServerSetup` | `Setup/` | Host creation | Already extension-like, low priority |
| `OpenSshPrivateKeyParser` | `Core/Encryption/` | Pure function, already testable | Debatable whether rule applies |

### 3. Eliminate `EmbeddingService` + `TokenizerChunker` allocation in `CliCommandRunner`

`CliCommandRunner` creates a `SqliteMemoryStore` which demands `IChunker` and
`IEmbeddingService` — neither is used by the config verb path (config verbs only
read/write settings). Options:

- Accept null in `SqliteMemoryStore` ctor for config-verb-only usage
- Extract `ISettingsStore` interface that `SqliteMemoryStore` implements
- Use a `Lazy<>` wrapper

### 4. Audit static classes across the codebase against the new rule

Full list of static classes with logic (not extensions/constants):

**Core layer:**
- `AccessModePolicy` — parsing/serialization
- `MarkdownChunker` — chunking logic
- `DegradationPolicy` — policy logic
- `ContentHash` — hashing logic
- `RatingPolicy` — policy logic
- `OpenSshPrivateKeyParser` — parsing (pure function, borderline)
- `SshKeyDerivation` — derivation (pure function, borderline)
- `ContextNaming` — naming logic
- `WatchListFormat` — formatting
- `WatchPath` — path logic
- `WatchScopeList` — scope logic

**Infrastructure layer:**
- `RuntimePlatform` — platform detection
- `EmbeddingBlob` — blob operations
- `EmbeddingMath` — math operations
- `StructureFusion` — fusion logic
- `SyncProviderParser` — parsing
- `ContextResolver` — resolution logic
- `FtsQueryNormalizer` — query normalization
- `LikePattern` — pattern building
- `MemorySchema` — schema building
- `MemorySql` — SQL building
- `ReciprocalRankFusion` — ranking logic
- `SearchContexts` — context building
- `SearchResultMerger` — merging logic
- `SnippetFallback` — fallback logic
- `SourceAffinityRanker` — ranking logic
- `SourcePathQuery` — query building
- `SqliteEncryptionInit` — encryption init

### 5. Remaining OpenSshKeyBuilder copies in test files

The BDD copy (`EncryptionBitwardenFeatureContext.cs`) was NOT consolidated onto
`TestOpenSshKeyBuilder` (different `Build(seed, pub)` signature). 4 old copies
still exist in unit test files but are now unused (tests migrated to the shared
helper) — they can be deleted.

### 6. Null-guard constructor tests for `EncryptionCommands`

Tests exist for the 5 guard clauses (added by subagent). Verify coverage is
complete and assertions check the correct exception type.

### 7. Documentation updates

- CLAUDE.md — check for stale references to `EncryptionState`, `ConfigVerbRunner`
- `.ai-badger/status-notes.json` — may reference old names
- Any plan docs referencing the old architecture

---

## Priority ordering for Part 2

1. Delete unused `OpenSshKeyBuilder` copies (cleanup, no risk)
2. Extract `IWatchCommands` + `ISyncCommands` (largest verb families, most deps)
3. Extract `IAccessCommands`, `IModelCommands`, `IRetrievalCommands`, `ISweepCommands`
4. Audit + convert Core layer static classes
5. Audit + convert Infrastructure layer static classes
6. Eliminate wasted `SqliteMemoryStore` allocations in verb path
7. Documentation update
