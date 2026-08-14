using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Node;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Hosting;

/// <summary>
///     The declared transitions of `serve --restart` (ADR-0043). The defect these gate: a probe
///     that got no answer was the same value as "nothing is listening", so `serve` concluded the
///     port was free, failed to bind one line later, and reported whichever line matched the
///     stale belief.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class RestartTransitionTests
{
    [Fact]
    public void ARefusedConnection_IsTheOnlyProofNothingIsListening()
    {
        RestartTransition.FromProbe(ProbeVerdict.NotListening).ShouldBe(RestartOutcome.Nothing);
    }

    [Fact]
    public void AProbeThatGotNoAnswer_IsNotNothingListening()
    {
        RestartTransition.FromProbe(ProbeVerdict.Unanswered).ShouldBe(RestartOutcome.Unknown);
    }

    [Fact]
    public void AListenerThatAnswered_SettlesNothing_AndTheCycleCarriesOn()
    {
        RestartTransition.FromProbe(ProbeVerdict.Answered).ShouldBeNull();
    }

    /// <summary>Knowing nothing is not a refusal: the bind is the only thing that can settle it.</summary>
    [Theory]
    [InlineData(RestartOutcome.Nothing)]
    [InlineData(RestartOutcome.Stopped)]
    [InlineData(RestartOutcome.Unknown)]
    public void AnUnrefutedPreCheck_LetsServeBind(RestartOutcome outcome)
    {
        RestartTransition.MayBind(outcome).ShouldBeTrue();
    }

    [Theory]
    [InlineData(RestartOutcome.Foreign)]
    [InlineData(RestartOutcome.NoToken)]
    [InlineData(RestartOutcome.Refused)]
    [InlineData(RestartOutcome.Unsupported)]
    [InlineData(RestartOutcome.TimedOut)]
    public void ARestartThatCouldNotGoAhead_NeverBinds(RestartOutcome outcome)
    {
        RestartTransition.MayBind(outcome).ShouldBeFalse();
    }

    /// <summary>
    ///     The contradiction: the pre-check established nothing, so nobody "took the port while this
    ///     one was starting" — there was never a moment this run held it or proved it free.
    /// </summary>
    [Theory]
    [InlineData(ProbeVerdict.Answered)]
    [InlineData(ProbeVerdict.Unanswered)]
    [InlineData(ProbeVerdict.NotListening)]
    public void ARefusedBind_AfterAPreCheckThatKnewNothing_IsNeverReportedAsALostPort(ProbeVerdict afterBind)
    {
        RestartTransition.AfterBindRefused(RestartOutcome.Unknown, afterBind, true)
            .ShouldBe(BindRefusal.HeldUnidentified);
    }

    /// <summary>A pre-check that did prove the port free, or freed it, can honestly report the loss.</summary>
    [Theory]
    [InlineData(RestartOutcome.Nothing)]
    [InlineData(RestartOutcome.Stopped)]
    public void ARefusedBind_AfterAProvenFreePort_IsALostPort(RestartOutcome believed)
    {
        RestartTransition.AfterBindRefused(believed, ProbeVerdict.Answered, true)
            .ShouldBe(BindRefusal.LostThePort);
    }

    [Fact]
    public void ARefusedBind_WithAHolderThatIsNotOurs_StaysPortInUse()
    {
        RestartTransition.AfterBindRefused(RestartOutcome.Stopped, ProbeVerdict.Unanswered, true)
            .ShouldBe(BindRefusal.PortInUse);
    }

    [Fact]
    public void WithoutRestart_AnAiRaccoonHolder_IsStillAttachedTo()
    {
        RestartTransition.AfterBindRefused(RestartOutcome.Unknown, ProbeVerdict.Answered, false)
            .ShouldBe(BindRefusal.Attach);
    }

    [Fact]
    public void WithoutRestart_AnyOtherHolder_IsPortInUse()
    {
        RestartTransition.AfterBindRefused(RestartOutcome.Unknown, ProbeVerdict.Unanswered, false)
            .ShouldBe(BindRefusal.PortInUse);
    }
}
