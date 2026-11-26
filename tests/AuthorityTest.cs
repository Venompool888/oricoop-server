using System;
using System.Collections.Generic;
using OriCoop.Authority;

namespace OriCoop.CServer
{
    public static class AuthorityTest
    {
        sealed class Packet
        {
            public int From, To;
            public byte[] Bytes;
            public bool Reliable;
        }

        sealed class Seen
        {
            public int Resets;
            public readonly List<LeaseChange> Leases = new List<LeaseChange>();
            public readonly List<StateUpdate> States = new List<StateUpdate>();
            public readonly List<CommittedEvent> Events = new List<CommittedEvent>();
            public readonly List<string> Order = new List<string>();

            public Seen(IAuthority client)
            {
                client.Reset += delegate { Resets++; Order.Add("reset"); };
                client.LeaseChanged += delegate(LeaseChange l) { Leases.Add(l); Order.Add("lease"); };
                client.StateReceived += delegate(StateUpdate s) { States.Add(s); Order.Add("state"); };
                client.Committed += delegate(CommittedEvent e) { Events.Add(e); Order.Add("event"); };
            }

            public string CommittedIds()
            {
                string result = "";
                foreach (CommittedEvent e in Events) if (!e.Rejected) result += e.Id + ",";
                return result;
            }
        }

        sealed class Link
        {
            public double Now;
            public readonly HostArbiter Host;
            public readonly IAuthority[] Clients = new IAuthority[4];
            public readonly Seen[] Views = new Seen[4];
            public readonly List<Packet> Queue = new List<Packet>();
            public readonly List<Packet> Sent = new List<Packet>();

            public Link() : this(true) { }

            public Link(bool announceGuests)
            {
                Host = new HostArbiter(delegate { return 1; },
                    delegate(int to, byte[] bytes, bool reliable) { Enqueue(1, to, bytes, reliable); },
                    delegate { return Now; });
                Clients[1] = Host.Local;
                for (int i = 2; i <= 3; i++)
                {
                    int seat = i;
                    Clients[i] = new AuthorityClient(AuthorityMode.HostP2P, delegate { return seat; },
                        delegate(byte[] bytes, bool reliable) { Enqueue(seat, 0, bytes, reliable); }, false);
                }
                for (int i = 1; i <= 3; i++) Views[i] = new Seen(Clients[i]);
                for (int i = 1; i <= 3; i++) if (i == 1 || announceGuests) Host.SeatJoined(i);
                Pump();
            }

            void Enqueue(int from, int to, byte[] bytes, bool reliable)
            {
                Packet p = new Packet { From = from, To = to, Bytes = bytes, Reliable = reliable };
                Queue.Add(p); Sent.Add(p);
            }

            public void Deliver(int index)
            {
                Packet p = Queue[index]; Queue.RemoveAt(index);
                if (p.To == 0) Host.FromRemote(p.From, p.Bytes);
                else ((AuthorityClient)Clients[p.To]).Receive(p.Bytes);
            }

            public void Pump() { while (Queue.Count != 0) Deliver(0); }
        }

        static int total, passed;
        static readonly byte[] Data = new byte[] { 4, 2 };

        static void Check(string name, bool ok)
        {
            total++;
            if (ok) passed++;
            Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        }

        // The host's AuthoritySource is lazy, so it can miss PlayerJoined for a guest that joined earlier.
        static void UnannouncedGuest()
        {
            Link l = new Link(false);
            l.Clients[2].RequestLease("late", 1, true); l.Pump();
            Check("unannounced guest is seated on first request", l.Clients[2].Owns("late") && l.Clients[1].OwnerOf("late") == 2);
            l.Clients[3].Propose(5, "late-claim", Data, true, false); l.Pump();
            Check("unannounced guest proposal commits", l.Views[3].Events.Count == 1 && l.Views[1].Events.Count == 1);
        }

