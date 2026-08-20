# C# Domain Model Extension — TDD Workflow

When extending an existing C# aggregate with a new subdomain (e.g., adding practice sessions to an Interview aggregate), follow this order. Each step is a buildable checkpoint.

## Step order

1. **Write failing tests first** — Create the test file covering: creation defaults, enum values, parent aggregate delegation methods (including not-found error cases), step type constants, schema generation (non-empty, no exceptions), and
   JSON serialization round-trips.

2. **Build to confirm failure** — `dotnet build tests/Project.Tests/Project.Tests.csproj` should show `CS0246: type or namespace name could not be found`.

3. **Create domain records** — New file in the subdirectory (e.g., `InterviewPrep/PracticeSession.cs`). Pattern:
    - `sealed record` with `required` for ID fields, default empty collections for lists
    - `init`-only properties everywhere (immutable)
    - `[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]` on records used via reflection/serialization
    - Enum in the same file when tightly coupled

4. **Extend parent record** — Add properties and delegation methods:
   ```csharp
   public IReadOnlyList<NewChild> Children { get; init; } = [];
   public Interview AddChild(NewChild child) => this with { Children = [..Children, child] };
   public Interview UpdateChild(NewChild child) => this with { Children = Children.Select(c => c.Id == child.Id ? child : c).ToList() };
   ```

5. **Add step type enum members** — Both the `StepType` enum and the string constants class (`XxxStepTypes`).

6. **Add LLM schema contracts** — Internal records with `[AdditionalProperties(false)]`, `[Required]`, `[MinLength]`, `[MinItems]`, `[MaxItems]` attributes. Generate schemas via `JsonSchemaGeneration.Generate<T>()`.

7. **Add parent aggregate delegation** — Methods on the Application-level aggregate that call `FindXxx()` then delegate:
   ```csharp
   public Application AddChild(string parentId, NewChild child) =>
       UpdateParent(FindParent(parentId).AddChild(child));
   ```

8. **Build and run tests** — `dotnet build && dotnet test --filter "RequiresInfra!=true"`.

## Serialization round-trip test pattern

```csharp
private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
{
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
};

[Fact]
public void Thing_round_trips_through_json()
{
    var original = BuildThingWithAllFieldsPopulated();
    var json = JsonSerializer.Serialize(original, Options);
    var deserialized = JsonSerializer.Deserialize<Thing>(json, Options);
    var reserialized = JsonSerializer.Serialize(deserialized, Options);
    reserialized.ShouldBe(json);
}
```

Key: set `DefaultIgnoreCondition = WhenWritingNull` to match the runtime serializer config. Round-trip equality proves all fields survive.

## Pitfalls

- **`namespace` vs `using` when rewriting files** — If the original file uses `namespace Foo.Bar;` (file-scoped), don't accidentally replace it with `using Foo.Bar;`. The `patch` tool's fuzzy matching can cause this when context lines
  overlap. Read the file after patching to verify.
- **Missing `using` in test files** — Domain subdirectories (e.g., `InterviewPrep`) require explicit `using` in tests even when the parent namespace is already imported. The build error `CS0246` is the signal.
- **Rider MCP tools and worktrees** — See `hermes-mcp-setup` skill pitfalls. Fall back to `terminal`/`write_file`/`patch`.
