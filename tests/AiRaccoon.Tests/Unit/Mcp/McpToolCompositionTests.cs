using System.Reflection;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Setup;
using AiRaccoon.Tools;
using DotNext.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Mcp;

/// <summary>
///     Every class carrying [McpServerTool] methods must be constructible from the real
///     composition root. The tool tests hand-build them, so a dropped registration is
///     invisible until the first live invocation (#119 item 1).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class McpToolCompositionTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot();

    public void Dispose() => Directory.Delete(_dataRoot, true);

    [Fact]
    public void CompositionRoot_ConstructsEveryToolClass()
    {
        var toolClasses = ToolClasses();
        toolClasses.Count.ShouldBe(7, $"found: {string.Join(", ", toolClasses.Select(t => t.Name))}");

        using var provider = BuildCompositionRoot();

        provider.GetRequiredService<ToolGate>().ShouldNotBeNull();
        foreach (var toolClass in toolClasses)
        {
            Should.NotThrow(() => ActivatorUtilities.CreateInstance(provider, toolClass), toolClass.Name);
        }
    }

    private static List<Type> ToolClasses() =>
        typeof(MemoryTools).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Any(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

    private ServiceProvider BuildCompositionRoot()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.RegisterMemoryServices(new InfrastructureOptions
        {
            DataRoot = _dataRoot,
            Scope = InstallScope.User
        }, IReadOnlyList<McpTransport>.Singleton(McpTransport.Http));
        return services.BuildServiceProvider();
    }
}
