using System.Net;
using System.Net.Sockets;
using System.Text;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Node;
using AiRaccoon.Hosting.Proxy;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Setup;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Setup.Serve;

/// <summary>
///     F70 (owner ruling K1, option a — private spawn): the proxy must never hand the data root's
///     loopback token to a listener that merely holds the configured port. It starts its own backend
///     on an ephemeral port and connects only to the URL that child printed; a squatter that binds
///     the port first is never contacted at all.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class BackendSessionsTokenExposureTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("backend-sessions-squatter");
    private readonly List<TcpListener> _listeners = [];

    public void Dispose()
    {
        foreach (var listener in _listeners)
        {
            listener.Stop();
        }

        TestData.DeleteTempRoot(_dataRoot);
    }

    /// <summary>
    ///     The gate: a squatter answering /mcp with a JSON-RPC body (what ServerProbe counts as
    ///     "an ai-raccoon server") must receive no request at all — no probe, and above all no
    ///     X-AiRaccoon-Token and no tool payload. Watched red before private spawn: the proxy
    ///     attached and sent the token byte-for-byte.
    /// </summary>
    [RetryFact]
    public async Task OpenAsync_WithASquatterHoldingTheConfiguredPort_DoesNotSendItTheToken()
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EnvEncryptionKeyProvider.EnvVarName, null));
        (await new McpTokenFile(_dataRoot).EnsureAsync(TestContext.Current.CancellationToken)).ShouldNotBeNull();
        var squatter = StartSquatter();
        var privatePort = 0;

        await using (var sessions = Subject(squatter.Port))
        {
            try
            {
                await sessions.OpenAsync(null, TestContext.Current.CancellationToken);
            }
            catch (BackendUnavailableException)
            {
                // Red: the session against the squatter cannot complete. The token is already sent
                // by then, which is exactly what the assertions below measure.
            }

            squatter.Requests.ShouldBeEmpty(
                $"the proxy contacted a squatter holding port {squatter.Port}; token headers seen: " +
                $"{string.Join(", ", squatter.TokenHeaderValues)}; request:\n{string.Join("\n---\n", squatter.Requests)}");

            if (sessions.Url.Length > 0)
            {
                sessions.Url.ShouldNotBe(UrlFor(squatter.Port),
                    "the squatter must not be treated as the backend");
                privatePort = new Uri(sessions.Url).Port;
            }
        }

        if (privatePort != 0)
        {
            await RaccoonBackendCleanup.ShutdownIfRunningAsync(_dataRoot, privatePort, CancellationToken.None);
        }
    }

    /// <summary>
    ///     The positive control for the gate: with the explicit opt-in, the proxy still reaches a
    ///     real ai-raccoon server on the configured port and opens a session through its token — so
    ///     "never attach at all" cannot satisfy the threshold test above.
    /// </summary>
    [RetryFact]
    public async Task OpenAsync_WithAttachAgainstARealServer_OpensASessionThroughTheToken()
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EnvEncryptionKeyProvider.EnvVarName, null));
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        lease.ReleaseForBind();
        await using var server = ServeHarness.Start(["--data-root", _dataRoot, "serve", "--port", port.ToString()]);
        await server.WaitForUrlAsync(TestContext.Current.CancellationToken);

        await using var sessions = Subject(port, attach: true);
        var session = await sessions.OpenAsync(null, TestContext.Current.CancellationToken);

        sessions.Url.ShouldBe(UrlFor(port));
        // The session only exists if the server accepted the token; listing tools proves it is the
        // real backend, not a listener that merely answers JSON-RPC.
        (await session.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken)).ShouldNotBeEmpty();

        (await server.StopAsync()).ShouldBe(ExitCode.Success);
    }

    private BackendSessions Subject(int port, bool attach = false) =>
        new(new BackendLauncher(TestData.CreateServerProbe(), BackendLauncher.DefaultBudget,
                TimeProvider.System, NullLogger<BackendLauncher>.Instance),
            new PlainHttpClientFactory(), NullLoggerFactory.Instance, ServeExecutable,
            new ServerConfig(port, McpTransport.Http, new InfrastructureOptions { DataRoot = _dataRoot, Scope = InstallScope.User })
            {
                Attach = attach
            });

    private static string UrlFor(int port) => $"http://127.0.0.1:{port}/mcp";

    private static string ServeExecutable =>
        Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "AiRaccoon.exe" : "AiRaccoon");

    /// <summary>
    ///     Holds a loopback port, records every request it receives and answers /mcp with a body
    ///     containing "jsonrpc" — the exact shape ServerProbe accepts as an ai-raccoon server.
    /// </summary>
    private Squatter StartSquatter()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _listeners.Add(listener);
        var squatter = new Squatter(listener);
        squatter.Start();
        return squatter;
    }

    private sealed class PlainHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class Squatter(TcpListener listener)
    {
        private const string Body = "{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32700,\"message\":\"x\"}}";

        private static readonly byte[] Response = Encoding.UTF8.GetBytes(
            $"HTTP/1.1 400 Bad Request\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(Body)}\r\nConnection: close\r\n\r\n{Body}");

        private readonly List<string> _requests = [];
        private readonly CancellationTokenSource _cts = new();

        public int Port { get; } = ((IPEndPoint)listener.LocalEndpoint).Port;

        public IReadOnlyList<string> Requests
        {
            get
            {
                lock (_requests)
                {
                    return [.. _requests];
                }
            }
        }

        public IReadOnlyList<string> TokenHeaderValues =>
        [
            .. Requests
                .SelectMany(request => request.Split("\r\n"))
                .Where(line => line.StartsWith($"{McpTokenGate.HeaderName}:", StringComparison.OrdinalIgnoreCase))
                .Select(line => line[(line.IndexOf(':') + 1)..].Trim())
        ];

        public void Start() => _ = AcceptAsync();

        private async Task AcceptAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException or SocketException)
                {
                    return;
                }

                _ = ServeAsync(client);
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            try
            {
                using (client)
                {
                    await using var stream = client.GetStream();
                    var request = await ReadHeadersAsync(stream);
                    lock (_requests)
                    {
                        _requests.Add(request);
                    }

                    await stream.WriteAsync(Response);
                    await stream.FlushAsync();
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
            {
                // The client went away; the recorded request (if any) is what this test measures.
            }
        }

        private static async Task<string> ReadHeadersAsync(NetworkStream stream)
        {
            var buffer = new byte[8192];
            var text = new StringBuilder();
            while (!text.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0)
                {
                    break;
                }

                text.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }

            return text.ToString();
        }
    }
}