        static void EventsAndClaims()
        {
            Link l = new Link();
            Check("mode and local arbiter identity", l.Clients[1].Mode == AuthorityMode.HostP2P &&
                l.Clients[1].ArbiterIsLocal && !l.Clients[2].ArbiterIsLocal && l.Clients[2].LocalSeat == 2);
            l.Clients[1].Propose(7, "host", Data, false, false);
            Check("host proposal returns through Committed", l.Views[1].Events.Count == 1 &&
                l.Views[1].Events[0].From == 1 && l.Views[1].Events[0].Seq == 1 && l.Views[1].Events[0].Id == 1);
            l.Clients[2].Propose(8, "guest2", Data, false, false);
            l.Clients[3].Propose(9, "guest3", Data, false, false);
            Check("guest proposals wait for arbiter", l.Views[2].Events.Count == 0);
            l.Pump();
            Check("three clients share event order", l.Views[1].CommittedIds() == "1,2,3," &&
                l.Views[2].CommittedIds() == l.Views[1].CommittedIds() &&
                l.Views[3].CommittedIds() == l.Views[1].CommittedIds());

            l.Clients[2].Propose(20, "pickup", Data, true, false);
            l.Clients[3].Propose(21, "pickup", Data, true, false);
            l.Deliver(l.Queue.Count - 1); // Let seat 3's competing request arrive first.
            l.Pump();
            l.Clients[1].Propose(22, "pickup", Data, true, false);
            l.Pump();
            CommittedEvent rejected = l.Views[2].Events[l.Views[2].Events.Count - 1];
            CommittedEvent hostRejected = l.Views[1].Events[l.Views[1].Events.Count - 1];
            Check("simultaneous claim has one winner and private rejections",
                l.Views[3].Events.Count == 4 && l.Views[3].Events[3].From == 3 &&
                rejected.Rejected && rejected.Id == 0 && rejected.Kind == 20 && rejected.Data[0] == 20 &&
                hostRejected.Rejected && hostRejected.Kind == 22 && l.Views[3].Events[3].Kind == 21);
            l.Clients[3].Propose(23, "pickup", Data, false, true);
            l.Pump();
            l.Clients[2].Propose(24, "pickup", Data, true, false);
            l.Pump();
            Check("release permits another claim", l.Views[2].Events[l.Views[2].Events.Count - 1].Kind == 24 &&
                !l.Views[2].Events[l.Views[2].Events.Count - 1].Rejected &&
                l.Views[1].CommittedIds() == "1,2,3,4,5,6," &&
                l.Views[3].CommittedIds() == l.Views[1].CommittedIds());

            l.Clients[1].Committed += delegate(CommittedEvent e)
            {
                if (e.Kind == 30) l.Clients[1].Propose(31, "nested", Data, false, false);
            };
            l.Clients[1].Propose(30, "outer", Data, false, false); l.Pump();
            Check("nested host proposal preserves order for guests", l.Views[1].CommittedIds() == "1,2,3,4,5,6,7,8," &&
                l.Views[2].CommittedIds() == l.Views[1].CommittedIds() && l.Views[3].CommittedIds() == l.Views[1].CommittedIds());
        }

        static void LeasesAndStates()
        {
            Link l = new Link();
            l.Clients[2].RequestLease("block", 5, true); l.Pump();
            bool same = true;
            for (int i = 1; i <= 3; i++)
                same &= l.Clients[i].OwnerOf("block") == 2 && l.Clients[i].EpochOf("block") == 1 &&
                    l.Views[i].Leases.Count == 1 && l.Views[i].Leases[0].Owner == 2;
            Check("guest lease grant agrees on all clients", same && l.Clients[2].Owns("block") && !l.Clients[1].Owns("block"));
            l.Clients[2].PublishState("block", Data, true); l.Pump();
            Check("owner state reaches only other clients", l.Views[1].States.Count == 1 &&
                l.Views[3].States.Count == 1 && l.Views[2].States.Count == 0 &&
                l.Views[1].States[0].From == 2 && l.Views[3].States[0].Epoch == 1 &&
                l.Views[3].States[0].Ver == 1 && l.Views[3].States[0].Active && l.Views[3].States[0].Data[0] == 4);
            l.Clients[2].PublishState("block", Data, false); l.Pump();
            Check("publish version increments", l.Views[1].States[1].Ver == 2 && !l.Views[1].States[1].Active);
            int sent = l.Sent.Count;
            l.Clients[1].PublishState("block", Data, true);
            l.Clients[3].PublishState("block", Data, true); l.Pump();
            Check("non-owner publishes nothing", l.Sent.Count == sent && l.Views[1].States.Count == 2 && l.Views[3].States.Count == 2);
            l.Now = 250;
            l.Clients[3].RequestLease("block", 1, false); l.Pump();
            l.Host.SeatLeft(2); l.Pump();
            Check("departure hands lease to interested guest", l.Clients[1].OwnerOf("block") == 3 &&
                l.Clients[3].Owns("block") && l.Clients[3].EpochOf("block") == 2 &&
                l.Views[1].Leases[1].Owner == 3 && l.Views[3].Leases[1].Epoch == 2);
            l.Clients[3].PublishState("block", new byte[] { 8 }, true); l.Pump();
            Check("new owner continues state version across epoch", l.Views[1].States.Count == 3 &&
                l.Views[1].States[2].Ver == 3 && l.Views[1].States[2].Epoch == 2);
            l.Clients[1].RequestLease("block", 2, false); l.Pump();
            l.Clients[3].ReleaseLease("block"); l.Pump();
            Check("ReleaseLease hands off to interested host", l.Clients[1].Owns("block") && l.Clients[3].OwnerOf("block") == 1);
            l.Clients[1].PublishState("block", Data, true); l.Pump();
            Check("host state excludes host and reaches guest", l.Views[1].States.Count == 3 &&
                l.Views[3].States.Count == 3 && l.Views[3].States[2].From == 1 && l.Views[3].States[2].Ver == 4);
            bool reliability = true;
            foreach (Packet p in l.Sent) reliability &= p.Reliable == (p.Bytes[0] != CsWire.CsState);
            Check("state unreliable and control messages reliable", reliability);
        }

