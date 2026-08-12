using AiRaccoon.Hosting.Common;

namespace AiRaccoon.Hosting.Node;

public interface IServerRestart
{
    /// <summary>Stops whatever ai-raccoon server owns <paramref name="port" /> and waits for it to let go.</summary>
    Task<RestartResult> CycleAsync(int port, McpTokenFile tokenFile, CancellationToken ctx);
}

/// <summary>What the restart attempt ended as, plus whatever the server said about itself.</summary>
public readonly record struct RestartResult(RestartOutcome Outcome, int? Pid = null, string? Version = null);
