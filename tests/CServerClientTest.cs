using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;
using OriCoop.Authority;

namespace OriCoop.CServer
{
    public static class CServerClientTest
    {
        sealed class GameMessage
        {
            public byte Type, Seat;
            public bool Reliable;
            public byte Value;
        }

        sealed class Client
        {
            public readonly CServerClient Session;
            public readonly List<GameMessage> Game = new List<GameMessage>();
            public readonly List<int> Assigned = new List<int>();
            public readonly List<int> Online = new List<int>();
            public readonly List<int> Offline = new List<int>();
            public readonly List<int> Arbiters = new List<int>();
            public readonly List<StateUpdate> States = new List<StateUpdate>();
            public readonly List<CommittedEvent> Events = new List<CommittedEvent>();
            public readonly List<string> Failures = new List<string>();
            public int Snapshots, Resets;
            public bool Left;

            public Client(byte mode, string code, byte[] token) : this(mode, code, token, 1) { }

            public Client(byte mode, string code, byte[] token, byte count)
            {
                CsIdentity[] identities = new CsIdentity[count];
                for (int i = 0; i < count; i++)
                    identities[i] = new CsIdentity { Steam = (ulong)(clients.Count * 10 + i + 1), Nick = "player" + i };
                Session = new CServerClient(new IPEndPoint(IPAddress.Loopback, server.LocalPort),
                    mode, code, token, count, identities);
                Session.SeatAssigned += Assigned.Add;
                Session.SeatOnline += Online.Add;
                Session.SeatOffline += Offline.Add;
                Session.ArbiterChanged += Arbiters.Add;
                Session.JoinFailed += Failures.Add;
                Session.Authority.StateReceived += States.Add;
                Session.Authority.Committed += Events.Add;
                Session.Authority.Reset += delegate { Resets++; };
            }

            public void Leave()
            {
                Session.Leave();
                Left = true;
            }
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
            foreach (Client c in clients)
            {
                if (c.Left) continue;
                c.Session.Peer.Poll();
                byte[] bytes; bool reliable;
                while (c.Session.Peer.TryReceive(out bytes, out reliable))
                {
                    if (bytes[0] >= CsWire.CsJoin && bytes[0] <= CsWire.CsKick)
                    {
                        if (bytes[0] == CsWire.CsSnapshot) c.Snapshots++;
                        c.Session.Receive(bytes);
                    }
                    else c.Game.Add(new GameMessage { Type = bytes[0], Seat = bytes[1],
                        Reliable = reliable, Value = bytes[2] });
                }
            }
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

        static Client Join(byte mode, string code, byte[] token)
        {
            Client c = new Client(mode, code, token);
            clients.Add(c);
            if (!Until(delegate { return c.Assigned.Count != 0 || c.Failures.Count != 0; }, 1500))
                throw new Exception("welcome timeout");
            return c;
        }

        static bool HasState(Client c)
        {
            foreach (StateUpdate state in c.States)
                if (state.Key == "lease" && state.From == 2 && state.Epoch == 1 && state.Ver == 1 &&
                    state.Active && state.Data.Length == 2 && state.Data[0] == 7 && state.Data[1] == 8) return true;
            return false;
        }

        static int Rejections(Client c)
        {
            int n = 0;
            foreach (CommittedEvent e in c.Events)
                if (e.Key == "claim" && e.Kind == 9 && e.Rejected && e.From == c.Session.Seat) n++;
            return n;
        }

        static CommittedEvent Accepted(Client c)
        {
            CommittedEvent found = null;
            foreach (CommittedEvent e in c.Events)
                if (e.Key == "claim" && e.Kind == 9 && !e.Rejected)
                {
                    if (found != null) return null;
                    found = e;
                }
            return found;
        }

