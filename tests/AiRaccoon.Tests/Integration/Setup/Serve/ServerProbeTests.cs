using System.Net;
using System.Net.Sockets;
using System.Text;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Tests.E2E;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Setup.Serve;

/// <summary>
///     ServerProbe acceptance: a foreign listener on the port is not an ai-raccoon server, and a
///     real ai-raccoon MCP endpoint is recognized (ADR-0020, R14 probe).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
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

    [RetryFact]
    public async Task Probe_RejectsAForeignListener()
    {
        var port = HoldForeignListener();

        var responds = await TestData.CreateServerProbe().RespondsAsync(port, TestContext.Current.CancellationToken);

        responds.ShouldBeFalse();
    }

    [RetryFact]
    public async Task Probe_RecognizesAnAiRaccoonServer()
    {
        using var factory = new McpServerFactory();
        using var httpClient = factory.CreateClient();

        var responds = await new ServerProbe(httpClient)
            .RespondsAsync(new Uri("http://localhost/mcp"), TestContext.Current.CancellationToken);

        responds.ShouldBeTrue();
    }

    [RetryFact]
    public async Task Probe_WhenTheCallerCancels_ThrowsInsteadOfReportingNoServer()
    {
        var port = HoldSilentListener();
        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            TestData.CreateServerProbe().RespondsAsync(port, caller.Token));
    }

    [RetryFact]
    public async Task Probe_WhenItsOwnTimeoutExpires_ReportsNoServer()
    {
        var port = HoldSilentListener();

        var responds = await TestData.CreateServerProbe().RespondsAsync(port, TestContext.Current.CancellationToken);

        responds.ShouldBeFalse();
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

    /// <summary>A listener that accepts and never answers, so every probe attempt runs into its own timeout.</summary>
    private int HoldSilentListener()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _listeners.Add(listener);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        _ = Task.Run(async () =>
        {
            var accepted = new List<TcpClient>();
            try
            {
                while (true)
                {
                    accepted.Add(await listener.AcceptTcpClientAsync());
                }
            }
            catch (Exception ex) when (ex is ObjectDisposedException or SocketException or IOException)
            {
                foreach (var client in accepted)
                {
                    client.Dispose();
                }
            }
        });

        return port;
    }
}
