using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Node;
using AiRaccoon.Observability;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Tests.TestHelpers;

/// <summary>What an impostor sends back for one identity challenge.</summary>
internal sealed record ImpostorReply(int Status, string Body);

/// <summary>One request as it arrived on the wire: the request line, the header block and the body bytes.</summary>
internal sealed record CapturedRequest(string Method, string Path, string Headers, byte[] Body)
{
    public string BodyText => Encoding.UTF8.GetString(Body);

    public bool CarriesTheTokenHeader =>
        Headers.Split("\r\n").Any(line => line.StartsWith($"{McpTokenGate.HeaderName}:", StringComparison.OrdinalIgnoreCase)
                                          || line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase));

    public override string ToString() => $"{Method} {Path} ({Body.Length} body bytes)";
}

/// <summary>
///     A hostile listener that claims to be ai-raccoon everywhere a claim is cheap: /observability
///     names ai-raccoon, POST /mcp answers the probe with jsonrpc, and POST /identity/prove is
///     answered by the attack under test. It records every request byte — headers and body — so a
///     gate can assert that no secret ever reached it. Raw TCP, one request per connection.
/// </summary>
internal sealed class Impostor : IDisposable
{
    private const int BodyBound = 64 * 1024;

    private static readonly string McpBody = "{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32700,\"message\":\"x\"}}";

    private readonly Func<string, Task<ImpostorReply>> _answerProof;
    private readonly CancellationTokenSource _cts = new();
    private readonly TcpListener _listener;
    private readonly List<CapturedRequest> _requests = [];

    /// <summary>Binds <paramref name="port"/> (0 = any free port) and answers challenges with <paramref name="answerProof"/>.</summary>
    public Impostor(int port, Func<string, Task<ImpostorReply>> answerProof)
    {
        Guard.IsNotNull(answerProof);
        _answerProof = answerProof;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptAsync();
    }

    public int Port { get; }

    public IReadOnlyList<CapturedRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>How many identity challenges reached this listener.</summary>
    public int Challenges => Requests.Count(request => request.Path == IdentityProof.EndpointPath);

    /// <summary>The secrets from <paramref name="secrets"/> that appear anywhere in any recorded request byte.</summary>
    public IReadOnlyList<string> SecretsSeen(IEnumerable<string> secrets)
    {
        var wire = string.Join("\n", Requests.Select(request => request.Headers + "\r\n\r\n" + request.BodyText));
        return [.. secrets.Where(secret => secret.Length > 0 && wire.Contains(secret, StringComparison.Ordinal))];
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _listener.Dispose();
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
                var request = await ReadRequestAsync(stream);
                if (request is null)
                {
                    return;
                }

                lock (_requests)
                {
                    _requests.Add(request);
                }

                var (status, body) = request.Path switch
                {
                    ObservabilityEndpoint.Path => (200,
                        $"{{\"name\":\"ai-raccoon\",\"version\":\"0.0.0-impostor\",\"pid\":{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)},\"otlp\":{{\"enabled\":false}}}}"),
                    "/mcp" => (400, McpBody),
                    IdentityProof.EndpointPath => await AnswerAsync(request.BodyText),
                    _ => (200, "{}")
                };
                await stream.WriteAsync(Response(status, body));
                await stream.FlushAsync();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            // The client went away; whatever was recorded is the measurement.
        }
    }

    private async Task<(int, string)> AnswerAsync(string challenge)
    {
        var reply = await _answerProof(challenge);
        return (reply.Status, reply.Body);
    }

    private static byte[] Response(int status, string body) =>
        Encoding.UTF8.GetBytes(
            $"HTTP/1.1 {status.ToString(CultureInfo.InvariantCulture)} X\r\nContent-Type: application/json\r\n" +
            $"Content-Length: {Encoding.UTF8.GetByteCount(body).ToString(CultureInfo.InvariantCulture)}\r\nConnection: close\r\n\r\n{body}");

    /// <summary>Reads the header block, then the body by Content-Length or chunked framing, bounded.</summary>
    private static async Task<CapturedRequest?> ReadRequestAsync(NetworkStream stream)
    {
        var buffer = new List<byte>();
        var chunk = new byte[8192];
        int headerEnd;
        while ((headerEnd = IndexOf(buffer, "\r\n\r\n"u8)) < 0)
        {
            var read = await stream.ReadAsync(chunk);
            if (read == 0)
            {
                return null;
            }

            buffer.AddRange(chunk.AsSpan(0, read));
        }

        var headers = Encoding.UTF8.GetString(buffer.GetRange(0, headerEnd).ToArray());
        var body = new List<byte>(buffer.Skip(headerEnd + 4));
        var lines = headers.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        var contentLength = lines
            .Where(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            .Select(line => int.Parse(line["Content-Length:".Length..].Trim(), CultureInfo.InvariantCulture))
            .FirstOrDefault(-1);
        var chunked = lines.Any(line => line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase)
                                        && line.Contains("chunked", StringComparison.OrdinalIgnoreCase));

        while (body.Count < BodyBound
               && (contentLength >= 0 ? body.Count < contentLength : chunked && IndexOf(body, "0\r\n\r\n"u8) < 0))
        {
            var read = await stream.ReadAsync(chunk);
            if (read == 0)
            {
                break;
            }

            body.AddRange(chunk.AsSpan(0, read));
        }

        var path = requestLine.Length > 1 ? requestLine[1] : "";
        var query = path.IndexOf('?');
        return new CapturedRequest(requestLine[0], query < 0 ? path : path[..query], headers, [.. body]);
    }

    private static int IndexOf(List<byte> haystack, ReadOnlySpan<byte> needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Count; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length && match; j++)
            {
                match = haystack[i + j] == needle[j];
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }
}
