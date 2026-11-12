using System.Reflection;
using System.Runtime.CompilerServices;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Layering;

/// <summary>
///     Fails when a production assembly is built without the runtime-async compiler switch, so that
///     the switch in Directory.Build.targets cannot be lost without a red test.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class RuntimeAsyncTests
{
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                                          | BindingFlags.Static | BindingFlags.DeclaredOnly;

    public static TheoryData<string> Assemblies => [.. ProductAssemblies.All().Select(a => a.GetName().Name!)];

    [Fact]
    public void ProductAssemblies_IncludeTheThreeProjects() =>
        new[] { "AiRaccoon", "AiRaccoon.Core", "AiRaccoon.Infrastructure" }
            .ShouldBeSubsetOf(ProductAssemblies.All().Select(a => a.GetName().Name!));

    [Theory]
    [MemberData(nameof(Assemblies))]
    public void ProductAssembly_CompilesAsyncMethodsAsRuntimeAsync(string assemblyName)
    {
        var methods = ProductAssemblies.All().Single(a => a.GetName().Name == assemblyName)
            .GetTypes().SelectMany(t => t.GetMethods(Declared)).ToList();

        methods.Where(m => m.GetCustomAttribute<AsyncStateMachineAttribute>() is not null)
            .Select(m => $"{m.DeclaringType}.{m.Name}")
            .ShouldBeEmpty($"{assemblyName}: async methods compiled to a state machine (ADR-0145 switch lost)");
        methods.ShouldContain(m => m.GetMethodImplementationFlags().HasFlag(MethodImplAttributes.Async),
            $"{assemblyName}: no method carries the runtime-async flag (ADR-0145)");
    }
}
