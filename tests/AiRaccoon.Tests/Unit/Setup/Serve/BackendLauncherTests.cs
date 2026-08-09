using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Setup.Serve;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup.Serve;

/// <summary>
///     BackendLauncher acceptance (ADR-0020): the proxy's stdout stays clean, a missing backend is
///     started and polled until it answers, an existing one is attached to without a spawn, and a
///     backend that cannot start fails inside the budget instead of hanging.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class BackendLauncherTests : IDisposable
{
    private const int StdoutFd = 1;

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-backend-launcher");
    private readonly List<TcpListener> _listeners = [];

    public void Dispose()
    {
        foreach (var listener in _listeners)
        {
            listener.Stop();
        }

        Directory.Delete(_dataRoot, true);
    }

    [Fact]
    public async Task Acquire_DoesNotWriteToItsOwnStdout()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "fd-level stdout capture is POSIX-only");
        using var env = await AcquireCleanEnvAsync(TestContext.Current.CancellationToken);
        var port = FreePort();
        var capturePath = Path.Combine(_dataRoot, "stdout-capture.txt");
        var managedStdout = new StringWriter();
        var originalStdout = Console.Out;
        BackendResult result;

        await using (var capture = new FileStream(capturePath, FileMode.Create, FileAccess.Write))
        {
            Console.SetOut(managedStdout);
            var savedStdout = Dup(StdoutFd);
            Dup2(capture.SafeFileHandle.DangerousGetHandle().ToInt32(), StdoutFd);
            try
            {
                result = await Launcher().AcquireAsync(port, ServeExecutable, ServeArguments(port),
                    TestContext.Current.CancellationToken);
                // The backend prints its bound URL right after binding; hold the capture open long
                // enough that an unredirected child would certainly have written it.
                await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            }
            finally
            {
                Dup2(savedStdout, StdoutFd);
                CloseFd(savedStdout);
                Console.SetOut(originalStdout);
            }
        }

        result.Url.ShouldBe(UrlFor(port));
        var captured = await File.ReadAllTextAsync(capturePath, TestContext.Current.CancellationToken);
        captured.ShouldNotContain(UrlFor(port));
        managedStdout.ToString().ShouldBeEmpty();
    }

    [Fact]
    public async Task Acquire_WhenNoServerIsListening_StartsOneAndAnswers()
    {
        using var env = await AcquireCleanEnvAsync(TestContext.Current.CancellationToken);
        var port = FreePort();

        var result = await Launcher().AcquireAsync(port, ServeExecutable, ServeArguments(port),
            TestContext.Current.CancellationToken);

        result.Url.ShouldBe(UrlFor(port));
        result.ServeExitCode.ShouldBeNull();
        var live = await ServerProbe.ForLoopback().RespondsAsync(port, TestContext.Current.CancellationToken);
        live.ShouldBeTrue();
    }

    [Fact]
    public async Task Acquire_WhenAServerIsAlreadyListening_DoesNotSpawn()
    {
        var port = HoldListener(FakeServerResponse);

        // An unstartable command: any spawn attempt throws instead of returning a URL.
        var result = await Launcher().AcquireAsync(port, "ai-raccoon-no-such-executable", [],
            TestContext.Current.CancellationToken);

        result.Url.ShouldBe(UrlFor(port));
        result.ServeExitCode.ShouldBeNull();
    }

    [Fact]
    public async Task Acquire_WhenTheBackendCannotStart_FailsWithinTheBudget()
    {
        using var env = await AcquireCleanEnvAsync(TestContext.Current.CancellationToken);
        var port = HoldListener(ForeignServerResponse);
        var stopwatch = Stopwatch.StartNew();

        var result = await Launcher().AcquireAsync(port, ServeExecutable, ServeArguments(port),
            TestContext.Current.CancellationToken);
        stopwatch.Stop();

        result.Url.ShouldBeNull();
        result.ServeExitCode.ShouldBe(ExitCode.PortInUse);
        stopwatch.Elapsed.ShouldBeLessThan(BackendLauncher.DefaultBudget);
    }

    [Fact]
    public async Task Acquire_WhenTheBackendNeverAnswers_GivesUpAtTheBudget()
    {
        var port = FreePort();
        var budget = TimeSpan.FromSeconds(2);
        var launcher = new BackendLauncher(ServerProbe.ForLoopback(), NullLogger.Instance, budget);
        var stopwatch = Stopwatch.StartNew();

        // A process that starts, never listens and outlives the budget.
        var result = await launcher.AcquireAsync(port, "sleep", ["20"], TestContext.Current.CancellationToken);
        stopwatch.Stop();

        result.Url.ShouldBeNull();
        result.ServeExitCode.ShouldBeNull();
        stopwatch.Elapsed.ShouldBeGreaterThanOrEqualTo(budget);
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(15));
    }

    private static BackendLauncher Launcher() => new(ServerProbe.ForLoopback(), NullLogger.Instance);

    private static string UrlFor(int port) => $"http://127.0.0.1:{port}/mcp";

    private static string ServeExecutable =>
        Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "AiRaccoon.exe" : "AiRaccoon");

    /// <summary>A short idle timeout so a spawned backend retires on its own — nothing here ever kills it.</summary>
    private string[] ServeArguments(int port) =>
    [
        "--data-root", _dataRoot, "serve", "--port", port.ToString(CultureInfo.InvariantCulture),
        "--idle-timeout", "20s"
    ];

    /// <summary>An ai-raccoon MCP endpoint's answer to the probe: 400 with a JSON-RPC error body.</summary>
    private static readonly string FakeServerResponse = Response("400 Bad Request", "application/json",
        "{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32700,\"message\":\"parse\"}}");

    /// <summary>Anything but an MCP endpoint.</summary>
    private static readonly string ForeignServerResponse = Response("200 OK", "text/plain", "ok");

    private static string Response(string status, string contentType, string body) =>
        $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";

    /// <summary>Holds a loopback port for the whole test, answering every connection with one canned response.</summary>
    private int HoldListener(string response)
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
                    var request = new byte[4096];
                    if (await stream.ReadAsync(request) > 0)
                    {
                        await stream.WriteAsync(Encoding.UTF8.GetBytes(response));
                        await stream.FlushAsync();
                    }
                }
                catch (Exception ex) when (ex is ObjectDisposedException or SocketException or IOException)
                {
                    return;
                }
            }
        });

        return port;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<IDisposable> AcquireCleanEnvAsync(CancellationToken cancellationToken)
    {
        // The spawned backend inherits this process's environment: clear the process-global
        // passphrase so a dev machine's value cannot poison a fresh-bank run.
        await TestData.EnvVarGate.WaitAsync(cancellationToken);
        var original = Environment.GetEnvironmentVariable(EnvEncryptionKeyProvider.EnvVarName);
        Environment.SetEnvironmentVariable(EnvEncryptionKeyProvider.EnvVarName, null);
        return new EnvRestore(original);
    }

    // DllImport rather than LibraryImport: the generated marshalling stub needs AllowUnsafeBlocks,
    // and three blittable int calls do not justify unsafe code across the whole test project.
#pragma warning disable SYSLIB1054
    [DllImport("libc", EntryPoint = "dup", SetLastError = true)]
    private static extern int Dup(int fd);

    [DllImport("libc", EntryPoint = "dup2", SetLastError = true)]
    private static extern int Dup2(int oldFd, int newFd);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int CloseFd(int fd);
#pragma warning restore SYSLIB1054

    private sealed class EnvRestore(string? original) : IDisposable
    {
        public void Dispose()
        {
            Environment.SetEnvironmentVariable(EnvEncryptionKeyProvider.EnvVarName, original);
            TestData.EnvVarGate.Release();
        }
    }
}
