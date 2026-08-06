namespace AiRaccoon.Core;

/// <summary>
///     Common response envelope for every MCP tool: the payload under Data, and the
///     waiting-promotion meta under Meta (what the agent still has to review). Required
///     members are positional, optional members are properties. The MCP output schema
///     derives from this record. Domain outcomes that are not plain success stay on the
///     McpException protocol channel — an in-band result slot was tried and removed
///     (docs/adr/0007) because every call site only ever produced the success sentinel.
/// </summary>
public sealed record ApiEnvelope<TData>(TData? Data, ResponseMeta Meta);

/// <summary>What is waiting for the agent right now; always present (zero is informative, never absent).</summary>
public sealed record ResponseMeta(
    int WaitingPromotionsCount,
    double? PromotionsWaitTimeSeconds,
    IReadOnlyDictionary<string, int>? WaitingByProject);
