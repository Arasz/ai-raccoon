using AiRaccoon.Core.Memory;

namespace AiRaccoon.Tools;

/// <summary>
///     Common response envelope for every MCP tool: the payload under Data, and the
///     waiting-promotion meta under Meta. Required members are positional, optional properties.
///     Lives with the tools (host's wire shape); non-success outcomes stay on McpException (docs/adr/0007).
/// </summary>
public sealed record ApiEnvelope<TData>(TData? Data, PromotionMeta Meta);