        // T-113: a deliberate leave frees the seat at once; re-entering the room's code rejoins a held seat with the token;
        // a token the server no longer knows falls back to a fresh join on the same connection.
        static void LeaveTests()
        {
            bool r;
            Check("plan_join_modes", CServerClient.PlanJoin(true, "", "", null, null, out r) == 0 && !r
                && CServerClient.PlanJoin(false, "ABC234", "", null, null, out r) == 1 && !r
                && CServerClient.PlanJoin(false, "ABC234", "XYZ567", new byte[16], null, out r) == 1 && !r
                && CServerClient.PlanJoin(false, "ABC234", "ABC234", new byte[16], null, out r) == 2 && r
                && CServerClient.PlanJoin(true, "", "ABC234", new byte[16], "ABC234", out r) == 2 && r
                && CServerClient.PlanJoin(true, "", "ABC234", new byte[16], null, out r) == 0 && !r);
            Client a = Join(0, "", null);
            string code = a.Session.Code;
            Client b = Join(1, code, null), c = Join(1, code, null), d = Join(1, code, null);
            bool full = Until(delegate { return a.Online.Count == 4; }, 700);
            byte[] releasedToken = d.Session.Token; int dSeat = d.Session.Seat;
            d.Session.ReleaseAll();
            bool freed = Until(delegate { return a.Offline.Contains(dSeat); }, 700);
            d.Leave();
            Client e = Join(1, code, null);
            Check("leave_releases_seat_for_new_join", full && freed && e.Failures.Count == 0 && e.Assigned.Count == 1 && e.Session.Seat == dSeat);

            // e drops without releasing (crash, or an older client): its seat is held; entering the code again rejoins it.
            byte[] eToken = e.Session.Token; int eSeat = e.Session.Seat;
            e.Leave();
            Until(delegate { return a.Offline.FindAll(delegate(int s) { return s == eSeat; }).Count >= 2; }, 12000);
            bool rejoin; byte mode = CServerClient.PlanJoin(false, code, code, eToken, null, out rejoin);
            Client f = new Client(mode, code, rejoin ? eToken : null); f.Session.RejoinFallsBackToJoin = rejoin; clients.Add(f);
            Until(delegate { return f.Assigned.Count != 0 || f.Failures.Count != 0; }, 1500);
            Check("reenter_code_rejoins_held_seat", rejoin && mode == 2 && f.Failures.Count == 0 && f.Assigned.Count == 1
                && f.Session.Seat == eSeat && !f.Session.UsedFallback);

            // A released seat's token is unknown: with a free seat, the client joins fresh instead of failing.
            int cSeat = c.Session.Seat;
            c.Session.ReleaseAll(); Until(delegate { return a.Offline.Contains(cSeat); }, 700); c.Leave();
            Client g = new Client(2, code, releasedToken); g.Session.RejoinFallsBackToJoin = true; clients.Add(g);
            Until(delegate { return g.Assigned.Count != 0 || g.Failures.Count != 0; }, 1500);
            Check("unknown_token_falls_back_to_fresh_join", g.Failures.Count == 0 && g.Assigned.Count == 1
                && g.Session.Seat == cSeat && g.Session.UsedFallback);
            foreach (Client x in new[] { a, b, f, g }) x.Leave();
        }

