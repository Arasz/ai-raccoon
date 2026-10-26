using AiRaccoon.Core.Decisions;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Decisions;

[Trait("Speed", "Fast")]
public sealed class DecisionStateTests
{
    [Theory]
    [InlineData(DecisionStatus.Proposed, DecisionStatus.Accepted)]
    [InlineData(DecisionStatus.Proposed, DecisionStatus.Rejected)]
    [InlineData(DecisionStatus.Accepted, DecisionStatus.Superseded)]
    [InlineData(DecisionStatus.Accepted, DecisionStatus.Deprecated)]
    public void Legal_transition_returns_a_new_state(DecisionStatus from, DecisionStatus to)
    {
        var state = new DecisionState(from);
        state.TransitionTo(to).Status.ShouldBe(to);
        state.Status.ShouldBe(from);
    }
    public static TheoryData<DecisionStatus, DecisionStatus> ForbiddenTransitions
    {
        get
        {
            var rows = new TheoryData<DecisionStatus, DecisionStatus>();
            foreach (var from in Enum.GetValues<DecisionStatus>())
            {
                foreach (var to in Enum.GetValues<DecisionStatus>())
                {
                    if ((from, to) is not
                        (DecisionStatus.Proposed, DecisionStatus.Accepted or DecisionStatus.Rejected) and not
                        (DecisionStatus.Accepted, DecisionStatus.Superseded or DecisionStatus.Deprecated))
                    {
                        rows.Add(from, to);
                    }
                }
            }

            return rows;
        }
    }

    [Theory]
    [MemberData(nameof(ForbiddenTransitions))]
    public void Forbidden_transitions_are_refused(DecisionStatus from, DecisionStatus to) =>
        Should.Throw<InvalidOperationException>(() => new DecisionState(from).TransitionTo(to));

    [Theory]
    [InlineData(DecisionStatus.Rejected)]
    [InlineData(DecisionStatus.Superseded)]
    [InlineData(DecisionStatus.Deprecated)]
    public void Terminal_decisions_cannot_be_amended(DecisionStatus status) =>
        Should.Throw<InvalidOperationException>(() => new DecisionState(status).EnsureCanAmend());
    [Theory]
    [InlineData(DecisionStatus.Proposed)]
    [InlineData(DecisionStatus.Accepted)]
    public void Open_decisions_can_be_amended(DecisionStatus status) =>
        Should.NotThrow(() => new DecisionState(status).EnsureCanAmend());

    [Fact]
    public void Undefined_states_and_targets_are_refused()
    {
        Should.Throw<ArgumentException>(() => new DecisionState((DecisionStatus)99));
        Should.Throw<InvalidOperationException>(() => new DecisionState(DecisionStatus.Accepted).TransitionTo((DecisionStatus)99));
    }
}
