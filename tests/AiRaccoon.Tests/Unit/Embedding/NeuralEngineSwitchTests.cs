using AiRaccoon.Core.Embedding;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding;

/// <summary>
///     ADR-0118: WebGPU serves until the ANE sessions load and the parity probe passes; any load
///     failure, timeout or probe failure refuses to the Neural Engine for the rest of the process.
///     Only the five declared moves are legal — every other (state, trigger) pair is a defect to
///     raise, never a silent no-op — and <see cref="NeuralEngineState.Refused" /> /
///     <see cref="NeuralEngineState.NeuralEngineServing" /> never move again.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class NeuralEngineSwitchTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);

    public static TheoryData<NeuralEngineState, NeuralEngineTrigger, NeuralEngineState> LegalMoves => new()
    {
        { NeuralEngineState.WebGpuServing, NeuralEngineTrigger.Started, NeuralEngineState.CompilingNeuralEngine },
        {
            NeuralEngineState.CompilingNeuralEngine, NeuralEngineTrigger.SessionsLoadedAndProbePassed,
            NeuralEngineState.NeuralEngineServing
        },
        { NeuralEngineState.CompilingNeuralEngine, NeuralEngineTrigger.LoadFailed, NeuralEngineState.Refused },
        { NeuralEngineState.CompilingNeuralEngine, NeuralEngineTrigger.TimedOut, NeuralEngineState.Refused },
        { NeuralEngineState.CompilingNeuralEngine, NeuralEngineTrigger.ProbeFailed, NeuralEngineState.Refused }
    };

    public static TheoryData<NeuralEngineState, NeuralEngineTrigger> UndeclaredPairs
    {
        get
        {
            var legal = new HashSet<(NeuralEngineState State, NeuralEngineTrigger Trigger)>
            {
                (NeuralEngineState.WebGpuServing, NeuralEngineTrigger.Started),
                (NeuralEngineState.CompilingNeuralEngine, NeuralEngineTrigger.SessionsLoadedAndProbePassed),
                (NeuralEngineState.CompilingNeuralEngine, NeuralEngineTrigger.LoadFailed),
                (NeuralEngineState.CompilingNeuralEngine, NeuralEngineTrigger.TimedOut),
                (NeuralEngineState.CompilingNeuralEngine, NeuralEngineTrigger.ProbeFailed)
            };

            var data = new TheoryData<NeuralEngineState, NeuralEngineTrigger>();
            foreach (var state in Enum.GetValues<NeuralEngineState>())
            {
                foreach (var trigger in Enum.GetValues<NeuralEngineTrigger>())
                {
                    if (!legal.Contains((state, trigger)))
                    {
                        data.Add(state, trigger);
                    }
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(LegalMoves))]
    public void Fire_ADeclaredMove_ReachesItsTarget(NeuralEngineState from, NeuralEngineTrigger trigger, NeuralEngineState to)
    {
        var machine = MachineIn(from);

        machine.Fire(trigger, "reason", At);

        machine.State.ShouldBe(to);
    }

    [Theory]
    [MemberData(nameof(UndeclaredPairs))]
    public void Fire_AnUndeclaredPair_Throws(NeuralEngineState from, NeuralEngineTrigger trigger)
    {
        var machine = MachineIn(from);

        Should.Throw<InvalidOperationException>(() => machine.Fire(trigger, "reason", At));
    }

    [Theory]
    [InlineData(NeuralEngineState.Refused)]
    [InlineData(NeuralEngineState.NeuralEngineServing)]
    public void Fire_FromATerminalState_AlwaysThrows(NeuralEngineState terminal)
    {
        foreach (var trigger in Enum.GetValues<NeuralEngineTrigger>())
        {
            var machine = MachineIn(terminal);

            Should.Throw<InvalidOperationException>(() => machine.Fire(trigger, "reason", At));
        }
    }

    [Fact]
    public void History_RecordsEachMoveInOrder()
    {
        var machine = new NeuralEngineSwitch();
        var first = At;
        var second = At.AddSeconds(12);

        machine.Fire(NeuralEngineTrigger.Started, "background compile begins", first);
        machine.Fire(NeuralEngineTrigger.ProbeFailed, "parity probe below 0.999", second);

        machine.History.Select(t => (t.From, t.To, t.Trigger, t.Reason, t.At)).ShouldBe(
        [
            (NeuralEngineState.WebGpuServing, NeuralEngineState.CompilingNeuralEngine, NeuralEngineTrigger.Started,
                "background compile begins", first),
            (NeuralEngineState.CompilingNeuralEngine, NeuralEngineState.Refused, NeuralEngineTrigger.ProbeFailed,
                "parity probe below 0.999", second)
        ]);
    }

    [Fact]
    public void ServesNeuralEngine_TrueOnlyOnceCompiledAndProbed()
    {
        var machine = new NeuralEngineSwitch();
        machine.ServesNeuralEngine.ShouldBeFalse();

        machine.Fire(NeuralEngineTrigger.Started, "background compile begins", At);
        machine.ServesNeuralEngine.ShouldBeFalse();

        machine.Fire(NeuralEngineTrigger.SessionsLoadedAndProbePassed, "parity probe passed", At);
        machine.ServesNeuralEngine.ShouldBeTrue();
    }

    private static NeuralEngineSwitch MachineIn(NeuralEngineState state)
    {
        var machine = new NeuralEngineSwitch();
        if (state == NeuralEngineState.WebGpuServing)
        {
            return machine;
        }

        machine.Fire(NeuralEngineTrigger.Started, "background compile begins", At);
        if (state == NeuralEngineState.CompilingNeuralEngine)
        {
            return machine;
        }

        if (state == NeuralEngineState.NeuralEngineServing)
        {
            machine.Fire(NeuralEngineTrigger.SessionsLoadedAndProbePassed, "parity probe passed", At);
            return machine;
        }

        machine.Fire(NeuralEngineTrigger.LoadFailed, "load failed", At);
        return machine;
    }
}
