using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace OriCoop.Net
{
    public enum PeerState { Idle, Listening, Connecting, Connected, Disconnected }

    public sealed class NetSim
    {
        public int LagMs;
        public int JitterMs;
        public float LossPercent;
        public float DupPercent;
    }

    public sealed class NetStats
    {
        public float RttMs, JitterMs, LossPercent;
        public float SendPps, RecvPps, SendKBps, RecvKBps;
        public int ReliablePending;
        public long ReliableResends;
        public double LastReceiveAgeMs;
        public long PacketsSent, PacketsReceived;
    }

    // One caller thread owns a peer and its transport. Dispose the transport separately.
    public sealed class NetPeer
    {
        public const ushort ProtocolVersion = 1;
        private const ushort Magic = 0x4F43;
        private const int Mtu = 1200, DataHeader = 19, FragmentSize = 1000;
        private const int MaxMessage = 4 * 1024 * 1024, Window = 256;
        private const byte Request = 1, Accept = 2, Reject = 3, Data = 4, Goodbye = 5;
        private const byte Unreliable = 0, Reliable = 1;
        private const int HistorySize = 8192, PacketWindow = 32;
        private static readonly Random Ids = new Random();
        private readonly Random random = new Random();
        private readonly IDatagramTransport transport;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly Func<double> nowMs;
        private readonly NetStats stats = new NetStats();
        private readonly NetSim sim = new NetSim();
        private readonly byte[] receiveBuffer = new byte[65536];
        private readonly Queue<byte[]> unreliable = new Queue<byte[]>();
        private readonly Queue<Fragment> waiting = new Queue<Fragment>();
        private readonly List<Fragment> inFlight = new List<Fragment>();
        private readonly List<SentPacket> packetWindow = new List<SentPacket>();
        private readonly Dictionary<uint, Assembly> assemblies = new Dictionary<uint, Assembly>();
        private readonly Queue<AppMessage> incoming = new Queue<AppMessage>();
        private readonly Queue<PeerEvent> events = new Queue<PeerEvent>();
        private readonly List<DelayedPacket> delayed = new List<DelayedPacket>();
        private readonly Queue<Traffic> sendTraffic = new Queue<Traffic>();
        private readonly Queue<Traffic> recvTraffic = new Queue<Traffic>();
        private readonly SentPacket[] sent = new SentPacket[65536];
        private readonly SentPacket[] history = new SentPacket[HistorySize];
        private int historyPosition, historyCount, sendBytes, recvBytes;
        private PeerState state;
        private EndPoint remote;
        private string disconnectReason;
        private bool isHost, haveReceived, ackDirty, polling;
        private uint session, nonce, nextMessage, expectedMessage, receivedBits;
        private ushort nextSequence = 1, latestSequence;
        private double lastReceive, lastSend, connectStart, lastRequest;
        private int receivedSinceAck;

        private sealed class Fragment
        {
            internal uint Id;
            internal ushort Index, Count;
            internal byte[] Bytes;
            internal bool Acked;
            internal double LastSent = -1;
        }
        private sealed class Assembly
        {
            internal byte[][] Pieces;
            internal int Count, Length;
        }
        private sealed class AppMessage
        {
            internal byte[] Bytes;
            internal bool Reliable;
        }
        private sealed class PeerEvent
        {
            internal bool Connected;
            internal string Reason;
        }
        private sealed class DelayedPacket
        {
            internal byte[] Bytes;
            internal EndPoint To;
            internal double Due;
        }
        private sealed class SentPacket
        {
            internal ushort Sequence;
            internal double Time;
            internal bool Acked, MeasureRtt;
            internal List<Fragment> Fragments;
        }
        private sealed class WireMessage
        {
            internal byte Kind;
            internal uint Id;
            internal ushort Index, Count;
            internal byte[] Bytes;
        }
        private sealed class Traffic
        {
            internal double Time;
            internal int Bytes;
        }

        public NetPeer(IDatagramTransport transport)
        {
            if (transport == null) throw new ArgumentNullException("transport");
            this.transport = transport;
        }

        internal NetPeer(IDatagramTransport transport, Func<double> nowMs) : this(transport)
        {
            this.nowMs = nowMs;
        }

        public PeerState State { get { return state; } }
        public EndPoint Remote { get { return remote; } }
        public string DisconnectReason { get { return disconnectReason; } }
        public NetStats Stats { get { return stats; } }
        public NetSim Sim { get { return sim; } }
        public double NowMs { get { return nowMs == null ? clock.Elapsed.TotalMilliseconds : nowMs(); } }
        // Zero uses ProtocolVersion; exposed only to exercise version rejection.
        public ushort ProtocolVersionOverride { get; set; }
        // 10 s: Wi-Fi roaming / router hiccups of ~5-8 s should not drop the link (lag test ②); NetHud shows a hint after 1 s of silence.
        public int TimeoutMs = 10000;
        public int KeepaliveMs = 200;
        public event Action Connected;
        public event Action<string> Disconnected;

        private static uint NewId()
        {
            return ((uint)Ids.Next(1, Int32.MaxValue) << 1) | (uint)Ids.Next(0, 2);
        }

        private void ClearSession()
        {
            unreliable.Clear(); waiting.Clear(); inFlight.Clear(); packetWindow.Clear(); assemblies.Clear(); incoming.Clear();
            delayed.Clear(); sendTraffic.Clear(); recvTraffic.Clear();
            Array.Clear(sent, 0, sent.Length); Array.Clear(history, 0, history.Length);
            historyPosition = historyCount = sendBytes = recvBytes = 0;
            nextSequence = 1; latestSequence = 0; receivedBits = 0;
            nextMessage = expectedMessage = 0;
            haveReceived = ackDirty = false; receivedSinceAck = 0;
            stats.RttMs = stats.JitterMs = stats.LossPercent = 0;
            stats.SendPps = stats.RecvPps = stats.SendKBps = stats.RecvKBps = 0;
            stats.ReliablePending = 0; stats.ReliableResends = 0;
            stats.PacketsSent = stats.PacketsReceived = 0; stats.LastReceiveAgeMs = 0;
            disconnectReason = null; lastReceive = lastSend = NowMs;
        }

        public void Listen()
        {
            ClearSession(); isHost = true; remote = null; session = nonce = 0;
            state = PeerState.Listening;
        }

        public void Connect(EndPoint host)
        {
            if (host == null) throw new ArgumentNullException("host");
            ClearSession(); isHost = false; remote = host; session = 0; nonce = NewId();
            connectStart = NowMs; lastRequest = connectStart - 250;
            state = PeerState.Connecting;
        }

        private void BecomeConnected()
        {
            state = PeerState.Connected; lastReceive = NowMs;
            events.Enqueue(new PeerEvent { Connected = true });
        }

        private void BecomeDisconnected(string reason)
        {
            if (state == PeerState.Disconnected || state == PeerState.Idle) return;
            state = PeerState.Disconnected; disconnectReason = reason;
            unreliable.Clear(); waiting.Clear(); inFlight.Clear(); packetWindow.Clear(); assemblies.Clear();
            stats.ReliablePending = 0;
            events.Enqueue(new PeerEvent { Reason = reason });
        }

        public void Disconnect(string reason)
        {
            if (state != PeerState.Connected && state != PeerState.Connecting) return;
            if (state == PeerState.Connected)
            {
                byte[] packet = StringPacket(Goodbye, session, reason);
                for (int i = 0; i < 3; i++) SendPacket(packet, remote);
            }
            BecomeDisconnected(reason ?? "disconnected");
        }

        private static void ValidateSlice(byte[] msg, int offset, int length, int maximum)
        {
            if (msg == null) throw new ArgumentNullException("msg");
            if (offset < 0 || length < 0 || offset > msg.Length - length) throw new ArgumentOutOfRangeException("length");
            if (length > maximum) throw new ArgumentOutOfRangeException("length");
        }

        public void SendUnreliable(byte[] msg, int offset, int length)
        {
            if (state != PeerState.Connected) return;
            ValidateSlice(msg, offset, length, Mtu - DataHeader - 3);
            byte[] copy = new byte[length]; Buffer.BlockCopy(msg, offset, copy, 0, length);
            unreliable.Enqueue(copy);
        }

        public void SendReliable(byte[] msg, int offset, int length)
        {
            if (state != PeerState.Connected) return;
            ValidateSlice(msg, offset, length, MaxMessage);
            uint id = nextMessage++;
            int count = Math.Max(1, (length + FragmentSize - 1) / FragmentSize);
            for (int i = 0; i < count; i++)
            {
                int size = Math.Min(FragmentSize, length - i * FragmentSize);
                byte[] bytes = new byte[size]; Buffer.BlockCopy(msg, offset + i * FragmentSize, bytes, 0, size);
                waiting.Enqueue(new Fragment { Id = id, Index = (ushort)i, Count = (ushort)count, Bytes = bytes });
                stats.ReliablePending++;
            }
        }

        // Largest reliable message still being reassembled (fragments received / total), for
        // progress bars such as the guest's save-transfer screen. False when none is pending.
        public bool IncomingProgress(out int have, out int total)
        {
            have = total = 0;
            foreach (Assembly a in assemblies.Values)
                if (a.Pieces.Length > total) { total = a.Pieces.Length; have = a.Count; }
            return total > 1;
        }

        public bool TryReceive(out byte[] msg, out bool reliable)
        {
            if (incoming.Count == 0) { msg = null; reliable = false; return false; }
            AppMessage item = incoming.Dequeue(); msg = item.Bytes; reliable = item.Reliable; return true;
        }

        private static BinaryWriter StartPacket(MemoryStream stream, byte type, uint id)
        {
            BinaryWriter writer = new BinaryWriter(stream);
            writer.Write(Magic); writer.Write(type); writer.Write(id);
            return writer;
        }

        // Strings are ushort byte length followed by UTF-8; bounded to fit an MTU.
        private static byte[] StringPacket(byte type, uint id, string reason)
        {
            reason = reason ?? "disconnected";
            if (reason.Length > 256) reason = reason.Substring(0, 256);
            byte[] text = Encoding.UTF8.GetBytes(reason);
            using (MemoryStream stream = new MemoryStream())
            {
                BinaryWriter writer = StartPacket(stream, type, id);
                writer.Write((ushort)text.Length); writer.Write(text); return stream.ToArray();
            }
        }

        private void SendRequest()
        {
            using (MemoryStream stream = new MemoryStream())
            {
                BinaryWriter writer = StartPacket(stream, Request, nonce);
                writer.Write(ProtocolVersionOverride == 0 ? ProtocolVersion : ProtocolVersionOverride);
                writer.Write(nonce); SendPacket(stream.ToArray(), remote);
            }
            lastRequest = NowMs;
        }

        private void SendAccept()
        {
            using (MemoryStream stream = new MemoryStream())
            {
                BinaryWriter writer = StartPacket(stream, Accept, nonce);
                writer.Write(session); SendPacket(stream.ToArray(), remote);
            }
        }

        private void RecordTraffic(Queue<Traffic> queue, int bytes, bool sending)
        {
            queue.Enqueue(new Traffic { Time = NowMs, Bytes = bytes });
            if (sending) { sendBytes += bytes; stats.PacketsSent++; }
            else { recvBytes += bytes; stats.PacketsReceived++; }
        }

        private void SendPacket(byte[] bytes, EndPoint to)
        {
            RecordTraffic(sendTraffic, bytes.Length, true);
            // Rejecting an unrelated endpoint must not postpone our guest's keepalive.
            if (Object.Equals(to, remote)) lastSend = NowMs;
            if (random.NextDouble() * 100 < sim.LossPercent) return;
            Schedule(bytes, to);
            if (random.NextDouble() * 100 < sim.DupPercent) Schedule(bytes, to);
        }

        private void Schedule(byte[] bytes, EndPoint to)
        {
            double jitter = sim.JitterMs > 0 ? random.NextDouble() * sim.JitterMs : 0;
            double due = NowMs + Math.Max(0, sim.LagMs) + jitter;
            if (due <= NowMs) SendRaw(bytes, to);
            else delayed.Add(new DelayedPacket { Bytes = bytes, To = to, Due = due });
        }

        private void SendRaw(byte[] bytes, EndPoint to)
        {
            try { transport.SendTo(bytes, 0, bytes.Length, to); }
            catch (SocketException) { /* A failed datagram behaves like loss. */ }
        }

        private void ReleaseDelayed()
        {
            double now = NowMs;
            for (int i = 0; i < delayed.Count; )
            {
                DelayedPacket item = delayed[i];
                if (item.Due > now) { i++; continue; }
                SendRaw(item.Bytes, item.To); delayed.RemoveAt(i);
            }
        }

        private void TrackPacket(ushort sequence, List<Fragment> fragments, bool measureRtt)
        {
            SentPacket old = history[historyPosition];
            if (old != null && Object.ReferenceEquals(sent[old.Sequence], old)) sent[old.Sequence] = null;
            SentPacket item = new SentPacket { Sequence = sequence, Time = NowMs, Fragments = fragments, MeasureRtt = measureRtt };
            sent[sequence] = item; history[historyPosition] = item;
            if (fragments.Count != 0) packetWindow.Add(item);
            historyPosition = (historyPosition + 1) % HistorySize;
            historyCount = Math.Min(historyCount + 1, HistorySize);
        }

        private void SendData(List<Fragment> fragments, List<byte[]> plain)
        {
            using (MemoryStream stream = new MemoryStream(Mtu))
            {
                ushort sequence = nextSequence++;
                BinaryWriter writer = StartPacket(stream, Data, session);
                writer.Write(sequence); writer.Write(haveReceived ? latestSequence : (ushort)0);
                writer.Write(haveReceived ? receivedBits : 0U); writer.Write(unchecked((uint)NowMs));
                foreach (Fragment fragment in fragments)
                {
                    writer.Write(Reliable); writer.Write(fragment.Id); writer.Write(fragment.Index); writer.Write(fragment.Count);
                    writer.Write((ushort)fragment.Bytes.Length); writer.Write(fragment.Bytes);
                    if (fragment.LastSent >= 0) stats.ReliableResends++;
                    fragment.LastSent = NowMs;
                }
                foreach (byte[] bytes in plain)
                {
                    writer.Write(Unreliable); writer.Write((ushort)bytes.Length); writer.Write(bytes);
                }
                TrackPacket(sequence, fragments, fragments.Count != 0 || plain.Count != 0);
                SendPacket(stream.ToArray(), remote);
            }
            ackDirty = false; receivedSinceAck = 0;
        }

        private void SendAck() { SendData(new List<Fragment>(), new List<byte[]>()); }

        public void Flush()
        {
            ReleaseDelayed();
            if (state != PeerState.Connected) return;
            inFlight.RemoveAll(delegate(Fragment f) { return f.Acked; });
            while (inFlight.Count < Window && waiting.Count != 0) inFlight.Add(waiting.Dequeue());
            double now = NowMs;
            // TCP-style RTO: RTT + 4 x mean deviation (+20 ms for ack/flush delay). Was 2xRTT+2xjitter, which cost
            // ~1 s per lost reliable packet at 400 ms RTT (lag test ④). Receivers drop duplicates, so early resends are safe.
            double resendAfter = Math.Min(1000, Math.Max(50, stats.RttMs + 4 * stats.JitterMs + 20));
            // Keep jittered reliable bursts inside the 32-bit ack history. The fragment
            // window is still 256; several small fragments can share one packet.
            packetWindow.RemoveAll(delegate(SentPacket p) { return p.Acked || now - p.Time >= resendAfter; });
            List<Fragment> due = new List<Fragment>();
            foreach (Fragment f in inFlight) if (f.LastSent < 0 || now - f.LastSent >= resendAfter) due.Add(f);
            int index = 0;
            while (index < due.Count || unreliable.Count != 0)
            {
                int size = DataHeader;
                List<Fragment> fragments = new List<Fragment>();
                List<byte[]> plain = new List<byte[]>();
                while (packetWindow.Count < PacketWindow && index < due.Count && size + 11 + due[index].Bytes.Length <= Mtu)
                {
                    Fragment f = due[index++]; fragments.Add(f); size += 11 + f.Bytes.Length;
                }
                while (unreliable.Count != 0 && size + 3 + unreliable.Peek().Length <= Mtu)
                {
                    byte[] bytes = unreliable.Dequeue(); plain.Add(bytes); size += 3 + bytes.Length;
                }
                if (fragments.Count == 0 && plain.Count == 0) break;
                SendData(fragments, plain);
            }
            if (ackDirty || NowMs - lastSend >= KeepaliveMs) SendAck();
            ReleaseDelayed(); UpdateStats();
        }

        private void Acknowledge(ushort sequence)
        {
            SentPacket item = sent[sequence];
            if (item == null || item.Acked) return;
            item.Acked = true;
            if (item.MeasureRtt)
            {
                float sample = (float)(NowMs - item.Time);
                if (stats.RttMs == 0) stats.RttMs = sample;
                else
                {
                    stats.JitterMs += (Math.Abs(sample - stats.RttMs) - stats.JitterMs) * 0.25f;
                    stats.RttMs += (sample - stats.RttMs) * 0.125f;
                }
            }
            foreach (Fragment f in item.Fragments)
            {
                if (f.Acked) continue;
                f.Acked = true; stats.ReliablePending--;
            }
            item.Fragments = new List<Fragment>();
        }

        private bool ReceiveSequence(ushort sequence)
        {
            if (!haveReceived) { haveReceived = true; latestSequence = sequence; receivedBits = 0; return true; }
            int forward = (ushort)(sequence - latestSequence);
            if (forward == 0) return false;
            if (forward < 32768)
            {
                receivedBits = forward > 32 ? 0U : forward == 32 ? 0x80000000U :
                    (receivedBits << forward) | (1U << (forward - 1));
                latestSequence = sequence; return true;
            }
            int back = (ushort)(latestSequence - sequence);
            if (back > 32) return false;
            uint bit = 1U << (back - 1);
            if ((receivedBits & bit) != 0) return false;
            receivedBits |= bit; return true;
        }

        private static string ReadReason(BinaryReader reader, MemoryStream stream)
        {
            int size = reader.ReadUInt16();
            if (size > 1024 || stream.Length - stream.Position != size) throw new InvalidDataException();
            return Encoding.UTF8.GetString(reader.ReadBytes(size));
        }

        private List<WireMessage> ReadMessages(BinaryReader reader, MemoryStream stream)
        {
            List<WireMessage> messages = new List<WireMessage>();
            Dictionary<uint, ushort> counts = new Dictionary<uint, ushort>();
            while (stream.Position < stream.Length)
            {
                WireMessage m = new WireMessage(); m.Kind = reader.ReadByte();
                if (m.Kind == Reliable) { m.Id = reader.ReadUInt32(); m.Index = reader.ReadUInt16(); m.Count = reader.ReadUInt16(); }
                else if (m.Kind != Unreliable) throw new InvalidDataException();
                int length = reader.ReadUInt16();
                if (length > stream.Length - stream.Position) throw new InvalidDataException();
                if (m.Kind == Reliable)
                {
                    if (m.Count == 0 || m.Count > (MaxMessage + FragmentSize - 1) / FragmentSize || m.Index >= m.Count || length > FragmentSize)
                        throw new InvalidDataException();
                    if (m.Index < m.Count - 1 && length != FragmentSize) throw new InvalidDataException();
                    if (m.Index == m.Count - 1 && ((m.Count - 1) * FragmentSize + length > MaxMessage || (length == 0 && m.Count != 1)))
                        throw new InvalidDataException();
                    Assembly assembly; ushort count;
                    if (assemblies.TryGetValue(m.Id, out assembly) && assembly.Pieces.Length != m.Count) throw new InvalidDataException();
                    if (counts.TryGetValue(m.Id, out count) && count != m.Count) throw new InvalidDataException();
                    counts[m.Id] = m.Count;
                }
                m.Bytes = reader.ReadBytes(length); messages.Add(m);
            }
            return messages;
        }

        private void Deliver(WireMessage m)
        {
            if (m.Kind == Unreliable) { incoming.Enqueue(new AppMessage { Bytes = m.Bytes }); return; }
            // Unsigned distance also handles uint message-id wraparound.
            if (unchecked(m.Id - expectedMessage) >= 0x80000000U) return;
            Assembly assembly;
            if (!assemblies.TryGetValue(m.Id, out assembly))
            {
                assembly = new Assembly { Pieces = new byte[m.Count][] }; assemblies.Add(m.Id, assembly);
            }
            if (assembly.Pieces[m.Index] == null)
            {
                assembly.Pieces[m.Index] = m.Bytes; assembly.Count++; assembly.Length += m.Bytes.Length;
            }
            while (assemblies.TryGetValue(expectedMessage, out assembly) && assembly.Count == assembly.Pieces.Length)
            {
                byte[] complete = new byte[assembly.Length]; int offset = 0;
                foreach (byte[] piece in assembly.Pieces) { Buffer.BlockCopy(piece, 0, complete, offset, piece.Length); offset += piece.Length; }
                incoming.Enqueue(new AppMessage { Bytes = complete, Reliable = true });
                assemblies.Remove(expectedMessage); expectedMessage++;
            }
        }

        // Diagnostics for packets dropped during the handshake (the first few of each kind are logged).
        public static Action<string> Trace;
        private int notes;
        private void Note(string message) { if (notes++ < 8 && Trace != null) Trace(message); }

        private void HandlePacket(int length, EndPoint from)
        {
            if (length < 7 || length > Mtu) { Note("packet ignored: length " + length); return; }
            using (MemoryStream stream = new MemoryStream(receiveBuffer, 0, length, false))
            {
                BinaryReader reader = new BinaryReader(stream);
                ushort magic = reader.ReadUInt16();
                if (magic != Magic) { Note("packet ignored: magic 0x" + magic.ToString("X4") + " (" + length + " bytes)"); return; }
                byte type = reader.ReadByte(); uint id = reader.ReadUInt32();
                if (type == Request)
                {
                    if (length != 13 || !isHost) { Note("connect request ignored: length " + length + ", isHost " + isHost + ", state " + state); return; }
                    ushort version = reader.ReadUInt16(); uint clientNonce = reader.ReadUInt32();
                    if (clientNonce == 0 || id != clientNonce) { Note("connect request ignored: bad nonce"); return; }
                    if (state == PeerState.Connected)
                    {
                        if (Object.Equals(remote, from) && nonce == clientNonce && version == ProtocolVersion)
                        {
                            lastReceive = NowMs; RecordTraffic(recvTraffic, length, false); SendAccept();
                        }
                        else if (Object.Equals(remote, from))
                        {
                            // T-030: our partner started a NEW connection from the same address (a new nonce): its side of
                            // this session is gone (its link timed out while ours still heard it). End the stale session now
                            // instead of refusing the partner until our own timeout (10 s); the app listens again and the
                            // partner's next request is accepted.
                            RecordTraffic(recvTraffic, length, false);
                            Note("partner reconnected from " + from + "; ending the stale session");
                            BecomeDisconnected("partner reconnected");
                        }
                        else SendPacket(StringPacket(Reject, clientNonce, "full"), from);
                        return;
                    }
                    if (state != PeerState.Listening && state != PeerState.Disconnected) { Note("connect request ignored: state " + state); return; }
                    // After ending a stale session for a reconnecting partner, accept again only once the app has handled
                    // that disconnect and listens again (a hub drops this peer's route on the Disconnected event).
                    if (state == PeerState.Disconnected && disconnectReason == "partner reconnected") return;
                    if (version != ProtocolVersion)
                    {
                        SendPacket(StringPacket(Reject, clientNonce, String.Format("protocol version mismatch: host {0}, guest {1}", ProtocolVersion, version)), from);
                        return;
                    }
                    ClearSession(); remote = from; nonce = clientNonce; session = NewId();
                    RecordTraffic(recvTraffic, length, false); BecomeConnected(); SendAccept();
                    Note("connect request accepted; accept sent to " + from);
                    return;
                }
                if (!Object.Equals(remote, from)) return;
                if (type == Accept)
                {
                    if (isHost || length != 11 || id != nonce) return;
                    uint acceptedSession = reader.ReadUInt32();
                    if (acceptedSession == 0) return;
                    if (state == PeerState.Connecting)
                    {
                        session = acceptedSession; stats.RttMs = (float)(NowMs - lastRequest);
                        RecordTraffic(recvTraffic, length, false); BecomeConnected();
                    }
                    else if (state == PeerState.Connected && acceptedSession == session)
                    {
                        lastReceive = NowMs; RecordTraffic(recvTraffic, length, false);
                    }
                    // A repeated accept must never reset sequences, queues, or events.
                    return;
                }
                if (type == Reject)
                {
                    if (state != PeerState.Connecting || id != nonce) return;
                    string reason = ReadReason(reader, stream); RecordTraffic(recvTraffic, length, false);
                    BecomeDisconnected(reason); return;
                }
                if (state != PeerState.Connected || id != session) return;
                if (type == Goodbye)
                {
                    string reason = ReadReason(reader, stream); lastReceive = NowMs;
                    RecordTraffic(recvTraffic, length, false); BecomeDisconnected(reason); return;
                }
                if (type != Data || length < DataHeader) return;
                ushort sequence = reader.ReadUInt16(), ack = reader.ReadUInt16(); uint bits = reader.ReadUInt32();
                reader.ReadUInt32(); // Remote timestamp is informational; RTT uses our local send table.
                List<WireMessage> messages = ReadMessages(reader, stream);
                lastReceive = NowMs; RecordTraffic(recvTraffic, length, false);
                Acknowledge(ack);
                for (int i = 0; i < 32; i++) if ((bits & (1U << i)) != 0) Acknowledge(unchecked((ushort)(ack - 1 - i)));
                bool fresh = ReceiveSequence(sequence);
                if (messages.Count != 0) { ackDirty = true; receivedSinceAck++; }
                if (fresh) foreach (WireMessage m in messages) Deliver(m);
                // Capture ack history during bursts before its 32-bit window rolls over.
                if (receivedSinceAck >= 8) SendAck();
            }
        }

        private void UpdateStats()
        {
            double now = NowMs;
            while (sendTraffic.Count != 0 && now - sendTraffic.Peek().Time >= 1000) sendBytes -= sendTraffic.Dequeue().Bytes;
            while (recvTraffic.Count != 0 && now - recvTraffic.Peek().Time >= 1000) recvBytes -= recvTraffic.Dequeue().Bytes;
            stats.SendPps = sendTraffic.Count; stats.RecvPps = recvTraffic.Count;
            stats.SendKBps = sendBytes / 1024f; stats.RecvKBps = recvBytes / 1024f;
            stats.LastReceiveAgeMs = now - lastReceive;
            int eligible = 0, lost = 0;
            double age = Math.Max(60, 2 * stats.RttMs);
            for (int i = 0; i < historyCount && eligible < 128; i++)
            {
                SentPacket item = history[(historyPosition - 1 - i + HistorySize) % HistorySize];
                if (now - item.Time < age) continue;
                eligible++; if (!item.Acked) lost++;
            }
            stats.LossPercent = eligible == 0 ? 0 : 100f * lost / eligible;
        }

        public void Poll()
        {
            if (polling) throw new InvalidOperationException("Poll is not reentrant");
            polling = true;
            try
            {
                ReleaseDelayed();
                EndPoint from; int length;
                while ((length = transport.Receive(receiveBuffer, out from)) > 0)
                {
                    // Truncated or malformed UDP is discarded without touching session state.
                    try { HandlePacket(length, from); }
                    catch (EndOfStreamException) { }
                    catch (InvalidDataException) { }
                }
                if (state == PeerState.Connecting)
                {
                    if (NowMs - connectStart >= 10000) BecomeDisconnected("no answer");
                    else if (NowMs - lastRequest >= 250) SendRequest();
                }
                if (state == PeerState.Connected && NowMs - lastReceive >= TimeoutMs) BecomeDisconnected("timed out");
                Flush(); UpdateStats();
                while (events.Count != 0)
                {
                    PeerEvent item = events.Dequeue();
                    if (item.Connected) { if (Connected != null) Connected(); }
                    else if (Disconnected != null) Disconnected(item.Reason);
                }
            }
            finally { polling = false; }
        }
    }
}


