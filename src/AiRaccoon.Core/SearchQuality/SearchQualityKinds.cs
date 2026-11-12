using AiRaccoon.Core.Memory;

namespace AiRaccoon.Core.SearchQuality;

/// <summary>Maps historical corpus selections onto the entry types they included.</summary>
public static class SearchQualityKinds
{
    /// <summary>
    ///     The flag set an old kind stands for, or null for a row that carries no kind (a row older
    ///     than the kind column: it ran the memory leg and said nothing about which).
    /// </summary>
    public static EntryType? MaskOf(string? kind) =>
        kind switch
        {
            SearchKindWireNames.Memory => EntryType.Memory | EntryType.File,
            SearchKindWireNames.Code => EntryType.Code,
            SearchKindWireNames.Both => EntryType.Memory | EntryType.File | EntryType.Code,
            _ => null
        };

    /// <summary>
    ///     The legacy kind label a selection records: code alone is <c>code</c>, any set that also
    ///     names Code is <c>both</c>, anything else is <c>memory</c>. The inverse of
    ///     <see cref="MaskOf" /> up to the corpus the label can express.
    /// </summary>
    public static string KindOf(EntryType types) =>
        types == EntryType.Code ? SearchKindWireNames.Code
        : types.HasFlag(EntryType.Code) ? SearchKindWireNames.Both
        : SearchKindWireNames.Memory;
}
