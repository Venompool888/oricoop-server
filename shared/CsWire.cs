using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OriCoop.Authority
{
    public sealed class CsIdentity
    {
        public ulong Steam;
        public string Nick = "";
    }

    public sealed class CsRosterEntry
    {
        public byte Seat;
        public bool Online;
        public ulong Steam;
        public string Nick = "";
    }

    public sealed class CsRoomListEntry
    {
        public string Code = "", Text = "";
        public byte Online, Capacity = 4;
        public readonly List<string> Nicks = new List<string>();
    }

    public sealed class CsLeaseEntry
    {
        public string Key = "";
        public byte Owner;
        public uint Epoch;
    }

    public sealed class CsStateEntry
    {
        public string Key = "";
        public uint Epoch, Ver;
        public byte Active;
        public byte[] Data = new byte[0];
    }

    public sealed class CsMessage
    {
        public byte Type, Seat, AssignedSeat, Proto = 2, Count = 1, Mode, Result, Op, Active, Owner;
        public byte Kind, Flags, From, ErrorCode, Target;
        public uint LastEvent, Epoch, Ver, Seq, Id;
        public ulong Steam;
        public float Score;
        public string Code = "", Nick = "", Key = "", Text = "";
        public byte[] Token = new byte[16], Data = new byte[0], Inner = new byte[0];
        public readonly List<CsIdentity> Identities = new List<CsIdentity>();
        public readonly List<int> Seats = new List<int>();
        public readonly List<CsRosterEntry> Roster = new List<CsRosterEntry>();
        public readonly List<CsRoomListEntry> Rooms = new List<CsRoomListEntry>();
        public readonly List<CsLeaseEntry> Leases = new List<CsLeaseEntry>();
        public readonly List<CsStateEntry> States = new List<CsStateEntry>();
    }

    public static class CsWire
    {
        public const byte CsJoin = 176, CsWelcome = 177, CsRoster = 178, CsLease = 179;
        public const byte CsGrant = 180, CsAction = 181, CsEvent = 182, CsState = 183;
        public const byte CsSnapshot = 184, CsError = 185, CsTo = 186;
        public const byte CsSeatAdd = 187, CsSeatRelease = 188;
        public const byte CsList = 189, CsRoomList = 190, CsRoomInfo = 191, CsKick = 192;

        static void WriteString(BinaryWriter w, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            if (bytes.Length > 255) throw new ArgumentException("UTF-8 string exceeds 255 bytes");
            w.Write((byte)bytes.Length); w.Write(bytes);
        }

        static byte[] ReadBytes(BinaryReader r, int count)
        {
            byte[] bytes = r.ReadBytes(count);
            if (bytes.Length != count) throw new EndOfStreamException();
            return bytes;
        }

        // T-173: strict UTF-8. Invalid bytes used to become U+FFFD (3 bytes each), so a 255-byte string could grow past
        // 255 bytes and make the server's own re-encode throw later; now the message is rejected as malformed.
        static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        static string ReadString(BinaryReader r)
        {
            byte[] bytes = ReadBytes(r, r.ReadByte());
            try { return StrictUtf8.GetString(bytes); }
            catch (ArgumentException) { throw new InvalidDataException("invalid UTF-8 in Cs string"); }
        }
        static void WriteData(BinaryWriter w, byte[] data)
        {
            if (data.Length > UInt16.MaxValue) throw new ArgumentException("blob exceeds 65535 bytes");
            w.Write((ushort)data.Length); w.Write(data);
        }
        static byte[] ReadData(BinaryReader r) { return ReadBytes(r, r.ReadUInt16()); }
        static void WriteLease(BinaryWriter w, CsLeaseEntry l)
        {
            WriteString(w, l.Key); w.Write(l.Owner); w.Write(l.Epoch);
        }
        static CsLeaseEntry ReadLease(BinaryReader r)
        {
            return new CsLeaseEntry { Key = ReadString(r), Owner = r.ReadByte(), Epoch = r.ReadUInt32() };
        }
        static void WriteState(BinaryWriter w, CsStateEntry s)
        {
            WriteString(w, s.Key); w.Write(s.Epoch); w.Write(s.Ver); w.Write(s.Active); WriteData(w, s.Data);
        }
        static CsStateEntry ReadState(BinaryReader r)
        {
            return new CsStateEntry { Key = ReadString(r), Epoch = r.ReadUInt32(), Ver = r.ReadUInt32(),
                Active = r.ReadByte(), Data = ReadData(r) };
        }

        public static byte[] Encode(CsMessage m)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                BinaryWriter w = new BinaryWriter(stream);
                w.Write(m.Type); w.Write(m.Seat);
                switch (m.Type)
                {
                    case CsJoin:
                        w.Write(m.Proto); w.Write(m.Mode); WriteString(w, m.Code);
                        if (m.Token.Length != 16) throw new ArgumentException("token must be 16 bytes");
                        w.Write(m.Token);
                        if (m.Proto == 1) { w.Write(m.Steam); WriteString(w, m.Nick); break; }
                        if (m.Count < 1 || m.Count > 3 ||
                            (m.Identities.Count != m.Count && !(m.Count == 1 && m.Identities.Count == 0)))
                            throw new ArgumentException("join needs 1-3 identities");
                        w.Write(m.Count);
                        if (m.Identities.Count == 0) { w.Write(m.Steam); WriteString(w, m.Nick); }
                        else foreach (CsIdentity identity in m.Identities)
                        { w.Write(identity.Steam); WriteString(w, identity.Nick); }
                        break;
                    case CsWelcome:
                        w.Write(m.Result); w.Write((byte)m.Seats.Count);
                        foreach (int seat in m.Seats) w.Write((byte)seat);
                        WriteString(w, m.Code);
                        if (m.Token.Length != 16) throw new ArgumentException("token must be 16 bytes");
                        w.Write(m.Token); w.Write(m.LastEvent); break;
                    case CsRoster:
                        if (m.Roster.Count > 255) throw new ArgumentException("roster too large");
                        w.Write((byte)m.Roster.Count);
                        foreach (CsRosterEntry e in m.Roster)
                        {
                            w.Write(e.Seat); w.Write((byte)(e.Online ? 1 : 0));
                            w.Write(e.Steam); WriteString(w, e.Nick);
                        }
                        break;
                    case CsLease:
                        w.Write(m.Op); WriteString(w, m.Key); w.Write(m.Score); w.Write(m.Active); break;
                    case CsGrant:
                        WriteLease(w, new CsLeaseEntry { Key = m.Key, Owner = m.Owner, Epoch = m.Epoch }); break;
                    case CsAction:
                        w.Write(m.Seq); w.Write(m.Kind); w.Write(m.Flags); WriteString(w, m.Key); WriteData(w, m.Data); break;
                    case CsEvent:
                        w.Write(m.Id); w.Write(m.From); w.Write(m.Seq); w.Write(m.Kind);
                        WriteString(w, m.Key); WriteData(w, m.Data); break;
                    case CsState:
                        WriteState(w, new CsStateEntry { Key = m.Key, Epoch = m.Epoch,
                            Ver = m.Ver, Active = m.Active, Data = m.Data }); break;
                    case CsSnapshot:
                        if (m.Leases.Count > UInt16.MaxValue || m.States.Count > UInt16.MaxValue)
                            throw new ArgumentException("snapshot too large");
                        w.Write((ushort)m.Leases.Count);
                        foreach (CsLeaseEntry l in m.Leases) WriteLease(w, l);
                        w.Write((ushort)m.States.Count);
                        foreach (CsStateEntry s in m.States) WriteState(w, s);
                        break;
                    case CsError:
                        w.Write(m.ErrorCode); WriteString(w, m.Text); break;
                    case CsTo:
                        w.Write(m.Target); w.Write(m.Inner); break;
                    case CsSeatAdd:
                        w.Write(m.Steam); WriteString(w, m.Nick); break;
                    case CsList: break;
                    case CsRoomList:
                        int roomCount = Math.Min(64, m.Rooms.Count);
                        w.Write((byte)roomCount);
                        for (int i = 0; i < roomCount; i++)
                        {
                            CsRoomListEntry room = m.Rooms[i];
                            WriteString(w, room.Code); w.Write(room.Online); w.Write(room.Capacity);
                            w.Write((byte)room.Nicks.Count);
                            foreach (string nick in room.Nicks) WriteString(w, nick);
                            WriteString(w, room.Text);
                        }
                        break;
                    case CsRoomInfo:
                        WriteString(w, m.Text); break;
                    case CsKick:
                    case CsSeatRelease:
                        w.Write(m.Target); break;
                    default: throw new InvalidDataException("unknown Cs message");
                }
                return stream.ToArray();
            }
        }

        public static CsMessage Decode(byte[] bytes)
        {
            using (MemoryStream stream = new MemoryStream(bytes, false))
            {
                BinaryReader r = new BinaryReader(stream);
                CsMessage m = new CsMessage { Type = r.ReadByte(), Seat = r.ReadByte() };
                switch (m.Type)
                {
                    case CsJoin:
                        m.Proto = r.ReadByte();
                        // Reject old/unknown protocols without interpreting their incompatible payload.
                        if (m.Proto != 2) return m;
                        m.Mode = r.ReadByte(); m.Code = ReadString(r); m.Token = ReadBytes(r, 16);
                        m.Count = r.ReadByte();
                        if (m.Count < 1 || m.Count > 3) throw new InvalidDataException("invalid seat count");
                        for (int i = 0; i < m.Count; i++)
                            m.Identities.Add(new CsIdentity { Steam = r.ReadUInt64(), Nick = ReadString(r) });
                        m.Steam = m.Identities[0].Steam; m.Nick = m.Identities[0].Nick;
                        break;
                    case CsWelcome:
                        m.Result = r.ReadByte(); int count = r.ReadByte();
                        if (count > 4) throw new InvalidDataException("invalid seats");
                        for (int i = 0; i < count; i++)
                        {
                            int seat = r.ReadByte();
                            if (seat < 1 || seat > 4 || m.Seats.Contains(seat)) throw new InvalidDataException("invalid seat");
                            m.Seats.Add(seat);
                        }
                        m.Seats.Sort(); m.AssignedSeat = (byte)Arbiter.PrimarySeat(m.Seats); m.Code = ReadString(r);
                        m.Token = ReadBytes(r, 16); m.LastEvent = r.ReadUInt32(); break;
                    case CsRoster:
                        int n = r.ReadByte();
                        for (int i = 0; i < n; i++)
                            m.Roster.Add(new CsRosterEntry { Seat = r.ReadByte(), Online = r.ReadByte() != 0,
                                Steam = r.ReadUInt64(), Nick = ReadString(r) });
                        break;
                    case CsLease:
                        m.Op = r.ReadByte(); m.Key = ReadString(r); m.Score = r.ReadSingle(); m.Active = r.ReadByte(); break;
                    case CsGrant:
                        CsLeaseEntry lease = ReadLease(r); m.Key = lease.Key; m.Owner = lease.Owner; m.Epoch = lease.Epoch; break;
                    case CsAction:
                        m.Seq = r.ReadUInt32(); m.Kind = r.ReadByte(); m.Flags = r.ReadByte();
                        m.Key = ReadString(r); m.Data = ReadData(r); break;
                    case CsEvent:
                        m.Id = r.ReadUInt32(); m.From = r.ReadByte(); m.Seq = r.ReadUInt32(); m.Kind = r.ReadByte();
                        m.Key = ReadString(r); m.Data = ReadData(r); break;
                    case CsState:
                        CsStateEntry state = ReadState(r); m.Key = state.Key; m.Epoch = state.Epoch;
                        m.Ver = state.Ver; m.Active = state.Active; m.Data = state.Data; break;
                    case CsSnapshot:
                        int leases = r.ReadUInt16();
                        for (int i = 0; i < leases; i++) m.Leases.Add(ReadLease(r));
                        int states = r.ReadUInt16();
                        for (int i = 0; i < states; i++) m.States.Add(ReadState(r));
                        break;
                    case CsError:
                        m.ErrorCode = r.ReadByte(); m.Text = ReadString(r); break;
                    case CsTo:
                        m.Target = r.ReadByte(); m.Inner = ReadBytes(r, (int)(stream.Length - stream.Position)); break;
                    case CsSeatAdd:
                        m.Steam = r.ReadUInt64(); m.Nick = ReadString(r); break;
                    case CsList: break;
                    case CsRoomList:
                        int roomCount = r.ReadByte();
                        if (roomCount > 64) throw new InvalidDataException("room list too large");
                        for (int i = 0; i < roomCount; i++)
                        {
                            CsRoomListEntry room = new CsRoomListEntry { Code = ReadString(r), Online = r.ReadByte(), Capacity = r.ReadByte() };
                            int nickCount = r.ReadByte();
                            for (int j = 0; j < nickCount; j++) room.Nicks.Add(ReadString(r));
                            room.Text = ReadString(r); m.Rooms.Add(room);
                        }
                        break;
                    case CsRoomInfo:
                        m.Text = ReadString(r); break;
                    case CsKick:
                    case CsSeatRelease:
                        m.Target = r.ReadByte(); break;
                    default: throw new InvalidDataException("unknown Cs message");
                }
                if (stream.Position != stream.Length) throw new InvalidDataException("trailing Cs bytes");
                return m;
            }
        }
    }
}
