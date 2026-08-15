using System.Reflection;
using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using ArchUnitNET.xUnitV3;
using ModelContextProtocol.Server;
using Shouldly;
using Xunit;
using Assembly = System.Reflection.Assembly;
using Type = System.Type;

namespace AiRaccoon.Tests.Unit.Layering;

/// <summary>
///     The mechanical layering guard the repo had already paid for: `TngTech.ArchUnitNET.xUnitV3`
///     was pinned in tests/Directory.Packages.props and referenced by nothing, so every architecture
///     finding of the 2026-08-14 review was invisible to CI at 0 warnings (docs/adr/0059).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class LayeringRulesTests
{
    private static readonly Assembly CoreAssembly = typeof(Core.Memory.SearchQuery).Assembly;
    private static readonly Assembly InfrastructureAssembly = typeof(Infrastructure.Sqlite.SqliteMemoryStore).Assembly;
    private static readonly Assembly HostAssembly = typeof(global::AiRaccoon.Tools.ToolGate).Assembly;

    private static readonly ArchUnitNET.Domain.Architecture Architecture =
        new ArchLoader().LoadAssemblies(CoreAssembly, InfrastructureAssembly, HostAssembly).Build();

    /// <summary>
    ///     Rule 1 — the layering the csproj already enforces, stated so it survives someone adding a
    ///     `ProjectReference` without thinking. Passed before this file existed; it is here as the
    ///     baseline the other two are measured against, not as a discovery.
    /// </summary>
    [Fact]
    public void Core_DependsOnNoOtherProjectAssembly()
    {
        var referenced = CoreAssembly.GetReferencedAssemblies().Select(a => a.Name).ToList();

        referenced.ShouldNotContain(InfrastructureAssembly.GetName().Name);
        referenced.ShouldNotContain(HostAssembly.GetName().Name);
    }

    /// <summary>
    ///     Rule 2 — the domain layer holds no networking concern. Failed when written:
    ///     `AiRaccoon.Resilience.ResiliencePipelineFactory` lived in Core and handled
    ///     `HttpRequestException` and `SocketException`, which is HTTP retry policy, not domain.
    /// </summary>
    /// <remarks>
    ///     Written first as ArchUnit's
    ///     <c>NotDependOnAny(Types().That().ResideInNamespace("System.Net"))</c> and it **passed
    ///     against the known violation** — that form matches against types loaded into the
    ///     architecture, and only our own three assemblies are loaded, so the target set was empty
    ///     and the rule could return exactly one answer. Reading each type's own dependency list
    ///     works because ArchUnit records a dependency's target by name whether or not it was
    ///     loaded. <see cref="Core_ReferencesNoNetworkingType_ScansANonEmptyTypeSet" /> is what
    ///     stops it silently reverting to the vacuous form.
    /// </remarks>
    [Fact]
    public void Core_ReferencesNoNetworkingType()
    {
        var offenders = NetworkingDependenciesInCore();

        offenders.ShouldBeEmpty(
            "networking is an infrastructure concern; the domain layer must stay framework-free: "
            + string.Join("; ", offenders));
    }

    /// <summary>
    ///     The guard on the guard: a dependency scan over an empty type set passes for the same
    ///     reason a broken one does.
    /// </summary>
    [Fact]
    public void Core_ReferencesNoNetworkingType_ScansANonEmptyTypeSet() =>
        CoreTypes().Count.ShouldBeGreaterThan(100,
            "the networking rule scans this set; if it empties, the rule stops being able to fail");

    private static List<IType> CoreTypes() =>
        [.. Architecture.Types.Where(t => t.Assembly.Name == CoreAssembly.GetName().Name)];

    private static List<string> NetworkingDependenciesInCore() =>
    [
        .. CoreTypes()
            .SelectMany(type => type.Dependencies
                .Select(d => d.Target.FullName)
                .Where(name => name.StartsWith("System.Net.", StringComparison.Ordinal)
                               || name.StartsWith("System.Net+", StringComparison.Ordinal)
                               || string.Equals(name, "System.Net", StringComparison.Ordinal))
                .Select(name => $"{type.Name} -> {name}"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
    ];

    /// <summary>
    ///     Rule 3 — an MCP tool class depends on ports, not on the concrete types behind them.
    ///     Failed when written: 8 of 8 tool classes injected the concrete `ToolGate`, because
    ///     `AddRequiredSingleton` registers every implementation under both its own type and its
    ///     interface, so injecting the concrete class is exactly as easy and nothing reports it.
    /// </summary>
    [Fact]
    public void EveryToolClass_InjectsOnlyInterfaces()
    {
        var offenders = new List<string>();

        foreach (var tool in HostAssembly.GetTypes().Where(HasMcpTool))
        {
            foreach (var ctor in tool.GetConstructors())
            {
                foreach (var parameter in ctor.GetParameters())
                {
                    if (IsPort(parameter.ParameterType))
                    {
                        continue;
                    }

                    offenders.Add($"{tool.Name}.{parameter.Name}: {parameter.ParameterType.Name}");
                }
            }
        }

        offenders.ShouldBeEmpty(
            "a tool class must depend on ports, not on the concrete types behind them: "
            + string.Join("; ", offenders));
    }

    private static bool HasMcpTool(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Any(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null);

    /// <summary>
    ///     A port, or a value the DI container supplies that no port could stand in for: an interface,
    ///     a framework abstraction the BCL ships as a class (<see cref="TimeProvider" />), or an
    ///     options/primitive carrier. Anything else is a concrete implementation being injected.
    /// </summary>
    private static bool IsPort(Type type) =>
        type.IsInterface
        || type == typeof(TimeProvider)
        || type.IsPrimitive
        || type == typeof(string);

    /// <summary>
    ///     Rule 4 — F13: <c>IMetricsReportService</c> is a port, so it lives beside the other ports
    ///     in Core (<c>IMemoryStore</c>, <c>IMeasurementRecorder</c>, <c>ISearchQualityService</c>)
    ///     and beside its own return type, <c>PerformanceReport</c> — not in Infrastructure with the
    ///     implementation that backs it. Resolved by name, not <c>typeof</c>, so this test compiles
    ///     (and fails honestly) both before and after the move.
    /// </summary>
    [Fact]
    public void IMetricsReportService_LivesInCoreMetrics()
    {
        var port = CoreAssembly.GetType("AiRaccoon.Core.Metrics.IMetricsReportService")
                   ?? InfrastructureAssembly.GetType("AiRaccoon.Infrastructure.Metrics.IMetricsReportService");

        port.ShouldNotBeNull("IMetricsReportService must exist somewhere in Core or Infrastructure");
        port.Assembly.ShouldBe(CoreAssembly, "IMetricsReportService is a port and belongs in Core, beside the other ports");
        port.Namespace.ShouldBe("AiRaccoon.Core.Metrics");
    }
}
