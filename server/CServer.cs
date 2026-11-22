using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using OriCoop.Authority;
using OriCoop.Net;

namespace OriCoop.CServer
{
    public sealed class CServer
    {
        // T-167: the server's UDP socket. Like the client's UdpTransport, but it can wait for data without spinning, and
        // "no data" is a readiness check instead of a WouldBlock exception (once per idle step, about 1000 a second).
        sealed class ServerSocket : IDisposable
        {
            readonly Socket socket;
            readonly int localPort;
            public ServerSocket(int bindPort)
            {
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                try
                {
                    socket.ReceiveBufferSize = 1024 * 1024;
                    socket.SendBufferSize = 1024 * 1024;
                    socket.Bind(new IPEndPoint(IPAddress.Any, bindPort));
                    socket.Blocking = false;
                    localPort = ((IPEndPoint)socket.LocalEndPoint).Port;
                    // Windows otherwise reports ICMP port-unreachable as a receive error (SIO_UDP_CONNRESET off).
                    try { socket.IOControl(-1744830452, new byte[] { 0, 0, 0, 0 }, null); }
                    catch (Exception) { }
                }
                catch { socket.Close(); throw; }
            }
            public int LocalPort { get { return localPort; } }
            public void SendTo(byte[] data, int offset, int length, EndPoint to)
            {
                socket.SendTo(data, offset, length, SocketFlags.None, to);
            }
            // Nonblocking; zero means no datagram (an empty datagram is read and dropped, so it cannot keep the
            // socket readable and turn Wait into a spin).
            public int Receive(byte[] buffer, out EndPoint from)
            {
                from = null;
                if (!socket.Poll(0, SelectMode.SelectRead)) return 0;
                EndPoint any = new IPEndPoint(IPAddress.Any, 0);
                try
                {
                    int length = socket.ReceiveFrom(buffer, 0, buffer.Length, SocketFlags.None, ref any);
                    if (length > 0) from = any;
                    return length;
                }
                catch (SocketException) { return 0; }
            }
            // Blocks until a datagram arrives or the timeout passes.
            public void Wait(int timeoutMs)
            {
                try { socket.Poll(timeoutMs * 1000, SelectMode.SelectRead); }
                catch (SocketException) { Thread.Sleep(timeoutMs); }
            }
            public void Dispose() { socket.Close(); }
        }

        sealed class RoutedTransport : IDatagramTransport
        {
            readonly ServerSocket socket;
            readonly EndPoint endpoint;
            readonly Queue<byte[]> incoming = new Queue<byte[]>();
            public RoutedTransport(ServerSocket socket, EndPoint endpoint)
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
            public List<int> Seats = new List<int>();
            public int PrimarySeat { get { return Arbiter.PrimarySeat(Seats); } }
            public double ClosingAt = -1;
            // T-170: who this connection said it is (from its accepted CsJoin), for the audit lines after it is gone.
            public string AuditRoom, AuditSeats, AuditSteam, AuditNick;
            public bool AuditEnded;
            // T-173: when this connection last got a room list, and when a list reply went to it while it had no room.
            public double LastListAt = Double.NegativeInfinity, ListedAt = -1;
        }

        sealed class Room
        {
            public string Code;
            public Arbiter Arbiter;
            public readonly List<Connection> Members = new List<Connection>();
            public StreamWriter Log;
        }

