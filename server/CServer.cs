using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using OriCoop.Authority;
using OriCoop.Net;

namespace OriCoop.CServer
{
    public sealed class CServer
    {
        sealed class RoutedTransport : IDatagramTransport
        {
            readonly UdpTransport socket;
            readonly EndPoint endpoint;
            readonly Queue<byte[]> incoming = new Queue<byte[]>();
            public RoutedTransport(UdpTransport socket, EndPoint endpoint)
            {
                this.socket = socket; this.endpoint = endpoint;
            }
            public int LocalPort { get { return socket.LocalPort; } }
            public void SendTo(byte[] bytes, int offset, int length, EndPoint to)
            {
                socket.SendTo(bytes, offset, length, to);
            }
            public void Enqueue(byte[] bytes, int length)
            {
                byte[] copy = new byte[length]; Buffer.BlockCopy(bytes, 0, copy, 0, length);
                incoming.Enqueue(copy);
            }
            public int Receive(byte[] buffer, out EndPoint from)
            {
                if (incoming.Count == 0) { from = null; return 0; }
                byte[] bytes = incoming.Dequeue(); from = endpoint;
                Buffer.BlockCopy(bytes, 0, buffer, 0, bytes.Length); return bytes.Length;
            }
            public void Dispose() { incoming.Clear(); }
        }

        sealed class Connection
        {
            public EndPoint Endpoint;
            public RoutedTransport Route;
            public NetPeer Peer;
            public Room Room;
            public int Seat;
            public double ClosingAt = -1;
        }

        sealed class Room
        {
            public string Code;
            public Arbiter Arbiter;
            public readonly Dictionary<int, Connection> Members = new Dictionary<int, Connection>();
            public StreamWriter Log;
        }

        const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        readonly Dictionary<EndPoint, Connection> peers = new Dictionary<EndPoint, Connection>();
        readonly Dictionary<string, Room> rooms = new Dictionary<string, Room>();
        readonly byte[] buffer = new byte[65536];
        readonly Random random = new Random();
        readonly Stopwatch clock = new Stopwatch();
        readonly ArbiterOptions options;
        readonly string logDirectory;
        UdpTransport socket;
        static volatile bool quitting;

        public CServer() : this(new ArbiterOptions(), null) { }
        public CServer(ArbiterOptions options) : this(options, null) { }
        public CServer(ArbiterOptions options, string logDirectory)
        {
            this.options = options; this.logDirectory = logDirectory;
        }
        public int LocalPort { get { return socket.LocalPort; } }
        public long DroppedStates
        {
            get
            {
                long count = 0;
                foreach (Room room in rooms.Values) count += room.Arbiter.DroppedStates;
                return count;
            }
        }

        public void Start(int port)
        {
            if (logDirectory != null) Directory.CreateDirectory(logDirectory);
            socket = new UdpTransport(port); clock.Start();
            Console.WriteLine("OriCoop C-server listening on UDP port " + LocalPort);
        }

        void Send(Connection connection, byte[] bytes, bool reliable)
        {
            if (connection.Peer.State != PeerState.Connected) return;
            if (reliable) connection.Peer.SendReliable(bytes, 0, bytes.Length);
            else connection.Peer.SendUnreliable(bytes, 0, bytes.Length);
        }

        void Dispatch(Room room, List<Outgoing> output, bool reliable)
        {
            foreach (Outgoing item in output)
            {
                if (room.Log != null && item.To == 0 && item.Message[0] == CsWire.CsEvent)
                {
                    CsMessage e = CsWire.Decode(item.Message);
                    room.Log.WriteLine(e.Id + " " + e.From + " " + e.Seq + " " + e.Kind + " " +
                        Convert.ToBase64String(Encoding.UTF8.GetBytes(e.Key)) + " " + Convert.ToBase64String(e.Data));
                    room.Log.Flush();
                }
                foreach (KeyValuePair<int, Connection> member in room.Members)
                    if (item.To == 0 || item.To == member.Key || (item.To < 0 && -item.To != member.Key))
                        Send(member.Value, item.Message, reliable);
            }
        }

        void Disconnected(Connection connection)
        {
            peers.Remove(connection.Endpoint);
            connection.Route.Dispose();
            if (connection.Room == null) return;
            connection.Room.Members.Remove(connection.Seat);
            Dispatch(connection.Room, connection.Room.Arbiter.Offline(connection.Seat, clock.Elapsed.TotalMilliseconds), true);
        }

        string NewCode()
        {
            string code;
            do
            {
                char[] chars = new char[6];
                for (int i = 0; i < chars.Length; i++) chars[i] = CodeAlphabet[random.Next(CodeAlphabet.Length)];
                code = new string(chars);
            } while (rooms.ContainsKey(code));
            return code;
        }

        void Error(Connection connection, string text, double nowMs)
        {
            Send(connection, CsWire.Encode(new CsMessage { Type = CsWire.CsError, ErrorCode = 1, Text = text }), true);
            connection.Peer.Flush();
            connection.ClosingAt = nowMs;
        }

