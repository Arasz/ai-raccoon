namespace AiRaccoon.Core.Memory.Filtering;

/// <summary>
///     The seam a future noise learner plugs into (ADR-0039). Deliberately not implemented with any
///     scoring logic yet: two research lanes disagree on what generalizes (leader-follower centroids
///     measured 93% singletons on real traffic; a linear probe scored 0.946 AUC but only in-sample,
///     on the one shape of noise this install has). Pure and synchronous over data already fetched —
///     no I/O, no embedding call, no threshold baked in here — so any future detector (clustering,
///     a probe, something else) can implement it without this interface changing.
/// </summary>
public interface INoiseDetector
{
    /// <summary>
    ///     <paramref name="storedSamples" /> is whatever <see cref="INoiseClusterStore" /> holds for
    ///     the caller's (project, user) scope — today, one row per confirmed-noise sample the
    ///     feedback path appended. A future detector decides what to do with them; this seam does
    ///     not.
    /// </summary>
    NoiseFilterResult Evaluate(float[] vector, IReadOnlyList<NoiseCluster> storedSamples);
}

/// <summary>The only <see cref="INoiseDetector" /> shipped today: always clean. See docs/adr/0039 — no scoring model has been validated on held-out data yet.</summary>
public sealed class NoOpNoiseDetector : INoiseDetector
{
    public NoiseFilterResult Evaluate(float[] vector, IReadOnlyList<NoiseCluster> storedSamples) => NoiseFilterResult.Clean;
}
