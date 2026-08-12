using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiRaccoon.Tests.E2E;
using AiRaccoon.Tests.Unit.Embedding;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Shouldly;
using Xunit;
using ProxyForwarder = AiRaccoon.Hosting.Proxy.ProxyForwarder;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     The stdio proxy's forwarding hook (docs/adr/0020-always-on-http-stdio-proxy.md): a local
///     McpServer with no tools of its own must answer entirely out of the live HTTP backend.
/// </summary>
[Trait(TestCategories.Category, TestCategories.E2E)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
[Collection(E2ETestCollection.Name)]
public sealed class ProxyForwardTests : IAsyncLifetime
{
    private const string LocalServerName = "proxy-local-must-not-be-advertised";

    private readonly List<McpServerFactory> _reacquired = [];
    private McpClient _backend = null!;
    private McpServerFactory _factory = null!;
    private bool _failReacquire;
    private FakeEmbeddingEndpoint _openAi = null!;
    private McpClient _proxyClient = null!;
    private McpServer _proxyServer = null!;
    private Task _proxyServerRun = null!;
    private int _reacquireCount;
    private BackendRecorder _recorder = null!;
    private ReadRecorder _wire = null!;

    public async ValueTask InitializeAsync()
    {
        await TestData.CreateBundledModel().EnsureAsync(TestContext.Current.CancellationToken);
        _openAi = await FakeEmbeddingEndpoint.StartAsync(TestContext.Current.CancellationToken);
        _factory = new McpServerFactory();
        _backend = await _factory.CreateClientAsync();
        _recorder = new BackendRecorder(_backend);

        // Deliberately no WithTools/WithPrompts: an interception failure has to surface as an
        // empty tool list, never as a second server quietly composing its own surface.
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = LocalServerName, Version = "0.0.0" }
        };
        options.Filters.Message.IncomingFilters.Add(
            ProxyForwarder.Create(_recorder, ReacquireAsync, NullLogger.Instance));

        var toProxy = new Pipe();
        var fromProxy = new Pipe();
        _proxyServer = McpServer.Create(
            new StreamServerTransport(toProxy.Reader.AsStream(), fromProxy.Writer.AsStream(), "proxy",
                NullLoggerFactory.Instance),
            options, NullLoggerFactory.Instance, null);
        _proxyServerRun = _proxyServer.RunAsync(CancellationToken.None);

        _wire = new ReadRecorder(fromProxy.Reader.AsStream());
        _proxyClient = await McpClient.CreateAsync(
            new StreamClientTransport(toProxy.Writer.AsStream(), _wire, NullLoggerFactory.Instance),
            cancellationToken: TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _proxyClient.DisposeAsync();
        await _proxyServer.DisposeAsync();
        await _proxyServerRun.WaitAsync(TimeSpan.FromSeconds(10)).ContinueWith(_ => { }, TaskScheduler.Default);
        await _backend.DisposeAsync();
        await _factory.DisposeAsync();
        foreach (var factory in _reacquired)
        {
            await factory.DisposeAsync();
        }

        await _openAi.DisposeAsync();
    }

    /// <summary>Stands in for the runner's backend acquisition: a brand new server, counted.</summary>
    private async Task<McpSession> ReacquireAsync(string? revision, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _reacquireCount);
        if (_failReacquire)
        {
            throw new HttpRequestException("no backend could be started");
        }

        var factory = new McpServerFactory();
        _reacquired.Add(factory);
        return await factory.CreateClientAsync();
    }

    [Fact]
    public async Task ToolList_ThroughTheProxy_MatchesTheBackendExactly()
    {
        using var cts = Deadline();

        var direct = await _backend.ListToolsAsync((RequestOptions?)null, cts.Token);
        var throughProxy = await _proxyClient.ListToolsAsync((RequestOptions?)null, cts.Token);

        var expected = direct.Select(t => t.Name).ToArray();
        expected.ShouldNotBeEmpty();
        throughProxy.Select(t => t.Name).OrderBy(n => n).ShouldBe(expected.OrderBy(n => n));
    }

    [Fact]
    public async Task UnknownMethod_IsForwarded()
    {
        using var cts = Deadline();

        var failure = await Should.ThrowAsync<McpProtocolException>(async () =>
            await _proxyClient.SendRequestAsync(
                new JsonRpcRequest { Method = "x/unknown", Id = new RequestId("unknown-1") }, cts.Token));

        // No method table anywhere: a method the proxy has never heard of still reaches the
        // backend, and the refusal the client sees is the backend's own words.
        _recorder.Requests.ShouldContain(r => r.Method == "x/unknown");
        failure.Message.ShouldContain("x/unknown");
    }

    [Fact]
    public async Task RequestId_IsPreserved()
    {
        using var cts = Deadline();

        var response = await _proxyClient.SendRequestAsync(
            new JsonRpcRequest { Method = "tools/list", Id = new RequestId("abc-1") }, cts.Token);

        response.Id.Id.ShouldBe("abc-1");
        _recorder.Requests.ShouldContain(r => r.Method == "tools/list" && Equals(r.Id.Id, "abc-1"));
    }

    [Fact]
    public async Task Notification_IsForwarded_AndProducesNoResponse()
    {
        using var cts = Deadline();
        var before = _wire.Text.Length;

        await _proxyClient.SendMessageAsync(
            new JsonRpcNotification
            {
                Method = "notifications/cancelled",
                Params = new JsonObject { ["requestId"] = "never-issued" }
            }, cts.Token);
        await WaitUntilAsync(
            () => _recorder.Notifications.Any(n => n.Method == "notifications/cancelled"), cts.Token);

        // The next frame on the wire must be that request's response: a notification that produced
        // a response of its own would land ahead of it.
        await _proxyClient.SendRequestAsync(
            new JsonRpcRequest { Method = "tools/list", Id = new RequestId("after-notification") }, cts.Token);

        var frames = _wire.Text[before..]
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        frames.Length.ShouldBe(1);
        frames[0].ShouldContain("after-notification");
    }

    [Fact]
    public void Initialize_ReportsTheBackendCapabilities()
    {
        // Against the live backend, never a literal: the local server registers nothing, so its
        // own capabilities cannot match these.
        JsonSerializer.Serialize(_proxyClient.ServerCapabilities)
            .ShouldBe(JsonSerializer.Serialize(_backend.ServerCapabilities));
        _proxyClient.ServerCapabilities.Tools.ShouldNotBeNull();
        _proxyClient.ServerCapabilities.Prompts.ShouldNotBeNull();
    }

    [Fact]
    public async Task WhenTheBackendDies_TheNextCallReacquiresAndSucceeds()
    {
        using var cts = Deadline();
        (await CallAsync("memory_list", cts.Token)).IsError.ShouldNotBe(true);

        // What IdleWatchdog does after four idle hours; the proxy never pings to prevent it.
        await _factory.DisposeAsync();

        (await CallAsync("memory_list", cts.Token)).IsError.ShouldNotBe(true);
        _reacquireCount.ShouldBe(1);
    }

    [Fact]
    public async Task WhenReacquireAlsoFails_TheClientGetsAJsonRpcError()
    {
        using var cts = Deadline();
        _failReacquire = true;
        await _factory.DisposeAsync();

        await Should.ThrowAsync<McpException>(async () => await CallAsync("memory_list", cts.Token));

        // Exactly once: not a loop, not a backoff ladder.
        _reacquireCount.ShouldBe(1);
    }

    [Fact]
    public async Task BackendError_DoesNotReacquire()
    {
        using var cts = Deadline();

        // A backend that answers is alive, however unhappy the answer. x/unknown is the trap:
        // it comes back as an HTTP-level failure, not an MCP one.
        await Should.ThrowAsync<McpException>(async () => await _proxyClient.SendRequestAsync(
            new JsonRpcRequest { Method = "x/unknown", Id = new RequestId("alive-1") }, cts.Token));
        await Should.ThrowAsync<McpException>(async () => await CallAsync("no_such_tool", cts.Token));

        _reacquireCount.ShouldBe(0);
    }

    private async Task<CallToolResult> CallAsync(string tool, CancellationToken cancellationToken) =>
        await _proxyClient.CallToolAsync(tool, new Dictionary<string, object?> { ["projectId"] = "reconnect" }, null,
            null, cancellationToken);

    private static CancellationTokenSource Deadline()
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        return cts;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(25, cancellationToken);
        }
    }

    /// <summary>Records what the forwarder hands the backend, then delegates to the live session.</summary>
    private sealed class BackendRecorder(McpSession inner) : McpSession
    {
        private readonly List<JsonRpcNotification> _notifications = [];
        private readonly List<JsonRpcRequest> _requests = [];

        public IReadOnlyList<JsonRpcRequest> Requests
        {
            get
            {
                lock (_requests)
                {
                    return [.. _requests];
                }
            }
        }

        public IReadOnlyList<JsonRpcNotification> Notifications
        {
            get
            {
                lock (_notifications)
                {
                    return [.. _notifications];
                }
            }
        }

        public override string? SessionId => inner.SessionId;

        public override string? NegotiatedProtocolVersion => inner.NegotiatedProtocolVersion;

        public override Task<JsonRpcResponse> SendRequestAsync(JsonRpcRequest request,
            CancellationToken cancellationToken = default)
        {
            lock (_requests)
            {
                _requests.Add(request);
            }

            return inner.SendRequestAsync(request, cancellationToken);
        }

        public override Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
        {
            if (message is JsonRpcNotification notification)
            {
                lock (_notifications)
                {
                    _notifications.Add(notification);
                }
            }

            return inner.SendMessageAsync(message, cancellationToken);
        }

        public override IAsyncDisposable RegisterNotificationHandler(string method,
            Func<JsonRpcNotification, CancellationToken, ValueTask> handler) =>
            inner.RegisterNotificationHandler(method, handler);

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Passthrough read side that keeps every byte the proxy sent to its client.</summary>
    private sealed class ReadRecorder(Stream inner) : Stream
    {
        private readonly StringBuilder _text = new();

        public string Text
        {
            get
            {
                lock (_text)
                {
                    return _text.ToString();
                }
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            Record(buffer.Span[..read]);
            return read;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            Record(buffer.AsSpan(offset, read));
            return read;
        }

        public override void Flush() => inner.Flush();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private void Record(ReadOnlySpan<byte> read)
        {
            if (read.IsEmpty)
            {
                return;
            }

            lock (_text)
            {
                _text.Append(Encoding.UTF8.GetString(read));
            }
        }
    }
}
