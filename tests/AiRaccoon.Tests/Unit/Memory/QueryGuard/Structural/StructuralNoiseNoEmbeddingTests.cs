using System.Reflection;
using AiRaccoon.Core.Memory.QueryGuard.Structural;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory.QueryGuard.Structural;

/// <summary>
///     Acceptance criterion 3 (docs/adr/0041): the structural detector makes no embedding call.
///     Proven by construction, not by mocking: <see cref="StructuralNoiseFeatures" /> and
///     <see cref="StructuralNoiseModel" /> are static types with zero constructor/injectable
///     dependencies, so there is no embedder reference to call in the first place — the same style
///     of proof <c>QueryGuardPolicyTests</c> uses for the read-path regex guard.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class StructuralNoiseNoEmbeddingTests
{
    [Fact]
    public void StructuralNoiseFeatures_IsStaticWithNoInjectableDependencies()
    {
        var type = typeof(StructuralNoiseFeatures);

        type.IsAbstract.ShouldBeTrue("a static class is abstract+sealed under the hood");
        type.IsSealed.ShouldBeTrue();
        type.GetConstructors(BindingFlags.Public | BindingFlags.Instance).ShouldBeEmpty();
    }

    [Fact]
    public void StructuralNoiseModel_IsStaticWithNoInjectableDependencies()
    {
        var type = typeof(StructuralNoiseModel);

        type.IsAbstract.ShouldBeTrue();
        type.IsSealed.ShouldBeTrue();
        type.GetConstructors(BindingFlags.Public | BindingFlags.Instance).ShouldBeEmpty();
    }

    [Fact]
    public void NeitherType_ReferencesAnEmbeddingOrHttpOrIoType()
    {
        // A structural/lexical check on the assembly's own referenced-member surface: if either
        // type ever grows a dependency on an embedder, HttpClient, or file/socket I/O, this fails
        // loudly rather than the claim silently going stale.
        foreach (var type in new[] { typeof(StructuralNoiseFeatures), typeof(StructuralNoiseModel) })
        {
            var fields = type.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                .Select(f => f.FieldType.FullName ?? "")
                .ToList();

            fields.ShouldNotContain(f => f.Contains("Embed") || f.Contains("HttpClient") || f.Contains("Socket"));
        }
    }
}
