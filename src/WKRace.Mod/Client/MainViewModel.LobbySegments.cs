using System;
using WKRace.Protocol;

namespace WKRace.Client
{
    // CREATE LOBBY can make the lobby play a segment (Segments) instead of the whole climb: its matches end at the
    // segment's end. On the official server only when the server knows it (ServerFeatures.LobbySegments); in Steam
    // lobbies always (the host's core is this version).
    internal sealed partial class MainViewModel
    {
        // The match server takes segment lobbies (it said so after the welcome).
        private bool _lobbySegments;

        public bool HasLobbySegments
        {
            get { return SteamMode || _lobbySegments && _link == LinkState.Connected; }
        }

        // The SEGMENT box of CREATE LOBBY, and the segment its dropdown shows.
        public bool LobbySegmentOn
        {
            get { return _settings.LobbySegment != Segments.None && Segments.Find(_settings.LobbySegment) != null; }
            set
            {
                if (value == LobbySegmentOn) return;
                _settings.LobbySegment = value ? Segments.All[Math.Max(0, Math.Min(_lobbySegmentIndex, Segments.All.Length - 1))].Id : Segments.None;
                _settings.Save();
                Raise();
            }
        }

        private int _lobbySegmentIndex;

        public int LobbySegmentIndex
        {
            get
            {
                for (int i = 0; i < Segments.All.Length; i++) if (Segments.All[i].Id == _settings.LobbySegment) return i;
                return Math.Max(0, Math.Min(_lobbySegmentIndex, Segments.All.Length - 1));
            }
            set
            {
                _lobbySegmentIndex = Math.Max(0, Math.Min(value, Segments.All.Length - 1));
                if (!LobbySegmentOn) return;
                _settings.LobbySegment = Segments.All[_lobbySegmentIndex].Id;
                _settings.Save();
                Raise();
            }
        }

        public string[] SegmentNames
        {
            get
            {
                var names = new string[Segments.All.Length];
                for (int i = 0; i < names.Length; i++) names[i] = Segments.All[i].Name;
                return names;
            }
        }

        // The segment a new lobby plays: the box's choice where segment lobbies exist, else the whole climb.
        private byte LobbySegmentToSend()
        {
            return HasLobbySegments && LobbySegmentOn ? _settings.LobbySegment : Segments.None;
        }

        // "SILOS segment. " for a lobby or a browser row that plays one, else "".
        private static string SegmentPrefix(byte segment)
        {
            SegmentInfo part = Segments.Find(segment);
            return part == null ? "" : part.Name + " segment. ";
        }

        // The note under the picker when the chosen segment has no artifact room.
        public string LobbySegmentNote
        {
            get
            {
                if (!LobbySegmentOn) return "";
                SegmentInfo part = Segments.Find(_settings.LobbySegment);
                return part != null && !part.Artifacts ? "Matches end at the end of the segment. No artifact bans: the segment ends before the artifact room." : "Matches end at the end of the segment.";
            }
        }
    }
}
