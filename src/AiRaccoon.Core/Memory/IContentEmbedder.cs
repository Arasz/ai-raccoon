namespace AiRaccoon.Core.Memory;

/// <summary>
///     Embeds arbitrary content (not necessarily a stored entry) into the bank's configured vector
///     space. Connection-free by design (ADR-0039) so callers — e.g. <see cref="Filtering.NoiseFeedbackCollector" /> —
///     can stay in the pure/domain layer; the Infrastructure implementation owns the SQLite connection.
/// </summary>
public interface IContentEmbedder
{
    /// <summary>Null when no embedding engine is configured — degrades rather than failing.</summary>
    Task<float[]?> EmbedContentAsync(string content, CancellationToken cancellationToken = default);
}
