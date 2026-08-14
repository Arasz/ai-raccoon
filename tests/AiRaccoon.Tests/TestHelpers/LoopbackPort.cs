using System.Net;
using System.Net.Sockets;

namespace AiRaccoon.Tests.TestHelpers;

/// <summary>
///     A loopback port that stays reserved until disposed. The classic "bind :0, read the number,
///     close" helper leaves the port unowned, so a concurrent test can take it before the server
///     under test binds; holding the listener closes that window.
/// </summary>
public sealed class LoopbackPort : IDisposable
{
    private readonly TcpListener _listener;
    private bool _released;

    private LoopbackPort(TcpListener listener, int port)
    {
        _listener = listener;
        Port = port;
    }

    public int Port { get; }

    /// <summary>Reserves a free loopback port and keeps holding it.</summary>
    public static LoopbackPort Reserve()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new LoopbackPort(listener, ((IPEndPoint)listener.LocalEndpoint).Port);
    }

    /// <summary>Reserves a free loopback port and answers connections, standing in for a foreign server.</summary>
    public static LoopbackPort Occupy()
    {
        var lease = Reserve();
        lease.AcceptUntilReleased();
        return lease;
    }

    /// <summary>Occupies one specific port, or returns null when something else already holds it.</summary>
    public static LoopbackPort? TryOccupy(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            listener.Start();
        }
        catch (SocketException)
        {
            listener.Dispose();
            return null;
        }

        var lease = new LoopbackPort(listener, port);
        lease.AcceptUntilReleased();
        return lease;
    }

    /// <summary>Releases the socket immediately before a server binds it. Idempotent.</summary>
    public void ReleaseForBind()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        _listener.Stop();
        _listener.Dispose();
    }

    public void Dispose() => ReleaseForBind();

    /// <summary>Accepts and closes each connection so a probe fails fast instead of parking in the backlog.</summary>
    private void AcceptUntilReleased() => _ = Task.Run(async () =>
    {
        while (true)
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync();
            }
            catch (Exception ex) when (ex is ObjectDisposedException or SocketException or InvalidOperationException)
            {
                return;
            }
        }
    });
}
