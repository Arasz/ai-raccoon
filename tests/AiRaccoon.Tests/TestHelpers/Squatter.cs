using System.Net;
using System.Net.Sockets;
using System.Text;
using AiRaccoon.Hosting.Node;

namespace AiRaccoon.Tests.TestHelpers;

/// <summary>
///     An F70 squatter: binds a loopback port and answers /mcp with a body containing "jsonrpc"
///     — the exact shape ServerProbe accepts as an ai-raccoon server — and every other path with
///     an empty JSON object, so a settings round-trip against it completes rather than throws.
///     It records every request line and header block it receives, so a test can assert that the
///     data root's token never reached it.
/// </summary>
internal sealed class Squatter : IDisposable
{
    private static readonly byte[] McpResponse = Response("400 Bad Request", "application/json",
        "{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32700,\"message\":\"x\"}}");

    private static readonly byte[] OtherResponse = Response("200 OK", "application/json", "{}");

    private readonly CancellationTokenSource _cts = new();
    private readonly TcpListener _listener;
    private readonly List<string> _requests = [];

    public Squatter() : this(0)
    {
    }

    /// <summary>Binds <paramref name="port"/>; 0 keeps the original any-free-port shape.</summary>
    public Squatter(int port)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptAsync();
    }

    public int Port { get; }

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

    private static byte[] Response(string status, string contentType, string body) =>
        Encoding.UTF8.GetBytes(
            $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}");

    private async Task AcceptAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
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

                var isMcp = request.StartsWith("POST /mcp", StringComparison.OrdinalIgnoreCase);
                await stream.WriteAsync(isMcp ? McpResponse : OtherResponse);
                await stream.FlushAsync();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            // The client went away; the recorded request (if any) is what this helper measures.
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
