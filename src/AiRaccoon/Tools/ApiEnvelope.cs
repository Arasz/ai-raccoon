using AiRaccoon.Core.Memory;

namespace AiRaccoon.Tools;

/// <summary>
///     Common response envelope for every MCP tool: the payload under Data, and the
///     waiting-promotion meta under Meta (what the agent still has to review). Required
///     members are positional, optional members are properties. It lives with the tools
///     rather than in Core because it is the host's wire shape — Core's ports speak
///     PromotionMeta (#118 item 1). Domain outcomes that are not plain success stay on the
///     McpException protocol channel — an in-band result slot was tried and removed
///     (docs/adr/0007) because every call site only ever produced the success sentinel.
/// </summary>
public sealed record ApiEnvelope<TData>(TData? Data, PromotionMeta Meta);