        const string CodeAlphabet = "23456789";   // T-046: six-digit codes from 2-9 only, so older clients (alphabet A-Z + 2-9) can still type and accept them
        readonly Dictionary<EndPoint, Connection> peers = new Dictionary<EndPoint, Connection>();
        readonly Dictionary<string, Room> rooms = new Dictionary<string, Room>();
        readonly byte[] buffer = new byte[65536];
        readonly Random random = new Random();
        readonly Stopwatch clock = new Stopwatch();
        readonly ArbiterOptions options;
        readonly string logDirectory;
        readonly bool logConnections;
        // T-162: when each dropped endpoint was last told its session is gone (at most one Goodbye per 250 ms each).
        readonly Dictionary<EndPoint, double> staleReplies = new Dictionary<EndPoint, double>();
        ServerSocket socket;
        static volatile bool quitting;
        // Receive timeout of each client connection (NetPeer's default); tests shorten it.
        public int PeerTimeoutMs = 10000;
        // T-173: CsList limits. One reply per endpoint per ListIntervalMs (extra requests are ignored), none while that
        // connection still has more than ListMaxPending reliable fragments unacknowledged, and a connection that only
        // listed (never joined) is closed ListCloseMs after its last reply. Without them one endpoint could queue
        // megabytes of reliable room lists per datagram. Tests shorten or disable them.
        public double ListIntervalMs = 1000, ListCloseMs = 3000;
        public int ListMaxPending = 256;
        public long StaleGoodbyes { get; private set; }

        // T-170: opt-in connection audit log. When enabled, each event goes to stdout and, with a log directory,
        // to connections.log. It includes the remote IP:port, room, seats, SteamIDs and nicks when available.
        public const string ConnectionsLogName = "connections.log";
        public Action<string> AuditSink = Console.WriteLine;
        string auditPath;

