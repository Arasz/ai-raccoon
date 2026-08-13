namespace AiRaccoon.Setup.Models;

public interface IEmbeddingAvailability
{
    /// <summary>Runs the ensure delegate under a 30 s bound and writes a warning when assets are unavailable; never throws.</summary>
    Task EnsureEmbeddingAvailabilityAsync(CancellationToken cancellationToken);
}
