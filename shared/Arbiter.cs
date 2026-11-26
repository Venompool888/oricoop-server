using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace OriCoop.Authority
{
    public sealed class ArbiterOptions
    {
        public double SeatHoldMs = 60000;
        public double ActiveWindowMs = 300;
        public double MinHoldMs = 500;
        public int EventKeep = 4096;
        public double RoomIdleMs = 60000;
        // T-146: a quiet owner loses a lease only to a better (lower) score; after this much silence it counts as stalled.
        public double StealAnywayMs = 2000;
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
            public uint LastEvent, WorldWatermark;
            public readonly HashSet<uint> Seen = new HashSet<uint>();
            public uint MaxSeq;
            public void NewSession() { Seen.Clear(); MaxSeq = 0; WorldWatermark = 0; }

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
        public const byte WorldBatchKind = 0x57, WorldWatermarkKind = 0x58;

        sealed class LeaseInfo
        {
            public byte Owner;
            public uint Epoch;
            public double HeldAt, LastActive = Double.NegativeInfinity;
            public float OwnerScore = Single.PositiveInfinity;   // T-146: the owner's latest request score
            public readonly Dictionary<int, float> Interest = new Dictionary<int, float>();
        }

        readonly SeatInfo[] seats = new SeatInfo[5];
        readonly Dictionary<string, LeaseInfo> leases = new Dictionary<string, LeaseInfo>();
        readonly Dictionary<string, CsStateEntry> states = new Dictionary<string, CsStateEntry>();
        // Claim key -> seat that won it. Keys that can happen again (respawning pickups, re-openable doors)
        // either append a generation ("p:<guid>#<gen>") or are released with FlagRelease.
        readonly Dictionary<string, int> claimed = new Dictionary<string, int>();
        readonly List<CsMessage> events = new List<CsMessage>();
        readonly HashSet<ulong> kicked = new HashSet<ulong>();
        string text = "";
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

        public CsRoomListEntry Listing()
        {
            CsRoomListEntry entry = new CsRoomListEntry { Code = code, Text = text };
            // T-173: a seat without a name (no Steam on that PC) is listed as P<seat>, like the room page, not as "".
            for (int s = 1; s <= 4; s++)
                if (seats[s] != null && seats[s].Online)
                { entry.Online++; entry.Nicks.Add(seats[s].Nick.Length > 0 ? seats[s].Nick : "P" + s); }
            return entry;
        }

        // T-173: the longest text a Cs string can carry (u8 length + UTF-8).
        public const int MaxTextBytes = 255;

        // T-173: text kept by the server (room info, nicks) is cut to whole characters that fit MaxTextBytes once
        // re-encoded, so a later CsRoster / CsRoomList can always be encoded (a too-long string made Encode throw and
        // stopped the whole server). A lone surrogate counts as the 3-byte U+FFFD it is encoded as.
        public static string ClampUtf8(string value, int maxBytes)
        {
            if (string.IsNullOrEmpty(value)) return "";
            if (Encoding.UTF8.GetByteCount(value) <= maxBytes) return value;
            int bytes = 0, i = 0;
            while (i < value.Length)
            {
                int length = char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]) ? 2 : 1;
                int size = Encoding.UTF8.GetByteCount(value.Substring(i, length));
                if (bytes + size > maxBytes) break;
                bytes += size; i += length;
            }
            return value.Substring(0, i);
        }

        bool IsManager(int seat)
        {
            for (int s = 1; s <= 4; s++)
                if (seats[s] != null && seats[s].Online) return ConnectionSeats(s).Contains(seat);
            return false;
        }

        // T-173: SteamID 0 means "no Steam on that PC" (shared by every such client): it is never banned or matched.
        bool IsKicked(CsMessage request)
        {
            if (request.Identities.Count == 0) return request.Steam != 0 && kicked.Contains(request.Steam);
            foreach (CsIdentity identity in request.Identities)
                if (identity.Steam != 0 && kicked.Contains(identity.Steam)) return true;
            return false;
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

        // Any existing device may become snapshot host if the current host leaves before transfer.
        // Skip only history all those devices have actually applied. Mixed/older clients report zero.
        uint SharedWorldWatermark(int joining)
        {
            uint watermark = uint.MaxValue;
            for (int s = 1; s <= 4; s++)
                if (seats[s] != null && seats[s].Online && seats[s].Token != seats[joining].Token &&
                    s == PrimarySeat(ConnectionSeats(s)))
                    watermark = Math.Min(watermark, seats[s].WorldWatermark);
            return watermark == uint.MaxValue ? 0 : watermark;
        }

        void Snapshot(List<Outgoing> output, int seat, uint after, bool fresh, double nowMs)
        {
            CsMessage snapshot = new CsMessage { Type = CsWire.CsSnapshot };
            foreach (KeyValuePair<string, LeaseInfo> pair in leases)
                snapshot.Leases.Add(new CsLeaseEntry { Key = pair.Key, Owner = pair.Value.Owner, Epoch = pair.Value.Epoch });
            foreach (CsStateEntry state in states.Values) snapshot.States.Add(state);
            Add(output, seat, snapshot, nowMs);
            uint covered = fresh ? SharedWorldWatermark(seat) : 0;
            foreach (CsMessage e in events)
                if (e.Id > after && (e.Kind != WorldBatchKind || e.Id > covered)) Add(output, seat, e, nowMs);
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
            else if (request.Mode != 0 && IsKicked(request)) result = 5;
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
                        seats[owned[i]] = new SeatInfo { Token = token, Steam = identity.Steam,
                            Nick = ClampUtf8(identity.Nick, MaxTextBytes) };
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
                Snapshot(output, seat, after, request.Mode != 2, nowMs);
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
                    Steam = request.Steam, Nick = ClampUtf8(request.Nick, MaxTextBytes) };
            }
            else if (request.Target == 0)
            {
                // T-113: Target 0 = this device leaves the room on purpose: free all its seats now instead of holding them
                // SeatHoldMs for a reconnect (a 4-player room said "full" to the player rejoining at once). Older servers
                // answer CsError (unowned seat) and keep the hold; older clients never send Target 0.
                output.AddRange(Offline(primary, nowMs));
                foreach (int s in owned)
                {
                    seats[s] = null;
                    foreach (LeaseInfo lease in leases.Values) lease.Interest.Remove(s);
                }
                Roster(output, nowMs);
                return output;
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
            bool fresh = info == null;
            if (fresh)
            {
                byte[] token = new byte[16];
                lock (Random) Random.GetBytes(token);
                info = new SeatInfo { Token = token, Steam = steam, Nick = ClampUtf8(nick, MaxTextBytes) };
                seats[seat] = info;
            }
            info.Online = true;
            info.NewSession();
            Snapshot(output, seat, info.LastEvent, fresh, nowMs);
            Roster(output, nowMs);
            return output;
        }

        void Grant(string key, LeaseInfo lease, int owner, List<Outgoing> output, double nowMs)
        {
            lease.Owner = (byte)owner; lease.Epoch++;
            lease.HeldAt = nowMs; lease.LastActive = Double.NegativeInfinity;
            float score;
            lease.OwnerScore = lease.Interest.TryGetValue(owner, out score) ? score : Single.PositiveInfinity;
            lease.Interest.Remove(owner);
            Add(output, 0, new CsMessage { Type = CsWire.CsGrant, Key = key,
                Owner = lease.Owner, Epoch = lease.Epoch }, nowMs);
        }

        // T-146: a quiet owner (no state for ActiveWindowMs, held MinHoldMs) hands the lease only to a score at least as good;
        // after StealAnywayMs of silence it is stalled and anyone takes over. One lost unreliable heartbeat or a 400 ms
        // hitch no longer gives an enemy to the far player for good.
        bool MayTake(LeaseInfo lease, float score, double nowMs)
        {
            double quiet = nowMs - lease.LastActive;
            if (quiet < options.ActiveWindowMs || nowMs - lease.HeldAt < options.MinHoldMs) return false;
            return quiet >= options.StealAnywayMs || score <= lease.OwnerScore;   // ties may take over, as before
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
            if (!any) { emptyAt = nowMs; text = ""; }
            Roster(output, nowMs);
            return output;
        }

        public List<Outgoing> Handle(int seat, CsMessage request, double nowMs)
        {
            List<Outgoing> output = new List<Outgoing>();
            if (!IsOnline(seat, nowMs)) return output;
            if (request.Type == CsWire.CsRoomInfo)
            {
                if (IsManager(seat)) text = ClampUtf8(request.Text, MaxTextBytes);
                return output;
            }
            if (request.Type == CsWire.CsKick)
            {
                List<int> target = ConnectionSeats(request.Target);
                if (!IsManager(seat) || target.Count == 0 || target.Contains(seat))
                {
                    Add(output, seat, new CsMessage { Type = CsWire.CsError, ErrorCode = 2, Text = "cannot kick own or empty seat or not manager" }, nowMs);
                    return output;
                }
                foreach (int s in target) if (seats[s].Steam != 0) kicked.Add(seats[s].Steam);   // T-173: never ban "no Steam" (0)
                Add(output, PrimarySeat(target), new CsMessage { Type = CsWire.CsError, ErrorCode = 3, Text = "server.err.kicked" }, nowMs);
                output.AddRange(ChangeSeat(target[0], new CsMessage { Type = CsWire.CsSeatRelease, Target = 0 }, nowMs));
                return output;
            }
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
                    if (lease.Owner == seat) { lease.OwnerScore = request.Score; return output; }
                    lease.Interest[seat] = request.Score;
                    if (lease.Owner == 0 || !IsOnline(lease.Owner, nowMs) || MayTake(lease, request.Score, nowMs))
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
                // T-146: versions are per owner and epoch; a new owner's first state must not be dropped because the old
                // owner's counter was higher (the new owner then looked silent and the old one took the lease back).
                if (!leases.TryGetValue(request.Key, out lease) || lease.Owner != seat || lease.Epoch != request.Epoch ||
                    (states.TryGetValue(request.Key, out prior) && prior.Epoch == request.Epoch && request.Ver <= prior.Ver))
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
                if (request.Kind == WorldWatermarkKind && request.Key == "w:applied")
                {
                    if (request.Data != null && request.Data.Length == 4)
                    {
                        uint through = BitConverter.ToUInt32(request.Data, 0);
                        if (through <= lastEvent) seats[seat].WorldWatermark = Math.Max(seats[seat].WorldWatermark, through);
                    }
                    return output; // receipt, not a gameplay event
                }
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
