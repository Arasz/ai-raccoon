namespace AiRaccoon.Core.Memory.Filtering;

/// <summary>
///     The seam a future noise learner plugs into (ADR-0039, amended by this task). The research
///     settled on a structural/lexical classifier over shape statistics — no embeddings, no stored
///     exemplars to compare against (leader-follower centroids measured 93% singleton clusters on
///     real traffic; noise silhouette is no better than signal's). So this seam takes only the raw
///     content: no vector, no stored-sample list. Any future detector (structural, lexical,
///     something else) implements this without I/O.
/// </summary>
public interface INoiseDetector
{
    NoiseFilterResult Evaluate(string content);
}

/// <summary>The only <see cref="INoiseDetector" /> shipped today: always clean. See docs/adr/0039 — no scoring model has been validated on held-out data yet.</summary>
public sealed class NoOpNoiseDetector : INoiseDetector
{
    public NoiseFilterResult Evaluate(string content) => NoiseFilterResult.Clean;
}
