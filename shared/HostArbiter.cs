using System;
using System.Collections.Generic;

namespace OriCoop.Authority
{
    public sealed class HostArbiter
    {
        readonly Func<int> localSeat;
        readonly Action<int, byte[], bool> sendToSeat;
        readonly Func<double> nowMs;
        readonly AuthorityClient client;
        Arbiter arbiter;

        public HostArbiter(Func<int> localSeat, Action<int, byte[], bool> sendToSeat, Func<double> nowMs)
        {
            this.localSeat = localSeat;
            this.sendToSeat = sendToSeat;
            this.nowMs = nowMs;
            arbiter = new Arbiter("", new ArbiterOptions(), nowMs());
            client = new AuthorityClient(AuthorityMode.HostP2P, localSeat,
                delegate(byte[] message, bool reliable) { FromRemote(localSeat(), message); }, true);
        }

        public IAuthority Local { get { return client; } }

        public void FromRemote(int seat, byte[] csMessage)
        {
            // AuthoritySource is lazy: a guest may have joined before the host first touched it (no PlayerJoined seen).
            if (!arbiter.IsOnline(seat, nowMs())) SeatJoined(seat);
            Route(arbiter.Handle(seat, CsWire.Decode(csMessage), nowMs()));
        }

        public void SeatJoined(int seat) { Route(arbiter.JoinAt(seat, 0, "", nowMs())); }
        public void SeatLeft(int seat) { Route(arbiter.Offline(seat, nowMs())); }

        public void Reset()
        {
            arbiter = new Arbiter("", new ArbiterOptions(), nowMs());
            client.ResetSession();
        }

        void Route(List<Outgoing> output)
        {
            int mine = localSeat();
            foreach (Outgoing item in output)
            {
                bool reliable = item.Message[0] != CsWire.CsState;
                for (int seat = 1; seat <= 4; seat++)
                    if (seat != mine && arbiter.IsOnline(seat, nowMs()) &&
                        (item.To == seat || item.To == 0 || (item.To < 0 && -item.To != seat)))
                        sendToSeat(seat, item.Message, reliable);
                if (item.To == mine || item.To == 0 || (item.To < 0 && -item.To != mine))
                    client.Receive(item.Message);
            }
        }
    }
}
