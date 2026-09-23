using System.Buffers.Text;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Node;
using Shouldly;
using Xunit;
using xRetry.v3;
using AiRaccoon.Tests.TestHelpers;

namespace AiRaccoon.Tests.E2E;

/// <summary>
///     The proxy presenting a token a real gated `serve` rejects: the server minted one token at
///     start, the file was rotated afterwards, so the proof succeeds (same root, same identity key)
///     and the token gate is what refuses. Every other proxy test either uses an ungated backend or
///     presents the right token, so this is the only one where the gate refuses.
/// </summary>
[Trait(TestCategories.Category, TestCategories.E2E)]
[Trait(TestCategories.Speed, TestCategories.Nightly)]
[Collection(E2ETestCollection.Name)]
public sealed class ProxyTokenRefusedE2ETests : IAsyncLifetime
{
    /// <summary>Only stops a hang from wedging the run; the assertion is the exit code and stderr.</summary>
    private static readonly TimeSpan HardCap = TimeSpan.FromSeconds(150);

    /// <summary>How long the gated backend gets to die once killed, before teardown calls it stuck.</summary>
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(30);

    private readonly string _root = TestData.CreateTempRoot("token-refused");
    private Process? _backend;
    private int _port;

    public async ValueTask InitializeAsync()
    {
        using var lease = LoopbackPort.Reserve();
        _port = lease.Port;
        lease.ReleaseForBind();
        _backend = StartGatedServe();
        await WaitForBackendAsync();
        // The proxy and the backend share this root, so the proof passes; rotate the token the
        // file holds to a well-formed value the running server never saw, and the gate refuses it.
        var tokenFile = new McpTokenFile(_root);
        tokenFile.Read().ShouldNotBeNull("the gated backend must have minted a token before it bound");
        await File.WriteAllTextAsync(tokenFile.Path,
            Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32)), TestContext.Current.CancellationToken);
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
            Delete(_root);
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
                    $"the gated backend on port {_port} survived kill, so it still holds {_root}");
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
    ///     the server actually expects are gone — so a rotated token reads as a mute backend.
    /// </summary>
    [RetryFact]
    public async Task WrongToken_SurfacesTheGatesVerdict()
    {
        var run = await RunProxyAsync();

        // The server's own file: naming it is what makes the refusal diagnosable.
        run.Stderr.ShouldContain(new McpTokenFile(_root).Path);
        run.Stderr.ShouldContain(McpTokenGate.HeaderName);
        run.ExitCode.ShouldBe(ExitCode.ProxyBackendUnavailable);
        // "At once, not at the SDK's handshake timeout" is pinned by the exit code and the stderr
        // above, not by a clock (PR #464); the harness HardCap alone guards a hang.
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
                     "--data-root", _root, "--quiet", "serve", "--port",
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
        var tokenFile = new McpTokenFile(_root).Path;
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
        RaccoonProcess.RunAsync(["--data-root", _root, "--port", _port.ToString(CultureInfo.InvariantCulture)],
            HardCap, TestContext.Current.CancellationToken);

    private static void Delete(string root)
    {
        TestData.DeleteTempRoot(root);
    }
}
