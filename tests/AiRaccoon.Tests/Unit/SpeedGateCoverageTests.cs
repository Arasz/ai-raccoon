using System.Reflection;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit;

/// <summary>
///     The CI jobs select on --filter "Speed=Fast", "Speed=Slow" and "Category=bdd". A test
///     class with no Speed trait matches none of them: it compiles, runs locally, and never
///     gates a merge. Speed-trait twin of <see cref="BddGateCoverageTests" />.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class SpeedGateCoverageTests
{
    [Fact]
    public void EveryTestClass_CarriesASpeedTrait()
    {
        var ungated = TestClasses()
            .Where(t => !Traits(t).Any(trait => trait.Name == TestCategories.Speed))
            .Select(t => t.FullName ?? t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        ungated.ShouldBeEmpty(
            "these classes carry no Speed trait, so no CI job runs them: " + string.Join(", ", ungated));
    }

    [Fact]
    public void TheGuardSeesTheTestClasses()
    {
        // Without this the guard passes vacuously the day reflection stops matching.
        TestClasses().Count.ShouldBeGreaterThan(100);
    }

    /// <summary>Hand-written xUnit classes: any type with a [Fact] or [Theory], minus Reqnroll's generated features (gated by Category=bdd).</summary>
    private static List<Type> TestClasses() =>
    [
        .. typeof(SpeedGateCoverageTests).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => Methods(t).Any(HasFactOrTheory))
            .Where(t => !Methods(t).Any(m => Traits(m).Any(trait => trait.Name == "FeatureTitle")))
    ];

    private static MethodInfo[] Methods(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

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