        static void RejoinAndReset()
        {
            Link l = new Link();
            l.Clients[2].RequestLease("snapshot", 1, false); l.Pump();
            l.Clients[2].PublishState("snapshot", Data, false); l.Pump();
            l.Clients[1].Propose(1, "before", Data, false, false); l.Pump();
            l.Host.SeatLeft(2); l.Pump();
            l.Clients[1].RequestLease("snapshot", 1, true); l.Pump();
            l.Clients[1].PublishState("snapshot", new byte[] { 9 }, true); l.Pump();
            l.Clients[1].Propose(2, "missed", Data, false, false); l.Pump();
            int resets = l.Views[2].Resets;
            l.Views[2].Order.Clear();
            l.Host.SeatJoined(2); l.Pump();
            Check("same-seat rejoin resets then restores leases and states", l.Views[2].Resets == resets + 1 &&
                l.Views[2].Order[0] == "reset" && l.Views[2].Order[1] == "lease" && l.Views[2].Order[2] == "state" &&
                l.Clients[2].OwnerOf("snapshot") == 1 && l.Views[2].States[l.Views[2].States.Count - 1].Data[0] == 9);
            Check("rejoin replays missed events once", l.Views[2].CommittedIds() == "1,2," &&
                l.Views[2].Order[3] == "event");
            l.Host.SeatJoined(2); l.Pump();
            Check("snapshot preserves last seen event id", l.Views[2].CommittedIds() == "1,2,");
            l.Clients[2].Propose(3, "after", Data, false, false); l.Pump();
            l.Host.SeatJoined(2); l.Pump();
            l.Clients[2].Propose(3, "after-again", Data, false, false); l.Pump();
            Check("snapshot preserves proposal sequence", l.Views[1].Events.Count == 4 &&
                l.Views[1].Events[3].Seq == 2 && l.Views[1].Events[3].From == 2);
            IAuthority local = l.Host.Local;
            l.Host.Reset();
            ((AuthorityClient)l.Clients[2]).ResetSession();
            ((AuthorityClient)l.Clients[3]).ResetSession();
            Check("session reset clears leases and keeps Local instance", Object.ReferenceEquals(local, l.Host.Local) &&
                local.OwnerOf("snapshot") == 0 && local.EpochOf("snapshot") == 0 && !local.Owns("snapshot"));
            for (int i = 1; i <= 3; i++) l.Host.SeatJoined(i);
            l.Pump();
            l.Clients[2].RequestLease("snapshot", 1, false); l.Pump();
            l.Clients[2].PublishState("snapshot", Data, false); l.Pump();
            l.Clients[2].Propose(4, "new-session", Data, false, false); l.Pump();
            StateUpdate state = l.Views[1].States[l.Views[1].States.Count - 1];
            CommittedEvent e = l.Views[2].Events[l.Views[2].Events.Count - 1];
            Check("new session restarts seq version and event id", e.Id == 1 && e.Seq == 1 && state.Ver == 1 && state.Epoch == 1);
        }

