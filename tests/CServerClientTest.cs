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

            public Client(byte mode, string code, byte[] token)
            {
                Session = new CServerClient(new IPEndPoint(IPAddress.Loopback, server.LocalPort),
                    mode, code, token, (ulong)(clients.Count + 1), "player" + (clients.Count + 1));
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
                    if (bytes[0] >= CsWire.CsJoin && bytes[0] <= CsWire.CsTo)
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

            byte[] game = new byte[] { 1, 4, 90 };
            b.Session.Peer.SendUnreliable(game, 0, game.Length);
            bool forwarded = Until(delegate { return a.Game.Count == 1 && c.Game.Count == 1; }, 700);
            RunFor(30);
            Check("game_rewrites_seat_2_no_echo", forwarded && b.Game.Count == 0 &&
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
            Check("wrong_code_chinese_failure", wrong.Failures.Count == 1 && wrong.Failures[0] == "无此房间" &&
                wrong.Session.Seat == 0 && wrong.Assigned.Count == 0 && !wrong.Session.IsArbiter);
            wrong.Leave();
            Client fourth = Join(1, code, null);
            Check("fourth_seat", fourth.Session.Seat == 4);
            Client fifth = Join(1, code, null);
            Check("fifth_room_full", fifth.Failures.Count == 1 && fifth.Failures[0] == "房间已满" && fifth.Session.Seat == 0);
            fifth.Leave();

            Client invalid = Join(2, code, new byte[16]);
            Check("invalid_reconnect_token", invalid.Failures.Count == 1 && invalid.Failures[0] == "重连凭证无效");
            invalid.Leave();
            Client version = Join(3, code, null);
            Check("version_failure", version.Failures.Count == 1 && version.Failures[0] == "版本不符");
            version.Leave();
            rejoin.Session.Receive(CsWire.Encode(new CsMessage { Type = CsWire.CsError, ErrorCode = 1, Text = "测试错误" }));
            Check("cs_error_join_failed", rejoin.Failures.Count == 1 && rejoin.Failures[0] == "测试错误");
        }

        public static int Main()
        {
            try
            {
                server = new CServer(new ArbiterOptions { SeatHoldMs = 1500, ActiveWindowMs = 300,
                    MinHoldMs = 500, RoomIdleMs = 1500 });
                server.Start(0);
                Tests();
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
