using System.Net;
using System.Net.Http.Json;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Node;
using AiRaccoon.Hosting.Proxy;
using AiRaccoon.Observability;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Hosting;

/// <summary>
///     Every outcome `serve --restart` can reach, with the requests it sent to get there: what the
///     probe saw, whether the listener proved and identified, whether a token was there, how
///     /shutdown answered, and whether the port then freed.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ServerRestartOutcomeTests : IDisposable
{
    private const int Port = 41999;
    private const int Pid = 4242;
    private const string Version = "9.9.9";

    private readonly string _dataRoot = TestData.CreateTempRoot("server-restart-outcomes");

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [Fact]
    public async Task NothingListening_IsNothing_AndSendsNoRequest()
    {
        var (result, http, prover) = await CycleAsync([ProbeVerdict.NotListening]);

        result.ShouldBe(new RestartResult(RestartOutcome.Nothing));
        http.Requests.ShouldBeEmpty();
        prover.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnUnansweredProbe_IsUnknown_AndSendsNoRequest()
    {
        var (result, http, prover) = await CycleAsync([ProbeVerdict.Unanswered]);

        result.ShouldBe(new RestartResult(RestartOutcome.Unknown));
        http.Requests.ShouldBeEmpty();
        prover.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnUnprovenListener_IsUnproven_AndSendsNoRequest()
    {
        var (result, http, _) = await CycleAsync([ProbeVerdict.Answered], prover: new FakeIdentityProver(IdentityProofFailure.NoKey));

        result.ShouldBe(new RestartResult(RestartOutcome.Unproven, Reason: IdentityProofFailure.NoKey));
        http.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "ai-raccoon")]
    [InlineData(HttpStatusCode.OK, "something-else")]
    public async Task AListenerThatWillNotIdentifyAsAiRaccoon_IsForeign_AndNeverAskedToStop(HttpStatusCode identify, string name)
    {
        var (result, http, _) = await CycleAsync([ProbeVerdict.Answered], identify: identify, name: name);

        result.ShouldBe(new RestartResult(RestartOutcome.Foreign));
        http.Requests.ShouldBe(["GET /observability"]);
    }

    [Fact]
    public async Task NoToken_IsNoToken_WithTheServerItFound()
    {
        var (result, http, _) = await CycleAsync([ProbeVerdict.Answered], mintToken: false);

        result.ShouldBe(new RestartResult(RestartOutcome.NoToken, Pid, Version));
        http.Requests.ShouldBe(["GET /observability"]);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, RestartOutcome.Refused)]
    [InlineData(HttpStatusCode.NotFound, RestartOutcome.Unsupported)]
    [InlineData(HttpStatusCode.MethodNotAllowed, RestartOutcome.Unsupported)]
    public async Task AShutdownTheServerTurnsDown_EndsWithoutWaiting(HttpStatusCode shutdown, RestartOutcome expected)
    {
        var probe = new SequenceProbe([ProbeVerdict.Answered]);
        var (result, http, _) = await CycleAsync(probe, shutdown: shutdown);

        result.ShouldBe(new RestartResult(expected, Pid, Version));
        http.Requests.ShouldBe(["GET /observability", "POST /shutdown"]);
        probe.Calls.ShouldBe(1, "a turned-down shutdown never polls the port");
    }

    [Fact]
    public async Task AnAcceptedShutdownWhosePortNeverFrees_TimesOut()
    {
        var (result, _, _) = await CycleAsync([ProbeVerdict.Answered], shutdown: HttpStatusCode.Accepted);

        result.ShouldBe(new RestartResult(RestartOutcome.TimedOut, Pid, Version));
    }

    [Fact]
    public async Task AnAcceptedShutdownWhosePortFrees_IsStopped_AndSendsTheToken()
    {
        var (result, http, _) = await CycleAsync([ProbeVerdict.Answered, ProbeVerdict.NotListening], shutdown: HttpStatusCode.Accepted);

        result.ShouldBe(new RestartResult(RestartOutcome.Stopped, Pid, Version));
        http.Requests.ShouldBe(["GET /observability", "POST /shutdown"]);
        http.ShutdownToken.ShouldBe(new McpTokenFile(_dataRoot).Read());
    }

    [Fact]
    public async Task AShutdownWhoseConnectionDies_CountsAsAccepted_AndThePortDecides()
    {
        var (result, _, _) = await CycleAsync([ProbeVerdict.Answered, ProbeVerdict.NotListening], shutdown: null);

        result.ShouldBe(new RestartResult(RestartOutcome.Stopped, Pid, Version));
    }

    private Task<(RestartResult Result, StubHttp Http, FakeIdentityProver Prover)> CycleAsync(ProbeVerdict[] verdicts,
        FakeIdentityProver? prover = null, HttpStatusCode identify = HttpStatusCode.OK, string name = ServerInfo.ServerName,
        bool mintToken = true, HttpStatusCode? shutdown = HttpStatusCode.Accepted) =>
        CycleAsync(new SequenceProbe(verdicts), prover, identify, name, mintToken, shutdown);

    private async Task<(RestartResult Result, StubHttp Http, FakeIdentityProver Prover)> CycleAsync(SequenceProbe probe,
        FakeIdentityProver? prover = null, HttpStatusCode identify = HttpStatusCode.OK, string name = ServerInfo.ServerName,
        bool mintToken = true, HttpStatusCode? shutdown = HttpStatusCode.Accepted)
    {
        var tokenFile = new McpTokenFile(_dataRoot);
        if (mintToken)
        {
            (await tokenFile.EnsureAsync(TestContext.Current.CancellationToken)).ShouldNotBeNull();
        }

        prover ??= new FakeIdentityProver();
        var http = new StubHttp(identify, new ServerInfo(name, Version, Pid, new OtlpInfo(false, null, null)), shutdown);
        var restart = new ServerRestart(probe, http, TimeSpan.FromMilliseconds(300), TimeProvider.System, prover,
            NullLogger<ServerRestart>.Instance);

        var result = await restart.CycleAsync(Port, tokenFile, TestContext.Current.CancellationToken);
        return (result, http, prover);
    }

    /// <summary>Answers each probe with the next verdict; the last one repeats.</summary>
    private sealed class SequenceProbe(ProbeVerdict[] verdicts) : IServerProbe
    {
        public int Calls { get; private set; }

        public Task<bool> RespondsAsync(int port, CancellationToken ctx) => throw new NotSupportedException();

        public Task<bool> RespondsAsync(Uri endpoint, CancellationToken ctx) => throw new NotSupportedException();

        public Task<ProbeVerdict> ProbeAsync(int port, CancellationToken ctx) =>
            Task.FromResult(verdicts[Math.Min(Calls++, verdicts.Length - 1)]);

        public Task<ProbeVerdict> ProbeAsync(Uri endpoint, CancellationToken ctx) => throw new NotSupportedException();
    }

    /// <summary>The server side of /observability and /shutdown; a null shutdown status drops the connection.</summary>
    private sealed class StubHttp(HttpStatusCode identify, ServerInfo info, HttpStatusCode? shutdown) : HttpMessageHandler, IHttpClientFactory
    {
        public List<string> Requests { get; } = [];

        public string? ShutdownToken { get; private set; }

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            if (request.RequestUri.AbsolutePath == ShutdownEndpoint.Path)
            {
                ShutdownToken = request.Headers.TryGetValues(McpTokenGate.HeaderName, out var values) ? values.Single() : null;
                return shutdown is { } status
                    ? Task.FromResult(new HttpResponseMessage(status))
                    : throw new HttpRequestException("connection reset");
            }

            return Task.FromResult(new HttpResponseMessage(identify) { Content = JsonContent.Create(info) });
        }
    }
}
