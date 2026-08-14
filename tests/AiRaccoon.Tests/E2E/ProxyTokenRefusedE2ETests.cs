using System.Diagnostics;
using System.Globalization;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Node;
using Shouldly;
using Xunit;
using AiRaccoon.Tests.TestHelpers;

namespace AiRaccoon.Tests.E2E;

/// <summary>
///     The proxy presenting a token a real gated `serve` rejects — the data-root mismatch row of
///     docs/plans/2026-08-09-mcp-loopback-token-flow.md. Every other proxy test either uses an
///     ungated backend or presents the right token, so this is the only one where the gate refuses.
/// </summary>
[Trait(TestCategories.Category, TestCategories.E2E)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
[Collection(E2ETestCollection.Name)]
public sealed class ProxyTokenRefusedE2ETests : IAsyncLifetime
{
    /// <summary>Comfortably above the real cost (probe plus one refused handshake) and comfortably
    /// below the SDK's 60 s initialize timeout, which is what a dropped refusal costs instead.</summary>
    private static readonly TimeSpan Promptly = TimeSpan.FromSeconds(20);

    /// <summary>Only stops a hang from wedging the run; the assertion is the stopwatch.</summary>
    private static readonly TimeSpan HardCap = TimeSpan.FromSeconds(150);

    /// <summary>How long the gated backend gets to die once killed, before teardown calls it stuck.</summary>
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(30);

    private readonly string _backendRoot = TestData.CreateTempRoot("token-refused-backend");
    private readonly string _proxyRoot = TestData.CreateTempRoot("token-refused-proxy");
    private Process? _backend;
    private int _port;

    public async ValueTask InitializeAsync()
    {
        using var lease = LoopbackPort.Reserve();
        _port = lease.Port;
        lease.ReleaseForBind();
        _backend = StartGatedServe();
        await WaitForBackendAsync();
        // A well-formed token that is deliberately not the backend's: the proxy reads this root,
        // the gate compares the other. Minted rather than written, so the refusal is the gate's
        // verdict on a real token and not the reader rejecting the file's shape.
        await new McpTokenFile(_proxyRoot).EnsureAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>A gated `serve` that outlives the test holds the port and the bank, so say so loudly.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopBackendAsync();
        }
        finally
        {
            Delete(_backendRoot);
            Delete(_proxyRoot);
        }
    }

    private async Task StopBackendAsync()
    {
        if (_backend is null)
        {
            return;
        }

        try
        {
            var stopped = await RaccoonProcess.KillTreeAndWaitAsync(_backend, ExitWait, CancellationToken.None);
            if (!stopped)
            {
                throw new InvalidOperationException(
                    $"the gated backend on port {_port} survived kill, so it still holds {_backendRoot}");
            }
        }
        finally
        {
            _backend.Dispose();
        }
    }

    /// <summary>
    ///     The gate's verdict has to reach the operator intact. Rewriting the 401 to a 200 costs
    ///     exactly that: the body correlates with no request, the SDK reports only that the POST
    ///     completed without a reply, and both the header it wanted and the file holding the token
    ///     the server actually expects are gone — so a data-root mismatch reads as a mute backend.
    /// </summary>
    [Fact]
    public async Task WrongToken_SurfacesTheGatesVerdict()
    {
        var started = Stopwatch.StartNew();
        var run = await RunProxyAsync();
        started.Stop();

        // The server's own file, not the proxy's: naming it is what makes the mismatch diagnosable.
        run.Stderr.ShouldContain(Path.Combine(_backendRoot, McpTokenFile.FileName));
        run.Stderr.ShouldContain(McpTokenGate.HeaderName);
        // And the proxy's own, which is the file the operator has to change.
        run.Stderr.ShouldContain(Path.Combine(_proxyRoot, McpTokenFile.FileName));
        run.ExitCode.ShouldBe(ExitCode.ProxyBackendUnavailable);
        // A refusal is knowable at once; it must never be paid for at the SDK's handshake timeout.
        started.Elapsed.ShouldBeLessThan(Promptly);
    }

    private Process StartGatedServe()
    {
        var startInfo = new ProcessStartInfo(AiRaccoonProcess.Executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in new[]
                 {
                     "--data-root", _backendRoot, "--quiet", "serve", "--port",
                     _port.ToString(CultureInfo.InvariantCulture)
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = Process.Start(startInfo)!;
        // Drain both pipes: a full 64 KB buffer would block the daemon mid-write for the whole test.
        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        return process;
    }

    /// <summary>Waits on the token file, which `serve` mints strictly before it binds.</summary>
    private async Task WaitForBackendAsync()
    {
        var tokenFile = Path.Combine(_backendRoot, McpTokenFile.FileName);
        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        for (var attempt = 0; attempt < 120; attempt++)
        {
            try
            {
                using var response = await probe.GetAsync(new Uri($"http://127.0.0.1:{_port}/observability"),
                    TestContext.Current.CancellationToken);
                if (response.IsSuccessStatusCode && File.Exists(tokenFile))
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                // Not listening yet.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);
        }

        throw new InvalidOperationException($"the gated backend never came up on port {_port}");
    }

    private Task<ProcessRun> RunProxyAsync() =>
        RaccoonProcess.RunAsync(["--data-root", _proxyRoot, "--port", _port.ToString(CultureInfo.InvariantCulture)],
            HardCap, TestContext.Current.CancellationToken);

    private static void Delete(string root)
    {
        TestData.DeleteTempRoot(root);
    }
}