        static void Tests()
        {
            Client a = Join(0, "", null);
            bool first = Until(delegate { return a.Session.IsArbiter; }, 700);
            Check("create_seat_1_arbiter", first && a.Session.Seat == 1 && a.Assigned.Count == 1 &&
                a.Assigned[0] == 1 && a.Session.Code.Length == 6 && a.Session.Token.Length == 16 &&
                a.Session.Authority.Mode == AuthorityMode.Server && !a.Session.Authority.ArbiterIsLocal &&
                a.Session.Authority.LocalSeat == 1);
            string code = a.Session.Code;
            byte[] token = a.Session.Token;
            Client b = Join(1, code, null), c = Join(1, code, null);
            bool roster = Until(delegate { return a.Online.Count == 3 && b.Online.Count == 3 &&
                c.Online.Count == 3 && a.Session.ArbiterSeat == 1 && b.Session.ArbiterSeat == 1 &&
                c.Session.ArbiterSeat == 1; }, 700);
            RunFor(30);
            Check("join_seats_2_3_roster_events", roster && b.Session.Seat == 2 && c.Session.Seat == 3 &&
                b.Assigned.Count == 1 && b.Assigned[0] == 2 && c.Assigned.Count == 1 && c.Assigned[0] == 3 &&
                b.Session.Code == code && c.Session.Code == code && !b.Session.IsArbiter && !c.Session.IsArbiter &&
                a.Online.Contains(2) && a.Online.Contains(3) && b.Online.Contains(1) && c.Online.Contains(1) &&
                a.Arbiters.Count == 1 && b.Arbiters.Count == 1 && c.Arbiters.Count == 1);

            byte[] game = new byte[] { 1, 2, 90 };
            b.Session.Peer.SendUnreliable(game, 0, game.Length);
            bool forwarded = Until(delegate { return a.Game.Count == 1 && c.Game.Count == 1; }, 700);
            RunFor(30);
            Check("game_accepts_seat_2_no_echo", forwarded && b.Game.Count == 0 &&
                a.Game[0].Type == 1 && c.Game[0].Type == 1 && a.Game[0].Seat == 2 && c.Game[0].Seat == 2 &&
                !a.Game[0].Reliable && !c.Game[0].Reliable && a.Game[0].Value == 90 && c.Game[0].Value == 90);

            a.Game.Clear(); b.Game.Clear(); c.Game.Clear();
            a.Session.SendTo(3, 40, true, delegate(BinaryWriter w) { w.Write((byte)42); });
            bool targeted = Until(delegate { return c.Game.Count == 1; }, 700);
            RunFor(30);
            Check("send_to_reliable_only_seat_3", targeted && a.Game.Count == 0 && b.Game.Count == 0 &&
                c.Game[0].Type == 40 && c.Game[0].Seat == 1 && c.Game[0].Value == 42 && c.Game[0].Reliable);
            c.Game.Clear();
            a.Session.SendTo(3, 40, false, delegate(BinaryWriter w) { w.Write((byte)43); });
            targeted = Until(delegate { return c.Game.Count == 1; }, 700);
            RunFor(30);
            Check("send_to_unreliable_only_seat_3", targeted && a.Game.Count == 0 && b.Game.Count == 0 &&
                c.Game[0].Type == 40 && c.Game[0].Seat == 1 && c.Game[0].Value == 43 && !c.Game[0].Reliable);

            b.Session.Authority.RequestLease("lease", 1, true);
            Check("lease_owner_2_everywhere", Until(delegate { return a.Session.Authority.OwnerOf("lease") == 2 &&
                b.Session.Authority.OwnerOf("lease") == 2 && c.Session.Authority.OwnerOf("lease") == 2; }, 700));
            b.Session.Authority.PublishState("lease", new byte[] { 7, 8 }, true);
            bool states = Until(delegate { return HasState(a) && HasState(c); }, 700);
            RunFor(30);
            Check("publish_state_received_no_echo", states && b.States.Count == 0);

            a.Session.Authority.Propose(9, "claim", new byte[] { 5 }, true, false);
            c.Session.Authority.Propose(9, "claim", new byte[] { 5 }, true, false);
            bool claims = Until(delegate { return Accepted(a) != null && Accepted(b) != null && Accepted(c) != null &&
                Rejections(a) + Rejections(c) == 1; }, 700);
            RunFor(30);
            CommittedEvent winner = Accepted(a);
            Check("simultaneous_claim_one_winner_one_rejected", claims && winner != null &&
                (winner.From == 1 || winner.From == 3) && winner.Id == Accepted(b).Id && winner.Id == Accepted(c).Id &&
                winner.From == Accepted(b).From && winner.From == Accepted(c).From &&
                Rejections(b) == 0 && (winner.From == 1 ? Rejections(c) == 1 : Rejections(a) == 1) &&
                a.Events.Count + b.Events.Count + c.Events.Count == 4);

            a.Leave();
            bool offline = Until(delegate { return b.Offline.Contains(1) && c.Offline.Contains(1) &&
                b.Arbiters.Contains(2) && c.Arbiters.Contains(2); }, 700);
            Check("leave_offline_1_arbiter_2", offline && b.Session.IsArbiter && !c.Session.IsArbiter &&
                b.Session.ArbiterSeat == 2 && c.Session.ArbiterSeat == 2 && b.Offline.Count == 1 && c.Offline.Count == 1);
            Client rejoin = Join(2, code, token);
            bool restored = Until(delegate { return rejoin.Session.IsArbiter && rejoin.Snapshots == 1 && HasState(rejoin) &&
                b.Session.ArbiterSeat == 1 && c.Session.ArbiterSeat == 1; }, 700);
            Check("token_reconnect_seat_1_snapshot_arbiter", restored && rejoin.Session.Seat == 1 &&
                rejoin.Assigned.Count == 1 && rejoin.Assigned[0] == 1 && rejoin.Resets == 1 &&
                rejoin.Session.Authority.OwnerOf("lease") == 2 && rejoin.Session.Code == code &&
                Convert.ToBase64String(rejoin.Session.Token) == Convert.ToBase64String(token) &&
                !b.Session.IsArbiter && !c.Session.IsArbiter && b.Online.Count == 4 && c.Online.Count == 4 &&
                b.Arbiters.Count == 3 && c.Arbiters.Count == 3);

            // The rejoined client restarts its seq at 1; seat 1 already used seq 1 before leaving.
            int before = b.Events.Count;
            rejoin.Session.Authority.Propose(9, "after-rejoin", new byte[] { 1 }, false, false);
            Check("rejoin_seq_restart_not_deduped", Until(delegate { return b.Events.Count == before + 1; }, 700));

            Client wrong = Join(1, "000000", null);
            Check("wrong_code_chinese_failure", wrong.Failures.Count == 1 && wrong.Failures[0] == "server.err.noroom" &&
                wrong.Session.Seat == 0 && wrong.Assigned.Count == 0 && !wrong.Session.IsArbiter);
            // T-148: the refused join is ended (not a connected, seatless peer stuck on "connecting"), with the reason.
            Check("refused_join_ends_connection", Until(delegate { return wrong.Session.Peer.State != OriCoop.Net.PeerState.Connected; }, 1000)
                && wrong.Session.RefusedWith == "server.err.noroom");
            int failuresBefore = wrong.Failures.Count;
            bool threw = false;
            try { wrong.Session.Receive(CsWire.Encode(new CsMessage { Type = CsWire.CsWelcome, Result = 7 })); } catch (Exception) { threw = true; }
            Check("unknown_refusal_code_handled", !threw && wrong.Failures.Count == failuresBefore + 1 && wrong.Failures[wrong.Failures.Count - 1] == "server.err.version");
            wrong.Leave();
            Client fourth = Join(1, code, null);
            Check("fourth_seat", fourth.Session.Seat == 4);
            Client fifth = Join(1, code, null);
            Check("fifth_room_full", fifth.Failures.Count == 1 && fifth.Failures[0] == "server.err.full" && fifth.Session.Seat == 0);
            fifth.Leave();

            Client invalid = Join(2, code, new byte[16]);
            Check("invalid_reconnect_token", invalid.Failures.Count == 1 && invalid.Failures[0] == "server.err.token");
            invalid.Leave();
            Client version = Join(3, code, null);
            Check("version_failure", version.Failures.Count == 1 && version.Failures[0] == "server.err.version");
            version.Leave();
            rejoin.Session.Receive(CsWire.Encode(new CsMessage { Type = CsWire.CsError, ErrorCode = 1, Text = "测试错误" }));
            Check("cs_error_join_failed", rejoin.Failures.Count == 1 && rejoin.Failures[0] == "测试错误");
        }

