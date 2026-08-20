# ILogger Constructor Injection — Test Update Pattern

When a C# class gains `ILogger<T>` as a new constructor parameter, every test that instantiates that class directly needs updating. This is a mechanical but error-prone pass.

## Step-by-step

### 1. Find all instantiation sites

```bash
grep -rn "new ClassName(" tests/ --include="*.cs"
```

### 2. For each file, check if `Microsoft.Extensions.Logging.Abstractions` is already imported

**This is the #1 gotcha.** Files that already use `NullLogger<T>` for OTHER classes (e.g.
`NullLogger<ChannelIngestPipeline>.Instance`) already have the import. Adding it again causes `CS0105: The using directive appeared previously` — a build-breaking error.

**Safe approach:**

```bash
grep -l "Microsoft.Extensions.Logging.Abstractions" tests/MyFile.cs
```

- If found → do NOT add the using, just update the constructor call.
- If not found → add `using Microsoft.Extensions.Logging.Abstractions;`

### 3. Update each constructor call

```csharp
// Before:
new MyClass(dependency1, userId)

// After:
new MyClass(dependency1, userId, NullLogger<MyClass>.Instance)
```

### 4. Build and verify

```bash
dotnet build 2>&1 | grep "error CS"
```

Common failures:

- `CS0105` — duplicate using (remove the extra one)
- `CS7036` — missing required parameter (didn't add NullLogger to all sites)
- `CS0234` — type not found (missing the using entirely)

## Pitfalls

### Duplicate `using` from bulk patching

When using the `patch` tool to add `using Microsoft.Extensions.Logging.Abstractions;` to multiple files at once, it may add the import to files that already have it (e.g. files that use `NullLogger<SomeOtherClass>` for other constructor
calls). Always check with `grep` before patching, or run `dotnet build` immediately after to catch `CS0105`.

### The guard-check test also needs the logger

If a test verifies that the constructor throws on invalid input (e.g. `Should.Throw<ArgumentException>(() => new MyClass(classifier, "  "))`), that call also needs the logger parameter:

```csharp
Should.Throw<ArgumentException>(() => new MyClass(classifier, "  ", NullLogger<MyClass>.Instance));
```

The logger is not the cause of the guard failure, but the constructor won't compile without it.

### DI registration sites (non-test)

Production DI registration in `ChannelIngestDependencies.cs` or similar also needs updating, but uses `serviceProvider.GetRequiredService<ILogger<T>>()` instead of `NullLogger`:

```csharp
new LlmClassificationStage(
    serviceProvider.GetRequiredService<LlmEmailClassifier>(),
    userId,
    serviceProvider.GetRequiredService<ILogger<LlmClassificationStage>>()
)
```
