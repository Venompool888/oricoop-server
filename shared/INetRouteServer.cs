using System;
using System.IO;

namespace OriCoop.Authority
{
    public interface INetRouteServer
    {
        bool IsArbiter { get; }
        void SendTo(int seat, byte type, bool reliable, Action<BinaryWriter> body);
    }
}
