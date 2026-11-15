using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading;
using OriCoop.Net;
using OriCoop.Authority;

namespace OriCoop.CServer
{
    public static class CServerTest
    {
        sealed class Received
        {
            public byte[] Bytes;
            public bool Reliable;
            public CsMessage Message;
        }

        sealed class Client : IDisposable
        {
            public readonly UdpTransport Socket = new UdpTransport(0);
            public readonly NetPeer Peer;
            public readonly List<Received> Inbox = new List<Received>();
            public CsMessage Welcome;

            public Client(int port)
            {
                Peer = new NetPeer(Socket);
                Peer.Connect(new IPEndPoint(IPAddress.Loopback, port));
            }

            public void Send(CsMessage message) { Send(CsWire.Encode(message), true); }
            public void Send(byte[] bytes, bool reliable)
            {
                if (reliable) Peer.SendReliable(bytes, 0, bytes.Length);
                else Peer.SendUnreliable(bytes, 0, bytes.Length);
            }

            public void Poll()
            {
                Peer.Poll();
                byte[] bytes; bool reliable;
                while (Peer.TryReceive(out bytes, out reliable))
                {
                    CsMessage message = bytes[0] >= CsWire.CsJoin && bytes[0] <= CsWire.CsTo
                        ? CsWire.Decode(bytes) : null;
                    Inbox.Add(new Received { Bytes = bytes, Reliable = reliable, Message = message });
                    if (message != null && message.Type == CsWire.CsWelcome) Welcome = message;
                }
                Peer.Flush();
            }

            public int Count(byte type)
            {
                int n = 0;
                foreach (Received item in Inbox) if (item.Bytes[0] == type) n++;
                return n;
            }

            public CsMessage Last(byte type)
            {
                for (int i = Inbox.Count - 1; i >= 0; i--)
                    if (Inbox[i].Bytes[0] == type) return Inbox[i].Message;
                return null;
            }

            public void Dispose() { Socket.Dispose(); }
        }

        static readonly List<Client> clients = new List<Client>();
        static readonly Stopwatch clock = Stopwatch.StartNew();
        static CServer server;
        static int passed, total;

        static void Check(string name, bool ok)
        {
            total++;
            if (ok) passed++;
            Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        }

        static void Pump()
        {
            if (clock.Elapsed.TotalSeconds >= 27) throw new Exception("test deadline");
            server.Step();
            foreach (Client c in clients) c.Poll();
            server.Step();
            Thread.Sleep(1);
        }

        static bool Until(Func<bool> condition, int timeoutMs)
        {
            double end = clock.Elapsed.TotalMilliseconds + timeoutMs;
            while (!condition() && clock.Elapsed.TotalMilliseconds < end) Pump();
            return condition();
        }

        static void RunFor(int ms)
        {
            double end = clock.Elapsed.TotalMilliseconds + ms;
            while (clock.Elapsed.TotalMilliseconds < end) Pump();
        }

        static Client Connect()
        {
            Client c = new Client(server.LocalPort);
            clients.Add(c);
            if (!Until(delegate { return c.Peer.State == PeerState.Connected; }, 1200))
                throw new Exception("handshake timeout");
            return c;
        }

        static Client Join(byte mode, string code, byte[] token)
        {
            Client c = Connect();
            c.Send(new CsMessage { Type = CsWire.CsJoin, Proto = 1, Mode = mode,
                Code = code, Token = token ?? new byte[16], Steam = (ulong)clients.Count,
                Nick = "player" + clients.Count });
            if (!Until(delegate { return c.Welcome != null; }, 1200))
                throw new Exception("welcome timeout");
            RunFor(25);
            return c;
        }

