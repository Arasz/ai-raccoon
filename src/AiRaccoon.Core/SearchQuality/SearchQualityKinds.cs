namespace AiRaccoon.Core.SearchQuality;

using AiRaccoon.Core.Memory;

/// <summary>Maps historical corpus selections onto the entry types they included.</summary>
public static class SearchQualityKinds
{
    /// <summary>
    ///     The flag set an old kind stands for, or null for a row that carries no kind (a row older
    ///     than the kind column: it ran the memory leg and said nothing about which).
    /// </summary>
    public static EntryType? MaskOf(string? kind) => kind switch
    {
        SearchKindWireNames.Memory => EntryType.Memory | EntryType.File,
        SearchKindWireNames.Code => EntryType.Code,
        SearchKindWireNames.Both => EntryType.Memory | EntryType.File | EntryType.Code,
        _ => null
    };
}
