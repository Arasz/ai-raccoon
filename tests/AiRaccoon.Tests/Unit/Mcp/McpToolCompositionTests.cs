using System.Reflection;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Core.Access;
using AiRaccoon.Access;
using AiRaccoon.Projects;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Projects;
using AiRaccoon.Setup;
using AiRaccoon.Tools;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Mcp;

/// <summary>
///     Every class carrying [McpServerTool] methods must be constructible from the real
///     composition root. The tool tests hand-build them, so a dropped registration is
///     invisible until the first live invocation.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class McpToolCompositionTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot();

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [Fact]
    public void CompositionRoot_ConstructsEveryToolClass()
    {
        var toolClasses = ToolClasses();
        toolClasses.Count.ShouldBe(11, $"found: {string.Join(", ", toolClasses.Select(t => t.Name))}");

        using var provider = BuildCompositionRoot();

        provider.GetRequiredService<ToolGate>().ShouldNotBeNull();
        foreach (var toolClass in toolClasses)
        {
            Should.NotThrow(() => ActivatorUtilities.CreateInstance(provider, toolClass), toolClass.Name);
        }
    }

    [Fact]
    public async Task CompositionRoot_ToolGateUsesTheRegisteredScopePolicy()
    {
        using var provider = BuildCompositionRoot(services =>
        {
            services.AddSingleton<IMemoryAccessGuard, AllowingGuard>();
            services.AddSingleton<IProjectRegistrationGuard, AllowingRegistrationGuard>();
            services.AddSingleton<IModelMigrationStore, NeverMigratingStore>();
        });
        var gate = provider.GetRequiredService<IToolGate>();
        var error = await Should.ThrowAsync<ArgumentException>(() =>
            gate.RequireAsync("shared", AccessRequirement.Write, "memory_write", TestContext.Current.CancellationToken));
        error.Message.ShouldBe("shared-project-unsupported: memory_write requires an ordinary project; shared supports document ingestion and watches.");
        (await gate.RequireAsync("shared", AccessRequirement.Read, "memory_search", TestContext.Current.CancellationToken))
            .ShouldBe("shared");
    }

    private sealed class AllowingGuard : IMemoryAccessGuard
    {
        public Task<AccessMode> ResolveAsync(string projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(AccessMode.Full);

        public Task EnsureAsync(string projectId, AccessRequirement requirement, string toolName,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static List<Type> ToolClasses() =>
    [
        .. typeof(MemoryTools).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Any(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
    ];

    private ServiceProvider BuildCompositionRoot(Action<ServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.RegisterMemoryServices(new InfrastructureOptions
        {
            DataRoot = _dataRoot,
            Scope = InstallScope.User
        });
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }
}
