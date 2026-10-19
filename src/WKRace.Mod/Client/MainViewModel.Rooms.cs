using System;
using System.Collections.Generic;
using System.Globalization;
using WKRace.Protocol;

namespace WKRace.Client
{
    using Message = WKRace.Protocol.Message;

    internal sealed class ChatRoomLine
    {
        public long UnixMs;
        public string From = "";
        public string Text = "";
        public bool System;

        // "10/12/2026 10:30am Person: text"
        public string Display()
        {
            string when = DateTimeOffset.FromUnixTimeMilliseconds(UnixMs).ToLocalTime().ToString("MM/dd/yyyy h:mmtt", CultureInfo.InvariantCulture).ToLowerInvariant();
            return when + "  " + Short();
        }

        public string Short()
        {
            return System ? "* " + Text : From + ": " + Text;
        }
    }

    // One chat room and what this player sees of it. A room a player has joined has a tab in the chat.
    internal sealed class ChatRoomModel
    {
        public byte Id;
        public string Name = "";
        // What the list of rooms says about it.
        public string About = "";
        public bool Joined;
        // Can be joined right now (the Match room only exists in a lobby or a match).
        public bool Available;
        // Lines that came in while another room was selected.
        public int Unread;
        public readonly List<ChatRoomLine> Lines = new List<ChatRoomLine>();
    }

    // The chat rooms: Global (everyone who is connected and has it open) and Match (the player's lobby or 1v1). More
    // rooms can be added to the list later. One room is selected: the chat shows its lines and sends to it.
    internal sealed partial class MainViewModel
    {
        private const int RoomKeepLines = 300;
        private const long RoomKeepMs = 24L * 60 * 60 * 1000;

        public readonly List<ChatRoomModel> Rooms = new List<ChatRoomModel>
        {
            new ChatRoomModel { Id = ChatRooms.Global, Name = "Global", About = "Everyone who is connected.", Available = true },
            new ChatRoomModel { Id = ChatRooms.Match, Name = "Match", About = "Your lobby or 1v1. Opens and closes with it." },
        };

        private byte _selectedRoom = ChatRooms.Global;
        // The player closed the tab themselves: no joining by itself until the next start (Global) or lobby (Match).
        private bool _globalLeft;
        private bool _matchLeft;
        private bool _inMatchRoom;
        private int _playersQueued = -1;

        public byte SelectedRoom { get { return _selectedRoom; } }

        public ChatRoomModel Room(byte id)
        {
            foreach (ChatRoomModel room in Rooms) if (room.Id == id) return room;
            return null;
        }

        public bool ChatConnected { get { return _server != null && _link == LinkState.Connected; } }

        private DateTime _chatSilencedUntil;

        // The seconds left of a silence for sending chat lines too fast, counting down to 0 (0: not silenced).
        public int ChatSilencedSeconds
        {
            get
            {
                double left = (_chatSilencedUntil - DateTime.UtcNow).TotalSeconds;
                return left > 0 ? (int)Math.Ceiling(left) : 0;
            }
        }

        // "IN QUEUE  -  2 PLAYERS": how many players are searching for a ranked 1v1 right now.
        public string QueuedText
        {
            get { return !ChatConnected || _playersQueued < 0 ? "" : "IN QUEUE  -  " + _playersQueued + (_playersQueued == 1 ? " PLAYER" : " PLAYERS"); }
        }

        // The Global chat is joined by itself after connecting, unless the player turned that off (OPTIONS, CHAT).
        public bool ChatAutoGlobal
        {
            // Steam lobbies have no Global room: there is no server everyone shares.
            get { return !SteamMode && !_settings.ChatGlobalOff; }
            set
            {
                if (_settings.ChatGlobalOff == !value) return;
                _settings.ChatGlobalOff = !value;
                _settings.Save();
                Raise();
                if (value && !Room(ChatRooms.Global).Joined) JoinRoom(ChatRooms.Global);
            }
        }

        public void SelectRoom(byte id)
        {
            ChatRoomModel room = Room(id);
            if (room == null || !room.Joined) return;
            _selectedRoom = id;
            room.Unread = 0;
        }

        // The next joined room after the selected one (Tab in the chat).
        public void SelectNextRoom()
        {
            int at = Rooms.FindIndex(r => r.Id == _selectedRoom);
            for (int i = 1; i <= Rooms.Count; i++)
            {
                ChatRoomModel room = Rooms[(Math.Max(0, at) + i) % Rooms.Count];
                if (!room.Joined) continue;
                SelectRoom(room.Id);
                return;
            }
        }

