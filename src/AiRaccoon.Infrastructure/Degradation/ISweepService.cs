using AiRaccoon.Core.Degradation;

namespace AiRaccoon.Infrastructure.Degradation;

public interface ISweepService
{
    Task<SweepOutcome> SweepAsync(
        string projectId, double threshold, bool dryRun, CancellationToken cancellationToken = default);
}
