using System.Collections.Concurrent;
using System.Net;

namespace AiRaccoon.Tests.TestHelpers;

/// <summary>A loopback HTTP listener standing in for the OTLP collector: records every request path
/// so a flush/export test can assert on what actually reached it.</summary>
public sealed class CapturingCollector : IDisposable
{
    private readonly Task _acceptLoop;
    private readonly CancellationTokenSource _cts = new();
    private readonly HttpListener _listener = new();

    public CapturingCollector()
    {
        using var lease = LoopbackPort.Reserve();
        Endpoint = $"http://127.0.0.1:{lease.Port}";
        _listener.Prefixes.Add($"{Endpoint}/");
        lease.ReleaseForBind();
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public string Endpoint { get; }

    public ConcurrentBag<string> RequestedPaths { get; } = [];

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _listener.Close();
        _cts.Dispose();
    }

    /// <summary>Waits for the request itself; the caller's token is the only hang guard (PR #464).</summary>
    public async Task WaitForRequestAsync(string path, CancellationToken cancellationToken)
    {
        while (!RequestedPaths.Any(p => p.Contains(path, StringComparison.Ordinal)))
        {
            await Task.Delay(50, cancellationToken);
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var context = await _listener.GetContextAsync().WaitAsync(_cts.Token);
                RequestedPaths.Add(context.Request.Url!.AbsolutePath);
                context.Response.StatusCode = 200;
                context.Response.Close();
            }
            catch (Exception) when (_cts.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
