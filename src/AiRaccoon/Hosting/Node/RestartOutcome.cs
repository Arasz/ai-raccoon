namespace AiRaccoon.Hosting.Node;

/// <summary>How a restart attempt ended; only Nothing and Stopped let `serve` go on to bind.</summary>
public enum RestartOutcome
{
    /// <summary>Nothing was listening — a restart is a plain start.</summary>
    Nothing,

    /// <summary>
    ///     The port gave the probe no answer, so nothing was asked to stop and nothing is known
    ///     about what holds it. Not the same fact as <see cref="Nothing" /> (ADR-0043).
    /// </summary>
    Unknown,

    /// <summary>The server stopped and the port freed.</summary>
    Stopped,

    /// <summary>Something is listening but does not identify as an ai-raccoon server.</summary>
    Foreign,

    /// <summary>No token to present, so nothing was asked to stop.</summary>
    NoToken,

    /// <summary>The server rejected our token — it serves another data root.</summary>
    Refused,

    /// <summary>The server has no /shutdown endpoint; it is too old to be cycled.</summary>
    Unsupported,

    /// <summary>The shutdown was accepted but the port was still in use at the bound.</summary>
    TimedOut
}
