using System.Text.Json.Serialization;

/// <param name="ContentCosine">
///     The vector leg's raw content-embedding cosine similarity to the query — set only by the
///     dual-vector builder, distinct from <paramref name="Ranking" /> (the alpha-fused score that
///     orders that leg). Transport-only for the evidence join: never serialized to the response.
/// </param>
/// <param name="EntryKind">
///     The kind the row holds — <c>memory</c>, <c>file</c>, or <c>none</c> for an unclassified
///     row; pass it as <c>get_entry</c>'s <c>entryType</c> to read the hit back.
/// </param>
public sealed record MemorySearchResult(
    string Hash,
    double Ranking,
    string Path,
    string Snippet,
    string? SourceFile = null,
    int ChunkIndex = 0,
    int TotalChunks = 0,
    [property: JsonIgnore] double? ContentCosine = null,
    string? EntryKind = null);
