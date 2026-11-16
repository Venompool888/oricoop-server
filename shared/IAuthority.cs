using System;

namespace OriCoop.Authority
{
    // Who arbitrates. Gameplay sync code only talks to IAuthority and never asks "am I the host?" for shared things.
    // HostP2P: the Arbiter runs inside the P2P host's game. Server: the Arbiter runs in CServer. See docs/c-server-design.md §5.
    public enum AuthorityMode { None, HostP2P, Server }

    public sealed class LeaseChange
    {
        public string Key;
        public int Owner;        // seat 1-4, 0 = nobody
        public uint Epoch;
    }

    public sealed class StateUpdate
    {
        public string Key;
        public int From;         // seat of the owner that published it
        public uint Epoch, Ver;
        public bool Active;
        public byte[] Data;
    }

    public sealed class CommittedEvent
    {
        public uint Id;          // global order; 0 for a rejected claim
        public int From;         // seat that proposed it
        public uint Seq;         // proposer's sequence number
        public byte Kind;        // gameplay kind (caller-defined); Rejected for a lost claim
        public bool Rejected;    // true only on the proposer, when its claim lost (Data[0] = the original kind)
        public string Key;
        public byte[] Data;
    }

    public interface IAuthority
    {
        AuthorityMode Mode { get; }
        int LocalSeat { get; }
        bool ArbiterIsLocal { get; }                 // true only for the P2P host

        int OwnerOf(string key);                     // last granted owner, 0 = nobody / unknown
        uint EpochOf(string key);
        bool Owns(string key);
        StateUpdate LatestState(string key);

        // Ask to simulate `key`. score: lower = more relevant (distance). active: I am interacting with it right now.
        void RequestLease(string key, float score, bool active);
        void ReleaseLease(string key);

        // Owner only: publish the latest state. Ignored (and counted) by the arbiter if not owner / stale epoch.
        void PublishState(string key, byte[] data, bool active);

        // Propose something that may happen once. claim: first proposer wins (others get Rejected).
        // release: free a claim this seat won (respawn). Results always arrive through Committed, also for the proposer.
        void Propose(byte kind, string key, byte[] data, bool claim, bool release);

        event Action<LeaseChange> LeaseChanged;
        event Action<StateUpdate> StateReceived;
        event Action<CommittedEvent> Committed;
        event Action Reset;                          // session ended / snapshot replaced everything
    }
}
