using AiRaccoon.Hosting.Common;

namespace AiRaccoon.Hosting.Node;

/// <summary>
///     The declared transitions of a `serve --restart` attempt (ADR-0043): what a probe settles,
///     what lets `serve` go on to bind, and what a refused bind proves about the pre-check.
/// </summary>
internal static class RestartTransition
{
    /// <summary>
    ///     The outcome a probe settles on its own, or null when an ai-raccoon answered and the
    ///     cycle carries on to identify it.
    /// </summary>
    public static RestartOutcome? FromProbe(ProbeVerdict verdict) =>
        verdict switch
        {
            ProbeVerdict.Answered => null,
            ProbeVerdict.NotListening => RestartOutcome.Nothing,
            _ => RestartOutcome.Unknown
        };

    /// <summary>
    ///     True when `serve` may go on to bind rather than refusing with an operator line. Knowing
    ///     nothing is not a refusal — the bind is the only thing that can settle it.
    /// </summary>
    public static bool MayBind(RestartOutcome outcome) => outcome is RestartOutcome.Nothing or RestartOutcome.Stopped or RestartOutcome.Unknown;

    /// <summary>
    ///     What an address-in-use bind proves, given what the pre-check believed and what the port
    ///     says now. The bind failure is proof the port is held: it refutes any belief that it was free.
    /// </summary>
    public static BindRefusal AfterBindRefused(RestartOutcome believed, ProbeVerdict afterBind, bool restarting) =>
        (restarting, believed, afterBind) switch
        {
            (false, _, ProbeVerdict.Answered) => BindRefusal.Attach,
            (false, _, _) => BindRefusal.PortInUse,
            // The pre-check established nothing, so nobody took the port from this run: it never
            // held it and never proved it free. Reporting a lost port here would invent the loss.
            (true, RestartOutcome.Unknown, _) => BindRefusal.HeldUnidentified,
            (true, _, ProbeVerdict.Answered) => BindRefusal.LostThePort,
            (true, _, _) => BindRefusal.PortInUse
        };
}

/// <summary>What `serve` reports once its bind was refused with address-in-use.</summary>
internal enum BindRefusal
{
    /// <summary>An ai-raccoon server owns the port and no restart was asked for: attach to it.</summary>
    Attach,

    /// <summary>The port is held by something this run cannot cycle.</summary>
    PortInUse,

    /// <summary>The pre-check proved the port was free, so the holder arrived after it: retryable.</summary>
    LostThePort,

    /// <summary>The pre-check got no answer, so nothing was asked to stop and the holder is unidentified.</summary>
    HeldUnidentified
}