        static void MultiSeatTests()
        {
            Client a = new Client(0, "", null, 2); clients.Add(a);
            Until(delegate { return a.Session.Seats.Count == 2 && a.Session.IsArbiter; }, 1500);
            Client b = Join(1, a.Session.Code, null);
            Check("multi_client_seats_primary_authority", a.Session.Seats.Count == 2 &&
                a.Session.Seats[0] == 1 && a.Session.Seats[1] == 2 && a.Session.PrimarySeat == 1 &&
                a.Session.Authority.LocalSeat == 1 && b.Session.PrimarySeat == 3 && a.Session.IsArbiter);
            a.Session.SendAs(2, 30, true, delegate(BinaryWriter w) { w.Write((byte)9); });
            Check("multi_client_sendas_secondary", Until(delegate { return b.Game.Count == 1; }, 700) &&
                b.Game[0].Seat == 2 && b.Game[0].Value == 9);
            bool rejected = false;
            try { a.Session.SendAs(3, 30, true, null); } catch (ArgumentException) { rejected = true; }
            Check("multi_client_sendas_unowned_rejected", rejected);
            b.Session.SendTo(2, 31, true, delegate(BinaryWriter w) { w.Write((byte)8); });
            Check("multi_client_sendto_secondary", Until(delegate { return a.Game.Count == 1; }, 700) &&
                a.Game[0].Type == 31 && a.Game[0].Seat == 3);
            a.Session.SendToAs(2, 3, 32, true, delegate(BinaryWriter w) { w.Write((byte)7); });
            Check("multi_client_sendtoas_secondary", Until(delegate { return b.Game.Count == 2; }, 700) &&
                b.Game[1].Type == 32 && b.Game[1].Seat == 2);
            // Seat 2's lease must survive adding/releasing a different local seat.
            a.Session.SendAs(2, CsWire.Encode(new CsMessage { Type = CsWire.CsLease, Op = 1, Key = "secondary", Score = 1 }), true);
            Until(delegate { return b.Session.Authority.OwnerOf("secondary") == 2; }, 700);
            a.Session.AddSeat(444, "extra");
            bool added = Until(delegate { return a.Session.Seats.Count == 3; }, 700);
            Check("multi_client_add_seat_keeps_secondary_lease", added && a.Session.Seats[2] == 4 &&
                b.Session.Authority.OwnerOf("secondary") == 2 && a.Session.Authority.LocalSeat == 1);
            a.Session.ReleaseSeat(4);
            bool released = Until(delegate { return a.Session.Seats.Count == 2; }, 700);
            a.Session.SendAs(2, CsWire.Encode(new CsMessage { Type = CsWire.CsState, Key = "secondary", Epoch = 1,
                Ver = 1, Data = new byte[] { 9 } }), true);
            bool state = Until(delegate { return b.States.Count == 1; }, 700);
            Check("multi_client_release_other_lease_survives", released && state && b.States[0].From == 2 &&
                b.Session.Authority.OwnerOf("secondary") == 2);
            byte[] token = a.Session.Token; string code = a.Session.Code;
            a.Leave();
            Check("multi_client_offline_all", Until(delegate { return b.Offline.Contains(1) && b.Offline.Contains(2); }, 700));
            Client rejoin = Join(2, code, token);
            Check("multi_client_rejoin_restores_all", Until(delegate { return rejoin.Session.Seats.Count == 2; }, 700) &&
                rejoin.Session.Seats[0] == 1 && rejoin.Session.Seats[1] == 2 && rejoin.Session.IsArbiter &&
                rejoin.Snapshots == 1 && rejoin.Session.Authority.LocalSeat == 1);
        }

