using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Node;

namespace AiRaccoon.Tests.TestHelpers;

/// <summary>
///     A backend that proves this root's identity over keep-alive HTTP/1.1, then loses its port at
///     the moment a test picks: once armed with <see cref="HandOverOn" />, the next request whose
///     line starts with the armed prefix makes it stop listening and a <see cref="Racer" /> bind
///     the port before the answer is written — so the caller reads a good answer while someone
///     else already owns the port. Connections it already accepted keep being served, like a
///     draining server, unless the handover also closes the one it happened on. POST /mcp is
///     refused and closed, so no client keeps a pooled connection from it.
/// </summary>
internal sealed class DyingBackend : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly TcpListener _listener;
    private readonly string _rootFp;
    private readonly List<string> _requests = [];
    private readonly ECDsa _signer;
    private (string Prefix, bool CloseConnection)? _handover;

    public DyingBackend(ECDsa signer, string rootFp)
    {
        _signer = signer;
        _rootFp = rootFp;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptAsync();
    }

    public int Port { get; }

    public string Url => $"http://127.0.0.1:{Port}/mcp";

    /// <summary>The listener that took the port at the handover; null until then.</summary>
    public Racer? Racer { get; private set; }

    /// <summary>Every request head this backend received, in order.</summary>
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

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        Racer?.Dispose();
        _cts.Dispose();
    }

    /// <summary>Arms the handover for the next request whose line starts with <paramref name="requestLinePrefix" />.</summary>
    public void HandOverOn(string requestLinePrefix, bool closeConnection) => _handover = (requestLinePrefix, closeConnection);

    private async Task AcceptAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException or SocketException or InvalidOperationException)
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
                while (await HttpWire.ReadRequestAsync(stream, _cts.Token) is { } request)
                {
                    lock (_requests)
                    {
                        _requests.Add(request.Head);
                    }

                    var (status, body, close) = Answer(request);
                    if (_handover is { } handover && request.Line.StartsWith(handover.Prefix, StringComparison.Ordinal))
                    {
                        _handover = null;
                        _listener.Stop();
                        Racer = new Racer(Port);
                        close |= handover.CloseConnection;
                    }

                    await stream.WriteAsync(HttpWire.Response(status, body, close), _cts.Token);
                    await stream.FlushAsync(_cts.Token);
                    if (close)
                    {
                        return;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException or OperationCanceledException)
        {
            // The client went away; the recorded requests are what this helper measures.
        }
    }

    private (string Status, string Body, bool Close) Answer(HttpWire.Request request)
    {
        if (request.Line.StartsWith($"POST {IdentityProof.EndpointPath}", StringComparison.Ordinal))
        {
            using var document = JsonDocument.Parse(request.Body);
            var nonce = document.RootElement.GetProperty("nonce").GetString()!;
            var keyId = IdentityProof.KeyId(_signer);
            var signature = IdentityProof.Sign(_signer, nonce, keyId, _rootFp, Port);
            return ("200 OK", JsonSerializer.Serialize(new { v = 1, keyId, signature }), false);
        }

        if (request.Line.StartsWith("GET /observability", StringComparison.Ordinal))
        {
            return ("200 OK", JsonSerializer.Serialize(new { name = "ai-raccoon", version = "0.0.0-dying", pid = Environment.ProcessId }), false);
        }

        if (request.Line.StartsWith($"POST {ShutdownEndpoint.Path}", StringComparison.Ordinal))
        {
            return ("202 Accepted", string.Empty, false);
        }

        return ("401 Unauthorized", """{"jsonrpc":"2.0","id":null,"error":{"code":-32001,"message":"nope"}}""", true);
    }
}

/// <summary>
///     Whoever takes a backend's port after it let go: records every request head and answers with
///     an empty JSON object, then leaves at the first POST /mcp — the port poll's probe — so a stop
///     path that polls for the port to free settles at once instead of at its bound.
/// </summary>
internal sealed class Racer : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly TcpListener _listener;
    private readonly List<string> _requests = [];

    public Racer(int port)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        _ = AcceptAsync();
    }

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

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _cts.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException or SocketException or InvalidOperationException)
            {
                return;
            }

            using (client)
            {
                try
                {
                    await using var stream = client.GetStream();
                    if (await HttpWire.ReadRequestAsync(stream, _cts.Token) is not { } request)
                    {
                        continue;
                    }

                    lock (_requests)
                    {
                        _requests.Add(request.Head);
                    }

                    if (request.Line.StartsWith("POST /mcp", StringComparison.Ordinal))
                    {
                        _listener.Stop();
                    }

                    await stream.WriteAsync(HttpWire.Response("200 OK", "{}", close: true), _cts.Token);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException or OperationCanceledException)
                {
                    // The client went away; the recorded request is what this helper measures.
                }
            }
        }
    }
}

/// <summary>Just enough HTTP/1.1 to read one request (head plus Content-Length body) and write one answer.</summary>
internal static class HttpWire
{
    internal static async Task<Request?> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new List<byte>();
        var one = new byte[1];
        while (!EndsWithBlankLine(buffer))
        {
            if (await stream.ReadAsync(one, cancellationToken) == 0)
            {
                return null;
            }

            buffer.Add(one[0]);
        }

        var head = Encoding.ASCII.GetString([.. buffer]);
        var length = head.Split("\r\n")
            .Where(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            .Select(line => int.Parse(line["Content-Length:".Length..].Trim(), CultureInfo.InvariantCulture))
            .FirstOrDefault();
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, cancellationToken);
        return new Request(head, body);
    }

    internal static byte[] Response(string status, string body, bool close) =>
        Encoding.UTF8.GetBytes(
            $"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: {(close ? "close" : "keep-alive")}\r\n\r\n{body}");

    private static bool EndsWithBlankLine(List<byte> buffer) =>
        buffer.Count >= 4 && buffer[^4] == '\r' && buffer[^3] == '\n' && buffer[^2] == '\r' && buffer[^1] == '\n';

    internal sealed record Request(string Head, byte[] Body)
    {
        public string Line => Head[..Head.IndexOf("\r\n", StringComparison.Ordinal)];
    }
}
