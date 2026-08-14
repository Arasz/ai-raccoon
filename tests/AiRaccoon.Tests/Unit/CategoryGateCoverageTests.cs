using System.Reflection;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit;

/// <summary>
///     The CI jobs select on --filter "Category=Unit", "Category=Integration" and "Category=bdd".
///     A test class with no Category trait matches none of them: it compiles, runs locally, and
///     never gates a merge. Category-trait twin of <see cref="SpeedGateCoverageTests" />.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class CategoryGateCoverageTests
{
    [Fact]
    public void EveryTestClass_CarriesACategoryTrait()
    {
        var ungated = TestClasses()
            .Where(t => Traits(t).All(trait => trait.Name != TestCategories.Category))
            .Select(t => t.FullName ?? t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        ungated.ShouldBeEmpty(
            "these classes carry no Category trait, so no CI job runs them: " + string.Join(", ", ungated));
    }

    [Fact]
    public void TheGuardSeesTheTestClasses()
    {
        // Without this the guard passes vacuously the day reflection stops matching.
        TestClasses().Count.ShouldBeGreaterThan(100);
    }

    /// <summary>Handwritten xUnit classes: any type with a [Fact] or [Theory], minus Reqnroll's generated features (gated by Category=bdd).</summary>
    private static List<Type> TestClasses() =>
    [
        .. typeof(CategoryGateCoverageTests).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => Methods(t).Any(HasFactOrTheory))
            .Where(t => !Methods(t).Any(m => Traits(m).Any(trait => trait.Name == "FeatureTitle")))
    ];

    private static MethodInfo[] Methods(Type type) => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

    private static bool HasFactOrTheory(MethodInfo method) =>
        method.GetCustomAttributesData().Any(a =>
            a.AttributeType.FullName is "Xunit.FactAttribute" or "Xunit.TheoryAttribute");

    private static List<(string Name, string Value)> Traits(MemberInfo member) =>
    [
        .. member.GetCustomAttributesData()
            .Where(a => a.AttributeType.FullName == "Xunit.TraitAttribute" && a.ConstructorArguments.Count == 2)
            .Select(a => (
                Name: a.ConstructorArguments[0].Value as string ?? string.Empty,
                Value: a.ConstructorArguments[1].Value as string ?? string.Empty))
    ];
}
