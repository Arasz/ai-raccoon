# TDD Workflow for Domain Models


### 1. Explore existing patterns first

Read 3–5 existing files in the same domain to understand:
- Naming conventions (sealed record, required props, factory methods)
- Guard clause style (ThrowHelper vs Guard vs custom exceptions)
- Test patterns (Shouldly assertions, helper factories, xunit v3)

### 2. Write failing tests (RED)

```csharp
[Fact]
public void Dismiss_from_Proposed_succeeds()
{
    var signal = NewSignal(disposition: SignalDisposition.Proposed);
    var result = signal.Dismiss();
    result.Disposition.ShouldBe(SignalDisposition.Dismissed);
}

[Fact]
public void Dismiss_from_Applied_throws()
{
    var signal = NewSignal(disposition: SignalDisposition.Applied);
    Should.Throw<InvalidOperationException>(() => signal.Dismiss());
}
```

### 3. Verify RED — build fails or tests throw NotImplementedException

**Simple case (one new type):** Build fails with CS0234/CS0246 (type not found).

**Multi-type case (3+ new types across multiple test files):** Create minimal stub files with `throw new NotImplementedException()` so the project compiles. Verify the test *runner* executes and tests fail at runtime — this confirms the test harness reaches your assertions correctly before you write any logic.

```bash
dotnet build  # Should succeed (stubs compile)
dotnet test --filter "FullyQualifiedName~MyFeature" --no-build  # Should fail with NotImplementedException
```

**Why stubs over compile failure for multi-type:** When tests reference 3–4 new types across multiple files, a compile error in one file masks whether other test files even parse correctly. Stubs let you validate the full test harness structure (namespaces, imports, helper factories) before implementing logic.

**Exception — uniform missing-namespace red (brand-new project/namespace):** When every test file fails with the SAME `CS0234: type or namespace 'X' does not exist` because the whole namespace is absent (new Core project), compile-error red is sufficient — skip the stubs. Nothing is masked: every file parses, only the namespace lookup fails, and the error IS "feature missing". Verify red once with `dotnet test`, implement, then green. This is the shape used when scaffolding a fresh pure-domain library (see `references/pure-domain-project-scaffolding.md`).

### 4. Implement minimal domain types (GREEN)

Create the types, make tests pass.

**Port/interface member additions (contract rework):** when the corrected
contract adds a method to a domain port (e.g. `IMemoryStore.ShareAsync`), the
Domain test demanding it (a stub implementor + assertion on the returned
entry) fails to compile with `CS0535: does not implement interface member`
in EVERY downstream implementor. That is the expected red — the downstream
breakage is the next red, not a regression. Add the member to the interface
(no default impl unless the contract allows DIM), then resolve implementors
one at a time with their own failing tests. Grep all errors, not the first:
`dotnet build 2>&1 | grep -E "error CS" | sort | uniq -c` — the build stops
at the first failing project, hiding downstream test errors until the
upstream project compiles.

### 5. Verify GREEN — all tests pass

```bash
dotnet test --filter "RequiresInfra!=true"
```

### 6. Verify domain purity still passes

```bash
dotnet test --filter "DomainPurity"
```

### 0. Scaffolding a brand-new pure-domain project

Adding a new Core library to an existing solution (csproj shape, `InternalsVisibleTo` SDK item, slnx membership, zero-dependency Guard class, uniform missing-namespace red phase, test-count semantics)? See `references/pure-domain-project-scaffolding.md`.

