namespace AiRaccoon.Core.Memory;

/// <summary>How a stored kind reads: a kind the row declares, a row that declares none, or a value this build does not know.</summary>
public enum StoredKindState
{
    /// <summary>The row declares a kind.</summary>
    Declared,

    /// <summary>The row declares no kind — it carries no value, or the <c>none</c> marker.</summary>
    Undeclared,

    /// <summary>The row carries a value that is not a kind this build knows: a defect, not a declaration.</summary>
    Unknown
}

/// <summary>
///     A stored kind, read as one of its three states. The three are not the same: an undeclared
///     row may be resolved from its shape, an unknown value may not — it is evidence, and rewriting
///     it is what destroys it.
/// </summary>
public readonly record struct StoredKind(StoredKindState State, EntryKind Kind, string? Raw)
{
    /// <summary>A row that declares the given kind.</summary>
    public static StoredKind Of(EntryKind kind) => new(StoredKindState.Declared, kind, null);

    /// <summary>A row that declares no kind.</summary>
    public static StoredKind Undeclared => new(StoredKindState.Undeclared, EntryKind.None, null);

    /// <summary>A row carrying a value this build does not know.</summary>
    public static StoredKind OfUnknown(string raw) => new(StoredKindState.Unknown, EntryKind.None, raw);
}