        public CServer() : this(new ArbiterOptions(), null) { }
        public CServer(ArbiterOptions options) : this(options, null) { }
        public CServer(ArbiterOptions options, string logDirectory) : this(options, logDirectory, false) { }
        public CServer(ArbiterOptions options, string logDirectory, bool logConnections)
        {
            this.options = options;
            this.logDirectory = logDirectory;
            this.logConnections = logConnections;
        }
        public int LocalPort { get { return socket.LocalPort; } }
        public long DroppedSenders { get; private set; }
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
            if (logDirectory != null)
            {
                Directory.CreateDirectory(logDirectory);
                if (logConnections) auditPath = Path.Combine(logDirectory, ConnectionsLogName);
            }
            socket = new ServerSocket(port); clock.Start();
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
                if (item.Message[0] == CsWire.CsRoster)
                {
                    CsMessage roster = CsWire.Decode(item.Message);
                    foreach (Connection member in room.Members)
                    {
                        if (member.Seats.Count == 0) continue;
                        CsMessage identities = new CsMessage();
                        foreach (CsRosterEntry entry in roster.Roster)
                            if (member.Seats.Contains(entry.Seat)) identities.Identities.Add(new CsIdentity { Steam = entry.Steam, Nick = entry.Nick });
                        member.AuditSeats = AuditSeatList(member.Seats);
                        AuditIdentities(identities, out member.AuditSteam, out member.AuditNick);
                    }
                }
                foreach (Connection member in room.Members)
                    if (item.To == 0 || member.Seats.Contains(item.To) ||
                        (item.To < 0 && !member.Seats.Contains(-item.To)))
                        Send(member, item.Message, reliable);
            }
        }

        // T-162: data from an endpoint whose connection is gone (we timed it out while its link failed one way) gets a
        // Goodbye for its session, so it rejoins now instead of after its own 10 s timeout. Anything else stays ignored.
        // The reason is "timed out" (what happened, and what the lobby already translates): the Goodbye is then 18 bytes,
        // smaller than the smallest Data packet (19, an empty keepalive), so a reply never outweighs its request.
        public const string StaleReason = "timed out";
        void ReplyStale(EndPoint from, int length, double nowMs)
        {
            double last;
            if (staleReplies.TryGetValue(from, out last) && nowMs - last < 250) return;
            byte[] goodbye = StaleSessionGoodbye(buffer, length, StaleReason);
            if (goodbye == null) return;
            if (staleReplies.Count >= 1024)
            {
                List<EndPoint> old = new List<EndPoint>();
                foreach (KeyValuePair<EndPoint, double> entry in staleReplies) if (nowMs - entry.Value >= 250) old.Add(entry.Key);
                foreach (EndPoint endpoint in old) staleReplies.Remove(endpoint);
                if (staleReplies.Count >= 1024) return;
            }
            staleReplies[from] = nowMs;
            socket.SendTo(goodbye, 0, goodbye.Length, from);
            StaleGoodbyes++;
            AuditStale(from, nowMs);
        }

        // T-170: the stale_goodbye audit line is rate limited (the goodbye itself is not): the first per endpoint,
        // then at most one per endpoint per minute with count= the goodbyes since the previous line, so a client
        // that keeps sending or spoofed UDP from many sources cannot flood connections.log and the journal.
        // Tracking is capped like staleReplies: entries older than 10 minutes are pruned at 1024 endpoints, and a
        // new endpoint is not logged while it stays full. Goodbyes counted after an endpoint's last line and never
        // followed by another goodbye a minute later are not logged.
        public const double StaleAuditIntervalMs = 60000, StaleAuditForgetMs = 600000;
        sealed class StaleAudit { public double LastLine; public long Count; }
        readonly Dictionary<EndPoint, StaleAudit> staleAudits = new Dictionary<EndPoint, StaleAudit>();
        void AuditStale(EndPoint from, double nowMs)
        {
            if (!logConnections) return;
            StaleAudit entry;
            if (staleAudits.TryGetValue(from, out entry))
            {
                entry.Count++;
                if (nowMs - entry.LastLine < StaleAuditIntervalMs) return;
            }
            else
            {
                if (staleAudits.Count >= 1024)
                {
                    List<EndPoint> old = new List<EndPoint>();
                    foreach (KeyValuePair<EndPoint, StaleAudit> e in staleAudits)
                        if (nowMs - e.Value.LastLine >= StaleAuditForgetMs) old.Add(e.Key);
                    foreach (EndPoint endpoint in old) staleAudits.Remove(endpoint);
                    if (staleAudits.Count >= 1024) return;
                }
                entry = new StaleAudit { Count = 1 };
                staleAudits[from] = entry;
            }
            Audit("stale_goodbye", from, null, null, null, null, StaleReason, " count=" + entry.Count);
            entry.LastLine = nowMs; entry.Count = 0;
        }

        // A NetPeer Goodbye (magic 0x4F43, type 5, the session id, u16 length + UTF-8 reason; little endian) for the
        // session id in a NetPeer Data packet's header (type 4, at least 19 bytes); null for anything else. Built here
        // so the fix needs no client change: the client's NetPeer already ends its session on a Goodbye for its id.
        public static byte[] StaleSessionGoodbye(byte[] packet, int length, string reason)
        {
            if (packet == null || length < 19 || length > 1200 || length > packet.Length) return null;
            if (packet[0] != 0x43 || packet[1] != 0x4F || packet[2] != 4) return null;
            if (packet[3] == 0 && packet[4] == 0 && packet[5] == 0 && packet[6] == 0) return null;
            byte[] text = Encoding.UTF8.GetBytes(reason);
            byte[] goodbye = new byte[9 + text.Length];
            goodbye[0] = 0x43; goodbye[1] = 0x4F; goodbye[2] = 5;
            Buffer.BlockCopy(packet, 3, goodbye, 3, 4);
            goodbye[7] = (byte)text.Length; goodbye[8] = (byte)(text.Length >> 8);
            Buffer.BlockCopy(text, 0, goodbye, 9, text.Length);
            return goodbye;
        }

        // T-170: user text (nicks, peer reasons) quoted on one line: backslash, quote and every control character
        // escaped, so a nick can never start a forged line or break a column.
        public static string AuditText(string text)
        {
            if (text == null) return "\"\"";
            StringBuilder b = new StringBuilder(text.Length + 2);
            b.Append('"');
            foreach (char c in text)
            {
                if (c == '\\') b.Append("\\\\");
                else if (c == '"') b.Append("\\\"");
                else if (c == '\n') b.Append("\\n");
                else if (c == '\r') b.Append("\\r");
                else if (c == '\t') b.Append("\\t");
                else if (c < 0x20 || c == 0x7F || (c >= 0x80 && c < 0xA0) || c == '\u2028' || c == '\u2029')
                    b.Append("\\u").Append(((int)c).ToString("x4"));
                else b.Append(c);
            }
            return b.Append('"').ToString();
        }

        static string AuditEndpoint(EndPoint endpoint)
        {
            IPEndPoint ip = endpoint as IPEndPoint;
            return ip == null ? (endpoint == null ? "-" : endpoint.ToString()) : ip.Address + ":" + ip.Port;
        }

        static string AuditSeatList(List<int> seats)
        {
            if (seats == null || seats.Count == 0) return "-";
            StringBuilder b = new StringBuilder();
            foreach (int seat in seats) { if (b.Length > 0) b.Append(','); b.Append(seat); }
            return b.ToString();
        }

        // SteamIDs and nicks of a CsJoin, in identity order (one per local player).
        static void AuditIdentities(CsMessage request, out string steam, out string nick)
        {
            StringBuilder s = new StringBuilder(), n = new StringBuilder();
            if (request.Identities.Count == 0) { s.Append(request.Steam); n.Append(AuditText(request.Nick)); }
            foreach (CsIdentity identity in request.Identities)
            {
                if (s.Length > 0) { s.Append(','); n.Append(','); }
                s.Append(identity.Steam); n.Append(AuditText(identity.Nick));
            }
            steam = s.ToString(); nick = n.ToString();
        }

        void Audit(string evt, EndPoint endpoint, string room, string seats, string steam, string nick, string reason)
        {
            Audit(evt, endpoint, room, seats, steam, nick, reason, "");
        }

        void Audit(string evt, EndPoint endpoint, string room, string seats, string steam, string nick, string reason,
            string extra)
        {
            if (!logConnections) return;
            string line = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture) +
                " " + evt + " ip=" + AuditEndpoint(endpoint) + " room=" + (String.IsNullOrEmpty(room) ? "-" : room) +
                " seats=" + (String.IsNullOrEmpty(seats) ? "-" : seats) +
                " steam=" + (String.IsNullOrEmpty(steam) ? "-" : steam) +
                " nick=" + (String.IsNullOrEmpty(nick) ? "-" : nick) +
                " reason=" + (reason == null ? "-" : AuditText(reason)) + extra;
            Action<string> sink = AuditSink;
            if (sink != null) sink(line);
            if (auditPath != null) AppendLine(auditPath, line);
        }

        // Opened, appended and closed per line (events are rare): nothing is buffered, and logrotate can simply
        // rename the file (the next line creates a new one), no copytruncate needed.
        public static void AppendLine(string path, string line)
        {
            try
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(line + "\n");
                using (FileStream file = new FileStream(path, FileMode.Append, FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete))
                    file.Write(bytes, 0, bytes.Length);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        void AuditEnd(Connection connection, string reason)
        {
            if (!logConnections || connection.AuditEnded) return;
            connection.AuditEnded = true;
            Audit(reason == "timed out" ? "timeout" : "leave", connection.Endpoint, connection.AuditRoom,
                connection.AuditSeats, connection.AuditSteam, connection.AuditNick, reason);
        }

        void Disconnected(Connection connection, string reason)
        {
            AuditEnd(connection, reason);
            peers.Remove(connection.Endpoint);
            connection.Route.Dispose();
            if (connection.Room == null) return;
            connection.Room.Members.Remove(connection);
            Dispatch(connection.Room, connection.Room.Arbiter.Offline(connection.PrimarySeat, clock.Elapsed.TotalMilliseconds), true);
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

        void Error(Connection connection, string text, double nowMs, byte errorCode = 1)
        {
            Send(connection, CsWire.Encode(new CsMessage { Type = CsWire.CsError, ErrorCode = errorCode, Text = text }), true);
            connection.Peer.Flush();
            connection.ClosingAt = nowMs;
        }

        void Join(Connection connection, CsMessage request, double nowMs)
        {
            Room room = null;
            byte failure = 0;
            if (request.Proto != 2 || request.Mode > 2) failure = 3;
            else if (request.Mode == 0)
            {
                string code = NewCode();
                room = new Room { Code = code, Arbiter = new Arbiter(code, options, nowMs) };
                if (logDirectory != null)
                    room.Log = new StreamWriter(Path.Combine(logDirectory, code + ".log"), true, new UTF8Encoding(false));
                rooms.Add(code, room);
            }
            else if (!rooms.TryGetValue(request.Code, out room)) failure = 1;
            string steam = null, nick = null;
            if (logConnections) AuditIdentities(request, out steam, out nick);
            if (failure != 0)
            {
                Audit(request.Mode == 2 ? "rejoin_refused" : "join_refused", connection.Endpoint, request.Code, null,
                    steam, nick, RefusalReason(failure));
                Send(connection, CsWire.Encode(new CsMessage { Type = CsWire.CsWelcome, Result = failure }), true);
                return;
            }
            int seat; byte[] welcome;
            List<Outgoing> output = room.Arbiter.Join(request, nowMs, out seat, out welcome);
            Send(connection, welcome, true);
            if (logConnections && seat == 0)
                Audit(request.Mode == 2 ? "rejoin_refused" : "join_refused", connection.Endpoint, room.Code, null,
                    steam, nick, RefusalReason(CsWire.Decode(welcome).Result));
            if (seat != 0)
            {
                Connection previous = Owner(room, seat);
                if (previous != null)
                {
                    room.Members.Remove(previous);
                    previous.Room = null;
                    previous.Peer.Disconnect("replaced");
                }
                connection.Room = room; connection.Seats = room.Arbiter.ConnectionSeats(seat);
                room.Members.Add(connection);
                if (logConnections)
                {
                    connection.AuditRoom = room.Code; connection.AuditSeats = AuditSeatList(connection.Seats);
                    connection.AuditSteam = steam; connection.AuditNick = nick;
                    Audit(request.Mode == 0 ? "create" : request.Mode == 2 ? "rejoin" : "join", connection.Endpoint,
                        room.Code, connection.AuditSeats, steam, nick, null);
                }
            }
            Dispatch(room, output, true);
        }

        // CsWelcome result codes (Arbiter.Join / CServerClient): 1 no room, 2 full, 3 version, 4 token unknown, 5 kicked.
        static string RefusalReason(byte result)
        {
            switch (result)
            {
                case 1: return "no_room";
                case 2: return "full";
                case 3: return "version";
                case 4: return "bad_token";
                case 5: return "kicked";
                default: return "result " + result;
            }
        }

        static Connection Owner(Room room, int seat)
        {
            foreach (Connection member in room.Members) if (member.Seats.Contains(seat)) return member;
            return null;
        }

        void Receive(Connection connection, byte[] bytes, bool reliable, double nowMs)
        {
            if (connection.ClosingAt >= 0) return;
            if (bytes.Length < 2) { Error(connection, "message needs type and seat", nowMs); return; }
            if (bytes[0] == CsWire.CsList)
            {
                // T-173: nothing to decode (a CsList has no body; trailing bytes are ignored), rate limited per endpoint.
                if (nowMs - connection.LastListAt < ListIntervalMs ||
                    connection.Peer.Stats.ReliablePending > ListMaxPending) return;
                connection.LastListAt = nowMs;
                CsMessage list = new CsMessage { Type = CsWire.CsRoomList };
                foreach (Room room in rooms.Values)
                {
                    CsRoomListEntry entry = room.Arbiter.Listing();
                    // T-173: only rooms whose manager published a room info text are public. Rooms hosted for 我的房间
                    // (and password rooms) never set one, so their codes are not handed to strangers.
                    if (entry.Online > 0 && entry.Text.Length > 0) list.Rooms.Add(entry);
                    if (list.Rooms.Count == 64) break;
                }
                Send(connection, CsWire.Encode(list), true);
                if (connection.Room == null) connection.ListedAt = nowMs;
                return;
            }
            if (connection.Room == null)
            {
                if (bytes[0] != CsWire.CsJoin) { Error(connection, "first message must be CsJoin or CsList", nowMs); return; }
                Join(connection, CsWire.Decode(bytes), nowMs);
                return;
            }
            if (!connection.Seats.Contains(bytes[1])) { DroppedSenders++; return; }
            if (bytes[0] >= CsWire.CsJoin && bytes[0] <= CsWire.CsKick)
            {
                CsMessage message = CsWire.Decode(bytes);
                if (message.Type == CsWire.CsTo)
                {
                    if (message.Inner.Length < 2) return;
                    if (!connection.Seats.Contains(message.Inner[1])) { DroppedSenders++; return; }
                    // CsTo wraps game traffic only; never tunnel server/control messages to another client.
                    if (message.Inner[0] >= CsWire.CsJoin && message.Inner[0] <= CsWire.CsKick) return;
                    Connection target = Owner(connection.Room, message.Target);
                    if (target != null && target != connection) Send(target, message.Inner, reliable);
                }
                else
                {
                    int primary = connection.PrimarySeat;
                    List<Outgoing> output = connection.Room.Arbiter.Handle(message.Seat, message, nowMs);
                    connection.Seats = connection.Room.Arbiter.ConnectionSeats(primary);
                    if (message.Type == CsWire.CsKick)
                        foreach (Outgoing item in output)
                        {
                            if (item.Message[0] != CsWire.CsError) continue;
                            CsMessage error = CsWire.Decode(item.Message);
                            if (error.ErrorCode != 3) continue;
                            Connection victim = Owner(connection.Room, item.To);
                            if (victim == null) continue;
                            Audit("kick", victim.Endpoint, victim.AuditRoom, victim.AuditSeats, victim.AuditSteam,
                                victim.AuditNick, "by seat " + message.Seat);
                            victim.AuditEnded = true;
                            Error(victim, error.Text, nowMs, error.ErrorCode);
                            connection.Room.Members.Remove(victim);
                            victim.Room = null;
                        }
                    Dispatch(connection.Room, output, message.Type == CsWire.CsState ? reliable : true);
                }
                return;
            }
            foreach (Connection member in connection.Room.Members)
                if (member != connection) Send(member, bytes, reliable);
        }

        // T-167: how many times Step ran (the idle test counts wake-ups with it).
        public long Steps { get { return Interlocked.Read(ref steps); } }
        long steps;

        // T-167: the server's own loop (Main and the idle test): step, then block until a datagram arrives or the
        // next step is due. With connections that is 1 ms, the old Sleep(1) cadence (NetPeer resends, keepalives and
        // timeouts, lease ticks). Rooms without connections only need their seat-hold and idle-room timers (seconds):
        // 50 ms. Nothing at all: 250 ms, which still answers Ctrl+C promptly. A datagram ends any wait at once.
        public const int ConnectedWaitMs = 1, RoomsWaitMs = 50, EmptyWaitMs = 250;
        public void Run(Func<bool> stop)
        {
            while (!stop())
            {
                Step();
                socket.Wait(peers.Count > 0 ? ConnectedWaitMs : rooms.Count > 0 ? RoomsWaitMs : EmptyWaitMs);
            }
        }

        public void Step()
        {
            Interlocked.Increment(ref steps);
            double nowMs = clock.Elapsed.TotalMilliseconds;
            foreach (Room room in rooms.Values) Dispatch(room, room.Arbiter.Tick(nowMs), true);
            EndPoint from; int length;
            while ((length = socket.Receive(buffer, out from)) > 0)
            {
                Connection connection;
                if (!peers.TryGetValue(from, out connection))
                {
                    if (length != 13 || buffer[0] != 0x43 || buffer[1] != 0x4F || buffer[2] != 1)
                    {
                        ReplyStale(from, length, nowMs);
                        continue;
                    }
                    connection = new Connection { Endpoint = from, Route = new RoutedTransport(socket, from) };
                    connection.Peer = new NetPeer(connection.Route);
                    connection.Peer.TimeoutMs = PeerTimeoutMs;
                    Connection captured = connection;
                    connection.Peer.Disconnected += delegate(string reason) { Disconnected(captured, reason); };
                    connection.Peer.Listen(); peers.Add(from, connection);
                    Audit("connect", from, null, null, null, null, null);
                }
                connection.Route.Enqueue(buffer, length);
            }
            foreach (Connection connection in new List<Connection>(peers.Values))
            {
                connection.Peer.Poll();
                if (connection.Peer.State == PeerState.Listening) { Disconnected(connection, "handshake failed"); continue; }
                byte[] bytes; bool reliable;
                while (connection.Peer.TryReceive(out bytes, out reliable))
                {
                    // T-173: any failure while handling one message (malformed, invalid UTF-8, too many entries, ...)
                    // closes only that connection; it used to leave Step and stop the whole server.
                    try { Receive(connection, bytes, reliable, nowMs); }
                    catch (Exception e)
                    {
                        if (!(e is IOException) && !(e is InvalidDataException))   // malformed input: no log line per packet
                            Console.Error.WriteLine("C-server: message from " + AuditEndpoint(connection.Endpoint) +
                                " failed: " + e.GetType().Name + ": " + e.Message);
                        if (connection.ClosingAt < 0)
                            try { Error(connection, "invalid Cs message", nowMs); } catch (Exception) { connection.ClosingAt = nowMs; }
                    }
                }
                connection.Peer.Flush();
                if (connection.Room == null && connection.ListedAt >= 0 && connection.ClosingAt < 0 &&
                    nowMs - connection.ListedAt >= ListCloseMs)
                {
                    connection.Peer.Disconnect("room list done"); connection.Peer.Poll();
                    continue;
                }
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
                AuditEnd(connection, "server stopped");
                connection.Peer.Disconnect("server stopped"); connection.Route.Dispose();
            }
            peers.Clear();
            foreach (Room room in rooms.Values) if (room.Log != null) room.Log.Dispose();
            rooms.Clear();
            if (socket != null) { socket.Dispose(); socket = null; }
            clock.Reset();
        }

        static bool ConnectionLoggingFromEnvironment()
        {
            string value = (Environment.GetEnvironmentVariable("ORICOOP_CSERVER_LOG_CONNECTIONS") ?? "").Trim();
            if (value.Length == 0 || value == "0") return false;
            if (value == "1") return true;

            bool enabled;
            if (Boolean.TryParse(value, out enabled)) return enabled;
            throw new ArgumentException("ORICOOP_CSERVER_LOG_CONNECTIONS must be true, false, 1 or 0");
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
                bool? logConnections = null;
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "--port") port = Int32.Parse(args[++i]);
                    else if (args[i] == "--log") log = args[++i];
                    else if (args[i] == "--log-connections") logConnections = true;
                    else if (args[i] == "--no-log-connections") logConnections = false;
                    else throw new ArgumentException(
                        "usage: CServer [--port 47022] [--log directory] [--log-connections|--no-log-connections]");
                }
                quitting = false;
                Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e) { e.Cancel = true; quitting = true; };
                server = new CServer(new ArbiterOptions(), log, logConnections ?? ConnectionLoggingFromEnvironment());
                server.Start(port);
                server.Run(delegate { return quitting; });
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine("C-server failed: " + ex.Message); return 1; }
            finally { if (server != null) server.Stop(); }
        }
    }
}
