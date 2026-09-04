namespace AiRaccoon.Core.Memory;

/// <summary>Which corpus memory_search reaches into; both is the default (memory + code, no cross-corpus fusion).</summary>
public enum SearchKind
{
    Memory,
    Code,
    Both
}

/// <summary>
///     Single source for the search-kind wire/storage vocabulary: the MCP <c>kind</c> param,
///     the <c>search_quality.kind</c> column (CHECK + backfill), and the dispatcher's stored value.
///     A rename must update this class, not three string literals — the pin test
///     (<c>SearchKindWireNames_AreTheStableContract</c>) fails the build instead of a CHECK
///     violation failing silently at runtime (quality recording is fire-and-forget).
/// </summary>
public static class SearchKindWireNames
{
    public const string Memory = "memory";
    public const string Code = "code";
    public const string Both = "both";

    public static string ToWireString(this SearchKind kind) => kind switch
    {
        SearchKind.Memory => Memory,
        SearchKind.Code => Code,
        SearchKind.Both => Both,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown search kind")
    };
}
