using System.Net;
using System.Net.Sockets;
using System.Text;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Resilience;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Hosting;

/// <summary>
///     The probe reports what it learned, not a boolean that reads every failure as an empty port
///     (ADR-0043). A refused connection is proof nothing holds the port; a request that timed out
///     is no proof of anything, and the two must not be the same value.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class ServerProbeVerdictTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(80);

    [Fact]
    public async Task AConnectionRefusedOnEveryAttempt_ReportsNotListening()
    {
        var probe = ProbeOver((_, _) => throw new HttpRequestException("Connection refused (127.0.0.1:7721)",
            new SocketException((int)SocketError.ConnectionRefused)));

        var verdict = await probe.ProbeAsync(7721, TestContext.Current.CancellationToken);

        verdict.ShouldBe(ProbeVerdict.NotListening);
    }

    /// <summary>
    ///     The defect this gates: a listener that holds the port and never answers used to be
    ///     indistinguishable from a port with nothing on it.
    /// </summary>
    [Fact]
    public async Task AListenerThatNeverAnswers_ReportsUnanswered_NotAnEmptyPort()
    {
        var probe = ProbeOver(async (_, attemptToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), attemptToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var verdict = await probe.ProbeAsync(7721, TestContext.Current.CancellationToken);

        verdict.ShouldBe(ProbeVerdict.Unanswered);
    }

    /// <summary>A connection accepted and then dropped proves a listener, so it is not an empty port either.</summary>
    [Fact]
    public async Task AConnectionThatDies_ReportsUnanswered()
    {
        var probe = ProbeOver((_, _) => throw new HttpRequestException("The connection was reset",
            new SocketException((int)SocketError.ConnectionReset)));

        var verdict = await probe.ProbeAsync(7721, TestContext.Current.CancellationToken);

        verdict.ShouldBe(ProbeVerdict.Unanswered);
    }

    [Fact]
    public async Task AReplyThatIsNotJsonRpc_ReportsUnanswered()
    {
        var probe = ProbeOver((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>hello</html>", Encoding.UTF8, "text/html")
        }));

        var verdict = await probe.ProbeAsync(7721, TestContext.Current.CancellationToken);

        verdict.ShouldBe(ProbeVerdict.Unanswered);
    }

    [Fact]
    public async Task AJsonRpcReply_ReportsAnswered_AndStillRespondsTrue()
    {
        var probe = ProbeOver((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"jsonrpc\": \"2.0\"}", Encoding.UTF8, "application/json")
        }));

        (await probe.ProbeAsync(7721, TestContext.Current.CancellationToken)).ShouldBe(ProbeVerdict.Answered);
        (await probe.RespondsAsync(7721, TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    [Fact]
    public async Task RespondsAsync_StaysFalseForEveryVerdictButAnswered()
    {
        var refused = ProbeOver((_, _) => throw new HttpRequestException("Connection refused (127.0.0.1:7721)",
            new SocketException((int)SocketError.ConnectionRefused)));
        var silent = ProbeOver(async (_, attemptToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), attemptToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        (await refused.RespondsAsync(7721, TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await silent.RespondsAsync(7721, TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    private static ServerProbe ProbeOver(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) =>
        new(new HttpClient(new MockHttpMessageHandler(handler)),
            ResiliencePipelineFactory.CreateProbePipeline(initialDelay: TimeSpan.FromMilliseconds(1)),
            ShortTimeout);

    private sealed class MockHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request, cancellationToken);
    }
}
