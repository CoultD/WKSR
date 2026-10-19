using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using WKRace.Protocol;

// Stand-ins for the parts of the WKSR program that have no place inside the game: the local connection to the game
// (now a direct hand-over), the game launcher (the game is running: this code runs in it), and voice chat (not built
// in the game yet). WKSR's logic uses them through the same names as before.

namespace WKRace.Client
{
    using Message = WKRace.Protocol.Message;

    // WKSR's side of the hand-over between its logic and the game code (WKRace.Mod.EmbeddedLink is the other side).
    // Messages cross as copies, as they did over the local socket, so neither side can change what the other keeps.
    internal sealed class ModBridge
    {
        private const int MaxQueued = 4096;

        internal static ModBridge Current;
        private readonly ConcurrentQueue<Message> _toMod = new ConcurrentQueue<Message>();

        public event Action<Message> Received;
        public event Action<bool> ConnectionChanged;

        public bool Connected { get; private set; }

        public void Start(int port)
        {
            Current = this;
        }

        public void Stop()
        {
            Connected = false;
            if (Current == this) Current = null;
        }

        public void Send(Message message)
        {
            if (!Connected || _toMod.Count >= MaxQueued) return;
            _toMod.Enqueue(Copy(message));
        }

        internal bool TryTake(out Message message)
        {
            return _toMod.TryDequeue(out message);
        }

        internal void FromMod(Message message)
        {
            Action<Message> handler = Received;
            if (handler != null) handler(Copy(message));
        }

        internal void SetConnected(bool connected)
        {
            if (Connected == connected) return;
            Connected = connected;
            if (!connected)
            {
                Message dropped;
                while (_toMod.TryDequeue(out dropped)) { }
            }
            Action<bool> handler = ConnectionChanged;
            if (handler != null) handler(connected);
        }

        private static Message Copy(Message message)
        {
            return MessageCodec.Decode(MessageCodec.Encode(message));
        }
    }

    // The game is running (this code is inside it), and its build comes from Steam's record of the installed game.
    internal static class GameLauncher
    {
        public const int SteamAppId = 3195790;
        private static string _build;

        public static bool IsRunning()
        {
            return true;
        }

        // "steam-<build id>", or "" when the game was not installed through Steam (or the record is unreadable).
        public static string FindGameBuild(string configured)
        {
            if (_build != null) return _build;
            _build = "";
            try
            {
                // <library>/steamapps/common/White Knuckle/White Knuckle_Data -> <library>/steamapps/appmanifest_<id>.acf
                DirectoryInfo data = new DirectoryInfo(UnityEngine.Application.dataPath);
                DirectoryInfo steamapps = data.Parent != null && data.Parent.Parent != null ? data.Parent.Parent.Parent : null;
                if (steamapps != null)
                {
                    string manifest = Path.Combine(steamapps.FullName, "appmanifest_" + SteamAppId + ".acf");
                    if (File.Exists(manifest))
                    {
                        Match m = Regex.Match(File.ReadAllText(manifest), "\"buildid\"\\s+\"(\\d+)\"");
                        if (m.Success) _build = "steam-" + m.Groups[1].Value;
                    }
                }
            }
            catch (Exception)
            {
            }
            return _build;
        }

        public static string Launch(string gamePath, string bepInExRoot, out string note)
        {
            note = null;
            return "The game is already running.";
        }
    }

    // Voice chat is not part of the in-game client yet: this accepts the settings and reports "not connected".
    internal sealed class VoiceEngine : IDisposable
    {
        public const int DefaultPushToTalkKey = 0x56;   // V

        public VoiceEngine(Action<string> log)
        {
        }

        public bool Enabled { get; set; }
        public bool PushToTalk { get; set; }
        public int PushToTalkKey { get; set; }
        public float MicVolume { get; set; }
        public float OutputVolume { get; set; }
        public string Device { get; set; }
        public bool Connected { get { return false; } }
        public bool Transmitting { get { return false; } }
        public float MicLevel { get { return 0f; } }
        public int RangeMeters { get { return 0; } }

        public static List<string> Devices() { return new List<string>(); }
        public void SetMuted(IEnumerable<string> names) { }
        public List<ushort> Speaking() { return new List<ushort>(); }
        public VoicePeer Peer(ushort id) { return null; }
        public void Connect(VoiceHello hello, string host) { }
        public void Disconnect() { }
        public void SetPeers(VoicePeers peers) { }
        public void SetListener(StateMessage s) { }
        public void SetSource(OpponentState s) { }
        public void ClearPositions() { }
        public void Dispose() { }
    }

    // What a bug report would carry. Reports go through Discord now; the type remains because WKSR's logic names it.
    internal sealed class BugReportContext
    {
        public string Url;
        public Guid PlayerId;
        public byte[] Token;
        public string Info;
        public Func<string> SessionLog;
    }

    // The mod's hotkeys are Unity key codes; this names them.
    internal static class KeyNames
    {
        public const int MouseMiddle = 325, Mouse4 = 326, Mouse5 = 327;

        public static string Name(int code)
        {
            if (code <= 0) return "NONE";
            if (code >= 97 && code <= 122) return ((char)('A' + code - 97)).ToString();
            if (code >= 48 && code <= 57) return ((char)code).ToString();
            if (code >= 256 && code <= 265) return "NUM " + (code - 256);
            if (code >= 282 && code <= 296) return "F" + (code - 281);
            switch (code)
            {
                case 8: return "BACKSPACE";
                case 9: return "TAB";
                case 13: return "ENTER";
                case 19: return "PAUSE";
                case 27: return "ESC";
                case 32: return "SPACE";
                case 127: return "DELETE";
                case 273: return "UP";
                case 274: return "DOWN";
                case 275: return "RIGHT";
                case 276: return "LEFT";
                case 277: return "INSERT";
                case 278: return "HOME";
                case 279: return "END";
                case 280: return "PAGE UP";
                case 281: return "PAGE DOWN";
                case 301: return "CAPS LOCK";
                case 303: return "RIGHT SHIFT";
                case 304: return "LEFT SHIFT";
                case 305: return "RIGHT CTRL";
                case 306: return "LEFT CTRL";
                case 307: return "RIGHT ALT";
                case 308: return "LEFT ALT";
                case MouseMiddle: return "MOUSE 3";
                case Mouse4: return "MOUSE 4";
                case Mouse5: return "MOUSE 5";
            }
            string name = ((UnityEngine.KeyCode)code).ToString();
            return string.IsNullOrEmpty(name) ? "KEY " + code : name.ToUpperInvariant();
        }
    }
}
