using System.Net;
using System.Net.Sockets;
using System.Text;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Infrastructure.Resilience;
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

    /// <summary>
    ///     Every real server is token-gated, so the shape the probe actually meets in the field is
    ///     401 + a JSON-RPC error envelope — a listener that identified itself. Reading that as
    ///     "no answer" is what makes `serve --restart` refuse to cycle any running server.
    /// </summary>
    [Fact]
    public async Task ATokenGated401CarryingAJsonRpcBody_ReportsAnswered()
    {
        var probe = ProbeOver((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":null,"error":{"code":-32001,"message":"ai-raccoon: this endpoint needs the X-AiRaccoon-Token header"}}""",
                Encoding.UTF8, "application/json"),
        }));

        var verdict = await probe.ProbeAsync(7721, TestContext.Current.CancellationToken);

        verdict.ShouldBe(ProbeVerdict.Answered,
            "a 401 whose body is a JSON-RPC envelope proves an ai-raccoon listener answered");
    }

    /// <summary>
    ///     The per-attempt bound must be RETRYABLE. A probe that advertises three attempts and
    ///     abandons after one is why a slow first answer reads as "nothing is there".
    /// </summary>
    [Fact]
    public async Task AnAttemptThatExceedsTheBound_IsRetried_AndALaterAnswerWins()
    {
        var attempts = 0;
        var probe = ProbeOver(async (_, attemptToken) =>
        {
            // Honour the token: HttpClient awaits the handler chain and will not abandon a
            // handler that ignores cancellation, so an unhonoured delay hangs the whole attempt.
            if (Interlocked.Increment(ref attempts) == 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), attemptToken);
            }

            return JsonRpcResponse(HttpStatusCode.Unauthorized);
        });

        var verdict = await probe.ProbeAsync(7721, TestContext.Current.CancellationToken);

        attempts.ShouldBe(2, "the first attempt timed out and the second answered");
        verdict.ShouldBe(ProbeVerdict.Answered);
    }

    /// <summary>Every attempt exceeding the bound still reports Unanswered — and uses every attempt.</summary>
    [Fact]
    public async Task EveryAttemptExceedingTheBound_UsesAllAttempts_ThenReportsUnanswered()
    {
        var attempts = 0;
        var probe = ProbeOver(async (_, attemptToken) =>
        {
            Interlocked.Increment(ref attempts);
            await Task.Delay(TimeSpan.FromSeconds(30), attemptToken);
            return JsonRpcResponse(HttpStatusCode.OK);
        }, maxAttempts: 3);

        var verdict = await probe.ProbeAsync(7721, TestContext.Current.CancellationToken);

        attempts.ShouldBe(3, "an exhausted retry must have actually retried");
        verdict.ShouldBe(ProbeVerdict.Unanswered);
    }

    /// <summary>
    ///     Caller cancellation is not a timeout: it must surface as cancellation and must NOT be
    ///     converted into a retryable failure. Green before the fix as well as after — its proof is
    ///     the mutation recorded in the plan (convert cancellation unconditionally and it goes red).
    /// </summary>
    [Fact]
    public async Task CallerCancellation_Propagates_AndIsNotRetried()
    {
        using var caller = new CancellationTokenSource();
        var attempts = 0;
        var probe = ProbeOver(async (_, attemptToken) =>
        {
            Interlocked.Increment(ref attempts);
            await caller.CancelAsync();
            await Task.Delay(TimeSpan.FromSeconds(30), attemptToken);
            return JsonRpcResponse(HttpStatusCode.OK);
        });

        await Should.ThrowAsync<OperationCanceledException>(() => probe.ProbeAsync(7721, caller.Token));

        attempts.ShouldBe(1, "a cancelled caller must not trigger a retry storm");
    }

    /// <summary>
    ///     The production client carries its own bound (NodeRegistration sets
    ///     client.Timeout = RequestTimeout), which races the pipeline's and throws a
    ///     TaskCanceledException whose INNER type is TimeoutException — Polly matches the outer.
    ///     Every other test here uses a bare HttpClient with the 100s default, so without this the
    ///     fix is green in tests and unchanged in the field.
    /// </summary>
    [Fact]
    public async Task TheRetrySurvivesTheProductionClientTimeout()
    {
        var attempts = 0;
        var client = new HttpClient(new MockHttpMessageHandler(async (_, attemptToken) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), attemptToken);
            }

            return JsonRpcResponse(HttpStatusCode.Unauthorized);
        }))
        { Timeout = ShortTimeout };
        var probe = new ServerProbe(client,
            ResiliencePipelineFactory.CreateProbePipeline(initialDelay: TimeSpan.FromMilliseconds(1)), ShortTimeout);

        var verdict = await probe.ProbeAsync(7721, TestContext.Current.CancellationToken);

        attempts.ShouldBe(2, "the client's own timeout must not defeat the retry");
        verdict.ShouldBe(ProbeVerdict.Answered);
    }

    private static HttpResponseMessage JsonRpcResponse(HttpStatusCode status) => new(status)
    {
        Content = new StringContent("""{"jsonrpc":"2.0","id":null,"result":{}}""", Encoding.UTF8, "application/json"),
    };

    private static ServerProbe ProbeOver(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler,
        int maxAttempts = 3) =>
        new(new HttpClient(new MockHttpMessageHandler(handler)),
            ResiliencePipelineFactory.CreateProbePipeline(maxAttempts, TimeSpan.FromMilliseconds(1)),
            ShortTimeout);

    private sealed class MockHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request, cancellationToken);
    }
}