        static void Clear() { foreach (Client c in clients) c.Inbox.Clear(); }
        static CsMessage Lease(string key, byte op, float score)
        {
            return new CsMessage { Type = CsWire.CsLease, Key = key, Op = op, Score = score };
        }
        static CsMessage State(string key, uint epoch, uint ver, byte active, byte value)
        {
            return new CsMessage { Type = CsWire.CsState, Key = key, Epoch = epoch,
                Ver = ver, Active = active, Data = new byte[] { value } };
        }
        static CsMessage Action(uint seq, byte kind, byte flags, string key)
        {
            return new CsMessage { Type = CsWire.CsAction, Seq = seq, Kind = kind,
                Flags = flags, Key = key, Data = new byte[] { 7 } };
        }
        static bool Grant(Client c, string key, byte owner, uint epoch)
        {
            foreach (Received r in c.Inbox)
                if (r.Message != null && r.Message.Type == CsWire.CsGrant &&
                    r.Message.Key == key && r.Message.Owner == owner && r.Message.Epoch == epoch) return true;
            return false;
        }
        static bool Online(Client c, byte seat, bool online, int count)
        {
            CsMessage roster = c.Last(CsWire.CsRoster);
            if (roster == null || roster.Roster.Count != count) return false;
            foreach (CsRosterEntry entry in roster.Roster)
                if (entry.Seat == seat) return entry.Online == online && entry.Nick.Length > 0 && entry.Steam != 0;
            return false;
        }
        static bool HasSnapshot(Client c, string key, uint epoch, uint ver, byte value)
        {
            CsMessage snapshot = c.Last(CsWire.CsSnapshot);
            if (snapshot == null) return false;
            bool lease = false, state = false;
            foreach (CsLeaseEntry l in snapshot.Leases)
                if (l.Key == key && l.Epoch == epoch) lease = true;
            foreach (CsStateEntry s in snapshot.States)
                if (s.Key == key && s.Ver == ver && s.Data.Length == 1 && s.Data[0] == value) state = true;
            return lease && state;
        }

