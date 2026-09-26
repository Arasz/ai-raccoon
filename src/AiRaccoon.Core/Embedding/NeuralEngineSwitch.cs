using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Embedding;

/// <summary>A state the CoreML/Neural-Engine background compile can be in (ADR-0118).</summary>
public enum NeuralEngineState
{
    WebGpuServing,
    CompilingNeuralEngine,
    NeuralEngineServing,
    Refused
}

/// <summary>An event that can move a <see cref="NeuralEngineSwitch" /> between states.</summary>
public enum NeuralEngineTrigger
{
    Started,
    SessionsLoadedAndProbePassed,
    LoadFailed,
    TimedOut,
    ProbeFailed
}

/// <summary>One recorded move: the state it left, the state it entered, what fired it, why, and when.</summary>
public sealed record NeuralEngineTransition(
    NeuralEngineState From,
    NeuralEngineState To,
    NeuralEngineTrigger Trigger,
    string Reason,
    DateTimeOffset At);

/// <summary>
///     The background-compile switch between WebGPU and the Neural Engine (ADR-0118): WebGPU
///     serves until the ANE sessions load and a parity probe passes, and any load failure, timeout
///     or probe failure refuses to the Neural Engine for the rest of the process. Thread-safe: the
///     background compile fires transitions while requests read <see cref="State" /> concurrently.
/// </summary>
public sealed class NeuralEngineSwitch
{
    private static readonly Dictionary<(NeuralEngineState State, NeuralEngineTrigger Trigger), NeuralEngineState> Transitions = new()
    {
        [(NeuralEngineState.WebGpuServing, NeuralEngineTrigger.Started)] = NeuralEngineState.CompilingNeuralEngine,
        [(NeuralEngineState.CompilingNeuralEngine, NeuralEngineTrigger.SessionsLoadedAndProbePassed)] =
            NeuralEngineState.NeuralEngineServing,
        [(NeuralEngineState.CompilingNeuralEngine, NeuralEngineTrigger.LoadFailed)] = NeuralEngineState.Refused,
        [(NeuralEngineState.CompilingNeuralEngine, NeuralEngineTrigger.TimedOut)] = NeuralEngineState.Refused,
        [(NeuralEngineState.CompilingNeuralEngine, NeuralEngineTrigger.ProbeFailed)] = NeuralEngineState.Refused
    };

    private readonly Lock gate = new();
    private readonly List<NeuralEngineTransition> history = [];

    /// <summary>The current state. Starts <see cref="NeuralEngineState.WebGpuServing" />.</summary>
    public NeuralEngineState State { get; private set; } = NeuralEngineState.WebGpuServing;

    /// <summary>Every move made so far, oldest first.</summary>
    public IReadOnlyList<NeuralEngineTransition> History
    {
        get
        {
            lock (gate)
            {
                return history.ToArray();
            }
        }
    }

    /// <summary>Whether embedding requests are currently served by the Neural Engine.</summary>
    public bool ServesNeuralEngine => State == NeuralEngineState.NeuralEngineServing;

    /// <summary>Moves the switch along a declared transition, or throws if none is declared for the current state and trigger.</summary>
    public void Fire(NeuralEngineTrigger trigger, string reason, DateTimeOffset at)
    {
        Guard.IsNotNull(reason);

        lock (gate)
        {
            if (!Transitions.TryGetValue((State, trigger), out var next))
            {
                throw new InvalidOperationException($"{State} has no declared transition for {trigger}.");
            }

            history.Add(new NeuralEngineTransition(State, next, trigger, reason, at));
            State = next;
        }
    }
}
