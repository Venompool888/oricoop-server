using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
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

            public Client(IPEndPoint endpoint)
            {
                Peer = new NetPeer(Socket);
                Peer.Connect(endpoint);
            }

            public void Send(CsMessage message)
            {
                if (message.Type != CsWire.CsJoin && message.Seat == 0 && Welcome != null)
                    message.Seat = Welcome.AssignedSeat;
                Send(CsWire.Encode(message), true);
            }
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
                    CsMessage message = bytes[0] >= CsWire.CsJoin && bytes[0] <= CsWire.CsSeatRelease
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
        static IPEndPoint remote;          // --host: test a deployed server instead of an in-process one
        static double deadlineSeconds = 27;
        static int wait = 700;             // reply timeout; longer over the internet
        static int passed, total;

        static void Check(string name, bool ok)
        {
            total++;
            if (ok) passed++;
            Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        }

        static void Pump()
        {
            if (clock.Elapsed.TotalSeconds >= deadlineSeconds) throw new Exception("test deadline");
            if (server != null) server.Step();
            foreach (Client c in clients) c.Poll();
            if (server != null) server.Step();
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
            Client c = new Client(remote ?? new IPEndPoint(IPAddress.Loopback, server.LocalPort));
            clients.Add(c);
            if (!Until(delegate { return c.Peer.State == PeerState.Connected; }, Math.Max(1200, wait * 2)))
                throw new Exception("handshake timeout");
            return c;
        }

        static Client Join(byte mode, string code, byte[] token)
        {
            return Join(mode, code, token, 1);
        }

        static Client Join(byte mode, string code, byte[] token, byte count)
        {
            Client c = Connect();
            CsMessage join = new CsMessage { Type = CsWire.CsJoin, Mode = mode,
                Code = code, Token = token ?? new byte[16], Count = count };
            for (int i = 0; i < count; i++)
                join.Identities.Add(new CsIdentity { Steam = (ulong)(clients.Count * 10 + i), Nick = "player" + i });
            c.Send(join);
            if (!Until(delegate { return c.Welcome != null; }, Math.Max(1200, wait * 2)))
                throw new Exception("welcome timeout");
            RunFor(remote != null ? wait / 2 : 25);
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
            a.Send(new byte[] { 1, 1, 90 }, false);
            bool forwarded = Until(delegate { return b.Count(1) == 1 && c.Count(1) == 1; }, 700);
            RunFor(30);
            Check("d_owned_seat_unreliable_no_echo", forwarded && a.Count(1) == 0 &&
                b.Inbox[0].Bytes[1] == 1 && c.Inbox[0].Bytes[1] == 1 && !b.Inbox[0].Reliable);
            Clear();
            a.Send(new CsMessage { Type = CsWire.CsTo, Target = 3, Inner = new byte[] { 2, 1, 42 } });
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
            // Drain the loop's in-flight states before counting from zero (a late one made c see 2 under load).
            Until(delegate { return c.Count(CsWire.CsState) > 0 && c.Last(CsWire.CsState).Ver == version; }, 700);
            RunFor(20);
            Clear();
            a.Send(State("K", 1, ++version, 1, 12));
            b.Send(Lease("K", 1, 5));
            bool stateForwarded = Until(delegate { return b.Count(CsWire.CsState) > 0 && c.Count(CsWire.CsState) > 0; }, 700);
            RunFor(30);
            long drops = server.DroppedStates;
            b.Send(State("K", 1, version + 1, 1, 99));
            a.Send(State("K", 1, version, 1, 98));
            Until(delegate { return c.Count(CsWire.CsState) >= 1 && server.DroppedStates >= drops + 2; }, 700);
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
            Until(delegate { return b2.Count(CsWire.CsEvent) >= 1 && HasSnapshot(b2, "K", 3, version, 22); }, 700);
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
            proto.Send(new CsMessage { Type = CsWire.CsJoin, Proto = 1, Mode = 0 });
            Check("protocol_mismatch", Until(delegate { return proto.Welcome != null; }, 700) && proto.Welcome.Result == 3);

            Client isolated = Join(0, "", null); Clear();
            a.Send(new byte[] { 3, 1, 1 }, true);
            bool roomDelivered = Until(delegate { return c.Count(3) == 1; }, 700); RunFor(50);
            Check("room_isolation", roomDelivered && c.Count(3) == 1 && isolated.Count(3) == 0);
            string idleCode = isolated.Welcome.Code;
            isolated.Peer.Disconnect("idle"); RunFor(1580);
            Client gone = Join(1, idleCode, null);
            Check("idle_room_removed", gone.Welcome.Result == 1);
        }

        static void MultiSeatTests()
        {
            Client a = Join(0, "", null, 2);
            string code = a.Welcome.Code;
            Client tooMany = Join(1, code, null, 3);
            Check("multi_atomic_room_full", tooMany.Welcome.Result == 2 && tooMany.Welcome.Seats.Count == 0 &&
                a.Last(CsWire.CsRoster).Roster.Count == 2);
            Client b = Join(1, code, null);
            Check("multi_two_plus_one", a.Welcome.Seats.Count == 2 && a.Welcome.Seats[0] == 1 &&
                a.Welcome.Seats[1] == 2 && b.Welcome.Seats.Count == 1 && b.Welcome.Seats[0] == 3 && Online(b, 2, true, 3));
            Client d = Join(1, code, null), fifth = Join(1, code, null);
            Check("multi_fourth_ok_fifth_full", d.Welcome.AssignedSeat == 4 && fifth.Welcome.Result == 2);
            Clear();
            long drops = server.DroppedSenders;
            a.Send(new byte[] { 20, 3, 9 }, true);
            a.Send(new CsMessage { Type = CsWire.CsTo, Target = 3, Inner = new byte[] { 21, 3, 9 } });
            a.Send(new CsMessage { Type = CsWire.CsLease, Seat = 3, Key = "spoof", Op = 1 });
            RunFor(50);
            Check("multi_spoof_dropped_counted", b.Count(20) == 0 && b.Count(21) == 0 &&
                b.Count(CsWire.CsGrant) == 0 && server.DroppedSenders == drops + 3);
            Clear();
            a.Send(new byte[] { 20, 2, 9 }, false);
            bool forwarded = Until(delegate { return b.Count(20) == 1 && d.Count(20) == 1; }, 700);
            Check("multi_secondary_sender_no_echo_or_duplicate", forwarded && a.Count(20) == 0 &&
                b.Inbox[0].Bytes[1] == 2 && d.Inbox[0].Bytes[1] == 2);
            Clear();
            b.Send(new CsMessage { Type = CsWire.CsTo, Target = 2, Inner = new byte[] { 21, 3, 9 } });
            a.Send(new CsMessage { Type = CsWire.CsTo, Target = 2, Inner = new byte[] { 22, 1, 9 } });
            bool targeted = Until(delegate { return a.Count(21) == 1; }, 700);
            RunFor(30);
            Check("multi_target_secondary_routes_connection_no_self_echo", targeted && a.Count(22) == 0 &&
                b.Count(21) == 0 && d.Count(21) == 0 && a.Inbox[0].Bytes[1] == 3);
            Clear();
            CsMessage lease = Lease("seat2", 1, 1); lease.Seat = 2; a.Send(lease);
            Check("multi_seat2_lease", Until(delegate { return Grant(b, "seat2", 2, 1); }, 700));
            a.Send(new CsMessage { Type = CsWire.CsSeatRelease, Target = 1 }); Until(delegate { return a.Count(CsWire.CsError) >= 1; }, 700); RunFor(25);
            Check("multi_primary_release_rejected", a.Count(CsWire.CsError) == 1 && a.Welcome.Seats.Count == 2);
            b.Send(new CsMessage { Type = CsWire.CsSeatAdd, Steam = 999, Nick = "full" }); Until(delegate { return b.Welcome.Result == 2; }, 700); RunFor(25);
            Check("multi_add_when_full_atomic", b.Welcome.Result == 2 && b.Welcome.Seats.Count == 1 && b.Welcome.Seats[0] == 3);
            a.Send(new CsMessage { Type = CsWire.CsSeatRelease, Target = 3 }); Until(delegate { return a.Count(CsWire.CsError) >= 2; }, 700); RunFor(25);
            Check("multi_release_unowned_rejected", a.Count(CsWire.CsError) == 2 && a.Welcome.Seats.Count == 2);
            byte[] token = a.Welcome.Token;
            a.Peer.Disconnect("multi offline");
            bool offline = Until(delegate { return Online(b, 1, false, 4) && Online(b, 2, false, 4); }, 700);
            Client rejoin = Join(2, code, token);
            Check("multi_rejoin_both_seats", offline && rejoin.Welcome.Seats.Count == 2 &&
                rejoin.Welcome.Seats[0] == 1 && rejoin.Welcome.Seats[1] == 2 && Online(b, 1, true, 4) &&
                Online(b, 2, true, 4) && rejoin.Count(CsWire.CsSnapshot) == 1);
            Clear();
            rejoin.Send(new CsMessage { Type = CsWire.CsSeatRelease, Target = 2 });
            bool released = Until(delegate { return rejoin.Welcome.Seats.Count == 1; }, 700);
            rejoin.Send(new CsMessage { Type = CsWire.CsSeatAdd, Steam = 222, Nick = "added" });
            bool added = Until(delegate { return rejoin.Welcome.Seats.Count == 2; }, 700);
            Check("multi_add_release_lowest_free_same_token", released && added && rejoin.Welcome.Seats[1] == 2 &&
                Convert.ToBase64String(token) == Convert.ToBase64String(rejoin.Welcome.Token));
            rejoin.Send(new CsMessage { Type = CsWire.CsSeatRelease, Target = 2 });
            Until(delegate { return rejoin.Welcome.Seats.Count == 1; }, 700);
            Clear(); drops = server.DroppedSenders;
            rejoin.Send(new byte[] { 23, 2, 0 }, true); RunFor(30);
            Check("multi_released_seat_cannot_send", b.Count(23) == 0 && server.DroppedSenders == drops + 1);
        }

        // T-113: CsSeatRelease Target 0 = the device leaves on purpose: all its seats are freed at once (no SeatHoldMs),
        // so a 4-player room takes a fresh join right away. Target 1 (the primary) is still refused, as before.
        static void LeaveLogicTests()
        {
            Arbiter room = new Arbiter("LEAVE1", new ArbiterOptions { SeatHoldMs = 60000 }, 0);
            int seat; byte[] welcome;
            CsMessage pair = new CsMessage { Type = CsWire.CsJoin, Count = 2 };
            for (int i = 0; i < 2; i++) pair.Identities.Add(new CsIdentity { Steam = (ulong)(30 + i), Nick = "pair" + i });
            room.Join(new CsMessage { Type = CsWire.CsJoin }, 0, out seat, out welcome);          // seat 1
            room.Join(pair, 1, out seat, out welcome);                                             // seats 2+3, one device
            byte[] pairToken = CsWire.Decode(welcome).Token;
            room.Join(new CsMessage { Type = CsWire.CsJoin }, 2, out seat, out welcome);           // seat 4
            room.Join(new CsMessage { Type = CsWire.CsJoin }, 3, out seat, out welcome);
            bool fullBefore = seat == 0 && CsWire.Decode(welcome).Result == 2;
            bool refused = false;
            foreach (Outgoing o in room.Handle(2, new CsMessage { Type = CsWire.CsSeatRelease, Target = 2 }, 4))
                if (CsWire.Decode(o.Message).Type == CsWire.CsError) refused = true;
            bool rosterWithout = false;
            foreach (Outgoing o in room.Handle(2, new CsMessage { Type = CsWire.CsSeatRelease, Target = 0 }, 5))
            {
                CsMessage m = CsWire.Decode(o.Message);
                if (m.Type == CsWire.CsRoster) rosterWithout = m.Roster.TrueForAll(delegate(CsRosterEntry e) { return e.Seat != 2 && e.Seat != 3; });
            }
            Check("leave_target0_frees_device_seats", fullBefore && refused && rosterWithout
                && !room.IsOnline(2, 6) && !room.IsOnline(3, 6) && room.ConnectionSeats(2).Count == 0);
            room.Join(new CsMessage { Type = CsWire.CsJoin }, 7, out seat, out welcome);
            Check("leave_then_fresh_join_fits_full_room", seat == 2 && CsWire.Decode(welcome).Result == 0);
            room.Join(new CsMessage { Type = CsWire.CsJoin, Mode = 2, Token = pairToken }, 8, out seat, out welcome);
            Check("leave_old_token_unknown", seat == 0 && CsWire.Decode(welcome).Result == 4);
        }

        static void MultiSeatLogicTests()
        {
            Arbiter room = new Arbiter("MULTI2", new ArbiterOptions { SeatHoldMs = 1000 }, 0);
            CsMessage join = new CsMessage { Type = CsWire.CsJoin, Count = 3 };
            for (int i = 0; i < 3; i++) join.Identities.Add(new CsIdentity { Steam = (ulong)(10 + i), Nick = "local" + i });
            int primary; byte[] welcome;
            room.Join(join, 0, out primary, out welcome);
            CsMessage initial = CsWire.Decode(welcome);
            Check("multi_three_seats_and_primary_helper", initial.Result == 0 && primary == 1 &&
                initial.Seats.Count == 3 && initial.Seats[2] == 3 && Arbiter.PrimarySeat(new int[] { 4, 2, 3 }) == 2 &&
                Arbiter.PrimarySeat(new int[0]) == 0);
            room.Handle(1, Lease("offline-group", 1, 1), 0);
            room.Handle(2, Lease("offline-group", 1, 1), 1);
            List<Outgoing> offline = room.Offline(2, 2);
            bool zero = false;
            foreach (Outgoing o in offline)
            {
                CsMessage m = CsWire.Decode(o.Message);
                if (m.Type == CsWire.CsGrant && m.Key == "offline-group" && m.Owner == 0) zero = true;
            }
            Check("multi_offline_secondary_marks_all_before_handoff", zero && !room.IsOnline(1, 2) &&
                !room.IsOnline(2, 2) && !room.IsOnline(3, 2));
            room.Tick(1002);
            room.Join(new CsMessage { Type = CsWire.CsJoin, Mode = 2, Token = initial.Token }, 1003, out primary, out welcome);
            Check("multi_expired_token_all_seats_freed", primary == 0 && CsWire.Decode(welcome).Result == 4);
            room.Join(join, 1004, out primary, out welcome);
            room.Join(new CsMessage { Type = CsWire.CsJoin }, 1005, out primary, out welcome);
            Check("multi_expiry_reuses_all_lowest_seats", primary == 4 && CsWire.Decode(welcome).Result == 0);
        }

        static void LogicTests()
        {
            Arbiter room = new Arbiter("ABC234", new ArbiterOptions { EventKeep = 2, SeatHoldMs = 1000 }, 0);
            int seat; byte[] welcome;
            CsMessage join = new CsMessage { Type = CsWire.CsJoin, Nick = "test" };
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
            List<Outgoing> rejoin = room.Join(new CsMessage { Type = CsWire.CsJoin, Mode = 2, Token = token2 },
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
            Check("wire_default_protocol_v2", new CsMessage().Proto == 2);
            CsMessage action = Action(0x12345678, 5, 1, "门");
            byte[] bytes = CsWire.Encode(action);
            CsMessage decoded = CsWire.Decode(bytes);
            Check("wire_little_endian_utf8", bytes[0] == 181 && bytes[1] == 0 && bytes[2] == 0x78 &&
                bytes[3] == 0x56 && bytes[4] == 0x34 && bytes[5] == 0x12 && bytes[8] == 3 &&
                decoded.Key == "门" && decoded.Seq == 0x12345678);
            bool all = true;
            for (byte id = CsWire.CsJoin; id <= CsWire.CsSeatRelease; id++)
            {
                CsMessage m = new CsMessage { Type = id, Seat = 4, Mode = 2, Code = "ABC234",
                    Token = new byte[16], Steam = 0x123456789abcdef0UL, Nick = "名字", Result = 2,
                    LastEvent = 42, Key = "key", Op = 1, Score = 1.25f, Active = 1, Owner = 3,
                    Epoch = 7, Ver = 8, Seq = 9, Kind = 10, Flags = 1, Id = 11, From = 2,
                    Data = new byte[] { 1, 2 }, ErrorCode = 5, Text = "错误", Target = 2, Inner = new byte[] { 1, 4, 5 } };
                m.Count = 2;
                m.Identities.Add(new CsIdentity { Steam = m.Steam, Nick = m.Nick });
                m.Identities.Add(new CsIdentity { Steam = 22, Nick = "second" });
                m.Seats.Add(1); m.Seats.Add(2);
                m.Roster.Add(new CsRosterEntry { Seat = 1, Online = true, Steam = m.Steam, Nick = m.Nick });
                m.Leases.Add(new CsLeaseEntry { Key = "k", Owner = 3, Epoch = 7 });
                m.States.Add(new CsStateEntry { Key = "k", Epoch = 7, Ver = 8, Active = 1, Data = m.Data });
                byte[] original = CsWire.Encode(m), roundtrip = CsWire.Encode(CsWire.Decode(original));
                all = all && Convert.ToBase64String(original) == Convert.ToBase64String(roundtrip);
            }
            Check("wire_all_13_messages_roundtrip", all);
            CsMessage join = new CsMessage { Type = CsWire.CsJoin, Count = 2 };
            join.Identities.Add(new CsIdentity { Steam = 0x123456789abcdef0UL, Nick = "a" });
            join.Identities.Add(new CsIdentity { Steam = 22, Nick = "门" });
            bytes = CsWire.Encode(join); decoded = CsWire.Decode(bytes);
            Check("wire_v2_join_count_and_identities", bytes[2] == 2 && bytes[21] == 2 &&
                bytes[22] == 0xf0 && decoded.Count == 2 && decoded.Identities.Count == 2 &&
                decoded.Identities[0].Steam == 0x123456789abcdef0UL && decoded.Identities[1].Steam == 22 &&
                decoded.Identities[1].Nick == "门");
            CsMessage welcome = new CsMessage { Type = CsWire.CsWelcome, LastEvent = 123 };
            welcome.Seats.Add(1); welcome.Seats.Add(2);
            bytes = CsWire.Encode(welcome); decoded = CsWire.Decode(bytes);
            Check("wire_v2_welcome_list_single_connection_token", bytes.Length == 27 && bytes[3] == 2 &&
                bytes[4] == 1 && bytes[5] == 2 && decoded.AssignedSeat == 1 && decoded.Token.Length == 16 &&
                decoded.LastEvent == 123);
            bool invalidCount = false;
            bytes = CsWire.Encode(join); bytes[21] = 4;
            try { CsWire.Decode(bytes); } catch (System.IO.InvalidDataException) { invalidCount = true; }
            Check("wire_v2_invalid_count_rejected", invalidCount);
        }

        static CsMessage GapAction(uint seq, byte flags, string key)
        {
            return new CsMessage { Type = CsWire.CsAction, Seq = seq, Kind = 3, Flags = flags, Key = key, Data = new byte[0] };
        }

        static int GapEvents(List<Outgoing> output, byte kind)
        {
            int n = 0;
            foreach (Outgoing o in output) { CsMessage m = CsWire.Decode(o.Message); if (m.Type == CsWire.CsEvent && m.Kind == kind) n++; }
            return n;
        }

        // C0 gaps: bounded per-seat dedup and claim release / respawn generation.
        static void GapTests()
        {
            Arbiter arb = new Arbiter("GAPTST", new ArbiterOptions(), 0);
            int s1, s2; byte[] w;
            arb.Join(new CsMessage { Type = CsWire.CsJoin, Mode = 0, Token = new byte[16], Nick = "a" }, 0, out s1, out w);
            arb.Join(new CsMessage { Type = CsWire.CsJoin, Mode = 1, Token = new byte[16], Nick = "b" }, 0, out s2, out w);
            for (uint i = 1; i <= 20000; i++) arb.Handle(s1, GapAction(i, 0, "x"), 1);
            Check("gap_dedup_bounded", arb.SeenCount(s1) <= 2 * Arbiter.SeqWindow);
            Check("gap_dedup_old_seq_dropped", GapEvents(arb.Handle(s1, GapAction(5, 0, "x"), 2), 3) == 0);
            Check("gap_dedup_recent_dup_dropped", GapEvents(arb.Handle(s1, GapAction(19999, 0, "x"), 2), 3) == 0);
            Check("gap_dedup_new_seq_ok", GapEvents(arb.Handle(s1, GapAction(20001, 0, "x"), 2), 3) == 1);

            Check("gap_claim_first", GapEvents(arb.Handle(s1, GapAction(30000, Arbiter.FlagClaim, "p:g"), 3), 3) == 1);
            Check("gap_claim_second_rejected", GapEvents(arb.Handle(s2, GapAction(1, Arbiter.FlagClaim, "p:g"), 3), 0xFF) == 1);
            Check("gap_release_by_other_ignored", GapEvents(arb.Handle(s2, GapAction(2, Arbiter.FlagRelease, "p:g"), 3), 3) == 0);
            Check("gap_release_by_winner", GapEvents(arb.Handle(s1, GapAction(30001, Arbiter.FlagRelease, "p:g"), 3), 3) == 1);
            Check("gap_claim_after_release", GapEvents(arb.Handle(s2, GapAction(3, Arbiter.FlagClaim, "p:g"), 3), 3) == 1);
            Check("gap_generation_key_independent", GapEvents(arb.Handle(s1, GapAction(30002, Arbiter.FlagClaim, "p:g#2"), 3), 3) == 1);
        }

        // T-162: a link that fails one way (the client's packets are lost while it still hears the server). The server
        // times the connection out; once the link is back, the client's data is answered with a Goodbye, so it drops
        // the dead session at once and can rejoin, instead of waiting for its own 10 s receive timeout.
        static void StaleSessionTests()
        {
            server.PeerTimeoutMs = 400;
            Client oneWay = Join(0, "", null);
            server.PeerTimeoutMs = 10000;
            Client watcher = Join(1, oneWay.Welcome.Code, null);
            byte seat = (byte)oneWay.Welcome.AssignedSeat;
            Check("stale_setup_two_seats", Until(delegate { return Online(watcher, seat, true, 2); }, 700));
            long goodbyes = server.StaleGoodbyes;
            oneWay.Peer.Sim.LossPercent = 100;
            bool serverDropped = Until(delegate { return Online(watcher, seat, false, 2); }, 1500);
            RunFor(300);
            bool stillConnected = oneWay.Peer.State == PeerState.Connected;
            oneWay.Peer.Sim.LossPercent = 0;
            double clear = clock.Elapsed.TotalMilliseconds;
            bool dropped = Until(delegate
            {
                oneWay.Send(new byte[] { 1, 4, 1 }, false);
                return oneWay.Peer.State == PeerState.Disconnected;
            }, 1500);
            double after = clock.Elapsed.TotalMilliseconds - clear;
            // The reason equals the client's own timeout's, so "< 1000 ms" (its own timeout is 10 s) and the server's count
            // tell the two apart.
            Check("stale_session_goodbye_after_one_way_link (" + (int)after + " ms)", serverDropped && stillConnected && dropped &&
                after < 1000 && oneWay.Peer.DisconnectReason == CServer.StaleReason && server.StaleGoodbyes > goodbyes);
            // The reply never outweighs its request: the Goodbye is smaller than the smallest Data packet (an empty
            // keepalive, 19 bytes), so the server cannot be used to amplify traffic toward a spoofed address.
            byte[] keepalive = new byte[19]; keepalive[0] = 0x43; keepalive[1] = 0x4F; keepalive[2] = 4; keepalive[3] = 9;
            byte[] reply = CServer.StaleSessionGoodbye(keepalive, keepalive.Length, CServer.StaleReason);
            Check("stale_goodbye_smaller_than_any_data (" + (reply == null ? -1 : reply.Length) + " bytes)",
                reply != null && reply.Length < keepalive.Length);
            // A socket the server never knew: only a Data packet with a session id is answered (not a Goodbye, not
            // session 0, not a short packet), so nothing else can make the server talk to an unknown endpoint.
            using (UdpTransport raw = new UdpTransport(0))
            {
                IPEndPoint to = new IPEndPoint(IPAddress.Loopback, server.LocalPort);
                long before = server.StaleGoodbyes;
                byte[] goodbye = { 0x43, 0x4F, 5, 1, 0, 0, 0, 0, 0 };
                byte[] sessionZero = new byte[19]; sessionZero[0] = 0x43; sessionZero[1] = 0x4F; sessionZero[2] = 4;
                byte[] shortData = { 0x43, 0x4F, 4, 1, 0, 0, 0, 0, 0 };
                raw.SendTo(goodbye, 0, goodbye.Length, to);
                raw.SendTo(sessionZero, 0, sessionZero.Length, to);
                raw.SendTo(shortData, 0, shortData.Length, to);
                RunFor(100);
                bool ignored = server.StaleGoodbyes == before;
                byte[] data = (byte[])sessionZero.Clone(); data[3] = 7;
                for (int i = 0; i < 5; i++) raw.SendTo(data, 0, data.Length, to);
                RunFor(100);
                Check("stale_goodbye_only_for_data_throttled", ignored && server.StaleGoodbyes == before + 1);
            }
        }

        // T-170: the connection audit log. A server with a log directory appends one line per event to
        // connections.log (and the same line to stdout): time, event, remote IP:port, room, seats, SteamIDs, nicks,
        // reason. A nick is player text: a newline in it must stay escaped inside its own line.
        static string[] ReadShared(string path)
        {
            if (!File.Exists(path)) return new string[0];
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader reader = new StreamReader(stream))
            {
                List<string> lines = new List<string>();
                string line;
                while ((line = reader.ReadLine()) != null) lines.Add(line);
                return lines.ToArray();
            }
        }

        static string FindLine(string[] lines, string evt, params string[] parts)
        {
            foreach (string line in lines)
            {
                string[] head = line.Split(' ');
                if (head.Length < 2 || head[1] != evt) continue;
                bool all = true;
                foreach (string part in parts) if (line.IndexOf(part, StringComparison.Ordinal) < 0) all = false;
                if (all) return line;
            }
            return null;
        }

        static void AuditTests()
        {
            CServer main = server;
            string dir = Path.Combine(Path.GetTempPath(), "oricoop-audit-" + Guid.NewGuid().ToString("N"));
            List<string> stdout = new List<string>();
            server = new CServer(new ArbiterOptions { SeatHoldMs = 1500, ActiveWindowMs = 300,
                MinHoldMs = 500, RoomIdleMs = 1500 }, dir);
            server.AuditSink = delegate(string line) { lock (stdout) stdout.Add(line); };
            try
            {
                server.Start(0);
                // Two local players on one connection; the second nick tries to forge a log line.
                Client a = Connect();
                CsMessage join = new CsMessage { Type = CsWire.CsJoin, Mode = 0, Code = "", Token = new byte[16], Count = 2 };
                join.Identities.Add(new CsIdentity { Steam = 76561190000000001UL, Nick = "Alpha" });
                join.Identities.Add(new CsIdentity { Steam = 76561190000000002UL,
                    Nick = "Be\"ta\n2026-01-01T00:00:00.000Z leave ip=6.6.6.6:1\t\u0007" });
                a.Send(join);
                bool welcomed = Until(delegate { return a.Welcome != null; }, 1200);
                string code = welcomed ? a.Welcome.Code : "?";
                byte[] token = welcomed ? a.Welcome.Token : new byte[16];
                Client refused = Join(1, "000000", null);
                // Rejoin with the token: replaces connection a (a gets a "replaced" leave line).
                Client back = Connect();
                CsMessage rejoin = new CsMessage { Type = CsWire.CsJoin, Mode = 2, Code = code, Token = token, Count = 2 };
                rejoin.Identities.Add(new CsIdentity { Steam = 76561190000000001UL, Nick = "Alpha" });
                rejoin.Identities.Add(new CsIdentity { Steam = 76561190000000002UL, Nick = "Beta" });
                back.Send(rejoin);
                Until(delegate { return back.Welcome != null; }, 1200);
                RunFor(50);
                back.Peer.Disconnect("bye");
                RunFor(100);
                // A connection whose packets stop arriving times out on the server.
                server.PeerTimeoutMs = 300;
                Client lost = Join(1, code, null);
                server.PeerTimeoutMs = 10000;
                lost.Peer.Sim.LossPercent = 100;
                RunFor(600);
                lost.Peer.Sim.LossPercent = 0;
                lost.Send(new byte[] { 1, 2, 1 }, false);
                RunFor(100);
                // A dropped client that keeps sending: 20 stale datagrams within 1 s get several goodbyes (one per
                // 250 ms) but exactly one audit line.
                int floodPort;
                long floodGoodbyes = server.StaleGoodbyes;
                using (UdpTransport flood = new UdpTransport(0))
                {
                    floodPort = flood.LocalPort;
                    IPEndPoint to = new IPEndPoint(IPAddress.Loopback, server.LocalPort);
                    byte[] data = new byte[19]; data[0] = 0x43; data[1] = 0x4F; data[2] = 4; data[3] = 11;
                    for (int i = 0; i < 20; i++) { flood.SendTo(data, 0, data.Length, to); RunFor(45); }
                    RunFor(50);
                }
                floodGoodbyes = server.StaleGoodbyes - floodGoodbyes;

                string[] lines = ReadShared(Path.Combine(dir, CServer.ConnectionsLogName));
                string ipA = "ip=127.0.0.1:" + a.Socket.LocalPort + " ";
                string created = FindLine(lines, "create", ipA, "room=" + code + " ", "seats=1,2 ",
                    "steam=76561190000000001,76561190000000002 ", "nick=\"Alpha\",\"Be");
                Console.WriteLine("  audit: " + (created ?? "(no create line)"));
                Check("audit_join_line_has_ip_steam_nick", welcomed && created != null);
                Regex shape = new Regex(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z [a-z_]+ ip=[0-9.]+:\d+ room=\S+ seats=\S+ steam=\S+ nick=.* reason=\S.*$");
                bool everyLineShaped = lines.Length > 0;
                foreach (string line in lines) if (!shape.IsMatch(line)) everyLineShaped = false;
                Check("audit_nick_newline_escaped", created != null &&
                    created.Contains("\"Be\\\"ta\\n2026-01-01T00:00:00.000Z leave ip=6.6.6.6:1\\t\\u0007\"") &&
                    Array.FindIndex(lines, delegate(string line) { return line.StartsWith("2026-01-01T", StringComparison.Ordinal); }) < 0 &&
                    everyLineShaped);
                Check("audit_connect_refused_rejoin_leave_timeout",
                    FindLine(lines, "connect", ipA) != null &&
                    FindLine(lines, "join_refused", "ip=127.0.0.1:" + refused.Socket.LocalPort + " ", "room=000000 ", "reason=\"no_room\"") != null &&
                    FindLine(lines, "rejoin", "ip=127.0.0.1:" + back.Socket.LocalPort + " ", "room=" + code + " ", "seats=1,2 ", "steam=76561190000000001,76561190000000002 ") != null &&
                    FindLine(lines, "leave", ipA, "reason=\"replaced\"") != null &&
                    FindLine(lines, "leave", "ip=127.0.0.1:" + back.Socket.LocalPort + " ", "steam=76561190000000001,", "reason=\"bye\"") != null &&
                    FindLine(lines, "timeout", "ip=127.0.0.1:" + lost.Socket.LocalPort + " ", "room=" + code + " ", "reason=\"timed out\"") != null &&
                    FindLine(lines, "stale_goodbye", "ip=127.0.0.1:" + lost.Socket.LocalPort + " ") != null);
                int floodLines = 0;
                foreach (string line in lines)
                    if (line.Contains(" stale_goodbye ip=127.0.0.1:" + floodPort + " ")) floodLines++;
                string floodLine = FindLine(lines, "stale_goodbye", "ip=127.0.0.1:" + floodPort + " ", "reason=\"timed out\" count=1");
                Check("audit_stale_goodbye_rate_limited (" + floodGoodbyes + " goodbyes, " + floodLines + " lines)",
                    floodGoodbyes >= 2 && floodLines == 1 && floodLine != null);
                bool same;
                lock (stdout) same = stdout.Count == lines.Length && String.Join("\n", stdout.ToArray()) == String.Join("\n", lines);
                Check("audit_stdout_same_lines (" + lines.Length + ")", same);
                foreach (Client x in new Client[] { a, refused, back, lost }) x.Peer.Disconnect("done");
                RunFor(50);
            }
            finally
            {
                server.Stop();
                server = main;
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
            // No log directory: stdout only, nothing written anywhere.
            CServer plain = new CServer(new ArbiterOptions());
            List<string> plainOut = new List<string>();
            plain.AuditSink = delegate(string line) { plainOut.Add(line); };
            plain.Start(0);
            using (Client c = new Client(new IPEndPoint(IPAddress.Loopback, plain.LocalPort)))
            {
                Stopwatch watch = Stopwatch.StartNew();
                while (c.Peer.State != PeerState.Connected && watch.ElapsedMilliseconds < 1000) { plain.Step(); c.Poll(); Thread.Sleep(1); }
                plain.Step();
                Check("audit_without_log_dir_stdout_only", plainOut.Count >= 1 && plainOut[0].Contains(" connect ip=127.0.0.1:" + c.Socket.LocalPort + " "));
                c.Peer.Disconnect("done"); c.Poll();
            }
            plain.Stop();
        }

        // Black-box checks against a deployed server (default options, real network): no server internals,
        // no timing that depends on the test-only short holds.
        static void RemoteTests()
        {
            Client a = Join(0, "", null);
            string code = a.Welcome.Code;
            Client b = Join(1, code, null), c = Join(1, code, null);
            Check("r_join_seats_roster", a.Welcome.Result == 0 && a.Welcome.AssignedSeat == 1 &&
                b.Welcome.AssignedSeat == 2 && c.Welcome.AssignedSeat == 3 && code.Length == 6 &&
                Until(delegate { return Online(a, 3, true, 3) && Online(b, 1, true, 3) && Online(c, 2, true, 3); }, wait));
            Client d = Join(1, code, null), fifth = Join(1, code, null);
            Check("r_fourth_and_full", d.Welcome.Result == 0 && d.Welcome.AssignedSeat == 4 && fifth.Welcome.Result == 2);
            Client bad = Join(1, "000000", null);
            Check("r_no_such_room", bad.Welcome.Result == 1);

            Clear();
            bool forwarded = false;
            for (int attempt = 0; attempt < 3 && !forwarded; attempt++)   // unreliable may be lost on the internet
            {
                a.Send(new byte[] { 1, 1, 90 }, false);
                forwarded = Until(delegate { return b.Count(1) >= 1 && c.Count(1) >= 1; }, wait);
            }
            RunFor(wait / 2);
            Check("r_owned_seat_unreliable_no_echo", forwarded && a.Count(1) == 0 &&
                b.Inbox[0].Bytes[1] == 1 && c.Inbox[0].Bytes[1] == 1 && !b.Inbox[0].Reliable);
            Clear();
            a.Send(new CsMessage { Type = CsWire.CsTo, Target = 3, Inner = new byte[] { 2, 1, 42 } });
            bool targeted = Until(delegate { return c.Count(2) == 1; }, wait);
            RunFor(wait / 2);
            Check("r_target_only_reliable", targeted && a.Count(2) == 0 && b.Count(2) == 0 &&
                d.Count(2) == 0 && c.Inbox[0].Bytes[1] == 1 && c.Inbox[0].Reliable);

            Clear();
            a.Send(Lease("K", 1, 10));
            bool granted = Until(delegate { return Grant(b, "K", 1, 1) && Grant(c, "K", 1, 1) && Grant(d, "K", 1, 1); }, wait);
            a.Send(State("K", 1, 1, 1, 11));
            bool state = Until(delegate { return b.Count(CsWire.CsState) == 1 && c.Count(CsWire.CsState) == 1; }, wait);
            RunFor(wait / 2);
            Check("r_lease_grant_state_forward", granted && state && a.Count(CsWire.CsState) == 0 &&
                c.Last(CsWire.CsState).Data[0] == 11);

            Clear(); a.Send(Action(1, 9, 1, "claim")); b.Send(Action(1, 9, 1, "claim"));
            bool claims = Until(delegate { return a.Count(CsWire.CsEvent) + b.Count(CsWire.CsEvent) == 3; }, wait);
            RunFor(wait / 2);
            int accepted = 0, rejected = 0;
            foreach (Client who in new Client[] { a, b })
                foreach (Received r in who.Inbox)
                    if (r.Message != null && r.Message.Type == CsWire.CsEvent)
                    {
                        if (r.Message.Kind == 0xFF && r.Message.Data.Length == 1 && r.Message.Data[0] == 9) rejected++;
                        else if (r.Message.Kind == 9) accepted++;
                    }
            Check("r_claim_first_only", claims && accepted == 2 && rejected == 1 &&
                c.Count(CsWire.CsEvent) == 1 && d.Count(CsWire.CsEvent) == 1);
            Clear(); a.Send(Action(2, 10, 0, "normal")); a.Send(Action(2, 10, 0, "normal"));
            bool oneEvent = Until(delegate { return c.Count(CsWire.CsEvent) == 1; }, wait);
            RunFor(wait / 2);
            Check("r_action_dedup", oneEvent && a.Count(CsWire.CsEvent) == 1 && b.Count(CsWire.CsEvent) == 1);

            byte[] bToken = b.Welcome.Token;
            b.Peer.Disconnect("test");
            bool offline = Until(delegate { return Online(a, 2, false, 4); }, wait * 2);
            Client b2 = Join(2, code, bToken);
            Check("r_token_reconnect", offline && b2.Welcome.Result == 0 && b2.Welcome.AssignedSeat == 2 &&
                Until(delegate { return Online(a, 2, true, 4); }, wait) && b2.Count(CsWire.CsSnapshot) >= 1);

            Client invalidFirst = Connect();
            invalidFirst.Send(new byte[] { 1, 4 }, true);
            bool error = Until(delegate { return invalidFirst.Count(CsWire.CsError) == 1; }, wait);
            bool closed = Until(delegate { return invalidFirst.Peer.State == PeerState.Disconnected; }, wait);
            Check("r_first_message_join_required", error && closed);
            Client proto = Connect();
            proto.Send(new CsMessage { Type = CsWire.CsJoin, Proto = 1, Mode = 0 });
            Check("r_protocol_mismatch", Until(delegate { return proto.Welcome != null; }, wait) && proto.Welcome.Result == 3);

            Client isolated = Join(0, "", null); Clear();
            a.Send(new byte[] { 3, 1, 1 }, true);
            bool inRoom = Until(delegate { return c.Count(3) == 1; }, wait);
            RunFor(wait / 2);
            Check("r_room_isolation", inRoom && isolated.Count(3) == 0 && isolated.Welcome.Code != code);
            foreach (Client x in clients) x.Peer.Disconnect("done");
            RunFor(100);
        }

        // T-167: with nobody connected, the server's own loop must sleep, not spin. It used to wake about 1000 times
        // a second (Sleep(1) after each Step, each one a non-blocking receive that threw WouldBlock): about 10% of a
        // core on a 2-vCPU VM. Oracle from the ticket: idle wake-ups at most 20 per second (one every 50 ms is the
        // shortest idle wait) and the process at most 5% of one core; a datagram still wakes the loop at once.
        static volatile bool idleStop;
        static void IdleTests()
        {
            CServer idle = new CServer(new ArbiterOptions());
            idle.Start(0);
            idleStop = false;
            Thread loop = new Thread(delegate() { idle.Run(delegate { return idleStop; }); });
            loop.IsBackground = true; loop.Start();
            Client c = null;
            try
            {
                Thread.Sleep(300);
                Process self = Process.GetCurrentProcess();
                long steps0 = idle.Steps; TimeSpan cpu0 = self.TotalProcessorTime;
                Stopwatch watch = Stopwatch.StartNew();
                Thread.Sleep(2000);
                self.Refresh();
                double seconds = watch.Elapsed.TotalSeconds;
                double wakesPerSecond = (idle.Steps - steps0) / seconds;
                double cpuShare = (self.TotalProcessorTime - cpu0).TotalSeconds / seconds;
                Console.WriteLine("  idle: " + wakesPerSecond.ToString("0.0") + " wake-ups/s, " +
                    (cpuShare * 100).ToString("0.0") + "% of one core");
                Check("idle_loop_wakeups_at_most_20_per_s", wakesPerSecond <= 20);
                Check("idle_loop_cpu_at_most_5_percent", cpuShare <= 0.05);

                // Waking on data: from an empty server (250 ms idle wait), each handshake must finish within 100 ms.
                // A loop that only slept would add a random 0-250 ms per sample; five samples at random phases all
                // under 100 ms by luck is about 1% (0.4^5). Each client says Goodbye, so the server is empty again.
                Random phase = new Random();
                long slowest = 0; bool allConnected = true;
                for (int i = 0; i < 5; i++)
                {
                    Thread.Sleep(300 + phase.Next(250));
                    c = new Client(new IPEndPoint(IPAddress.Loopback, idle.LocalPort));
                    Stopwatch handshake = Stopwatch.StartNew();
                    while (c.Peer.State != PeerState.Connected && handshake.ElapsedMilliseconds < 1000)
                    {
                        c.Poll(); Thread.Sleep(1);
                    }
                    allConnected &= c.Peer.State == PeerState.Connected;
                    slowest = Math.Max(slowest, handshake.ElapsedMilliseconds);
                    c.Peer.Disconnect("sample done"); c.Dispose(); c = null;
                }
                Console.WriteLine("  idle: slowest of 5 handshakes " + slowest + " ms");
                Check("idle_loop_wakes_on_datagram", allConnected && slowest < 100);
            }
            finally
            {
                idleStop = true;
                loop.Join(2000);
                idle.Stop();
                if (c != null) c.Dispose();
            }
        }

        public static int Main(string[] args)
        {
            for (int i = 0; i + 1 < args.Length; i += 2)
                if (args[i] == "--host")
                {
                    string host = args[i + 1]; int port = 47022;
                    int colon = host.LastIndexOf(':');
                    if (colon > 0) { port = int.Parse(host.Substring(colon + 1)); host = host.Substring(0, colon); }
                    IPAddress ip;
                    if (!IPAddress.TryParse(host, out ip)) ip = Dns.GetHostAddresses(host)[0];
                    remote = new IPEndPoint(ip, port);
                }
            try
            {
                if (remote != null)
                {
                    Console.WriteLine("Remote C-server " + remote);
                    deadlineSeconds = 90; wait = 3000;
                    RemoteTests();
                }
                else
                {
                    WireTests(); LogicTests(); GapTests(); MultiSeatLogicTests(); LeaveLogicTests();
                    server = new CServer(new ArbiterOptions { SeatHoldMs = 1500, ActiveWindowMs = 300,
                        MinHoldMs = 500, RoomIdleMs = 1500 });
                    server.AuditSink = null;   // T-170: audit lines are checked in AuditTests; keep this output readable
                    server.Start(0);
                    UdpTests();
                    StaleSessionTests();
                    MultiSeatTests();
                    AuditTests();
                    IdleTests();
                }
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
