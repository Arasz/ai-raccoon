namespace AiRaccoon.Core;

/// <summary>
///     Common response envelope for every MCP tool: the payload under Data, the waiting-
///     promotion meta under Meta (what the agent still has to review), and the in-band
///     domain outcome under Result. Required members are positional, optional members are
///     properties. The MCP output schema derives from this record.
/// </summary>
public sealed record ApiEnvelope<TData>(TData? Data, ResponseMeta Meta, OperationStatus Result);

/// <summary>What is waiting for the agent right now; always present (zero is informative, never absent).</summary>
public sealed record ResponseMeta(
    int WaitingPromotionsCount,
    double? PromotionsWaitTimeSeconds,
    IReadOnlyDictionary<string, int>? WaitingByProject);

/// <summary>
///     In-band domain outcome with an HTTP status code. Protocol errors (invalid params,
///     access denied) stay McpException — the transport channel; this is for outcomes a
///     client can act on structurally.
/// </summary>
public sealed record OperationStatus(int Code)
{
    public string? Message { get; init; }

    public static readonly OperationStatus Ok = new(200) { Message = "ok" };
}
