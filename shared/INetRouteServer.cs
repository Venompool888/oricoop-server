using System;
using System.IO;

namespace OriCoop.Authority
{
    public interface INetRouteServer
    {
        bool IsArbiter { get; }
        int ArbiterSeat { get; }   // the seat that arbitrates now (lowest online seat); 0 before the first state
        void SendTo(int seat, byte type, bool reliable, Action<BinaryWriter> body);
    }
}
