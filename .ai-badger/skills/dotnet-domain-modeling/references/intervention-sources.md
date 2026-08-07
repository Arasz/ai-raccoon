## Intervention Sources

When a domain feature raises interventions on another aggregate:

```csharp
// 1. Define the constant
public static class ApplicationInterventionSource
{
    public const string ChannelMonitoring = "channelMonitoring";
}

// 2. Register in the aggregate's LocalInterventionSources
protected override HashSet<string> LocalInterventionSources { get; } =
    [..existing, ApplicationInterventionSource.ChannelMonitoring];

// 3. Test both raise and clear
[Fact]
public void RequireIntervention_from_channelMonitoring_succeeds() { ... }
[Fact]
public void ClearIntervention_from_channelMonitoring_succeeds() { ... }
```
