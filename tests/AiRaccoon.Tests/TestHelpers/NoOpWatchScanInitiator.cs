using AiRaccoon.Infrastructure.Watch;

namespace AiRaccoon.Tests.TestHelpers;

/// <summary>No-op <see cref="IWatchScanInitiator"/> for test stacks that never edit `ai-raccoon.ignore`
/// and so never need a real re-scan trigger wired up.</summary>
public sealed class NoOpWatchScanInitiator : IWatchScanInitiator
{
    public void EnqueueInitialScan(string projectId, string path)
    {
    }
}
