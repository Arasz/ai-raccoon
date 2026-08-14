# Microsoft.ML.Tokenizers 2.0.0 — probed API surface & gotchas

Probed 2026-08 against packages `Microsoft.ML.Tokenizers 2.0.0` +
`Microsoft.ML.Tokenizers.Data.O200kBase 2.0.0` on .NET 10 (AiRaccoon FR-NM-10 chunking). All facts verified by compiling/running against the real packages.

## API surface (2.0.0 — differs from 1.x!)

No `O200kBase` class in 2.0.0 (it existed in 1.x). Public types in the
`Microsoft.ML.Tokenizers` assembly: `TiktokenTokenizer`, `BpeTokenizer`,
`WordPieceTokenizer`, `SentencePieceTokenizer`, `LlamaTokenizer`, `Phi2Tokenizer`,
`CodeGenTokenizer`, `EnglishRobertaTokenizer`, `BertTokenizer`, plus normalizers, pre-tokenizers, options, enums. No `O200k*` types at all.

Creating the o200k_base tokenizer (loads embedded BPE from the Data package):

```csharp
using Microsoft.ML.Tokenizers;
var tok = TiktokenTokenizer.CreateForEncoding("o200k_base");
```

`CreateForEncoding(string encodingName, IReadOnlyDictionary<string,int>? extraSpecialTokens = null, Normalizer? normalizer = null)`. Also `CreateForModel(...)` overloads take a vocab stream.

Counting:

```csharp
int CountTokens(string text);                                   // works — bools are optional
int CountTokens(string text, bool considerPreTokenization, bool considerNormalization);
IReadOnlyList<int> EncodeToIds(string text);                    // ids for special-token tests
```

`Tokenizer` is NOT `IDisposable` (verified via reflection) — no disposal wiring.

## Method-group conversion trap

`TiktokenTokenizer.CountTokens` has signature `int CountTokens(string, bool, bool)`
with optional bools. Optional parameters do NOT participate in method-group conversion, so `TokenCount c = tokenizer.CountTokens;` fails to compile. Wrap:

```csharp
private int CountTokens(string text) => _tokenizer.CountTokens(text);
```

## Dependency graph & vulnerability pin

- `Microsoft.ML.Tokenizers 2.0.0` net8.0 group depends ONLY on
  `Google.Protobuf 3.30.2`.
- `Microsoft.ML.Tokenizers.Data.O200kBase 2.0.0` is **netstandard2.0-only**; its nuspec pulls the full polyfill set: `Microsoft.Bcl.Memory 9.0.4`,
  `Microsoft.Bcl.AsyncInterfaces 9.0.4`, `System.IO.Pipelines 9.0.4`,
  `System.Text.Json 9.0.4`, `System.Buffers`, `System.Memory`, etc.
- `Microsoft.Bcl.Memory 9.0.4` is vulnerable (GHSA-73j8-2gch-69rq, patched 9.0.14 / 10.0.4). With `TreatWarningsAsErrors` the restore FAILS with NU1903 — before any code is compiled.
- Fix (central package management, `CentralPackageTransitivePinningEnabled=false`):
  a `PackageVersion` alone does NOT pin transitives — you must ALSO add a direct
  `PackageReference` in the project that consumes the tokenizers:

```xml
<!-- Directory.Packages.props -->
<PackageVersion Include="Microsoft.Bcl.Memory" Version="9.0.14" />
<PackageVersion Include="Microsoft.ML.Tokenizers" Version="2.0.0" />
<PackageVersion Include="Microsoft.ML.Tokenizers.Data.O200kBase" Version="2.0.0" />
```

```xml
<!-- referencing csproj -->
<PackageReference Include="Microsoft.Bcl.Memory"/>
<PackageReference Include="Microsoft.ML.Tokenizers"/>
<PackageReference Include="Microsoft.ML.Tokenizers.Data.O200kBase"/>
```

The nuspec constraint is a minimum (`9.0.4`), so 9.0.14 satisfies it with no NU1107. Bcl.Memory 9.0.14 ships a tiny `lib/net9.0` type-forwarder (16 KB, 145-byte XML) — on net10 the types come from the runtime, so the pin is compile-safe.

## Measured o200k_base counts (lock test fixtures to these)

| Text                                                                                                | Tokens |
|-----------------------------------------------------------------------------------------------------|--------|
| `""`                                                                                                | 0      |
| `"Hello"`                                                                                           | 1      |
| `"Hello world"`                                                                                     | 2      |
| `"Hello, world!"`                                                                                   | 4      |
| `"def add(a, b):\n    return a + b"`                                                                | 11     |
| `"## Section 1\n\nThis is paragraph 1 with enough words to make the note exceed the token budget."` | 22     |

Counting is deterministic (same input → same count, repeated calls).

## API-probing recipe (library changed under you)

1. Add the packages, `dotnet restore` the real project.
2. `dotnet new console` in `/tmp/<name>`, `dotnet add package` the same versions (NuGet cache is warm — fast).
3. Reflect over `typeof(Tokenizer).Assembly.GetExportedTypes()` /
   `GetMethods()` to dump the real surface instead of guessing.
4. Write a one-shot `Program.cs` that instantiates the factory and prints counts for the exact strings you plan to hardcode in tests.
5. Delete the scratch project afterwards.

## Layering pattern used (TextChunker-style)

- Core (dependency-free): `delegate int TokenCount(string)` + `IChunker`
  (`IReadOnlyList<string> Chunk(string text, int maxTokens, int overlayTokens = 0)`)
    + static `MarkdownChunker.Split(text, maxTokens, overlayTokens, TokenCount)` — pure, deterministic, fence-aware (lines whose trimmed start is ` ``` ` or `~~~`
      open/close atomic fence units; a fence is never split even past maxTokens; overlay = maximal suffix of the previous chunk's units within the overlay budget; guards: `maxTokens > 0`, `0 <= overlay < maxTokens`).
- Infrastructure: `TokenizerChunker : IChunker` wraps the tokenizer, passing
  `CountTokens` into the pure splitter.
