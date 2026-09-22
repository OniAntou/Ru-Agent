using System.Net;
using System.Net.Sockets;

namespace RuAgent.Hosting;

public static class PortProbe
{
    public static bool IsLoopbackPortOccupied(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return false;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }
}
