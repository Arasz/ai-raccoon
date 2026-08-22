namespace AiRaccoon.Tests.Integration.Retrieval;

/// <summary>
///     The tuning / held-out partition of the retrieval query catalog (docs/adr/0056). One source
///     for the ids the parameter sweeps both selected and gated on, and for the held-out sets
///     derived from them.
/// </summary>
internal static class RetrievalTuningSets
{
    /// <summary>
    ///     The 11 expected-source queries ADR-0005's and ADR-0006's grids selected their parameters
    ///     over — and gate them with. Every metric measured over these is in-sample by construction.
    /// </summary>
    public static readonly string[] TuningQueryIds = [];

    /// <summary>
    ///     The ids the parameter sweeps EVALUATE their chosen point against. Deliberately separate
    ///     from <see cref="TuningQueryIds" />, which records what a sweep once SELECTED parameters
    ///     over: on this corpus nothing was selected here, so that set is empty, and deriving the
    ///     sweep's evaluation set from it left both sweeps measuring zero queries and reporting
    ///     nDCG@5 = 0 (found while porting to the public corpus, ADR-0090). Derived from the
    ///     catalog rather than hand-listed so it cannot drift from what is gradeable.
    /// </summary>
    public static string[] SweepGateQueryIds(IEnumerable<CatalogQuery> catalog) =>
        [.. Gradeable(catalog).Select(q => q.Id)];

    /// <summary>The catalog entries that can be scored at all: those carrying an expectedSource.</summary>
    public static IReadOnlyList<CatalogQuery> Gradeable(IEnumerable<CatalogQuery> catalog) =>
        [.. catalog.Where(q => q.ExpectedSource is not null).OrderBy(q => q.Id, StringComparer.Ordinal)];

    /// <summary>
    ///     The document an expectedSource points at: everything before the '#section' suffix. Two
    ///     queries sharing a document are not independent — tuning on one leaks into the other.
    /// </summary>
    public static string Document(string expectedSource)
    {
        var hash = expectedSource.IndexOf('#', StringComparison.Ordinal);
        return hash < 0 ? expectedSource : expectedSource[..hash];
    }

    /// <summary>
    ///     What generated the document, from the expectedSource prefix: 'docs' is the reviewed
    ///     project's own tree, 'ai-badger' is the vendored framework. Measured on the corpus:
    ///     those are the only two generators docs-memory.db carries. // TODO(S6a): re-measure
    /// </summary>
    public static string Family(string expectedSource)
    {
        var colon = expectedSource.IndexOf(':', StringComparison.Ordinal);
        return colon < 0
            ? throw new InvalidOperationException($"expectedSource '{expectedSource}' carries no family prefix")
            : expectedSource[..colon];
    }

    /// <summary>The documents the sweeps tuned over — the leakage set.</summary>
    public static IReadOnlySet<string> TunedDocuments(IEnumerable<CatalogQuery> catalog) =>
        Gradeable(catalog)
            .Where(q => TuningQueryIds.Contains(q.Id))
            .Select(q => Document(q.ExpectedSource!))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    ///     Held out: gradeable queries whose expected document no tuning query touches. This is the
    ///     only set on which a pinned floor is an out-of-sample number.
    /// </summary>
    public static IReadOnlyList<CatalogQuery> HeldOut(IEnumerable<CatalogQuery> catalog)
    {
        var materialized = Gradeable(catalog);
        var tuned = TunedDocuments(materialized);
        return [.. materialized.Where(q => !tuned.Contains(Document(q.ExpectedSource!)))];
    }

    /// <summary>
    ///     Queries the sweeps never scored but whose document they did: a weaker tier, gated
    ///     separately and labelled, because the parameters saw this document's other sections.
    /// </summary>
    public static IReadOnlyList<CatalogQuery> DocumentLeaked(IEnumerable<CatalogQuery> catalog)
    {
        var materialized = Gradeable(catalog);
        var tuned = TunedDocuments(materialized);
        return
        [
            .. materialized.Where(q =>
                !TuningQueryIds.Contains(q.Id) && tuned.Contains(Document(q.ExpectedSource!)))
        ];
    }
}
