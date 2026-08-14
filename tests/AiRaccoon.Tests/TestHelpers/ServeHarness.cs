using AiRaccoon.Hosting.Common;
using AiRaccoon.Setup.Cli;
using CommunityToolkit.Diagnostics;
using Shouldly;

namespace AiRaccoon.Tests.TestHelpers;

/// <summary>
///     One in-process `serve` run: captured streams, a wait for a line it prints, and a bounded
///     stop. Disposal cancels the run and awaits it, so no server outlives the test that started it.
/// </summary>
public sealed class ServeHarness : IAsyncDisposable
{
    /// <summary>Cancels the run if a test neither stops nor disposes it.</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(90);

    private readonly CancellationTokenSource _cts;
    private readonly TimeSpan _deadline;
    private readonly LockingWriter _stderr;
    private readonly LockingWriter _stdout;
    private bool _disposed;

    private ServeHarness(Task<int> exit, LockingWriter stdout, LockingWriter stderr, CancellationTokenSource cts,
        TimeSpan deadline)
    {
        Exit = exit;
        _stdout = stdout;
        _stderr = stderr;
        _cts = cts;
        _deadline = deadline;
    }

    public Task<int> Exit { get; }

    public string Stdout => _stdout.ToString();

    public string Stderr => _stderr.ToString();

    public static ServeHarness Start(string[] args, TimeSpan? lifetime = null) =>
        StartAsync(args, lifetime).GetAwaiter().GetResult();

    /// <summary>The gate, when given, holds every runner back until it completes, so concurrent
    /// starts really do race for the port.</summary>
    public static async Task<ServeHarness> StartAsync(string[] args, TimeSpan? lifetime = null, Task? gate = null)
    {
        Guard.IsNotNull(args);
        CliArgs.TryParse(args, out var parsed);
        parsed!.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["serve"]);
        var stdout = new LockingWriter();
        var stderr = new LockingWriter();
        var cts = new CancellationTokenSource(lifetime ?? DefaultLifetime);

        if (gate is not null)
        {
            await gate;
        }

        var exit = TestData.CreateNodeRunner(parsed.ServerConfig.Options)
            .RunAsync(parsed, new StandardStreams(TextReader.Null, stdout, stderr), cts.Token);
        return new ServeHarness(exit, stdout, stderr, cts, WaitByPolling.DefaultDeadline);
    }

    /// <summary>Test seam: a harness over streams the caller drives, with no server behind it.</summary>
    internal static ServeHarness ForCapturedOutput(LockingWriter stdout, LockingWriter stderr, Task<int> exit,
        TimeSpan deadline) =>
        new(exit, stdout, stderr, new CancellationTokenSource(), deadline);

    public Task<string> WaitForUrlAsync(CancellationToken cancellationToken) =>
        WaitForLineAsync(line => line.StartsWith("http://", StringComparison.Ordinal), cancellationToken, "a URL line");

    /// <summary>Returns the first captured line the predicate accepts; throws naming the wait and
    /// dumping both streams when the run ends first or the deadline passes.</summary>
    public async Task<string> WaitForLineAsync(Func<string, bool> match, CancellationToken cancellationToken,
        string description = "a matching line")
    {
        Guard.IsNotNull(match);
        string? found = null;
        var settled = await WaitByPolling.WaitForAsync(
            () =>
            {
                found = FindLine(match);
                return ValueTask.FromResult(found is not null || Exit.IsCompleted);
            },
            WaitByPolling.DefaultFirstTick, WaitByPolling.DefaultMaxTick, _deadline, TimeProvider.System,
            cancellationToken);

        if (found is not null)
        {
            return found;
        }

        if (settled)
        {
            // The run ended: rescan, so a line printed as it exited is not lost to the race.
            found = FindLine(match);
            if (found is not null)
            {
                return found;
            }

            throw new InvalidOperationException(
                $"serve exited {await Exit} before printing {description}; stdout: {Stdout}; stderr: {Stderr}");
        }

        throw new TimeoutException(
            $"timed out after {_deadline} waiting for {description}; stdout: {Stdout}; stderr: {Stderr}");
    }

    public async Task<int> StopAsync()
    {
        await _cts.CancelAsync();
        return await Exit;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            await StopAsync();
        }
        catch (OperationCanceledException)
        {
            // The run was already cancelled; disposal has nothing left to report.
        }
        finally
        {
            _cts.Dispose();
        }
    }

    private string? FindLine(Func<string, bool> match)
    {
        foreach (var line in Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.TrimEnd('\r');
            if (match(trimmed))
            {
                return trimmed;
            }
        }

        return null;
    }
}
