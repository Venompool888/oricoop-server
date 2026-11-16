using System;
using System.Collections.Generic;

namespace OriCoop.Authority
{
    public sealed class AuthorityClient : IAuthority
    {
        readonly AuthorityMode mode;
        readonly Func<int> localSeat;
        readonly Action<byte[], bool> sendToArbiter;
        readonly bool arbiterIsLocal;
        readonly Dictionary<string, CsLeaseEntry> leases = new Dictionary<string, CsLeaseEntry>();
        readonly Dictionary<string, uint> versions = new Dictionary<string, uint>();
        readonly Dictionary<string, uint> receivedVersions = new Dictionary<string, uint>();
        readonly Dictionary<string, StateUpdate> latest = new Dictionary<string, StateUpdate>();
        uint lastEvent, nextSeq = 1;

        public AuthorityClient(AuthorityMode mode, Func<int> localSeat,
            Action<byte[], bool> sendToArbiter, bool arbiterIsLocal)
        {
            this.mode = mode;
            this.localSeat = localSeat;
            this.sendToArbiter = sendToArbiter;
            this.arbiterIsLocal = arbiterIsLocal;
        }

        public AuthorityMode Mode { get { return mode; } }
        public int LocalSeat { get { return localSeat(); } }
        public bool ArbiterIsLocal { get { return arbiterIsLocal; } }

        public int OwnerOf(string key)
        {
            if (mode == AuthorityMode.None) return LocalSeat;
            CsLeaseEntry lease;
            return leases.TryGetValue(key, out lease) ? lease.Owner : 0;
        }

        public uint EpochOf(string key)
        {
            CsLeaseEntry lease;
            return leases.TryGetValue(key, out lease) ? lease.Epoch : 0;
        }

        public bool Owns(string key) { return mode == AuthorityMode.None || (LocalSeat != 0 && OwnerOf(key) == LocalSeat); }

        public StateUpdate LatestState(string key)
        {
            StateUpdate state;
            return latest.TryGetValue(key, out state) ? state : null;
        }

        void Send(CsMessage message, bool reliable)
        {
            message.Seat = (byte)LocalSeat;
            sendToArbiter(CsWire.Encode(message), reliable);
        }

        public void RequestLease(string key, float score, bool active)
        {
            if (mode == AuthorityMode.None) return;
            Send(new CsMessage { Type = CsWire.CsLease, Op = 1, Key = key, Score = score,
                Active = (byte)(active ? 1 : 0) }, true);
        }

        public void ReleaseLease(string key)
        {
            if (mode == AuthorityMode.None) return;
            Send(new CsMessage { Type = CsWire.CsLease, Op = 2, Key = key }, true);
        }

        public void PublishState(string key, byte[] data, bool active)
        {
            if (mode == AuthorityMode.None || !Owns(key)) return;
            uint ver;
            versions.TryGetValue(key, out ver);
            versions[key] = ++ver;
            latest[key] = new StateUpdate { Key = key, From = LocalSeat, Epoch = EpochOf(key),
                Ver = ver, Active = active, Data = (byte[])data.Clone() };
            Send(new CsMessage { Type = CsWire.CsState, Key = key, Epoch = EpochOf(key), Ver = ver,
                Active = (byte)(active ? 1 : 0), Data = data }, false);
        }

        public void Propose(byte kind, string key, byte[] data, bool claim, bool release)
        {
            uint seq = nextSeq++;
            if (mode == AuthorityMode.None)
            {
                if (Committed != null) Committed(new CommittedEvent { Id = ++lastEvent, From = LocalSeat,
                    Seq = seq, Kind = kind, Key = key, Data = data });
                return;
            }
            Send(new CsMessage { Type = CsWire.CsAction, Seq = seq, Kind = kind, Key = key, Data = data,
                Flags = (byte)((claim ? Arbiter.FlagClaim : 0) | (release ? Arbiter.FlagRelease : 0)) }, true);
        }

        void Grant(CsLeaseEntry lease)
        {
            CsLeaseEntry previous;
            if (leases.TryGetValue(lease.Key, out previous) && lease.Epoch <= previous.Epoch) return;
            leases[lease.Key] = lease;
            if (LeaseChanged != null) LeaseChanged(new LeaseChange { Key = lease.Key, Owner = lease.Owner, Epoch = lease.Epoch });
        }

        void State(CsStateEntry state, bool snapshot)
        {
            uint ver;
            if (snapshot ? state.Epoch > EpochOf(state.Key) : state.Epoch != EpochOf(state.Key)) return;
            if (receivedVersions.TryGetValue(state.Key, out ver) && state.Ver <= ver) return;
            receivedVersions[state.Key] = state.Ver;
            versions.TryGetValue(state.Key, out ver);
            if (state.Ver > ver) versions[state.Key] = state.Ver;
            // NetPlay rewrites forwarded headers to the host seat; the lease identifies the publisher.
            StateUpdate update = new StateUpdate { Key = state.Key, From = OwnerOf(state.Key),
                Epoch = state.Epoch, Ver = state.Ver, Active = state.Active != 0, Data = state.Data };
            latest[state.Key] = update;
            if (StateReceived != null) StateReceived(update);
        }

        public void Receive(byte[] csMessage)
        {
            CsMessage message = CsWire.Decode(csMessage);
            switch (message.Type)
            {
                case CsWire.CsGrant:
                    Grant(new CsLeaseEntry { Key = message.Key, Owner = message.Owner, Epoch = message.Epoch });
                    break;
                case CsWire.CsState:
                    State(new CsStateEntry { Key = message.Key, Epoch = message.Epoch, Ver = message.Ver,
                        Active = message.Active, Data = message.Data }, false);
                    break;
                case CsWire.CsEvent:
                    bool rejected = message.Kind == 0xFF;
                    if (!rejected)
                    {
                        if (message.Id <= lastEvent) return;
                        lastEvent = message.Id;
                    }
                    if (Committed != null) Committed(new CommittedEvent { Id = message.Id, From = message.From,
                        Seq = message.Seq, Kind = rejected ? message.Data[0] : message.Kind, Rejected = rejected,
                        Key = message.Key, Data = message.Data });
                    break;
                case CsWire.CsSnapshot:
                    leases.Clear();
                    receivedVersions.Clear();
                    latest.Clear();
                    if (Reset != null) Reset();
                    foreach (CsLeaseEntry lease in message.Leases) Grant(lease);
                    foreach (CsStateEntry state in message.States) State(state, true);
                    break;
                // Session/roster/error presentation belongs to the transport; IAuthority has no such events.
                case CsWire.CsRoster:
                case CsWire.CsWelcome:
                case CsWire.CsError:
                    break;
            }
        }

        public void ResetSession()
        {
            leases.Clear(); versions.Clear(); receivedVersions.Clear(); latest.Clear(); lastEvent = 0; nextSeq = 1;
            if (Reset != null) Reset();
        }

        public event Action<LeaseChange> LeaseChanged;
        public event Action<StateUpdate> StateReceived;
        public event Action<CommittedEvent> Committed;
        public event Action Reset;
    }
}