        static void UdpTests()
        {
            Client a = Join(0, "", null);
            string code = a.Welcome.Code;
            Client b = Join(1, code, null), c = Join(1, code, null);
            Check("a_join_seats_roster", a.Welcome.Result == 0 && a.Welcome.AssignedSeat == 1 &&
                b.Welcome.AssignedSeat == 2 && c.Welcome.AssignedSeat == 3 && code.Length == 6 &&
                a.Welcome.Token.Length == 16 && Online(a, 3, true, 3) && Online(b, 1, true, 3) && Online(c, 2, true, 3));
            Client d = Join(1, code, null), fifth = Join(1, code, null);
            Check("b_fourth_and_full", d.Welcome.Result == 0 && d.Welcome.AssignedSeat == 4 && fifth.Welcome.Result == 2);
            Client bad = Join(1, "000000", null);
            Check("c_no_such_room", bad.Welcome.Result == 1);

            Clear();
            a.Send(new byte[] { 1, 4, 90 }, false);
            bool forwarded = Until(delegate { return b.Count(1) == 1 && c.Count(1) == 1; }, 700);
            RunFor(30);
            Check("d_rewrite_seat_unreliable_no_echo", forwarded && a.Count(1) == 0 &&
                b.Inbox[0].Bytes[1] == 1 && c.Inbox[0].Bytes[1] == 1 && !b.Inbox[0].Reliable);
            Clear();
            a.Send(new CsMessage { Type = CsWire.CsTo, Target = 3, Inner = new byte[] { 2, 4, 42 } });
            bool targeted = Until(delegate { return c.Count(2) == 1; }, 700);
            RunFor(30);
            Check("e_target_only_reliable", targeted && a.Count(2) == 0 && b.Count(2) == 0 &&
                d.Count(2) == 0 && c.Inbox[0].Bytes[1] == 1 && c.Inbox[0].Reliable);

            Clear();
            a.Send(Lease("K", 1, 10));
            bool firstGrant = Until(delegate { return Grant(c, "K", 1, 1); }, 700);
            uint version = 0;
            double activeEnd = clock.Elapsed.TotalMilliseconds + 560;
            while (clock.Elapsed.TotalMilliseconds < activeEnd)
            {
                a.Send(State("K", 1, ++version, 1, 11));
                RunFor(35);
            }
            Clear();
            a.Send(State("K", 1, ++version, 1, 12));
            b.Send(Lease("K", 1, 5));
            bool stateForwarded = Until(delegate { return b.Count(CsWire.CsState) > 0 && c.Count(CsWire.CsState) > 0; }, 700);
            RunFor(30);
            long drops = server.DroppedStates;
            b.Send(State("K", 1, version + 1, 1, 99));
            a.Send(State("K", 1, version, 1, 98));
            RunFor(40);
            Check("f_active_owner_filter_and_version", firstGrant && stateForwarded &&
                b.Count(CsWire.CsGrant) == 0 && a.Count(CsWire.CsState) == 0 &&
                c.Count(CsWire.CsState) == 1 && server.DroppedStates == drops + 2);

            RunFor(330); Clear();
            b.Send(Lease("K", 1, 5));
            bool moved = Until(delegate { return Grant(a, "K", 2, 2); }, 700);
            drops = server.DroppedStates;
            a.Send(State("K", 1, version + 100, 1, 98));
            b.Send(State("K", 2, ++version, 1, 22));
            bool newState = Until(delegate { return c.Count(CsWire.CsState) == 1; }, 700);
            Check("g_idle_transfer_old_epoch", moved && newState && server.DroppedStates == drops + 1 &&
                c.Last(CsWire.CsState).Epoch == 2 && c.Last(CsWire.CsState).Data[0] == 22);

            Clear(); a.Send(Action(1, 9, 1, "claim")); b.Send(Action(1, 9, 1, "claim"));
            bool claims = Until(delegate { return a.Count(CsWire.CsEvent) + b.Count(CsWire.CsEvent) == 3; }, 700);
            RunFor(30);
            int accepted = 0, rejected = 0;
            foreach (Client who in new Client[] { a, b })
                foreach (Received r in who.Inbox)
                    if (r.Message != null && r.Message.Type == CsWire.CsEvent)
                    {
                        if (r.Message.Kind == 0xFF && r.Message.Data.Length == 1 && r.Message.Data[0] == 9) rejected++;
                        else if (r.Message.Kind == 9) accepted++;
                    }
            Check("h_claim_first_only_private_rejection", claims && accepted == 2 && rejected == 1 &&
                c.Count(CsWire.CsEvent) == 1 && d.Count(CsWire.CsEvent) == 1);
            Clear(); a.Send(Action(2, 10, 0, "normal")); a.Send(Action(2, 10, 0, "normal"));
            bool oneEvent = Until(delegate { return c.Count(CsWire.CsEvent) == 1; }, 700);
            RunFor(30);
            Check("i_action_dedup", oneEvent && a.Count(CsWire.CsEvent) == 1 && b.Count(CsWire.CsEvent) == 1 &&
                c.Last(CsWire.CsEvent).Id == 2);

            c.Send(Lease("K", 1, 1)); RunFor(25); Clear();
            byte[] bToken = b.Welcome.Token;
            b.Peer.Disconnect("test");
            bool failover = Until(delegate { return Grant(c, "K", 3, 3) && Online(a, 2, false, 4); }, 700);
            a.Send(Action(3, 12, 0, "during-offline"));
            Until(delegate { return c.Count(CsWire.CsEvent) == 1; }, 700);
            Client b2 = Join(2, code, bToken);
            int snapIndex = -1, eventIndex = -1;
            for (int i = 0; i < b2.Inbox.Count; i++)
            {
                if (b2.Inbox[i].Bytes[0] == CsWire.CsSnapshot) snapIndex = i;
                if (b2.Inbox[i].Bytes[0] == CsWire.CsEvent) eventIndex = i;
            }
            Check("j_offline_handoff_roster_token_replay", failover && b2.Welcome.Result == 0 &&
                b2.Welcome.AssignedSeat == 2 && HasSnapshot(b2, "K", 3, version, 22) &&
                b2.Count(CsWire.CsEvent) == 1 && b2.Last(CsWire.CsEvent).Key == "during-offline" &&
                snapIndex >= 0 && eventIndex > snapIndex && Online(a, 2, true, 4));

            b2.Send(Lease("takeover", 1, 10));
            Until(delegate { return Grant(c, "takeover", 2, 1); }, 700);
            c.Send(Lease("takeover", 1, 1)); RunFor(25); Clear();
            bool bWasConnected = b2.Peer.State == PeerState.Connected;
            Client b3 = Join(2, code, bToken);
            bool replaced = Until(delegate { return b2.Peer.State == PeerState.Disconnected; }, 700);
            RunFor(40);
            bool offlineRoster = false;
            foreach (Received r in a.Inbox)
                if (r.Message != null && r.Message.Type == CsWire.CsRoster)
                    foreach (CsRosterEntry entry in r.Message.Roster)
                        if (entry.Seat == 2 && !entry.Online) offlineRoster = true;
            Check("m_token_takeover", bWasConnected && b3.Welcome.Result == 0 &&
                b3.Welcome.AssignedSeat == 2 && replaced && b2.Peer.DisconnectReason == "replaced" &&
                Grant(c, "takeover", 3, 2) && offlineRoster && Online(a, 2, true, 4) && Online(b3, 2, true, 4));

            d.Peer.Disconnect("late join"); b3.Peer.Disconnect("expiry");
            RunFor(1580);
            Client expired = Join(2, code, bToken);
            Check("k_expired_token", expired.Welcome.Result == 4 && Online(a, 1, true, 2));
            Client lateD = Join(1, code, null);
            Check("l_late_join_snapshot", lateD.Welcome.Result == 0 && lateD.Welcome.AssignedSeat == 2 &&
                HasSnapshot(lateD, "K", 3, version, 22) && lateD.Count(CsWire.CsEvent) == 3);

            Client invalidFirst = Connect();
            invalidFirst.Send(new byte[] { 1, 4 }, true);
            bool error = Until(delegate { return invalidFirst.Count(CsWire.CsError) == 1; }, 700);
            bool closed = Until(delegate { return invalidFirst.Peer.State == PeerState.Disconnected; }, 700);
            Check("first_message_join_required", error && closed);
            Client proto = Connect();
            proto.Send(new CsMessage { Type = CsWire.CsJoin, Proto = 2, Mode = 0 });
            Check("protocol_mismatch", Until(delegate { return proto.Welcome != null; }, 700) && proto.Welcome.Result == 3);

            Client isolated = Join(0, "", null); Clear();
            a.Send(new byte[] { 3, 4, 1 }, true); RunFor(50);
            Check("room_isolation", c.Count(3) == 1 && isolated.Count(3) == 0);
            string idleCode = isolated.Welcome.Code;
            isolated.Peer.Disconnect("idle"); RunFor(1580);
            Client gone = Join(1, idleCode, null);
            Check("idle_room_removed", gone.Welcome.Result == 1);
        }

