using AiRaccoon.Hosting.Common;

namespace AiRaccoon.Tests.TestHelpers;

/// <summary>
///     A probe that answers one chosen verdict, so the acquire policy's three arms (refused,
///     answered, no answer) are pinned without a real socket. Records every port it was asked about.
/// </summary>
internal sealed class FakeServerProbe(ProbeVerdict verdict) : IServerProbe
{
    public List<int> Calls { get; } = [];

    public ProbeVerdict Verdict { get; set; } = verdict;

    public Task<bool> RespondsAsync(int port, CancellationToken ctx) => Task.FromResult(Responds());

    public Task<bool> RespondsAsync(Uri endpoint, CancellationToken ctx) => Task.FromResult(Responds());

    public Task<ProbeVerdict> ProbeAsync(int port, CancellationToken ctx)
    {
        Calls.Add(port);
        return Task.FromResult(Verdict);
    }

    public Task<ProbeVerdict> ProbeAsync(Uri endpoint, CancellationToken ctx) => Task.FromResult(Verdict);

    private bool Responds() => Verdict is ProbeVerdict.Answered;
}
