using System.Net;
using System.Net.Sockets;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Harness;

/// <summary>
///     Pins the occupied-port leases that stand in for a foreign server: the number stays taken for
///     the whole test, and connections are accepted then closed so a probe fails fast.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class LoopbackPortOccupyTests
{
    [Fact]
    public void AnOccupiedPort_CannotBeBoundByTheServerUnderTest()
    {
        using var lease = LoopbackPort.Occupy();

        var rival = new TcpListener(IPAddress.Loopback, lease.Port);

        Should.Throw<SocketException>(() => rival.Start())
            .SocketErrorCode.ShouldBe(SocketError.AddressAlreadyInUse);
    }

    [Fact]
    public async Task AnOccupiedPort_AcceptsThenClosesEachConnection()
    {
        using var lease = LoopbackPort.Occupy();

        // A bare reservation would leave this connection parked in the backlog forever;
        // the accept loop closes it, so a probe learns "not my server" instead of timing out.
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, lease.Port, TestContext.Current.CancellationToken);

        var buffer = new byte[1];
        var read = await client.GetStream()
            .ReadAsync(buffer, TestContext.Current.CancellationToken)
            .AsTask()
            .WaitAsync(TestContext.Current.CancellationToken);

        read.ShouldBe(0);
    }

    [Fact]
    public void TryOccupy_WhenThePortIsFree_TakesIt()
    {
        var free = LoopbackPort.Reserve();
        var number = free.Port;
        free.ReleaseForBind();

        using var lease = LoopbackPort.TryOccupy(number);

        lease.ShouldNotBeNull();
        lease.Port.ShouldBe(number);
    }

    [Fact]
    public void TryOccupy_WhenThePortIsAlreadyTaken_ReturnsNull()
    {
        using var holder = LoopbackPort.Occupy();

        LoopbackPort.TryOccupy(holder.Port).ShouldBeNull();
    }

    [Fact]
    public void DisposingAnOccupiedPort_FreesTheNumber()
    {
        var lease = LoopbackPort.Occupy();
        var number = lease.Port;

        lease.Dispose();

        var server = new TcpListener(IPAddress.Loopback, number);
        Should.NotThrow(() => server.Start());
        server.Stop();
        server.Dispose();
    }
}
