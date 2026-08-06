# Injectable Components (Static-Class Policy)

Rule (AiRaccoon project convention): **the only allowed static classes are extensions and
const/readonly value sources. Any class that contains logic must be an injectable component
with an interface** — easy to mock in unit tests.

## What's allowed as static

| Allowed | Example |
|---------|---------|
| Extension methods | `CliOptionsExtensions.ToServerConfig(this CliOptions)` |
| Constants / settings keys | `EncryptionSettingsKeys.Source = "encryption.source"` |
| Pure-data records (not static) | `sealed record EncryptionData(string Source)` |
| `Dependencies` extension on `IServiceCollection` | `extension(IServiceCollection services)` |

## What must be injectable

Any class whose methods contain non-trivial logic (control flow, I/O, decisions, state machines,
or call external dependencies) must:

1. Be an instance class (not static)
2. Have a corresponding interface (e.g. `IEncryptionCommands`)
3. Receive dependencies through constructor injection
4. Be registered in DI or composed at the composition root

## Conversion pattern: static → injectable

Before:
```csharp
internal static partial class ConfigCommands
{
    private static async Task<int> EncryptionBitwardenAsync(
        ParseResult parseResult, IMemoryStore store,
        SqliteConnectionFactory? bank, ICliSecretManager? bws,
        IEncryptionKeyProvider? env, IEncryptionState? encryptionState,
        ILogger? logger, CancellationToken ct)
    {
        Guard.IsNotNull(bank);
        Guard.IsNotNull(bws);
        // ... handler logic
    }
}
```

After:
```csharp
public interface IEncryptionCommands
{
    Task<int> BitwardenAsync(ParseResult parseResult, IMemoryStore store,
        TextWriter stdout, TextWriter stderr, TextReader stdin,
        CancellationToken ct);
    Task<int> ShowAsync(IMemoryStore store, TextWriter stdout, CancellationToken ct);
    Task<int> UnsetAsync(IMemoryStore store, TextWriter stdout,
        TextWriter stderr, CancellationToken ct);
}

internal sealed class EncryptionCommands : IEncryptionCommands
{
    private readonly SqliteConnectionFactory _bank;
    private readonly ICliSecretManager _bws;
    private readonly IEncryptionKeyProvider _env;
    private readonly IEncryptionState _encryptionState;
    private readonly ILogger _logger;

    public EncryptionCommands(
        SqliteConnectionFactory bank,
        ICliSecretManager bws,
        IEncryptionKeyProvider env,
        IEncryptionState encryptionState,
        ILogger<EncryptionCommands> logger)
    {
        _bank = bank;
        _bws = bws;
        _env = env;
        _encryptionState = encryptionState;
        _logger = logger;
    }

    public async Task<int> BitwardenAsync(...) { /* same logic, field access */ }
}
```

Benefits:
- Tests can mock `IEncryptionCommands` instead of threading nullable params
- Dependencies are explicit at construction, not scattered through nullable optional params
- Adding a dependency doesn't change the public interface's parameter list
- New rule compliance: no static class with logic

## Relationship to existing patterns

This rule applies to **service/component classes** — classes that hold dependencies, manage
state, or coordinate I/O. It does NOT apply to:

- **Pure static utility functions** (e.g., `EmbeddingMath.CosineSimilarity`, `ContentHash.Compute`)
  These are computations with no dependencies, no state, and trivial testability. They should
  eventually be converted to injectable components too, but the priority is on classes that
  orchestrate dependencies (command handlers, resolvers, composition roots).

- **Pipeline stages in the Deterministic Classification Pipeline pattern** — these are pure
  functions with Guard.IsNotNull at entry. When they grow dependencies, convert them to
  injectable components.

## Migration priority

1. Command/verb handlers (e.g., `ConfigCommands`, `EncryptionCommands`) — highest priority,
   these have the most dependencies threaded through parameters
2. CLI infrastructure (`CliArgs`, `CliRendering`) — parsing and rendering logic
3. Composition roots (`ConfigVerbRunner`) — thin wrapper that becomes an injectable orchestrator
4. Pure utility classes (`EmbeddingMath`, `ContentHash`, etc.) — lowest priority, least benefit
