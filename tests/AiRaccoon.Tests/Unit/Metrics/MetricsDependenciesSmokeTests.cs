using AiRaccoon.Core.Metrics;
using AiRaccoon.Infrastructure.Metrics;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Setup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Metrics;

/// <summary>
///     DI smoke: the writer resolves fully wired on the sole surviving host, and the flusher hosts
///     exactly once (docs/plans/2026-08-15-performance-metrics-implementation.md, WP3).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class MetricsDependenciesSmokeTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot();

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [Fact]
    public void RegisterMemoryServices_ResolvesTheRecorderAndHostsTheFlusherOnce()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.RegisterMemoryServices(new InfrastructureOptions
        {
            DataRoot = _dataRoot,
            Scope = InstallScope.User
        });

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IMeasurementRecorder>().ShouldBeOfType<MetricsRecorder>();
        provider.GetServices<IHostedService>().OfType<MetricsFlusher>().ShouldHaveSingleItem();
    }
}
