## Constructor-Validated Records

When a record must reject invalid input **at construction** (blank ids, out-of-range limits), use an explicit constructor with guards plus get-only auto-properties. Optional params get defaults in the constructor signature; callers use
named arguments.

```csharp
public sealed record MemoryWriteRequest
{
    public MemoryWriteRequest(
        string projectId,
        string content,
        string? context = null,
        bool isolated = false,
        string? agentId = null,
        string? workspaceId = null)
    {
        Guard.NotNullOrWhiteSpace(projectId, nameof(projectId));
        Guard.NotNullOrWhiteSpace(content, nameof(content));

        ProjectId = projectId;
        Content = content;
        Context = context;
        Isolated = isolated;
        AgentId = agentId;
        WorkspaceId = workspaceId;
    }

    public string ProjectId { get; }
    public string Content { get; }
    public string? Context { get; }
    public bool Isolated { get; }
    public string? AgentId { get; }
    public string? WorkspaceId { get; }

    // Computed property — no backing field, so record equality ignores it.
    public string ContextName => ContextNaming.WorkspaceContext(WorkspaceId!);
}
```

Why not the alternatives:

| Shape                                                                               | Problem                                                                                    |
|-------------------------------------------------------------------------------------|--------------------------------------------------------------------------------------------|
| Positional primary ctor + same-signature chaining ctor (`: this(...)` + validation) | Legal but easy to get wrong (defaults on both ctors, ambiguity risk)                       |
| `required ... init` properties                                                      | Compile-time presence, but cannot run validation logic                                     |
| Hand-rolled `?? throw` / `if (x == null) throw` inline                              | Repo invariants prefer a guard helper — reads as intent, consistent exception type/message |

Key nuance: **computed (expression-bodied) properties are NOT part of record value equality** — they have no backing field, so the synthesized `Equals`/`GetHashCode` skip them. A derived property (e.g.
`Context => ContextNaming.WorkspaceContext(Id)`) can therefore live inside a value-equality record safely. Stored auto-properties ARE compared.

Guard exception types: `ArgumentException` for blank strings, `ArgumentOutOfRangeException` for out-of-range numerics. Tests assert the specific type, not the base.
