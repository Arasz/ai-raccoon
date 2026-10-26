using AiRaccoon.Core.Decisions;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Decisions;

[Trait("Speed", "Fast")]
public sealed class DecisionTaskSelectorTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1001)]
    public void Latest_task_count_must_be_positive_and_bounded(int count) =>
        Should.Throw<ArgumentException>(() => new DecisionTaskSelector(last: count));
    [Fact]
    public void Default_is_exactly_the_latest_task()
    {
        var selector = new DecisionTaskSelector();
        selector.Last.ShouldBe(1);
        selector.All.ShouldBeFalse();
        selector.TaskIds.ShouldBeNull();
    }

    [Fact]
    public void All_and_explicit_ids_do_not_inherit_latest_default()
    {
        new DecisionTaskSelector(all: true).Last.ShouldBeNull();
        var ids = new[] { "A", "b" };
        var selector = new DecisionTaskSelector(taskIds: ids);
        ids[0] = "changed";
        selector.Last.ShouldBeNull();
        selector.TaskIds.ShouldBe(new[] { "A", "b" });
    }

    [Fact]
    public void Selectors_are_mutually_exclusive()
    {
        Should.Throw<ArgumentException>(() => new DecisionTaskSelector(last: 1, all: true));
        Should.Throw<ArgumentException>(() => new DecisionTaskSelector(last: 1, taskIds: ["a"]));
        Should.Throw<ArgumentException>(() => new DecisionTaskSelector(all: true, taskIds: ["a"]));
    }

    [Fact]
    public void Explicit_task_ids_are_nonempty_unique_bounded_and_case_sensitive()
    {
        Should.Throw<ArgumentException>(() => new DecisionTaskSelector(taskIds: []));
        Should.Throw<ArgumentException>(() => new DecisionTaskSelector(taskIds: ["a", "a"]));
        Should.Throw<ArgumentException>(() => new DecisionTaskSelector(taskIds: [" "]));
        Should.Throw<ArgumentException>(() => new DecisionTaskSelector(taskIds: ["a\n"]));
        Should.Throw<ArgumentException>(() => new DecisionTaskSelector(taskIds: [new string('x', 201)]));
        Should.Throw<ArgumentException>(() => new DecisionTaskSelector(taskIds: Enumerable.Range(0, 101).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()));
        new DecisionTaskSelector(taskIds: ["a", "A"]).TaskIds!.Count.ShouldBe(2);
    }
}
