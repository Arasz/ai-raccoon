using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Node;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Setup.Cli;
using AiRaccoon.Tests.TestHelpers;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.E2E;

/// <summary>
///     Loopback-token acceptance against a real `serve`
///     (docs/plans/2026-08-09-mcp-loopback-token-flow.md): /mcp demands the header, /observability
///     stays open, and the 401 keeps ServeRunner's probe recognizing the server.
/// </summary>
[Trait(TestCategories.Category, TestCategories.E2E)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
[Collection(E2ETestCollection.Name)]
public sealed class McpTokenGateE2ETests(McpTokenGateE2ETests.ServeFixture serve) : IClassFixture<McpTokenGateE2ETests.ServeFixture>
{
    private int Port => serve.Port;

    private string Url => $"http://127.0.0.1:{Port}/mcp";

    private string Token => new McpTokenFile(serve.DataRoot).Read()!;

    [Fact]
    public async Task Probe_StillRecognizesAServerThatDemandsAToken()
    {
        // The probe proves "an ai-raccoon server is here", never "I may use it": it carries no
        // token, and ServeRunner's attach path plus the proxy's acquire both depend on it.
        var responds = await TestData.CreateServerProbe().RespondsAsync(Port, TestContext.Current.CancellationToken);

        responds.ShouldBeTrue();
    }

    [Fact]
    public async Task Mcp_WithoutAToken_Is401()
    {
        using var response = await PostInitializeAsync(token: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldContain("jsonrpc"); // the probe's second discriminator
        body.ShouldContain(McpTokenFile.FileName); // names the file so a data-root mismatch is diagnosable
    }

    [Fact]
    public async Task Mcp_WithTheWrongToken_Is401()
    {
        using var response = await PostInitializeAsync("not-the-token");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Mcp_WithATokenOfADifferentLength_Is401()
    {
        using var response = await PostInitializeAsync(Token[..^1]);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Mcp_WithTheToken_Succeeds()
    {
        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Add(McpTokenGate.HeaderName, Token);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Name = "mcp-token-gate-test",
                Endpoint = new Uri(Url),
                TransportMode = HttpTransportMode.StreamableHttp
            },
            httpClient,
            LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning)),
            true);

        await using var client = await McpClient.CreateAsync(transport, cancellationToken: TestContext.Current.CancellationToken);
        var result = await client.CallToolAsync("memory_stats",
            new Dictionary<string, object?> { ["projectId"] = "acme" }, null, null, TestContext.Current.CancellationToken);

        // Content is IList<ContentBlock>: ToString() is the type name, so it can never be empty.
        result.IsError.ShouldNotBe(true);
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        text.ShouldContain("\"entries\"");
    }

    [Fact]
    public async Task Observability_NeedsNoToken()
    {
        // ADR-0008 discovery: `serve observability` finds a live server without knowing its data root.
        using var response = await serve.HttpClient.GetAsync($"http://127.0.0.1:{Port}/observability",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task<HttpResponseMessage> PostInitializeAsync(string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Url);
        request.Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""",
            Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        if (token is not null)
        {
            request.Headers.Add(McpTokenGate.HeaderName, token);
        }

        return await serve.HttpClient.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>One real token-guarded `serve` for the whole class: a boot per test would hold the
    /// process-global passphrase gate six times over.</summary>
    [UsedImplicitly]
    public sealed class ServeFixture : IAsyncLifetime
    {
        private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(120));
        private readonly LockingWriter _stderr = new();
        private readonly LockingWriter _stdout = new();
        private readonly TimeProvider _timeProvider = TimeProvider.System;

        private string? _originalPassphrase;
        private Task<int> _serve = Task.FromResult(0);
        public HttpClient HttpClient { get; } = new();

        public string DataRoot { get; } = TestData.CreateTempRoot("ai-raccoon-mcp-token-gate");

        public int Port { get; private set; }

        public async ValueTask InitializeAsync()
        {
            await TestData.EnvVarGate.WaitAsync(TestContext.Current.CancellationToken);
            _originalPassphrase = Environment.GetEnvironmentVariable(EnvEncryptionKeyProvider.EnvVarName);
            Environment.SetEnvironmentVariable(EnvEncryptionKeyProvider.EnvVarName, null);

            Port = FreePort();
            CliArgs.TryParse(["--data-root", DataRoot, "serve", "--port", Port.ToString()], out var parsed);
            parsed!.Errors.ShouldBeEmpty();
            _serve = TestData.CreateNodeRunner(parsed.ServerConfig.Options).RunAsync(parsed, new StandardStreams(TextReader.Null, _stdout, _stderr), _cts.Token);
            await WaitForListeningAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            await _serve;
            _cts.Dispose();
            Environment.SetEnvironmentVariable(EnvEncryptionKeyProvider.EnvVarName, _originalPassphrase);
            TestData.EnvVarGate.Release();
            Directory.Delete(DataRoot, true);
            HttpClient.Dispose();
        }

        private async Task WaitForListeningAsync()
        {
            var success = await WaitByPolling
                .WaitForAsync(() => ValueTask.FromResult(_stdout.ToString().Contains("http://", StringComparison.Ordinal)), _timeProvider, TestContext.Current.CancellationToken)
                .ConfigureAwait(false);

            if (!success)
            {
                throw new TimeoutException($"serve never reported a URL; stderr: {_stderr}");
            }
        }

        private static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }

    /// <summary>Thread-safe capture for the runner's stdout/stderr writers.</summary>
    private sealed class LockingWriter : TextWriter
    {
        private readonly StringBuilder _buffer = new();
        private readonly Lock _lock = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            lock (_lock)
            {
                _buffer.Append(value);
            }
        }

        public override void Write(string? value)
        {
            lock (_lock)
            {
                _buffer.Append(value);
            }
        }

        public override void WriteLine(string? value)
        {
            lock (_lock)
            {
                _buffer.AppendLine(value);
            }
        }

        public override string ToString()
        {
            lock (_lock)
            {
                return _buffer.ToString();
            }
        }
    }
}
