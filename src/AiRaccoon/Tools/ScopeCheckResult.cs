namespace AiRaccoon.Tools;

/// <summary>Whether a tool can use the requested project scope.</summary>
public sealed record ScopeCheckResult(bool IsAllowed);
