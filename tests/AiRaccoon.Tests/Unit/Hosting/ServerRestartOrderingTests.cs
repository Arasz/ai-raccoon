using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Node;
using AiRaccoon.Hosting.Proxy;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Hosting;

/// <summary>
///     ADR-0107 PC.4: the identity proof must settle before the token is ever read, so an unproven
///     listener with no token reports Unproven (50), never NoToken (51) — the token read is
///     unreachable while the proof has not settled, and the order must stay that way.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ServerRestartOrderingTests
{
    private static ServerRestart Subject(IIdentityProver prover) =>
        new(new FakeServerProbe(ProbeVerdict.Answered), new PlainHttpClientFactory(), TimeSpan.FromMilliseconds(50),
            TimeProvider.System, prover, NullLogger<ServerRestart>.Instance);

    [Fact]
    public async Task CycleAsync_UnprovenListener_WithNoTokenEither_ReportsUnproven_NotNoToken()
    {
        var restart = Subject(new FakeIdentityProver(IdentityProofFailure.NoKey));
        var dataRoot = TestData.CreateTempRoot("server-restart-ordering");
        try
        {
            // No token minted for this root: Read() returns null. If the token were checked before
            // (or regardless of) the proof, this would report NoToken instead.
            var tokenFile = new McpTokenFile(dataRoot);

            var result = await restart.CycleAsync(1, tokenFile, TestContext.Current.CancellationToken);

            result.Outcome.ShouldBe(RestartOutcome.Unproven);
        }
        finally
        {
            TestData.DeleteTempRoot(dataRoot);
        }
    }

    private sealed class PlainHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
