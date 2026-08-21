using AiRaccoon.Core.Memory.Code;

namespace AiRaccoon.Core.Memory;

/// <summary>
///     What a <see cref="ISearchDispatcher" /> call produced: the memory leg's results (plus the
///     raw <see cref="SearchResults" /> the caller needs for measurement recording, when that leg
///     ran), and the code leg's results plus any warning it carries. Code is null exactly when
///     the requested kind never runs the code leg — see <see cref="ISearchDispatcher" />.
/// </summary>
public sealed record SearchDispatchResult(
    IReadOnlyList<MemorySearchResult> Results,
    SearchResults? MemorySearchResults,
    IReadOnlyList<CodeSearchResult>? CodeResults,
    string? CodeWarning);
