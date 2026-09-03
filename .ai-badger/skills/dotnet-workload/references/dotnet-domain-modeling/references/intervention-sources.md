## Intervention Sources

When a domain feature raises interventions on another aggregate:

```csharp
// 1. Define the constant
public static class <Aggregate>InterventionSource
{
    public const string ChannelMonitoring = "channelMonitoring";
}

// 2. Register in the aggregate's LocalInterventionSources
protected override HashSet<string> LocalInterventionSources { get; } =
    [..existing, <Aggregate>InterventionSource.ChannelMonitoring];

// 3. Test both raise and clear
[Fact]
public void RequireIntervention_from_channelMonitoring_succeeds() { ... }
[Fact]
public void ClearIntervention_from_channelMonitoring_succeeds() { ... }
```
