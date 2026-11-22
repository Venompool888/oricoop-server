using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading;
using OriCoop.Net;

namespace OriCoop.Authority
{
    public static class CServerRoomList
    {
        public static List<CsRoomListEntry> Fetch(EndPoint server, int timeoutMs, out string error)
        {
            using (UdpTransport transport = new UdpTransport(0))
            {
                NetPeer peer = new NetPeer(transport);
                peer.Connected += delegate
                {
                    byte[] request = CsWire.Encode(new CsMessage { Type = CsWire.CsList });
                    peer.SendReliable(request, 0, request.Length);
                };
                peer.Connect(server);
                Stopwatch clock = Stopwatch.StartNew();
                try
                {
                    while (clock.ElapsedMilliseconds < timeoutMs)
                    {
                        peer.Poll();
                        byte[] bytes; bool reliable;
                        while (peer.TryReceive(out bytes, out reliable))
                        {
                            CsMessage reply = CsWire.Decode(bytes);
                            if (reply.Type == CsWire.CsRoomList) { error = null; return reply.Rooms; }
                        }
                        if (peer.State == PeerState.Disconnected) { error = peer.DisconnectReason; return null; }
                        Thread.Sleep(1);
                    }
                    error = "timed out";
                    return null;
                }
                finally { peer.Disconnect("room list done"); }
            }
        }
    }

    // T-171/T-173: the room list page's state (NativeCsRooms), free of Unity so CServerClientTest covers it. The fetch
    // runs on a worker thread; everything else on the main thread. A result is kept only for the server it was fetched
    // from (a slow fetch for the previous server no longer shows its rooms on the new server's page), and a press maps
    // to the rooms the page last drew (Shown), not to a list the worker replaced since.
    public sealed class CsRoomListCache
    {
        public const int Idle = 0, Loading = 1, Loaded = 2, Failed = 3;
        readonly object gate = new object();
        List<CsRoomListEntry> rooms;                         // last good list for `server` (null = none yet)
        List<CsRoomListEntry> shown = new List<CsRoomListEntry>();
        // T-173 (review F15): the last few lists received for `server`, newest first. The menu also builds its rows (and
        // so calls TakeShown) on a cursor move without drawing them, so `shown` can be newer than the labels on screen;
        // Drawn finds the room by the text actually drawn on the row among these lists instead.
        readonly List<List<CsRoomListEntry>> recent = new List<List<CsRoomListEntry>>();
        const int RecentLists = 4;
        string server = "";
        int generation, state, shownFrame;
        bool shownTaken;
        double next;

        public int State { get { lock (gate) return state; } }

        // Main thread: the server the page shows now. Returns the generation to fetch for, or -1 when no fetch is due
        // (one is running for this server, or the last one is younger than intervalSeconds).
        public int BeginFetch(string address, double now, double intervalSeconds)
        {
            lock (gate)
            {
                address = address ?? "";
                if (address != server)
                {
                    server = address; generation++; rooms = null; shown = new List<CsRoomListEntry>(); shownTaken = false;
                    recent.Clear();
                    state = Idle; next = 0;
                }
                if (state == Loading || now < next) return -1;
                state = Loading; next = now + intervalSeconds;
                return generation;
            }
        }

        public void ReloadNow() { lock (gate) next = 0; }

        // Worker: the result (null = failed) of the fetch BeginFetch returned `fetchGeneration` for. Dropped (false)
        // when the page switched server since; a failure keeps the server's last good list.
        public bool Complete(int fetchGeneration, List<CsRoomListEntry> result)
        {
            lock (gate)
            {
                if (fetchGeneration != generation) return false;
                if (result != null)
                {
                    rooms = result;
                    recent.Insert(0, new List<CsRoomListEntry>(result));
                    if (recent.Count > RecentLists) recent.RemoveAt(recent.Count - 1);
                }
                state = result != null ? Loaded : Failed;
                return true;
            }
        }

        // Main thread, when the page's rows are built: the rooms those rows show from now on. Every call with the same
        // frame gets the same list (the menu builds its Chinese and English rows one after the other and pairs them).
        public List<CsRoomListEntry> TakeShown(int frame)
        {
            lock (gate)
            {
                if (frame != shownFrame || !shownTaken)
                    shown = rooms == null ? new List<CsRoomListEntry>() : new List<CsRoomListEntry>(rooms);
                shownFrame = frame; shownTaken = true;
                return new List<CsRoomListEntry>(shown);
            }
        }

        // The room drawn on room row `index` (0 = first room row), or null for the status row / anything else.
        public CsRoomListEntry Shown(int index)
        {
            lock (gate) return index >= 0 && index < shown.Count ? shown[index] : null;
        }

        // T-173 (review F15): the room whose row text `drawnText` is on screen at room row `index` (0 = first room row), or
        // null when no room of the recent lists is drawn like that (the status row, a list already gone). `describe` must
        // build a room's row text exactly as the drawing code does; the text holds the room code, so a match is that room
        // even when the list was replaced or reordered after the row was drawn (a press joins what the player saw).
        public CsRoomListEntry Drawn(int index, string drawnText, Func<CsRoomListEntry, string> describe)
        {
            if (string.IsNullOrEmpty(drawnText) || describe == null) return null;
            List<CsRoomListEntry> candidates = new List<CsRoomListEntry>();
            lock (gate)
            {
                if (index >= 0 && index < shown.Count) candidates.Add(shown[index]);
                candidates.AddRange(shown);
                foreach (List<CsRoomListEntry> list in recent) candidates.AddRange(list);
            }
            foreach (CsRoomListEntry room in candidates)
                if (room != null && describe(room) == drawnText) return room;
            return null;
        }

        // "甲, 乙" for a room row; a nameless seat (older servers send "" for a PC without Steam) shows as P<n>.
        public static string PlayersLine(CsRoomListEntry room)
        {
            List<string> names = new List<string>();
            for (int i = 0; i < room.Nicks.Count; i++)
                names.Add(string.IsNullOrEmpty(room.Nicks[i]) ? "P" + (i + 1) : room.Nicks[i]);
            return string.Join(", ", names.ToArray());
        }
    }
}