        static void LogicTests()
        {
            Arbiter room = new Arbiter("ABC234", new ArbiterOptions { EventKeep = 2, SeatHoldMs = 1000 }, 0);
            int seat; byte[] welcome;
            CsMessage join = new CsMessage { Type = CsWire.CsJoin, Proto = 1, Nick = "test" };
            room.Join(join, 0, out seat, out welcome);
            byte[] token1 = CsWire.Decode(welcome).Token;
            room.Join(join, 0, out seat, out welcome);
            byte[] token2 = CsWire.Decode(welcome).Token;
            room.Handle(1, Lease("release", 1, 20), 0);
            room.Handle(2, Lease("release", 1, 10), 100);
            List<Outgoing> release = room.Handle(1, Lease("release", 2, 0), 110);
            Check("release_lowest_interest", release.Count == 1 &&
                CsWire.Decode(release[0].Message).Owner == 2 && CsWire.Decode(release[0].Message).Epoch == 2);
            release = room.Handle(2, Lease("release", 2, 0), 120);
            Check("release_without_interest_owner_zero", release.Count == 1 && CsWire.Decode(release[0].Message).Owner == 0);
            room.Handle(1, Action(1, 3, 0, "one"), 130);
            room.Handle(1, Action(2, 3, 0, "two"), 140);
            room.Handle(1, Action(3, 3, 0, "three"), 150);
            List<Outgoing> late = room.Join(join, 160, out seat, out welcome);
            List<uint> ids = new List<uint>();
            foreach (Outgoing o in late) if (o.Message[0] == CsWire.CsEvent) ids.Add(CsWire.Decode(o.Message).Id);
            Check("event_retention_global_order", ids.Count == 2 && ids[0] == 2 && ids[1] == 3);
            Check("distinct_random_tokens", !Convert.ToBase64String(token1).Equals(Convert.ToBase64String(token2)));
            room.Offline(2, 170);
            List<Outgoing> rejoin = room.Join(new CsMessage { Type = CsWire.CsJoin, Proto = 1, Mode = 2, Token = token2 },
                180, out seat, out welcome);
            List<Outgoing> dup = room.Handle(1, Action(3, 3, 0, "three"), 190);
            Check("dedup_survives_reconnect", rejoin.Count > 0 && seat == 2 && dup.Count == 0);
            room.Handle(1, Lease("minhold", 1, 0), 200);
            List<Outgoing> tooSoon = room.Handle(2, Lease("minhold", 1, 0), 699);
            List<Outgoing> held = room.Handle(2, Lease("minhold", 1, 0), 700);
            Check("min_hold_boundary", tooSoon.Count == 0 && held.Count == 1 && CsWire.Decode(held[0].Message).Epoch == 2);
            room.Handle(2, State("minhold", 2, 1, 1, 5), 710);
            List<Outgoing> stale = room.Handle(1, Lease("minhold", 1, 0), 1209);
            Check("active_window_boundary", stale.Count == 1 && CsWire.Decode(stale[0].Message).Owner == 1);
        }