        public void JoinRoom(byte id)
        {
            ChatRoomModel room = Room(id);
            if (room == null || room.Joined || !room.Available) return;
            if (id == ChatRooms.Global)
            {
                if (!ChatConnected) return;
                _globalLeft = false;
                room.Lines.Clear();
                _server.Send(new ChatJoin { Room = id, Join = true });
            }
            else if (id == ChatRooms.Match) _matchLeft = false;
            room.Joined = true;
            room.Unread = 0;
            _selectedRoom = id;
        }

        public void LeaveRoom(byte id)
        {
            ChatRoomModel room = Room(id);
            if (room == null || !room.Joined) return;
            if (id == ChatRooms.Global)
            {
                _globalLeft = true;
                if (ChatConnected) _server.Send(new ChatJoin { Room = id, Join = false });
            }
            else if (id == ChatRooms.Match) _matchLeft = true;
            CloseRoom(room);
        }

        private void CloseRoom(ChatRoomModel room)
        {
            room.Joined = false;
            room.Unread = 0;
            room.Lines.Clear();
            if (_selectedRoom != room.Id) return;
            foreach (ChatRoomModel other in Rooms)
            {
                if (!other.Joined) continue;
                SelectRoom(other.Id);
                return;
            }
        }

        // A line typed in the game's chat, for the selected room.
        public void Say(string text)
        {
            SendChat(text);
        }

        // Signed in (again): the Global room is asked for anew, and comes back with its last 24 hours.
        private void OnWelcomeChat()
        {
            ChatRoomModel global = Room(ChatRooms.Global);
            global.Joined = false;
            global.Lines.Clear();
            if (ChatAutoGlobal && !_globalLeft)
            {
                byte selected = _selectedRoom;
                JoinRoom(ChatRooms.Global);
                // A lobby that survived the reconnect keeps the chat on its room.
                if (selected != ChatRooms.Global && Room(selected) != null && Room(selected).Joined) _selectedRoom = selected;
            }
        }

        // Offline (DISCONNECT): no rooms.
        private void CloseChatRooms()
        {
            foreach (ChatRoomModel room in Rooms)
            {
                room.Joined = false;
                room.Unread = 0;
                room.Lines.Clear();
            }
            _inMatchRoom = false;
            _playersQueued = -1;
            _chatSilencedUntil = default(DateTime);
        }

        // The Match room follows the player's lobby or match against others: its tab opens with it, and closes
        // (with its lines) when the player leaves. Runs in a row in the same lobby keep the same room.
        private void UpdateMatchRoom()
        {
            bool inRoom = ChatConnected && (_hasLobby || (_hasMatch && _practice == null && !_rankedSolo));
            if (inRoom == _inMatchRoom) return;
            _inMatchRoom = inRoom;
            ChatRoomModel room = Room(ChatRooms.Match);
            room.Available = inRoom;
            if (inRoom)
            {
                _matchLeft = false;
                room.Lines.Clear();
                room.Joined = true;
                room.Unread = 0;
                _selectedRoom = room.Id;
            }
            else CloseRoom(room);
        }

        private void OnRoomChat(ChatMessage chat)
        {
            if (chat.System && chat.SilencedMs > 0) _chatSilencedUntil = DateTime.UtcNow.AddMilliseconds(chat.SilencedMs);
            if (chat.Room == ChatRooms.Match) UpdateMatchRoom();
            ChatRoomModel room = Room(chat.Room);
            // A note that belongs to no open room (a refusal, /mute help) shows where the player is looking.
            if (room == null || !room.Joined)
            {
                if (!chat.System || (room != null && room.Id == ChatRooms.Match && _matchLeft && _inMatchRoom)) return;
                room = Room(_selectedRoom);
                if (room == null || !room.Joined)
                {
                    Log(chat.Text);
                    return;
                }
            }
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            room.Lines.Add(new ChatRoomLine { UnixMs = chat.SentUnixMs > 0 ? chat.SentUnixMs : now, From = chat.From ?? "", Text = chat.Text ?? "", System = chat.System });
            int drop = 0;
            while (drop < room.Lines.Count && (room.Lines.Count - drop > RoomKeepLines || room.Lines[drop].UnixMs <= now - RoomKeepMs)) drop++;
            if (drop > 0) room.Lines.RemoveRange(0, drop);
            if (!chat.History && room.Id != _selectedRoom) room.Unread++;
        }
    }
}
