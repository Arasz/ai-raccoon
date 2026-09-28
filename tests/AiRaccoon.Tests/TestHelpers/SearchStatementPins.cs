namespace AiRaccoon.Tests.TestHelpers;

/// <summary>
///     The search-path statement counts the pins assert, in one home: the FTS-only shape
///     (<c>vectorWeight: 0</c>) is pinned twice — through the evidence pipeline and through the full
///     tools path — so its literal lives here instead of being pair-updated by hand between the two
///     tests. Deliberate, not incidental: adding a query to the search path means updating these
///     literals (SearchResultsTests.PhaseNames precedent: the pin is the review gate), and any
///     search-path query change must reconcile both shapes.
/// </summary>
public static class SearchStatementPins
{
    /// <summary>
    ///     The FTS-only path with evidence flowing: 16 since the pooled-handle cache
    ///     (<c>SqliteConnectionFactory.InitializedHandles</c> — a re-open of an initialised handle
    ///     reads the bank state once instead of <c>PRAGMA user_version</c> + <c>application_id</c>),
    ///     +1 the ADR-0124 every-open vec-trigger-body probe (2026-09-28), the open path's fourth
    ///     schema/watch check. Asserted by <c>SearchEvidencePipelineTests</c>' pipeline pin and by
    ///     the P4 G5 conjunction through the full tools path.
    /// </summary>
    public const int FtsOnly = 16;

    /// <summary>
    ///     The both-legs path with evidence flowing on both legs: 3 open PRAGMAs, 1 bank-state read
    ///     on an already-initialised pooled handle, 4 schema/watch checks (the ADR-0124
    ///     vec-trigger-body probe joined them 2026-09-28), the 2-statement settings snapshot, 5
    ///     embedding-setting reads, 1 context resolve, 2 shared + 2 project vector-candidate queries,
    ///     2 FTS candidate queries, 1 grouped snippet lookup, and 1 access bump per served row (5).
    /// </summary>
    public const int BothLegs = 28;
}
