using System.Reflection;
using System.Text.RegularExpressions;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Sqlite.Memory;
using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Tools;
using ArchUnitNET.Domain;
using ArchUnitNET.Loader;
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
    private static readonly Assembly CoreAssembly = typeof(SearchQuery).Assembly;
    private static readonly Assembly InfrastructureAssembly = typeof(SqliteMemoryStore).Assembly;
    private static readonly Assembly HostAssembly = typeof(ToolGate).Assembly;

    private static readonly Architecture Architecture =
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

    private static List<IType> CoreTypes() => [.. Architecture.Types.Where(t => t.Assembly.Name == CoreAssembly.GetName().Name)];

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

    /// <summary>
    ///     Rule 5 (plan D3 follow-up, quality-gate finding on PR #432): a CLI verb never reaches a
    ///     reconcile/vec-DDL operation through a PORT it receives as a method argument, not just
    ///     through a constructor-injected field. <c>ConfigCommands</c> holds <c>IMemoryStore</c> as
    ///     its own field and threads it into every leaf verb method
    ///     (<c>SettingsCommands.ModelSetLocalAsync(parseResult, store, ...)</c> and dozens more) — a
    ///     graph walk over a leaf command's own constructed fields never sees that, because the leaf
    ///     command type itself holds no field of that port type. This rule instead derives every
    ///     port type that flows into a leaf command's PUBLIC METHODS as an argument
    ///     (`derive-or-delete-the-list` — not a hand-picked list of "the memory-shaped ports") and
    ///     asserts none of them, nor anything they inherit, declares a member shaped like the D3
    ///     defect: <see cref="AiRaccoon.Infrastructure.Embedding.IEntryEmbedder.ReconcileVecDimensionsAsync" />
    ///     itself lives on an infrastructure-only port never passed to a CLI command, precisely so
    ///     this stays true.
    /// </summary>
    [Fact]
    public void CliReachablePorts_ExposeNoReconcileOrVecDdlMember()
    {
        var ports = CliReachablePorts();
        ports.Length.ShouldBeGreaterThanOrEqualTo(3,
            "the derivation must find real ports (IMemoryStore, IModelMigrationStore, ICodeEngineStore at least) or this test checks nothing");

        var offenders = ports
            .SelectMany(port => ForbiddenMembersOf(port).Select(member => $"{port.Name}.{member}"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty(
            "a port reachable from a CLI command must never expose a reconcile/vec-DDL-shaped member — "
            + "a CLI verb calling through it would run exactly the bank write `cli-asks-the-server-acts` "
            + $"forbids: {string.Join("; ", offenders)}");
    }

    /// <summary>
    ///     The positive control the rule above needs: proves <see cref="ForbiddenMembersOf" /> can
    ///     actually find a violation, not merely fail to find ones that were never there.
    /// </summary>
    [Fact]
    public void ForbiddenMembersOf_FindsAPlantedReconcileMember() =>
        ForbiddenMembersOf(typeof(IPlantedReconcilePort)).ShouldContain("ReconcileFooAsync");

    private static Type[] LeafCommandTypesForPortDerivation() =>
        [
            .. typeof(ConfigCommands).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single().GetParameters()
                .Select(p => p.ParameterType)
                .Where(t => t.Name.EndsWith("Commands", StringComparison.Ordinal))
        ];

    /// <summary>Every port type a leaf CLI command's own public methods take as an ARGUMENT
    /// (where <c>IMemoryStore</c> actually reaches the CLI — as a parameter, not a field), plus
    /// everything each one inherits, so a member on a base interface is covered too.</summary>
    private static Type[] CliReachablePorts() =>
        [
            .. LeafCommandTypesForPortDerivation()
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                .SelectMany(m => m.GetParameters())
                .Select(p => p.ParameterType)
                .Where(IsAiRaccoonPort)
                .SelectMany(t => new[] { t }.Concat(t.GetInterfaces().Where(IsAiRaccoonPort)))
                .Distinct()
        ];

    private static bool IsAiRaccoonPort(Type type) =>
        type.IsInterface && type.Namespace is not null && type.Namespace.StartsWith("AiRaccoon", StringComparison.Ordinal);

    /// <summary>Method names shaped like the D3 defect: a reconcile call or raw vec0 DDL leaking onto a port.</summary>
    private static IEnumerable<string> ForbiddenMembersOf(Type port) =>
        port.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .Where(name => Regex.IsMatch(name, "Reconcile|Vec0|Ddl", RegexOptions.IgnoreCase));

    private interface IPlantedReconcilePort
    {
        Task ReconcileFooAsync(CancellationToken cancellationToken);
    }
}
