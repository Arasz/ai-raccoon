using System.Net;
using System.Net.Sockets;
using CommunityToolkit.Diagnostics;

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

    /// <summary>
    ///     Reserves a port, releases it and runs <paramref name="bind" /> on it, retrying on a fresh
    ///     port when the number was taken in between. The window cannot be closed — another process
    ///     may take it the instant the lease lets go — so a lost race costs a retry, not a failure.
    /// </summary>
    public static T BindWithRetry<T>(Func<int, T> bind, int attempts = 4)
    {
        Guard.IsNotNull(bind);
        Guard.IsGreaterThan(attempts, 0);

        for (var attempt = 1; ; attempt++)
        {
            var lease = Reserve();
            var port = lease.Port;
            lease.ReleaseForBind();
            try
            {
                return bind(port);
            }
            catch (Exception ex) when (attempt < attempts && LostThePort(ex))
            {
                // Next pass draws a different number, so retrying cannot lose to the same thief.
            }
            catch (Exception ex) when (attempt >= attempts && LostThePort(ex))
            {
                throw new InvalidOperationException(
                    $"a loopback port was taken between reservation and bind on all {attempts} attempts", ex);
            }
        }
    }

    /// <summary>Async twin of <see cref="BindWithRetry{T}" />; the bind is rebuilt on the fresh port.</summary>
    public static async Task<T> BindWithRetryAsync<T>(Func<int, Task<T>> bind, int attempts = 4)
    {
        Guard.IsNotNull(bind);
        Guard.IsGreaterThan(attempts, 0);

        for (var attempt = 1; ; attempt++)
        {
            var lease = Reserve();
            var port = lease.Port;
            lease.ReleaseForBind();
            try
            {
                return await bind(port).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < attempts && LostThePort(ex))
            {
                // Next pass draws a different number, so retrying cannot lose to the same thief.
            }
            catch (Exception ex) when (attempt >= attempts && LostThePort(ex))
            {
                throw new InvalidOperationException(
                    $"a loopback port was taken between reservation and bind on all {attempts} attempts", ex);
            }
        }
    }

    /// <summary>True when the failure is another process owning the port, not the caller's own bug.</summary>
    private static bool LostThePort(Exception exception) => exception switch
    {
        SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse } => true,
        _ => exception.InnerException is not null && LostThePort(exception.InnerException)
    };

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
