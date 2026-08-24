namespace AiRaccoon.Core.Memory;

/// <summary>Which corpus memory_search reaches into; both is the default (memory + code, no cross-corpus fusion).</summary>
public enum SearchKind
{
    Memory,
    Code,
    Both
}
