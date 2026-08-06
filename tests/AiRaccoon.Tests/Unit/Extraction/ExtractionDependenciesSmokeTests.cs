using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Extraction;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Setup;
using DotNext.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Extraction;

/// <summary>
///     DI smoke: the composition root hosts the background extraction service exactly once.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ExtractionDependenciesSmokeTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot();

    public void Dispose() => Directory.Delete(_dataRoot, true);

    [Fact]
    public void RegisterMemoryServices_HostsTheExtractionServiceOnce()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.RegisterMemoryServices(new InfrastructureOptions
        {
            DataRoot = _dataRoot,
            Scope = InstallScope.User
        }, IReadOnlyList<McpTransport>.Singleton(McpTransport.Http));

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<SharedExtractionService>().ShouldNotBeNull();
        provider.GetServices<IHostedService>().OfType<ExtractionHostedService>().ShouldHaveSingleItem();
    }
}
