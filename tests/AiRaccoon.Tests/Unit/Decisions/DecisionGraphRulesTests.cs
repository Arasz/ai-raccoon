using AiRaccoon.Core.Decisions;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Decisions;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class DecisionGraphRulesTests
{
    [Fact]
    public void Dependency_that_closes_a_transitive_cycle_is_refused()
    {
        DecisionLink[] links = [new("a", "b", DecisionEdgeKind.DependsOn), new("b", "c", DecisionEdgeKind.DependsOn)];
        Should.Throw<InvalidOperationException>(() => DecisionGraphRules.EnsureCanLink("c", "a", DecisionEdgeKind.DependsOn, links));
    }
    [Theory]
    [InlineData(DecisionEdgeKind.DependsOn)]
    [InlineData(DecisionEdgeKind.Supersedes)]
    [InlineData(DecisionEdgeKind.Supports)]
    [InlineData(DecisionEdgeKind.Contradicts)]
    public void Self_links_are_refused(DecisionEdgeKind kind) =>
        Should.Throw<InvalidOperationException>(() => DecisionGraphRules.EnsureCanLink("a", "a", kind, []));

    [Fact]
    public void Supersession_cycles_are_refused_independently_of_dependency_edges()
    {
        DecisionLink[] links = [new("a", "b", DecisionEdgeKind.Supersedes), new("b", "c", DecisionEdgeKind.Supersedes)];
        Should.Throw<InvalidOperationException>(() => DecisionGraphRules.EnsureCanLink("c", "a", DecisionEdgeKind.Supersedes, links));
        Should.NotThrow(() => DecisionGraphRules.EnsureCanLink("c", "a", DecisionEdgeKind.DependsOn, links));
    }

    [Fact]
    public void Contextual_cycles_and_shared_prerequisites_are_allowed()
    {
        DecisionLink[] links = [new("a", "b", DecisionEdgeKind.Supports), new("b", "a", DecisionEdgeKind.Supports), new("c", "b", DecisionEdgeKind.DependsOn)];
        Should.NotThrow(() => DecisionGraphRules.EnsureCanLink("b", "a", DecisionEdgeKind.Supports, links));
        Should.NotThrow(() => DecisionGraphRules.EnsureCanLink("d", "b", DecisionEdgeKind.DependsOn, links));
    }
}
