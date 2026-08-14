using System.Net;
using System.Net.Sockets;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Harness;

/// <summary>
///     Pins the retry that survives a port stolen between release and bind. The window cannot be
///     closed — another process may take the number the instant the lease lets go — so the contract
///     is that a stolen port costs a retry, not a failed test.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class LoopbackPortRetryTests
{
    [Fact]
    public void BindWithRetry_WhenTheFirstPortIsStolen_SucceedsOnAFreshOne()
    {
        var attempts = new List<int>();

        var bound = LoopbackPort.BindWithRetry(port =>
        {
            attempts.Add(port);
            if (attempts.Count == 1)
            {
                throw new SocketException((int)SocketError.AddressAlreadyInUse);
            }

            return port;
        });

        attempts.Count.ShouldBe(2, "the first attempt lost its port and must be retried");
        attempts[0].ShouldNotBe(attempts[1], "a retry on the same number would lose the race again");
        bound.ShouldBe(attempts[1]);
    }

    [Fact]
    public void BindWithRetry_WhenTheBindSucceeds_DoesNotRetry()
    {
        var attempts = 0;

        LoopbackPort.BindWithRetry(port =>
        {
            attempts++;
            return port;
        });

        attempts.ShouldBe(1);
    }

    [Fact]
    public void BindWithRetry_WhenEveryAttemptLosesThePort_ThrowsNamingTheContention()
    {
        // Giving up silently would turn a lost race into an unexplained failure much later.
        var thrown = Should.Throw<InvalidOperationException>(() => LoopbackPort.BindWithRetry<int>(
            _ => throw new SocketException((int)SocketError.AddressAlreadyInUse), attempts: 2));

        thrown.Message.ShouldContain("2");
    }

    [Fact]
    public void BindWithRetry_DoesNotSwallowAnUnrelatedFailure()
    {
        // Only a lost port is worth retrying; anything else is the test's own bug.
        Should.Throw<InvalidOperationException>(() => LoopbackPort.BindWithRetry<int>(
            _ => throw new InvalidOperationException("the server is misconfigured")))
            .Message.ShouldBe("the server is misconfigured");
    }

    [Fact]
    public void BindWithRetry_ReleasesTheLeaseBeforeTheBindRuns()
    {
        // The caller binds the port itself, so the lease must be gone by the time it tries.
        var bound = LoopbackPort.BindWithRetry(port =>
        {
            var server = new TcpListener(IPAddress.Loopback, port);
            server.Start();
            server.Stop();
            server.Dispose();
            return port;
        });

        bound.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task BindWithRetryAsync_WhenTheFirstPortIsStolen_RebuildsOnAFreshOne()
    {
        var attempts = new List<int>();

        var bound = await LoopbackPort.BindWithRetryAsync(port =>
        {
            attempts.Add(port);
            return attempts.Count == 1
                ? throw new SocketException((int)SocketError.AddressAlreadyInUse)
                : Task.FromResult(port);
        });

        attempts.Count.ShouldBe(2);
        attempts[0].ShouldNotBe(attempts[1]);
        bound.ShouldBe(attempts[1]);
    }

    [Fact]
    public async Task BindWithRetryAsync_UnwrapsTheBindFailureKestrelWraps()
    {
        // Kestrel surfaces the lost port as an IOException wrapping the SocketException.
        var attempts = 0;

        await LoopbackPort.BindWithRetryAsync(port =>
        {
            attempts++;
            return attempts == 1
                ? throw new IOException($"Failed to bind to address http://127.0.0.1:{port}",
                    new SocketException((int)SocketError.AddressAlreadyInUse))
                : Task.FromResult(port);
        });

        attempts.ShouldBe(2, "a wrapped AddressAlreadyInUse is still a lost port");
    }
}
