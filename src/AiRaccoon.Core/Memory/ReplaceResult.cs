using AiRaccoon.Core.Ingestion;

namespace AiRaccoon.Core.Memory;

/// <summary>
///     The outcome of a watch-digest replace-by-path (<see cref="IMemoryStore.ReplaceIfFileChangedAsync" />):
///     whether anything changed, and which corpus it wrote new rows to.
///     <see cref="CorpusKind.Neither" /> covers every case nothing was inserted — a hash-skip
///     (<see cref="Replaced" /> false), an ignored/hidden path, or a routeless extension — so a
///     caller can gate a corpus-specific action on <see cref="Corpus" /> alone.
/// </summary>
public readonly record struct ReplaceResult(bool Replaced, CorpusKind Corpus);
