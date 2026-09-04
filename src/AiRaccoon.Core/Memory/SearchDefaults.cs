namespace AiRaccoon.Core.Memory;

/// <summary>
///     Single place of truth for the search result-shape defaults (ADR-0096): 8 hits per
///     leg, keeping only hits scoring at least 0.6 of the response's top hit. A separate
///     static class (not consts on <see cref="SearchQuery" />) because a record
///     primary-constructor default cannot reference a sibling const (CS0103), while an
///     external type's const is fine — so both the record signature and the MCP tool
///     layer bind here with no literal duplication and nothing to keep in sync.
/// </summary>
public static class SearchDefaults
{
    public const int Limit = 8;
    public const double MinRelativeScore = 0.6;
}