        static void RoomListAndKickTests()
        {
            Client manager = Join(0, "", null), target = Join(1, manager.Session.Code, null);
            manager.Session.SetRoomInfo("lv=1;pct=6;area=林地");
            RunFor(30);
            List<CsRoomListEntry> entries = null;
            string error = null;
            Thread fetch = new Thread(delegate() { entries = CServerRoomList.Fetch(
                new IPEndPoint(IPAddress.Loopback, server.LocalPort), 700, out error); });
            fetch.Start();
            Until(delegate { return !fetch.IsAlive; }, 1500); fetch.Join();
            CsRoomListEntry entry = entries == null ? null : entries.Find(delegate(CsRoomListEntry r) { return r.Code == manager.Session.Code; });
            Check("fetch_inprocess_room_entry", error == null && entry != null && entry.Online == 2 && entry.Capacity == 4
                && entry.Nicks.Count == 2 && entry.Nicks[0] == "player0" && entry.Text == "lv=1;pct=6;area=林地");
            int deadPort;
            using (OriCoop.Net.UdpTransport unused = new OriCoop.Net.UdpTransport(0)) deadPort = unused.LocalPort;
            Stopwatch timeout = Stopwatch.StartNew();
            entries = CServerRoomList.Fetch(new IPEndPoint(IPAddress.Loopback, deadPort), 150, out error);
            Check("fetch_dead_port_timeout", entries == null && !String.IsNullOrEmpty(error) && timeout.ElapsedMilliseconds >= 150
                && timeout.ElapsedMilliseconds < 500);
            manager.Session.Kick(target.Session.Seat);
            Check("client_kicked_error_refused_and_disconnect", Until(delegate { return target.Failures.Count == 1; }, 700)
                && target.Failures[0] == "server.err.kicked" && target.Session.RefusedWith == "server.err.kicked"
                && target.Session.Peer.State == OriCoop.Net.PeerState.Disconnected
                && target.Session.Peer.DisconnectReason == "server.err.kicked");
            // T-173 (review F03): a kicked session ends for good; only other refusals and drops reconnect.
            Check("client_kicked_never_retries", !CServerClient.RetryAllowed(target.Session.RefusedWith)
                && CServerClient.RetryAllowed("server.err.token") && CServerClient.RetryAllowed("server.err.noroom")
                && CServerClient.RetryAllowed(null) && CServerClient.RetryAllowed("timed out"));
            Client blocked = new Client(2, manager.Session.Code, target.Session.Token);
            blocked.Session.RejoinFallsBackToJoin = true; clients.Add(blocked);
            blocked.Session.Receive(CsWire.Encode(new CsMessage { Type = CsWire.CsWelcome, Result = 5 }));
            Check("client_result5_maps_kicked_no_fallback", blocked.Failures.Count == 1 && blocked.Failures[0] == "server.err.kicked"
                && blocked.Session.RefusedWith == "server.err.kicked" && !blocked.Session.UsedFallback
                && blocked.Session.Peer.DisconnectReason == "server.err.kicked");
            manager.Leave(); target.Leave(); blocked.Leave();

            // T-173 (review F10): a room-service room (我的房间 / password room, CServerClient.Private) sets its room info
            // locally only, so the server's public list (rooms with a room info text) never shows its code.
            Client hidden = Join(0, "", null);
            hidden.Session.Private = true;
            hidden.Session.SetRoomInfo("lv=1;pct=6;area=林地");
            RunFor(30);
            List<CsRoomListEntry> listed = null;
            string listError = null;
            Thread list = new Thread(delegate() { listed = CServerRoomList.Fetch(
                new IPEndPoint(IPAddress.Loopback, server.LocalPort), 700, out listError); });
            list.Start();
            Until(delegate { return !list.IsAlive; }, 1500); list.Join();
            Check("private_room_info_stays_off_room_list", listError == null && listed != null
                && hidden.Session.RoomInfo == "lv=1;pct=6;area=林地"
                && !listed.Exists(delegate(CsRoomListEntry r) { return r.Code == hidden.Session.Code; }));
            hidden.Leave();
        }

