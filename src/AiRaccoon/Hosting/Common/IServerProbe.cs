namespace AiRaccoon.Hosting.Common;

public interface IServerProbe
{
    Task<bool> RespondsAsync(int port, CancellationToken ctx);
    Task<bool> RespondsAsync(Uri endpoint, CancellationToken ctx);

    /// <summary>What the probe learned, keeping "no answer" apart from "nothing is there" (ADR-0043).</summary>
    Task<ProbeVerdict> ProbeAsync(int port, CancellationToken ctx);

    /// <summary>What the probe learned, keeping "no answer" apart from "nothing is there" (ADR-0043).</summary>
    Task<ProbeVerdict> ProbeAsync(Uri endpoint, CancellationToken ctx);
}
