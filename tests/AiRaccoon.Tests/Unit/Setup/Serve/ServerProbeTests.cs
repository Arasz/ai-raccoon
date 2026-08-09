using System.Net;
using System.Net.Sockets;
using System.Text;
using AiRaccoon.Setup.Serve;
using AiRaccoon.Tests.E2E;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup.Serve;

/// <summary>
///     ServerProbe acceptance: a foreign listener on the port is not an ai-raccoon server, and a
///     real ai-raccoon MCP endpoint is recognized (ADR-0020, R14 probe).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ServerProbeTests : IDisposable
{
    private readonly List<TcpListener> _listeners = [];

    public void Dispose()
    {
        foreach (var listener in _listeners)
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Probe_RejectsAForeignListener()
    {
        var port = HoldForeignListener();

        var responds = await ServerProbe.ForLoopback().RespondsAsync(port, TestContext.Current.CancellationToken);

        responds.ShouldBeFalse();
    }

    [Fact]
    public async Task Probe_RecognizesAnAiRaccoonServer()
    {
        using var factory = new McpServerFactory();
        using var httpClient = factory.CreateClient();

        var responds = await new ServerProbe(httpClient)
            .RespondsAsync(new Uri("http://localhost/mcp"), TestContext.Current.CancellationToken);

        responds.ShouldBeTrue();
    }

    /// <summary>A plain-HTTP listener that answers 200 OK with text — anything but an MCP endpoint.</summary>
    private int HoldForeignListener()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _listeners.Add(listener);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var client = await listener.AcceptTcpClientAsync();
                    await using var stream = client.GetStream();
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(
                        "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"));
                    await stream.FlushAsync();
                }
                catch (Exception ex) when (ex is ObjectDisposedException or SocketException or IOException)
                {
                    return;
                }
            }
        });

        return port;
    }
}
