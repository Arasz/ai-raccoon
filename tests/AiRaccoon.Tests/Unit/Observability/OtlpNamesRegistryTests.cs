using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Observability;
using AiRaccoon.Setup;
using DotNext.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Observability;

/// <summary>
///     A9/derive-or-delete-the-list: OtlpNames.Meters/Sources must cover every Meter/ActivitySource
///     the real DI container actually creates, not just the ones a caller remembered to spread into
///     it. Walks every resolved service instead of pinning a second hand-kept expected list (the
///     same shape as LoggerMessageEventIdTests' assembly walk, applied to the DI graph).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class OtlpNamesRegistryTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-otlp-names");

    public void Dispose() => Directory.Delete(_dataRoot, true);

    [Fact]
    public void EveryCreatedMeterAndActivitySource_IsInTheRegistry()
    {
        var (meterNames, sourceNames) = DiscoverCreatedScopeNames();

        meterNames.ShouldBeSubsetOf(OtlpNames.Meters);
        sourceNames.ShouldBeSubsetOf(OtlpNames.Sources);
    }

    /// <summary>Builds the real container, force-resolves every registered service, and reads back
    /// the Meter/ActivitySource each instance publicly exposes.</summary>
    private (HashSet<string> Meters, HashSet<string> Sources) DiscoverCreatedScopeNames()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.RegisterMemoryServices(
            new InfrastructureOptions { DataRoot = _dataRoot, Scope = InstallScope.User },
            IReadOnlyList<McpTransport>.Singleton(McpTransport.Http));

        using var provider = services.BuildServiceProvider();

        var meterNames = new HashSet<string>(StringComparer.Ordinal);
        var sourceNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var serviceType in services.Select(d => d.ServiceType).Distinct())
        {
            if (serviceType.IsGenericTypeDefinition)
            {
                continue;
            }

            foreach (var instance in provider.GetServices(serviceType))
            {
                CollectScopeNames(instance, meterNames, sourceNames);
            }
        }

        return (meterNames, sourceNames);
    }

    private static void CollectScopeNames(object? instance, ISet<string> meterNames, ISet<string> sourceNames)
    {
        if (instance is null)
        {
            return;
        }

        foreach (var property in instance.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.PropertyType == typeof(Meter) && property.GetValue(instance) is Meter meter)
            {
                meterNames.Add(meter.Name);
            }
            else if (property.PropertyType == typeof(ActivitySource) && property.GetValue(instance) is ActivitySource source)
            {
                sourceNames.Add(source.Name);
            }
        }
    }
}
