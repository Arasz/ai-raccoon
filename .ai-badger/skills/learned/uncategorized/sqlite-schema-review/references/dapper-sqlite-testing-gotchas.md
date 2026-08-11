# Dapper + SQLite integration testing gotchas

Compilation pitfalls hit writing integration tests against `SqliteMemoryStore` (ai-raccoon, 2026-08). All three are "compiles on second try" issues — the fix is a one-line change, but the error message doesn't point at the real cause.

## 1. `ExecuteScalarAsync<T>` requires `using Dapper;`

```csharp
// CS1061: 'SqliteConnection' does not contain a definition for 'ExecuteScalarAsync'
var count = await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM entries");
```

**Fix:** Add `using Dapper;` to the file. The `ExecuteScalarAsync<T>` extension method lives in Dapper, not in `System.Data` or `Microsoft.Data.Sqlite`. Without the using, the compiler sees only the ADO.NET `ExecuteScalarAsync()` (returns
`object?`, not `T`).

## 2. Raw SQL needs `CommandDefinition` wrapper

```csharp
// CS1503: cannot convert from 'string' to 'CommandDefinition'
var id = await connection.ExecuteScalarAsync<long>("SELECT ...", cancellationToken);
```

**Fix:** Wrap in `CommandDefinition`:

```csharp
var id = await connection.ExecuteScalarAsync<long>(
    new CommandDefinition("SELECT ...", cancellationToken: cancellationToken));
```

Dapper's `ExecuteScalarAsync<T>(CommandDefinition)` is the extension method that accepts
`CancellationToken`. The raw-string overload doesn't take a token. Note the **named parameter**
`cancellationToken:` — positional won't compile because `CommandDefinition` has other optional params before it.

## 3. Shouldly `ShouldContain` lambda + `is` pattern = CS8122

```csharp
// CS8122: An expression tree may not contain an 'is' pattern-matching operator.
results.ShouldContain(r => r.SourceFile is not null);
```

**Fix:** Use LINQ `.Any()` + `.ShouldBeTrue()`:

```csharp
results.Any(r => r.SourceFile is not null).ShouldBeTrue("...");
```

`ShouldContain(predicate)` compiles the lambda as an expression tree (for EF/provider translation). Expression trees don't support `is` pattern matching. `.Any()` is a normal Func delegate — no expression tree, no restriction.

## 4. Verification tests in TDD (GREEN-first shape)

When a work package's test exercises production code already implemented by an earlier work package, the test is GREEN immediately. This is a valid TDD pattern — the test serves as a **verification gate** for existing code, not as a RED
driver for new code. Example: WP6's
`FileIngest_CreatesSourceId_ForIngestedChunks` was GREEN because WP2's `WriteAsync` already resolves `source_id`. The test still has value: it proves the integration contract holds end-to-end through the watch pipeline, not just at the unit
level.