        void Join(Connection connection, CsMessage request, double nowMs)
        {
            Room room = null;
            byte failure = 0;
            if (request.Proto != 1 || request.Mode > 2) failure = 3;
            else if (request.Mode == 0)
            {
                string code = NewCode();
                room = new Room { Code = code, Arbiter = new Arbiter(code, options, nowMs) };
                if (logDirectory != null)
                    room.Log = new StreamWriter(Path.Combine(logDirectory, code + ".log"), true, new UTF8Encoding(false));
                rooms.Add(code, room);
            }
            else if (!rooms.TryGetValue(request.Code, out room)) failure = 1;
            if (failure != 0)
            {
                Send(connection, CsWire.Encode(new CsMessage { Type = CsWire.CsWelcome, Result = failure }), true);
                return;
            }
            int seat; byte[] welcome;
            List<Outgoing> output = room.Arbiter.Join(request, nowMs, out seat, out welcome);
            Send(connection, welcome, true);
            if (seat != 0)
            {
                Connection previous;
                if (room.Members.TryGetValue(seat, out previous))
                {
                    room.Members.Remove(seat);
                    previous.Room = null;
                    previous.Peer.Disconnect("replaced");
                }
                connection.Room = room; connection.Seat = seat;
                room.Members.Add(seat, connection);
            }
            Dispatch(room, output, true);
        }

        void Receive(Connection connection, byte[] bytes, bool reliable, double nowMs)
        {
            if (connection.ClosingAt >= 0) return;
            if (bytes.Length < 2) { Error(connection, "message needs type and seat", nowMs); return; }
            if (connection.Room == null)
            {
                if (bytes[0] != CsWire.CsJoin) { Error(connection, "first message must be CsJoin", nowMs); return; }
                Join(connection, CsWire.Decode(bytes), nowMs);
                return;
            }
            if (bytes[0] >= CsWire.CsJoin && bytes[0] <= CsWire.CsTo)
            {
                CsMessage message = CsWire.Decode(bytes);
                if (message.Type == CsWire.CsTo)
                {
                    if (message.Inner.Length < 2) return;
                    message.Inner[1] = (byte)connection.Seat;
                    Connection target;
                    if (connection.Room.Members.TryGetValue(message.Target, out target)) Send(target, message.Inner, reliable);
                }
                else
                    Dispatch(connection.Room, connection.Room.Arbiter.Handle(connection.Seat, message, nowMs),
                        message.Type == CsWire.CsState ? reliable : true);
                return;
            }
            bytes[1] = (byte)connection.Seat;
            foreach (KeyValuePair<int, Connection> member in connection.Room.Members)
                if (member.Key != connection.Seat) Send(member.Value, bytes, reliable);
        }

        public void Step()
        {
            double nowMs = clock.Elapsed.TotalMilliseconds;
            foreach (Room room in rooms.Values) Dispatch(room, room.Arbiter.Tick(nowMs), true);
            EndPoint from; int length;
            while ((length = socket.Receive(buffer, out from)) > 0)
            {
                Connection connection;
                if (!peers.TryGetValue(from, out connection))
                {
                    if (length != 13 || buffer[0] != 0x43 || buffer[1] != 0x4F || buffer[2] != 1) continue;
                    connection = new Connection { Endpoint = from, Route = new RoutedTransport(socket, from) };
                    connection.Peer = new NetPeer(connection.Route);
                    Connection captured = connection;
                    connection.Peer.Disconnected += delegate(string reason) { Disconnected(captured); };
                    connection.Peer.Listen(); peers.Add(from, connection);
                }
                connection.Route.Enqueue(buffer, length);
            }
            foreach (Connection connection in new List<Connection>(peers.Values))
            {
                connection.Peer.Poll();
                if (connection.Peer.State == PeerState.Listening) { Disconnected(connection); continue; }
                byte[] bytes; bool reliable;
                while (connection.Peer.TryReceive(out bytes, out reliable))
                {
                    try { Receive(connection, bytes, reliable, nowMs); }
                    catch (IOException) { Error(connection, "invalid Cs message", nowMs); }
                }
                connection.Peer.Flush();
                if (connection.ClosingAt >= 0 && nowMs - connection.ClosingAt >= 20 &&
                    (connection.Peer.Stats.ReliablePending == 0 || nowMs - connection.ClosingAt >= 500))
                {
                    connection.Peer.Disconnect("Cs protocol error"); connection.Peer.Poll();
                }
            }
            List<string> expired = new List<string>();
            foreach (Room room in rooms.Values)
                if (room.Arbiter.IsIdle(nowMs)) expired.Add(room.Code);
            foreach (string code in expired)
            {
                Room room = rooms[code];
                if (room.Log != null) room.Log.Dispose();
                rooms.Remove(code);
            }
        }

        public void Stop()
        {
            foreach (Connection connection in peers.Values)
            {
                connection.Peer.Disconnect("server stopped"); connection.Route.Dispose();
            }
            peers.Clear();
            foreach (Room room in rooms.Values) if (room.Log != null) room.Log.Dispose();
            rooms.Clear();
            if (socket != null) { socket.Dispose(); socket = null; }
            clock.Reset();
        }

        public static int Main(string[] args)
        {
            CServer server = null;
            try
            {
                string envPort = Environment.GetEnvironmentVariable("ORICOOP_CSERVER_PORT");
                int port = String.IsNullOrEmpty(envPort) ? 47022 : Int32.Parse(envPort);
                string log = Environment.GetEnvironmentVariable("ORICOOP_CSERVER_LOG_DIR");
                if (String.IsNullOrEmpty(log)) log = Environment.GetEnvironmentVariable("LOGS_DIRECTORY");
                if (String.IsNullOrEmpty(log)) log = null;
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "--port") port = Int32.Parse(args[++i]);
                    else if (args[i] == "--log") log = args[++i];
                    else throw new ArgumentException("usage: CServer [--port 47022] [--log directory]");
                }
                quitting = false;
                Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e) { e.Cancel = true; quitting = true; };
                server = new CServer(new ArbiterOptions(), log);
                server.Start(port);
                while (!quitting) { server.Step(); Thread.Sleep(1); }
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine("C-server failed: " + ex.Message); return 1; }
            finally { if (server != null) server.Stop(); }
        }
    }
}
