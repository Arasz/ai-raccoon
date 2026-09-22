using AiRaccoon.Hosting.Common;

namespace AiRaccoon.Hosting.Node;

public interface IServerRestart
{
    /// <summary>
    ///     Stops whatever ai-raccoon server owns <paramref name="port" /> and waits for it to let go.
    ///     <paramref name="attaching" /> is the explicit `--attach` opt-in: a restart sends the
    ///     listener the data root's token, so without it an identifying listener is refused
    ///     (<see cref="RestartOutcome.AttachRequired" />) rather than trusted on its self-asserted
    ///     /observability name (F70/K1).
    /// </summary>
    Task<RestartResult> CycleAsync(int port, McpTokenFile tokenFile, bool attaching, CancellationToken ctx);
}

/// <summary>What the restart attempt ended as, plus whatever the server said about itself.</summary>
public readonly record struct RestartResult(RestartOutcome Outcome, int? Pid = null, string? Version = null);