        static void ClientProtocol()
        {
            List<Packet> sent = new List<Packet>();
            int seat = 4;
            AuthorityClient c = new AuthorityClient(AuthorityMode.Server, delegate { return seat; },
                delegate(byte[] bytes, bool reliable) { sent.Add(new Packet { Bytes = bytes, Reliable = reliable }); }, false);
            Seen seen = new Seen(c);
            c.Receive(CsWire.Encode(new CsMessage { Type = CsWire.CsWelcome, AssignedSeat = 4, LastEvent = 100 }));
            c.Receive(CsWire.Encode(new CsMessage { Type = CsWire.CsRoster }));
            c.Receive(CsWire.Encode(new CsMessage { Type = CsWire.CsError, ErrorCode = 1, Text = "test" }));
            c.Receive(CsWire.Encode(new CsMessage { Type = CsWire.CsEvent, Id = 2, Kind = 7, From = 3 }));
            c.Receive(CsWire.Encode(new CsMessage { Type = CsWire.CsEvent, Id = 2, Kind = 7 }));
            c.Receive(CsWire.Encode(new CsMessage { Type = CsWire.CsEvent, Id = 1, Kind = 7 }));
            c.Receive(CsWire.Encode(new CsMessage { Type = CsWire.CsEvent, Kind = 0xFF, Data = new byte[] { 8 }, From = 4 }));
            Check("remote client decodes control and drops duplicate or older events", c.Mode == AuthorityMode.Server &&
                !c.ArbiterIsLocal && seen.Events.Count == 2 && seen.Events[0].Id == 2 && seen.Events[1].Rejected && seen.Events[1].Kind == 8);
            c.RequestLease("wire", 2.5f, true); c.ReleaseLease("wire");
            c.Propose(5, "wire", Data, true, true);
            CsMessage request = CsWire.Decode(sent[0].Bytes);
            CsMessage release = CsWire.Decode(sent[1].Bytes);
            CsMessage action = CsWire.Decode(sent[2].Bytes);
            Check("request release and proposal wire fields", request.Type == CsWire.CsLease && request.Seat == 4 &&
                request.Score == 2.5f && request.Active == 1 && request.Op == 1 && release.Op == 2 &&
                action.Seq == 1 && action.Flags == (Arbiter.FlagClaim | Arbiter.FlagRelease));
            c.Receive(CsWire.Encode(new CsMessage { Type = CsWire.CsGrant, Key = "old", Owner = 4, Epoch = 9 }));
            c.Receive(CsWire.Encode(new CsMessage { Type = CsWire.CsState, Seat = 1, Key = "old", Epoch = 9, Ver = 1 }));
            Check("forwarded state identifies lease owner despite host header", seen.States[0].From == 4);
            CsMessage snapshot = new CsMessage { Type = CsWire.CsSnapshot };
            snapshot.Leases.Add(new CsLeaseEntry { Key = "wire", Owner = 4, Epoch = 3 });
            snapshot.States.Add(new CsStateEntry { Key = "wire", Epoch = 3, Ver = 12, Data = Data });
            c.Receive(CsWire.Encode(snapshot));
            c.PublishState("wire", Data, true);
            CsMessage state = CsWire.Decode(sent[3].Bytes);
            Check("snapshot replaces leases and seeds publication version", c.OwnerOf("old") == 0 &&
                c.Owns("wire") && state.Ver == 13 && state.Epoch == 3 && !sent[3].Reliable);
            c.ResetSession(); seat = 2; c.Propose(6, "reset", Data, false, false);
            Check("reset uses current local seat and restarts sequence", CsWire.Decode(sent[4].Bytes).Seat == 2 &&
                CsWire.Decode(sent[4].Bytes).Seq == 1 && c.OwnerOf("wire") == 0);
        }

        static void OfflineAndSeatIdentity()
        {
            AuthorityClient single = new AuthorityClient(AuthorityMode.None, delegate { return 1; },
                delegate { throw new Exception("offline send"); }, false);
            Seen seen = new Seen(single);
            single.Propose(2, "offline", Data, true, false);
            Check("offline owns all keys and commits immediately", single.Owns("any") && single.OwnerOf("any") == 1 &&
                seen.Events.Count == 1 && seen.Events[0].Kind == 2 && !seen.Events[0].Rejected);
            List<Packet> sent = new List<Packet>();
            HostArbiter host = new HostArbiter(delegate { return 3; },
                delegate(int to, byte[] bytes, bool reliable) { sent.Add(new Packet { To = to, Bytes = bytes }); },
                delegate { return 0; });
            host.SeatJoined(3); host.SeatJoined(1);
            host.Local.RequestLease("seat3", 0, false); sent.Clear();
            host.Local.PublishState("seat3", Data, true);
            Check("host may occupy a non-one seat", host.Local.Owns("seat3") && sent.Count == 1 && sent[0].To == 1 &&
                CsWire.Decode(sent[0].Bytes).Seat == 3);
        }

