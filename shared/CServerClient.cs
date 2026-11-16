using System;
using System.IO;
using System.Net;
using OriCoop.Net;

namespace OriCoop.Authority
{
    public sealed class CServerClient : INetRouteServer
    {
        readonly UdpTransport transport;
        bool[] online = new bool[5];

        public CServerClient(EndPoint server, byte mode, string code, byte[] token, ulong steam, string nick)
        {
            byte[] join = CsWire.Encode(new CsMessage { Type = CsWire.CsJoin, Mode = mode,
                Code = code, Token = token ?? new byte[16], Steam = steam, Nick = nick });
            Code = "";
            Token = new byte[16];
            transport = new UdpTransport(0);
            Peer = new NetPeer(transport);
            Authority = new AuthorityClient(AuthorityMode.Server, delegate { return Seat; },
                delegate(byte[] message, bool reliable)
                {
                    message[1] = (byte)Seat;
                    Send(message, reliable);
                }, false);
            Peer.Connected += delegate { Send(join, true); };
            Peer.Connect(server);
        }

        public NetPeer Peer { get; private set; }
        public int Seat { get; private set; }
        public string Code { get; private set; }
        public byte[] Token { get; private set; }
        public AuthorityClient Authority { get; private set; }
        public int ArbiterSeat { get; private set; }
        public bool IsArbiter { get { return Seat != 0 && ArbiterSeat == Seat; } }

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
                        Seat = message.AssignedSeat;
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

        public void SendTo(int seat, byte type, bool reliable, Action<BinaryWriter> body)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                BinaryWriter writer = new BinaryWriter(stream);
                writer.Write(type);
                writer.Write((byte)Seat);
                if (body != null) body(writer);
                Send(CsWire.Encode(new CsMessage { Type = CsWire.CsTo, Seat = (byte)Seat,
                    Target = (byte)seat, Inner = stream.ToArray() }), reliable);
            }
        }

        public void Leave()
        {
            Peer.Disconnect("leave");
            transport.Dispose();
        }
    }
}
