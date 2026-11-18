using System;
using System.IO;
using System.Collections.Generic;
using System.Net;
using OriCoop.Net;

namespace OriCoop.Authority
{
    public sealed class CServerClient : INetRouteServer
    {
        readonly UdpTransport transport;
        readonly List<int> seats = new List<int>();
        bool[] online = new bool[5];

        public CServerClient(EndPoint server, byte mode, string code, byte[] token, ulong steam, string nick)
            : this(server, mode, code, token, 1, new CsIdentity[] { new CsIdentity { Steam = steam, Nick = nick } }) { }

        public CServerClient(EndPoint server, byte mode, string code, byte[] token, int count, CsIdentity[] identities)
        {
            if (count < 1 || count > 3 || identities == null || identities.Length != count)
                throw new ArgumentException("join needs 1-3 per-seat identities");
            CsMessage request = new CsMessage { Type = CsWire.CsJoin, Mode = mode,
                Code = code, Token = token ?? new byte[16], Count = (byte)count };
            request.Identities.AddRange(identities);
            byte[] join = CsWire.Encode(request);
            Code = "";
            Token = new byte[16];
            transport = new UdpTransport(0);
            Peer = new NetPeer(transport);
            Authority = new AuthorityClient(AuthorityMode.Server, delegate { return PrimarySeat; },
                delegate(byte[] message, bool reliable) { SendAs(PrimarySeat, message, reliable); }, false);
            Peer.Connected += delegate { Send(join, true); };
            Peer.Connect(server);
        }

        public NetPeer Peer { get; private set; }
        public IList<int> Seats { get { return seats.AsReadOnly(); } }
        public int PrimarySeat { get { return Arbiter.PrimarySeat(seats); } }
        public int Seat { get { return PrimarySeat; } }   // single-seat compatibility shim
        public string Code { get; private set; }
        public byte[] Token { get; private set; }
        public AuthorityClient Authority { get; private set; }
        public int ArbiterSeat { get; private set; }
        public bool IsArbiter { get { return PrimarySeat != 0 && ArbiterSeat == PrimarySeat; } }

        public event Action<int> SeatAssigned;
        public event Action<string> JoinFailed;
        public event Action<int> SeatOnline;
        public event Action<int> SeatOffline;
        public event Action<int> ArbiterChanged;

        void Send(byte[] message, bool reliable)
        {
            if (reliable) Peer.SendReliable(message, 0, message.Length);
            else Peer.SendUnreliable(message, 0, message.Length);
        }

        public void Receive(byte[] csMessage)
        {
            CsMessage message = CsWire.Decode(csMessage);
            switch (message.Type)
            {
                case CsWire.CsWelcome:
                    if (message.Result == 0)
                    {
                        seats.Clear(); seats.AddRange(message.Seats);
                        Code = message.Code;
                        Token = message.Token;
                        if (SeatAssigned != null) SeatAssigned(Seat);
                    }
                    else if (JoinFailed != null)
                    {
                        // L10n keys (the Unity glue shows L10n.T(key)); this file stays free of Unity code.
                        string[] reasons = { "", "server.err.noroom", "server.err.full", "server.err.version", "server.err.token" };
                        JoinFailed(reasons[message.Result]);
                    }
                    break;
                case CsWire.CsRoster:
                    bool[] previous = online;
                    online = new bool[5];
                    foreach (CsRosterEntry entry in message.Roster) online[entry.Seat] = entry.Online;
                    int oldArbiter = ArbiterSeat;
                    ArbiterSeat = 0;
                    for (int seat = 1; seat <= 4; seat++)
                        if (online[seat]) { ArbiterSeat = seat; break; }
                    for (int seat = 1; seat <= 4; seat++)
                    {
                        if (online[seat] && !previous[seat] && SeatOnline != null) SeatOnline(seat);
                        if (!online[seat] && previous[seat] && SeatOffline != null) SeatOffline(seat);
                    }
                    if (oldArbiter != ArbiterSeat && ArbiterChanged != null) ArbiterChanged(ArbiterSeat);
                    break;
                case CsWire.CsError:
                    if (JoinFailed != null) JoinFailed(message.Text);
                    break;
                default:
                    Authority.Receive(csMessage);
                    break;
            }
        }

        void RequireLocal(int seat)
        {
            if (!seats.Contains(seat)) throw new ArgumentException("sender seat is not local", "seat");
        }

        static byte[] GameMessage(int seat, byte type, Action<BinaryWriter> body)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                BinaryWriter writer = new BinaryWriter(stream);
                writer.Write(type); writer.Write((byte)seat);
                if (body != null) body(writer);
                return stream.ToArray();
            }
        }

        public void Send(byte type, bool reliable, Action<BinaryWriter> body)
        {
            SendAs(PrimarySeat, type, reliable, body);
        }

        public void SendAs(int seat, byte type, bool reliable, Action<BinaryWriter> body)
        {
            RequireLocal(seat);
            Send(GameMessage(seat, type, body), reliable);
        }

        public void SendAs(int seat, byte[] message, bool reliable)
        {
            RequireLocal(seat);
            if (message == null || message.Length < 2) throw new ArgumentException("message needs type and seat");
            byte[] stamped = (byte[])message.Clone(); stamped[1] = (byte)seat;
            Send(stamped, reliable);
        }

        public void SendTo(int seat, byte type, bool reliable, Action<BinaryWriter> body)
        {
            SendToAs(PrimarySeat, seat, type, reliable, body);
        }

        public void SendToAs(int sender, int target, byte type, bool reliable, Action<BinaryWriter> body)
        {
            RequireLocal(sender);
            Send(CsWire.Encode(new CsMessage { Type = CsWire.CsTo, Seat = (byte)sender,
                Target = (byte)target, Inner = GameMessage(sender, type, body) }), reliable);
        }

        public void AddSeat(ulong steam, string nick)
        {
            SendAs(PrimarySeat, CsWire.Encode(new CsMessage { Type = CsWire.CsSeatAdd, Steam = steam, Nick = nick }), true);
        }

        public void ReleaseSeat(int seat)
        {
            RequireLocal(seat);
            SendAs(PrimarySeat, CsWire.Encode(new CsMessage { Type = CsWire.CsSeatRelease, Target = (byte)seat }), true);
        }

        public void Leave()
        {
            Peer.Disconnect("leave");
            transport.Dispose();
        }
    }
}
