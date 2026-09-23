using System.Net;
using System.Net.Sockets;

namespace RuAgent.Hosting;

public static class PortProbe
{
    /// <summary>
    /// True when the loopback port cannot be bound. Unknown bind failures are
    /// reported as occupied: Hyper-V/WSL2 excluded-port ranges and firewall
    /// restrictions surface as AccessDenied rather than AddressAlreadyInUse,
    /// and a port the probe cannot claim must never be advertised as free --
    /// the host would only die later inside Kestrel with no explanation.
    /// </summary>
    public static bool IsLoopbackPortOccupied(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return false;
        }
        catch (SocketException)
        {
            return true;
        }
        catch (Exception)
        {
            return true;
        }
    }
}
