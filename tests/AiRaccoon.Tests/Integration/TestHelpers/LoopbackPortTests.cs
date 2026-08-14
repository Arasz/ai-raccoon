using System.Net;
using System.Net.Sockets;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.TestHelpers;

/// <summary>
///     Pins the reservation the old FreePort() shape could not make: while a lease is held the
///     number cannot be taken by anyone else, and it becomes bindable once released.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class LoopbackPortTests
{
    [Fact]
    public void AHeldPort_CannotBeTakenByAnotherListener()
    {
        // The defect this replaces: FreePort() Stop()s before returning, so this rebind SUCCEEDS.
        using var lease = LoopbackPort.Reserve();

        var rival = new TcpListener(IPAddress.Loopback, lease.Port);

        Should.Throw<SocketException>(() => rival.Start())
            .SocketErrorCode.ShouldBe(SocketError.AddressAlreadyInUse);
    }

    [Fact]
    public void AReleasedPort_IsBindableByTheServerUnderTest()
    {
        var lease = LoopbackPort.Reserve();
        var port = lease.Port;

        lease.ReleaseForBind();

        var server = new TcpListener(IPAddress.Loopback, port);
        Should.NotThrow(() => server.Start());
        server.Stop();
        server.Dispose();
    }

    [Fact]
    public void ReleasingTwice_IsHarmless()
    {
        var lease = LoopbackPort.Reserve();

        lease.ReleaseForBind();

        Should.NotThrow(() => lease.Dispose());
    }

    [Fact]
    public void ConcurrentReservations_NeverHandOutTheSameNumber()
    {
        var leases = Enumerable.Range(0, 32).Select(_ => LoopbackPort.Reserve()).ToList();
        try
        {
            leases.Select(lease => lease.Port).Distinct().Count().ShouldBe(leases.Count);
        }
        finally
        {
            leases.ForEach(lease => lease.Dispose());
        }
    }
}
