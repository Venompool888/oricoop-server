using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace OriCoop.Authority
{
    public sealed class ArbiterOptions
    {
        public double SeatHoldMs = 60000;
        public double ActiveWindowMs = 300;
        public double MinHoldMs = 500;
        public int EventKeep = 4096;
        public double RoomIdleMs = 60000;
    }

    public sealed class Outgoing
    {
        public int To;
        public byte[] Message;
    }

    public sealed class Arbiter
    {
        sealed class SeatInfo
        {
            public byte[] Token;
            public bool Online;
            public ulong Steam;
            public string Nick;
            public double OfflineAt;
            public uint LastEvent;
            public readonly HashSet<uint> Seen = new HashSet<uint>();
            public uint MaxSeq;
            public void NewSession() { Seen.Clear(); MaxSeq = 0; }

            // Bounded dedup: remembers the last SeqWindow sequence numbers; anything older counts as already seen.
            public bool FirstTime(uint seq)
            {
                if (MaxSeq >= SeqWindow && seq <= MaxSeq - SeqWindow) return false;
                if (!Seen.Add(seq)) return false;
                if (seq > MaxSeq) MaxSeq = seq;
                if (Seen.Count > 2 * SeqWindow)
                {
                    uint floor = MaxSeq >= SeqWindow ? MaxSeq - SeqWindow : 0;
                    Seen.RemoveWhere(delegate(uint s) { return s <= floor; });
                }
                return true;
            }
        }

        static readonly RandomNumberGenerator Random = new RNGCryptoServiceProvider();
        public const uint SeqWindow = 4096;
        public const byte FlagClaim = 1, FlagRelease = 2;

        sealed class LeaseInfo
        {
            public byte Owner;
            public uint Epoch;
            public double HeldAt, LastActive = Double.NegativeInfinity;
            public readonly Dictionary<int, float> Interest = new Dictionary<int, float>();
        }

        readonly SeatInfo[] seats = new SeatInfo[5];
        readonly Dictionary<string, LeaseInfo> leases = new Dictionary<string, LeaseInfo>();
        readonly Dictionary<string, CsStateEntry> states = new Dictionary<string, CsStateEntry>();
        // Claim key -> seat that won it. Keys that can happen again (respawning pickups, re-openable doors)
        // either append a generation ("p:<guid>#<gen>") or are released with FlagRelease.
        readonly Dictionary<string, int> claimed = new Dictionary<string, int>();
        readonly List<CsMessage> events = new List<CsMessage>();
        readonly ArbiterOptions options;
        readonly string code;
        uint lastEvent;
        double emptyAt;
        long droppedStates;

        public Arbiter(string code, ArbiterOptions options, double nowMs)
        {
            this.code = code;
            this.options = options;
            emptyAt = nowMs;
        }

        public long DroppedStates { get { return droppedStates; } }
        public int SeenCount(int seat) { return seats[seat] == null ? 0 : seats[seat].Seen.Count; }

        public bool IsOnline(int seat, double nowMs)
        {
            return seat >= 1 && seat <= 4 && seats[seat] != null && seats[seat].Online;
        }

        public bool IsIdle(double nowMs)
        {
            for (int s = 1; s <= 4; s++) if (IsOnline(s, nowMs)) return false;
            return nowMs - emptyAt >= options.RoomIdleMs;
        }

        void Add(List<Outgoing> output, int to, CsMessage message, double nowMs)
        {
            output.Add(new Outgoing { To = to, Message = CsWire.Encode(message) });
        }

        void Roster(List<Outgoing> output, double nowMs)
        {
            CsMessage message = new CsMessage { Type = CsWire.CsRoster };
            for (byte s = 1; s <= 4; s++)
            {
                SeatInfo info = seats[s];
                if (info != null) message.Roster.Add(new CsRosterEntry { Seat = s,
                    Online = info.Online, Steam = info.Steam, Nick = info.Nick });
            }
            Add(output, 0, message, nowMs);
        }

        void Snapshot(List<Outgoing> output, int seat, uint after, double nowMs)
        {
            CsMessage snapshot = new CsMessage { Type = CsWire.CsSnapshot };
            foreach (KeyValuePair<string, LeaseInfo> pair in leases)
                snapshot.Leases.Add(new CsLeaseEntry { Key = pair.Key, Owner = pair.Value.Owner, Epoch = pair.Value.Epoch });
            foreach (CsStateEntry state in states.Values) snapshot.States.Add(state);
            Add(output, seat, snapshot, nowMs);
            foreach (CsMessage e in events) if (e.Id > after) Add(output, seat, e, nowMs);
        }

        public List<Outgoing> Tick(double nowMs)
        {
            List<Outgoing> output = new List<Outgoing>();
            bool changed = false;
            for (int s = 1; s <= 4; s++)
            {
                SeatInfo info = seats[s];
                if (info == null || info.Online || nowMs - info.OfflineAt < options.SeatHoldMs) continue;
                seats[s] = null; changed = true;
                foreach (LeaseInfo lease in leases.Values) lease.Interest.Remove(s);
            }
            if (changed) Roster(output, nowMs);
            return output;
        }

        public static int PrimarySeat(IEnumerable<int> seatSet)
        {
            int primary = 0;
            foreach (int seat in seatSet) if (primary == 0 || seat < primary) primary = seat;
            return primary;
        }

        // All seats of a device share the same token object; seat identity/lease ownership stays independent.
        public List<int> ConnectionSeats(int seat)
        {
            List<int> owned = new List<int>();
            if (seat < 1 || seat > 4 || seats[seat] == null) return owned;
            byte[] token = seats[seat].Token;
            for (int s = 1; s <= 4; s++)
                if (seats[s] != null && seats[s].Token == token) owned.Add(s);
            return owned;
        }

        CsMessage Welcome(byte result, List<int> owned)
        {
            int primary = PrimarySeat(owned);
            CsMessage message = new CsMessage { Type = CsWire.CsWelcome, Result = result, Code = code,
                Token = primary == 0 ? new byte[16] : seats[primary].Token, LastEvent = lastEvent };
            message.Seats.AddRange(owned);
            return message;
        }

        public List<Outgoing> Join(CsMessage request, double nowMs, out int seat, out byte[] welcome)
        {
            List<Outgoing> output = Tick(nowMs);
            List<int> owned = new List<int>();
            byte result = 0;
            if (request.Proto != 2 || request.Mode > 2 || request.Count < 1 || request.Count > 3 ||
                (request.Identities.Count != request.Count && !(request.Count == 1 && request.Identities.Count == 0))) result = 3;
            else if (request.Mode == 2)
            {
                string token = Convert.ToBase64String(request.Token);
                for (int s = 1; s <= 4; s++)
                    if (seats[s] != null && Convert.ToBase64String(seats[s].Token) == token)
                    { owned = ConnectionSeats(s); break; }
                if (owned.Count == 0) result = 4;
                else if (seats[owned[0]].Online) output.AddRange(Offline(owned[0], nowMs));
            }
            else
            {
                for (int s = 1; s <= 4 && owned.Count < request.Count; s++)
                    if (seats[s] == null) owned.Add(s);
                if (owned.Count != request.Count) { owned.Clear(); result = 2; }
                else
                {
                    byte[] token = new byte[16];
                    lock (Random) Random.GetBytes(token);
                    for (int i = 0; i < owned.Count; i++)
                    {
                        CsIdentity identity = request.Identities.Count == 0
                            ? new CsIdentity { Steam = request.Steam, Nick = request.Nick } : request.Identities[i];
                        seats[owned[i]] = new SeatInfo { Token = token, Steam = identity.Steam, Nick = identity.Nick };
                    }
                }
            }
            seat = PrimarySeat(owned);
            uint after = seat == 0 ? 0 : seats[seat].LastEvent;
            if (result == 0)
                foreach (int s in owned) { seats[s].Online = true; seats[s].NewSession(); }
            welcome = CsWire.Encode(Welcome(result, owned));
            if (result == 0)
            {
                Snapshot(output, seat, after, nowMs);
                Roster(output, nowMs);
            }
            return output;
        }

        List<Outgoing> ChangeSeat(int seat, CsMessage request, double nowMs)
        {
            List<Outgoing> output = new List<Outgoing>();
            List<int> owned = ConnectionSeats(seat);
            int primary = PrimarySeat(owned);
            if (request.Type == CsWire.CsSeatAdd)
            {
                int free = 0;
                for (int s = 1; s <= 4; s++) if (seats[s] == null) { free = s; break; }
                if (free == 0) { Add(output, primary, Welcome(2, owned), nowMs); return output; }
                seats[free] = new SeatInfo { Token = seats[primary].Token, Online = true,
                    Steam = request.Steam, Nick = request.Nick };
            }
            else
            {
                if (!owned.Contains(request.Target) || request.Target == primary)
                {
                    Add(output, primary, new CsMessage { Type = CsWire.CsError, ErrorCode = 2,
                        Text = "cannot release primary or unowned seat" }, nowMs);
                    return output;
                }
                int released = request.Target;
                seats[released] = null;
                foreach (LeaseInfo lease in leases.Values) lease.Interest.Remove(released);
                foreach (KeyValuePair<string, LeaseInfo> pair in leases)
                    if (pair.Value.Owner == released) Handoff(pair.Key, pair.Value, output, nowMs);
            }
            owned = ConnectionSeats(primary);
            Add(output, primary, Welcome(0, owned), nowMs);
            Roster(output, nowMs);
            return output;
        }

        public List<Outgoing> JoinAt(int seat, ulong steam, string nick, double nowMs)
        {
            List<Outgoing> output = new List<Outgoing>();
            SeatInfo info = seats[seat];
            if (info == null)
            {
                byte[] token = new byte[16];
                lock (Random) Random.GetBytes(token);
                info = new SeatInfo { Token = token, Steam = steam, Nick = nick };
                seats[seat] = info;
            }
            info.Online = true;
            info.NewSession();
            Snapshot(output, seat, info.LastEvent, nowMs);
            Roster(output, nowMs);
            return output;
        }

        void Grant(string key, LeaseInfo lease, int owner, List<Outgoing> output, double nowMs)
        {
            lease.Owner = (byte)owner; lease.Epoch++;
            lease.HeldAt = nowMs; lease.LastActive = Double.NegativeInfinity;
            lease.Interest.Remove(owner);
            Add(output, 0, new CsMessage { Type = CsWire.CsGrant, Key = key,
                Owner = lease.Owner, Epoch = lease.Epoch }, nowMs);
        }

        void Handoff(string key, LeaseInfo lease, List<Outgoing> output, double nowMs)
        {
            int best = 0;
            float score = Single.PositiveInfinity;
            for (int s = 1; s <= 4; s++)
            {
                float candidate;
                if (s != lease.Owner && IsOnline(s, nowMs) && lease.Interest.TryGetValue(s, out candidate) &&
                    (best == 0 || candidate < score)) { best = s; score = candidate; }
            }
            Grant(key, lease, best, output, nowMs);
        }

        public List<Outgoing> Offline(int seat, double nowMs)
        {
            List<Outgoing> output = new List<Outgoing>();
            if (!IsOnline(seat, nowMs)) return output;
            List<int> owned = ConnectionSeats(seat);
            // Mark the entire device offline before choosing successors (never hand off to its other seat).
            foreach (int s in owned)
            {
                seats[s].Online = false; seats[s].OfflineAt = nowMs; seats[s].LastEvent = lastEvent;
                foreach (LeaseInfo lease in leases.Values) lease.Interest.Remove(s);
            }
            foreach (KeyValuePair<string, LeaseInfo> pair in leases)
                if (owned.Contains(pair.Value.Owner)) Handoff(pair.Key, pair.Value, output, nowMs);
            bool any = false;
            for (int s = 1; s <= 4; s++) if (IsOnline(s, nowMs)) any = true;
            if (!any) emptyAt = nowMs;
            Roster(output, nowMs);
            return output;
        }

        public List<Outgoing> Handle(int seat, CsMessage request, double nowMs)
        {
            List<Outgoing> output = new List<Outgoing>();
            if (!IsOnline(seat, nowMs)) return output;
            if (request.Type == CsWire.CsSeatAdd || request.Type == CsWire.CsSeatRelease)
                return ChangeSeat(seat, request, nowMs);
            LeaseInfo lease;
            if (request.Type == CsWire.CsLease)
            {
                if (!leases.TryGetValue(request.Key, out lease))
                {
                    if (request.Op != 1) return output;
                    lease = new LeaseInfo(); leases.Add(request.Key, lease);
                }
                if (request.Op == 1)
                {
                    if (lease.Owner == seat) return output;
                    lease.Interest[seat] = request.Score;
                    if (lease.Owner == 0 || !IsOnline(lease.Owner, nowMs) ||
                        (nowMs - lease.LastActive >= options.ActiveWindowMs && nowMs - lease.HeldAt >= options.MinHoldMs))
                        Grant(request.Key, lease, seat, output, nowMs);
                }
                else if (request.Op == 2)
                {
                    lease.Interest.Remove(seat);
                    if (lease.Owner == seat) Handoff(request.Key, lease, output, nowMs);
                }
            }
            else if (request.Type == CsWire.CsState)
            {
                CsStateEntry prior;
                if (!leases.TryGetValue(request.Key, out lease) || lease.Owner != seat || lease.Epoch != request.Epoch ||
                    (states.TryGetValue(request.Key, out prior) && request.Ver <= prior.Ver))
                {
                    droppedStates++; return output;
                }
                states[request.Key] = new CsStateEntry { Key = request.Key, Epoch = request.Epoch,
                    Ver = request.Ver, Active = request.Active, Data = (byte[])request.Data.Clone() };
                if (request.Active != 0) lease.LastActive = nowMs;
                request.Seat = (byte)seat;
                Add(output, -seat, request, nowMs);
            }
            else if (request.Type == CsWire.CsAction)
            {
                if (!seats[seat].FirstTime(request.Seq)) return output;
                if ((request.Flags & FlagRelease) != 0)
                {
                    // Release: only the winner (or anyone once the winner's seat is gone) frees the key; broadcast as an event.
                    int winner;
                    if (!claimed.TryGetValue(request.Key, out winner)) return output;
                    if (winner != seat && seats[winner] != null) return output;
                    claimed.Remove(request.Key);
                }
                else if ((request.Flags & FlagClaim) != 0 && claimed.ContainsKey(request.Key))
                {
                    Add(output, seat, new CsMessage { Type = CsWire.CsEvent, From = (byte)seat,
                        Seq = request.Seq, Kind = 0xFF, Key = request.Key, Data = new byte[] { request.Kind } }, nowMs);
                    return output;
                }
                else if ((request.Flags & FlagClaim) != 0) claimed[request.Key] = seat;
                CsMessage e = new CsMessage { Type = CsWire.CsEvent, Id = ++lastEvent, From = (byte)seat,
                    Seq = request.Seq, Kind = request.Kind, Key = request.Key, Data = (byte[])request.Data.Clone() };
                events.Add(e);
                while (events.Count > options.EventKeep) events.RemoveAt(0);
                Add(output, 0, e, nowMs);
            }
            return output;
        }
    }
}
