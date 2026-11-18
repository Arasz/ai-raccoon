using System.Collections.Concurrent;
using AiRaccoon.Infrastructure.Ingestion;

namespace AiRaccoon.Tests.TestHelpers;

/// <summary>
///     Builds an <see cref="IgnoreRulesProvider" /> on the production file provider. Every instance is
///     disposed when the test process exits, so no polling timer outlives the run.
/// </summary>
internal static class TestIgnoreRules
{
    private static readonly ConcurrentBag<IgnoreRulesProvider> Created = [];

    static TestIgnoreRules() => AppDomain.CurrentDomain.ProcessExit += (_, _) =>
    {
        foreach (var provider in Created)
        {
            provider.Dispose();
        }
    };

    public static IgnoreRulesProvider Create()
    {
        var provider = new IgnoreRulesProvider(IgnoreRulesProvider.CreatePhysicalProvider);
        Created.Add(provider);
        return provider;
    }
}
