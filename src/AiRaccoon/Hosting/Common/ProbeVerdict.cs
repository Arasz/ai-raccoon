namespace AiRaccoon.Hosting.Common;

/// <summary>
///     What one probe of a port learned. Only <see cref="NotListening" /> proves the port is free
///     (ADR-0043); <see cref="Unanswered" /> is the absence of a fact, not a fact about absence.
/// </summary>
public enum ProbeVerdict
{
    /// <summary>An ai-raccoon MCP endpoint answered.</summary>
    Answered,

    /// <summary>The connection was refused: nothing holds the port.</summary>
    NotListening,

    /// <summary>
    ///     No usable answer — the probe timed out, the connection died, or the reply was not
    ///     ai-raccoon's. Whether anything holds the port is unknown.
    /// </summary>
    Unanswered
}
