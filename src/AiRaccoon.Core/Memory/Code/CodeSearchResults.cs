namespace AiRaccoon.Core.Memory.Code;

/// <summary>Warning is the code section's own degraded-mode/trim note (§3.3/§3.6) — SearchDispatcher forwards it as-is.</summary>
public sealed record CodeSearchResults(IReadOnlyList<CodeSearchResult> Results, string? Warning = null);
