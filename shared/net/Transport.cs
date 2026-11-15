using System;
using System.Net;
using System.Net.Sockets;

namespace OriCoop.Net
{
    public interface IDatagramTransport : IDisposable
    {
        void SendTo(byte[] data, int offset, int length, EndPoint to);
        // Nonblocking; zero means that no datagram is available.
        int Receive(byte[] buffer, out EndPoint from);
        int LocalPort { get; }
    }

    public sealed class UdpTransport : IDatagramTransport
    {
        private readonly Socket socket;
        private readonly int localPort;

        public UdpTransport(int bindPort)
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                socket.ReceiveBufferSize = 1024 * 1024;
                socket.SendBufferSize = 1024 * 1024;
                socket.Bind(new IPEndPoint(IPAddress.Any, bindPort));
                socket.Blocking = false;
                localPort = ((IPEndPoint)socket.LocalEndPoint).Port;
                // Windows otherwise reports ICMP port-unreachable as a receive error.
                try { socket.IOControl(-1744830452, new byte[] { 0, 0, 0, 0 }, null); }
                catch (Exception) { }
            }
            catch
            {
                socket.Close();
                throw;
            }
        }

        public int LocalPort { get { return localPort; } }

        public void SendTo(byte[] data, int offset, int length, EndPoint to)
        {
            socket.SendTo(data, offset, length, SocketFlags.None, to);
        }

        public int Receive(byte[] buffer, out EndPoint from)
        {
            from = new IPEndPoint(IPAddress.Any, 0);
            try { return socket.ReceiveFrom(buffer, 0, buffer.Length, SocketFlags.None, ref from); }
            catch (SocketException) { from = null; return 0; }
        }

        public void Dispose() { socket.Close(); }
    }
}