        static void RoomInfoClearsWhenEmpty()
        {
            Arbiter room = new Arbiter("234567", new ArbiterOptions(), 0);
            room.JoinAt(1, 1, "one", 0);
            room.JoinAt(2, 2, "two", 0);
            room.Handle(1, new CsMessage { Type = CsWire.CsRoomInfo, Text = "old" }, 1);
            room.Offline(1, 2);
            room.Handle(2, new CsMessage { Type = CsWire.CsRoomInfo, Text = "next" }, 3);
            CsRoomListEntry listing = room.Listing();
            Check("room_info_manager_follows_online_not_held_seats", listing.Online == 1 && listing.Text == "next"
                && listing.Nicks.Count == 1 && listing.Nicks[0] == "two");
            room.Offline(2, 4);
            room.JoinAt(2, 2, "two", 5);
            Check("room_info_cleared_when_last_device_offline", room.Listing().Text == "");
            room.Handle(2, new CsMessage { Type = CsWire.CsRoomInfo, Text = "again" }, 6);
            room.Handle(2, new CsMessage { Type = CsWire.CsSeatRelease, Target = 0 }, 7);
            Check("room_info_cleared_when_last_device_releases", room.Listing().Online == 0 && room.Listing().Text == "");
        }

        static List<uint> ReplayedEvents(List<Outgoing> packets)
        {
            List<uint> events = new List<uint>();
            foreach (Outgoing p in packets)
                if (p.Message[0] == CsWire.CsEvent) events.Add(CsWire.Decode(p.Message).Id);
            return events;
        }

        static void WorldHistoryOnJoin()
        {
            Arbiter room = new Arbiter("", new ArbiterOptions(), 0);
            int seat; byte[] welcome;
            var join = new CsMessage { Type = CsWire.CsJoin };
            room.Join(join, 0, out seat, out welcome);
            for (uint seq = 1; seq <= 5; seq++)
                room.Handle(1, new CsMessage { Type = CsWire.CsAction, Seq = seq,
                    Kind = seq == 2 ? (byte)1 : Arbiter.WorldBatchKind, Key = "w", Data = Data }, 0);
            room.Handle(1, new CsMessage { Type = CsWire.CsAction, Seq = 6, Kind = Arbiter.WorldWatermarkKind,
                Key = "w:applied", Data = BitConverter.GetBytes(4U) }, 0);
            List<uint> fresh = ReplayedEvents(room.Join(join, 0, out seat, out welcome));
            Check("fresh_device_replays_non_world_and_uncovered_world_events", fresh.Count == 2 && fresh[0] == 2 && fresh[1] == 5);
            var token = CsWire.Decode(welcome).Token;
            List<uint> mixed = ReplayedEvents(room.Join(join, 0, out seat, out welcome));
            Check("device_with_no_receipt_keeps_history_for_later_joiners", mixed.Count == 5);
            room.Offline(2, 0);
            room.Handle(1, new CsMessage { Type = CsWire.CsAction, Seq = 7,
                Kind = Arbiter.WorldBatchKind, Key = "w", Data = Data }, 0);
            room.Handle(1, new CsMessage { Type = CsWire.CsAction, Seq = 8, Kind = Arbiter.WorldWatermarkKind,
                Key = "w:applied", Data = BitConverter.GetBytes(6U) }, 0);
            var resume = new CsMessage { Type = CsWire.CsJoin, Mode = 2, Token = token };
            List<uint> missed = ReplayedEvents(room.Join(resume, 0, out seat, out welcome));
            Check("returning_device_replays_its_missed_world_event", missed.Count == 1 && missed[0] == 6);
        }

        public static int Main()
        {
            try
            {
                UnannouncedGuest(); EventsAndClaims(); LeasesAndStates(); RejoinAndReset(); ClientProtocol(); OfflineAndSeatIdentity(); RoomInfoClearsWhenEmpty(); WorldHistoryOnJoin();
            }
            catch (Exception e) { Check("unexpected exception: " + e, false); }
            Console.WriteLine("RESULT " + (passed == total ? "PASS " : "FAIL ") + passed + "/" + total);
            return passed == total ? 0 : 1;
        }
    }
}