        static void WireTests()
        {
            CsMessage action = Action(0x12345678, 5, 1, "门");
            byte[] bytes = CsWire.Encode(action);
            CsMessage decoded = CsWire.Decode(bytes);
            Check("wire_little_endian_utf8", bytes[0] == 181 && bytes[1] == 0 && bytes[2] == 0x78 &&
                bytes[3] == 0x56 && bytes[4] == 0x34 && bytes[5] == 0x12 && bytes[8] == 3 &&
                decoded.Key == "门" && decoded.Seq == 0x12345678);
            bool all = true;
            for (byte id = CsWire.CsJoin; id <= CsWire.CsTo; id++)
            {
                CsMessage m = new CsMessage { Type = id, Seat = 4, Proto = 1, Mode = 2, Code = "ABC234",
                    Token = new byte[16], Steam = 0x123456789abcdef0UL, Nick = "名字", Result = 2,
                    LastEvent = 42, Key = "key", Op = 1, Score = 1.25f, Active = 1, Owner = 3,
                    Epoch = 7, Ver = 8, Seq = 9, Kind = 10, Flags = 1, Id = 11, From = 2,
                    Data = new byte[] { 1, 2 }, ErrorCode = 5, Text = "错误", Target = 2, Inner = new byte[] { 1, 4, 5 } };
                m.Roster.Add(new CsRosterEntry { Seat = 1, Online = true, Steam = m.Steam, Nick = m.Nick });
                m.Leases.Add(new CsLeaseEntry { Key = "k", Owner = 3, Epoch = 7 });
                m.States.Add(new CsStateEntry { Key = "k", Epoch = 7, Ver = 8, Active = 1, Data = m.Data });
                byte[] original = CsWire.Encode(m), roundtrip = CsWire.Encode(CsWire.Decode(original));
                all = all && Convert.ToBase64String(original) == Convert.ToBase64String(roundtrip);
            }
            Check("wire_all_11_messages_roundtrip", all);
        }

        public static int Main()
        {
            try
            {
                WireTests(); LogicTests();
                server = new CServer(new ArbiterOptions { SeatHoldMs = 1500, ActiveWindowMs = 300,
                    MinHoldMs = 500, RoomIdleMs = 1500 });
                server.Start(0);
                UdpTests();
            }
            catch (Exception ex) { Check("unexpected_exception", false); Console.WriteLine(ex); }
            finally
            {
                if (server != null) server.Stop();
                foreach (Client c in clients) c.Dispose();
            }
            bool success = passed == total;
            Console.WriteLine("RESULT " + (success ? "PASS " + passed : "FAIL " + (total - passed)) + "/" + total);
            return success ? 0 : 1;
        }
    }
}
