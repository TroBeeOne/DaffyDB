using System.Net.Sockets;

namespace DaffySQL.Client;

/// <summary>
/// TCP keepalive on every session. An idle pooled session whose server vanished - a failover, a
/// firewall that forgot the flow - is otherwise only found out when a query is sent on it. SqlClient
/// sets similar values on its own sockets. Lifted from DaffyDuo.
/// </summary>
internal static class SocketTuning
{
    public static void EnableKeepAlive(Socket socket)
    {
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 30);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 4);
        }
        catch (SocketException)
        {
            // Not every platform accepts every option; keepalive is a safety net, not a requirement.
        }
    }
}