        // T-173: CServerClient.Created = this PC created the room (its own mode-0 join, or a token rejoin of a room it created).
        static void CreatedTests()
        {
            Client creator = Join(0, "", null), joiner = Join(1, creator.Session.Code, null);
            Check("created_true_for_mode0_false_for_joiner", creator.Session.Created && !joiner.Session.Created);
            string code = creator.Session.Code;
            Client back = new Client(2, code, creator.Session.Token);
            back.Session.RejoinCreated = true; clients.Add(back);
            Client joinerBack = new Client(2, code, joiner.Session.Token);
            clients.Add(joinerBack);
            Check("created_kept_by_creator_token_rejoin_not_by_joiner", Until(delegate {
                return back.Assigned.Count == 1 && joinerBack.Assigned.Count == 1; }, 1500)
                && back.Session.Created && !joinerBack.Session.Created);
            Client fallback = new Client(2, code, new byte[16]);
            fallback.Session.RejoinFallsBackToJoin = true; fallback.Session.RejoinCreated = true; clients.Add(fallback);
            Check("created_false_after_rejoin_falls_back_to_join", Until(delegate { return fallback.Assigned.Count == 1; }, 1500)
                && fallback.Session.UsedFallback && !fallback.Session.Created);
            creator.Leave(); joiner.Leave(); back.Leave(); joinerBack.Leave(); fallback.Leave();
        }

        static CsRoomListEntry Room(string code, params string[] nicks)
        {
            CsRoomListEntry room = new CsRoomListEntry { Code = code, Online = (byte)nicks.Length, Text = "t" };
            room.Nicks.AddRange(nicks);
            return room;
        }

        static List<CsRoomListEntry> Rooms(params CsRoomListEntry[] rooms) { return new List<CsRoomListEntry>(rooms); }

