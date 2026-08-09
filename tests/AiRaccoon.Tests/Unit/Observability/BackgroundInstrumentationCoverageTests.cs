using AiRaccoon.Core.Observability;
using AiRaccoon.Setup.Serve;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Observability;

/// <summary>
///     WP13's own gate: a fifth background service may not arrive dark. Derived from the assembly
///     graph rather than a list of the four we know about, so a new hosted service joins the guard
///     by existing (the shape of LoggerMessageEventIdTests' walk).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class BackgroundInstrumentationCoverageTests
{
    [Fact]
    public void EveryHostedService_TakesTheOperationTelemetryPort()
    {
        var dark = HostedServices()
            .Where(t => !t.GetConstructors()
                .Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(IOperationTelemetry))))
            .Select(t => t.FullName ?? t.Name)
            .ToList();

        dark.ShouldBeEmpty(
            $"Uninstrumented background service(s): {string.Join(", ", dark)}. Take IOperationTelemetry "
            + "and open a scope per pass, or the pass is invisible to the collector.");
    }

    /// <summary>The guard's reach, not a second copy of the list: a walk that found nothing would
    /// pass the check above forever.</summary>
    [Fact]
    public void TheGuard_ReachesBothProductAssembliesThatHostServices()
    {
        var found = HostedServices();

        found.ShouldNotBeEmpty();
        found.ShouldContain(typeof(IdleWatchdog)); // the server project
        found.ShouldContain(t => t.Assembly.GetName().Name == "AiRaccoon.Infrastructure");
    }

    private static IReadOnlyList<Type> HostedServices() =>
    [
        .. ProductAssemblies.All()
            .SelectMany(a => a.GetTypes())
            .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(IHostedService).IsAssignableFrom(t))
    ];
}
