using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Node;
using AiRaccoon.Hosting.Proxy;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using xRetry.v3;
using Xunit;

namespace AiRaccoon.Tests.Integration.Setup.Serve;

/// <summary>
///     `serve --restart`'s stop is proof-gated (ADR-0106 D5), and the proof only speaks for the
///     connection it rode: the server identifies, and before that answer is read it has let go of
///     its port and a racer holds it. Every request after the proof must ride the proven
///     connection, so the token reaches the proven server or nobody.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class ServerRestartTokenExposureTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("server-restart-token-exposure");

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [RetryTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cycle_WhenTheServerLosesItsPortRightAfterIdentifying_SendsTheRacerNoSecretBytes(bool serverClosesTheConnection)
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        var tokenFile = new McpTokenFile(_dataRoot);
        var token = await tokenFile.EnsureAsync(TestContext.Current.CancellationToken);
        token.ShouldNotBeNull();
        var keyFile = new IdentityKeyFile(options);
        using var key = await keyFile.EnsureAsync(TestContext.Current.CancellationToken);
        key.ShouldNotBeNull();
        using var server = new DyingBackend(key, IdentityProof.RootFingerprint(keyFile.StateDirectory));
        server.HandOverOn("GET /observability", serverClosesTheConnection);
        var restart = new ServerRestart(TestData.CreateServerProbe(), TimeSpan.FromSeconds(5),
            TimeProvider.System, new IdentityProver(options, new HttpClient()), NullLogger<ServerRestart>.Instance);

        await restart.CycleAsync(server.Port, tokenFile, TestContext.Current.CancellationToken);

        var racer = server.Racer.ShouldNotBeNull("the cycle never identified the server, so the handover never ran");
        racer.TokenHeaderValues.ShouldBeEmpty(
            $"the racer on the server's port received the token; requests:\n{string.Join("\n---\n", racer.Requests)}");
        var shutdowns = server.Requests.Where(head => head.StartsWith($"POST {ShutdownEndpoint.Path}", StringComparison.Ordinal)).ToList();
        if (serverClosesTheConnection)
        {
            shutdowns.ShouldBeEmpty("the proven connection was gone, so nothing may carry the stop");
        }
        else
        {
            shutdowns.Count.ShouldBe(1, "the stop rides the proven connection, so the proven server still receives it");
            shutdowns[0].ShouldContain($"{McpTokenGate.HeaderName}: {token}");
        }
    }
}