        // T-173 (review F14, F15, F16): the room list page's state (NativeCsRooms over CsRoomListCache).
        static void RoomListCacheTests()
        {
            CsRoomListCache cache = new CsRoomListCache();
            int a = cache.BeginFetch("a:47022", 0, 5);
            int b = cache.BeginFetch("b:47022", 0.5, 5);
            bool staleDropped = a >= 0 && b >= 0 && a != b && !cache.Complete(a, Rooms(Room("111111", "x")))
                && cache.TakeShown(1).Count == 0 && cache.State == CsRoomListCache.Loading;
            bool failedClean = cache.Complete(b, null) && cache.State == CsRoomListCache.Failed && cache.TakeShown(2).Count == 0;
            Check("room_list_slow_fetch_for_previous_server_dropped", staleDropped && failedClean);
            Check("room_list_fetch_not_due_inside_interval", cache.BeginFetch("b:47022", 1, 5) == -1);

            int c = cache.BeginFetch("b:47022", 10, 5);
            cache.Complete(c, Rooms(Room("222222", "x"), Room("333333", "y")));
            List<CsRoomListEntry> drawn = cache.TakeShown(3);
            int d = cache.BeginFetch("b:47022", 20, 5);
            cache.Complete(d, Rooms(Room("333333", "y")));
            bool pressMapsDrawn = drawn.Count == 2 && cache.Shown(0).Code == "222222" && cache.Shown(1).Code == "333333"
                && cache.Shown(2) == null && cache.TakeShown(3).Count == 2;
            Check("room_list_press_maps_rows_drawn_not_newer_list", pressMapsDrawn
                && cache.TakeShown(4).Count == 1 && cache.Shown(0).Code == "333333" && cache.Shown(1) == null);

            CsRoomListCache fresh = new CsRoomListCache();
            int e = fresh.BeginFetch("c:47022", 0, 5);
            fresh.TakeShown(1);
            fresh.Complete(e, Rooms(Room("444444", "z")));
            Check("room_list_loading_row_press_does_not_join_unseen_room", fresh.Shown(0) == null);

            // T-173 (review F15): a cursor move rebuilds the rows (TakeShown in a new frame) without drawing them, so a
            // press maps to the room whose text is drawn on the row, not to `shown` at that index.
            Func<CsRoomListEntry, string> describe = delegate(CsRoomListEntry r) { return "ROOM " + r.Code + "\n" + CsRoomListCache.PlayersLine(r); };
            CsRoomListCache moved = new CsRoomListCache();
            int m1 = moved.BeginFetch("d:47022", 0, 5);
            moved.TakeShown(1);                                    // drawn: create, join, "loading..."
            moved.Complete(m1, Rooms(Room("777777", "x")));
            moved.TakeShown(2);                                    // cursor moved onto the status row: rows built, not drawn
            bool statusRowNoJoin = moved.Shown(0) != null && moved.Drawn(0, "loading", describe) == null
                && moved.Drawn(0, null, describe) == null;
            CsRoomListEntry ra = Room("888888", "a"), rb = Room("999999", "b");
            int m2 = moved.BeginFetch("d:47022", 10, 5);
            moved.Complete(m2, Rooms(ra, rb));
            moved.TakeShown(3);                                    // drawn: [A, B]
            string drawnA = describe(ra), drawnB = describe(rb);
            int m3 = moved.BeginFetch("d:47022", 20, 5);
            moved.Complete(m3, Rooms(Room("999999", "b", "c"), Room("888888", "a")));
            moved.TakeShown(4);                                    // reordered list taken by a cursor move, not drawn
            CsRoomListEntry pressA = moved.Drawn(0, drawnA, describe), pressB = moved.Drawn(1, drawnB, describe);
            bool reorderedJoinsDrawn = moved.Shown(0).Code == "999999" && pressA != null && pressA.Code == "888888"
                && pressB != null && pressB.Code == "999999";
            moved.BeginFetch("e:47022", 30, 5);
            bool otherServerForgets = moved.Drawn(0, drawnA, describe) == null;
            Check("room_list_press_maps_text_drawn_not_rows_built_on_cursor_move", statusRowNoJoin && reorderedJoinsDrawn && otherServerForgets);

            Check("room_list_players_line_names_nameless_seats", CsRoomListCache.PlayersLine(Room("555555", "", "B")) == "P1, B"
                && CsRoomListCache.PlayersLine(Room("666666", "A", "B")) == "A, B");
        }

        public static int Main()
        {
            try
            {
                server = new CServer(new ArbiterOptions { SeatHoldMs = 1500, ActiveWindowMs = 300,
                    MinHoldMs = 500, RoomIdleMs = 1500 });
                server.Start(0);
                Tests();
                MultiSeatTests();
                LeaveTests();
                RoomListAndKickTests();
                CreatedTests();
                RoomListCacheTests();
            }
            catch (Exception ex) { Check("unexpected_exception", false); Console.WriteLine(ex); }
            finally
            {
                foreach (Client c in clients) if (!c.Left) c.Leave();
                if (server != null) server.Stop();
            }
            bool success = passed == total;
            Console.WriteLine("RESULT " + (success ? "PASS " + passed : "FAIL " + (total - passed)) + "/" + total);
            return success ? 0 : 1;
        }
    }
}
