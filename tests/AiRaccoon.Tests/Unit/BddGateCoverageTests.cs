using System.Reflection;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit;

/// <summary>
///     The BDD CI job selects scenarios with --filter "Category=bdd", which comes from the
///     @bdd tag on each feature. A feature file added without that tag would compile, run
///     locally, and never gate a merge — the exact omission this guard exists to catch.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class BddGateCoverageTests
{
    private const string GateCategory = "bdd";

    [Fact]
    public void EveryFeatureClass_CarriesTheGateCategory()
    {
        var untagged = FeatureClasses()
            .Where(t => !Traits(t).Contains((TestCategories.Category, GateCategory)))
            .Select(t => t.FullName ?? t.Name)
            .ToList();

        untagged.ShouldBeEmpty(
            $"these feature files need a @{GateCategory} tag or their scenarios never gate a PR: "
            + string.Join(", ", untagged));
    }

    [Fact]
    public void TheGuardSeesTheFeatureClasses()
    {
        // Without this the guard passes vacuously the day reflection stops matching.
        FeatureClasses().Count.ShouldBeGreaterThanOrEqualTo(4);
    }

    /// <summary>Reqnroll stamps every generated scenario with a FeatureTitle trait; nothing hand-written has one.</summary>
    private static List<Type> FeatureClasses() =>
    [
        .. typeof(BddGateCoverageTests).Assembly.GetTypes()
            .Where(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Any(m => Traits(m).Any(trait => trait.Name == "FeatureTitle")))
    ];

    private static List<(string Name, string Value)> Traits(MemberInfo member) =>
    [
        .. member.GetCustomAttributesData()
            .Where(a => a.AttributeType.FullName == "Xunit.TraitAttribute" && a.ConstructorArguments.Count == 2)
            .Select(a => (
                Name: a.ConstructorArguments[0].Value as string ?? string.Empty,
                Value: a.ConstructorArguments[1].Value as string ?? string.Empty))
    ];
}
