using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Newtonsoft.Json;
using WKRace.Protocol;

namespace WKRace.Client
{
    // %AppData%\WKRace\client.json: the player's identity and choices. It is the file the WKSR program used, with the
    // same fields, so a player keeps their name, rating and Steam link when WKSR moves into the game. It never holds a
    // server address.
    internal sealed class ClientSettings
    {
        public Guid PlayerId { get; set; } = Guid.NewGuid();
        public string Token { get; set; }
        // A random name until the player picks one (never the Windows account name, which is often a real name).
        public string Name { get; set; } = NameGenerator.Next();
        public int ModPort { get; set; } = ProtocolConstants.DefaultLocalPort;
        // The modifier pickers: CREATE LOBBY (mode and modifiers) and SOLO (MatchRules flags). Ranked always uses the
        // ranked modifiers.
        public int LobbyMode { get; set; }
        public int LobbyRules { get; set; } = (int)MatchRules.Ranked;
        public int PracticeRules { get; set; } = (int)MatchRules.Ranked;
        // The SEGMENTS page's picker. The ranked modifiers are the ones a segment's leaderboard runs use.
        public int SegmentRules { get; set; } = (int)MatchRules.Ranked;
        // The ghost's opacity in the game, 0-100.
        public int GhostOpacity { get; set; } = 45;
        // The game's hotkeys (ready, forfeit, splits, ghost, chat; Unity key codes), or null for the mod's own.
        public int[] Keys { get; set; }
        // Players whose chat is hidden.
        public List<string> Muted { get; set; } = new List<string>();
        // Proximity voice.
        public bool VoiceEnabled { get; set; } = true;
        public string VoiceDevice { get; set; } = "";
        public int VoiceMicVolume { get; set; } = 100;
        public int VoiceOutVolume { get; set; } = 100;
        public bool VoicePushToTalk { get; set; } = true;
        public int VoiceKey { get; set; } = VoiceEngine.DefaultPushToTalkKey;
        public List<string> VoiceMuted { get; set; } = new List<string>();
        public string GamePath { get; set; }
        public string BepInExRoot { get; set; }
        public string LastModVersion { get; set; }
        // The newest version whose "what is new for your data" notice this player has seen.
        public string NoticeSeen { get; set; }
        // The player pressed CONNECT in the WKSR window (and not DISCONNECT since): the game signs in to the match
        // server when it starts. Off until they do: nothing is sent anywhere before that.
        public bool Online { get; set; }
        // The player turned off joining the Global chat by itself (OPTIONS, CHAT).
        public bool ChatGlobalOff { get; set; }
        public bool LobbyUnapproved { get; set; }
        // CREATE LOBBY: listed in the lobby browser (else only joinable with its code).
        public bool LobbyPublic { get; set; } = true;
        // The player chose STEAM LOBBIES (instead of CONNECT) on the WKSR window's first page: kept for the next start.
        public bool SteamLobbies { get; set; }
        public bool SoloUnapproved { get; set; }

        private string _path;

        // Set when an existing settings file could not be used. The file has been copied to DamagedCopy and nothing
        // has been written over it yet: Save() would replace the player's identity with a new one.
        [JsonIgnore] public string LoadProblem { get; private set; }
        [JsonIgnore] public string DamagedCopy { get; private set; }
        [JsonIgnore] public string FilePath { get { return _path; } }
        // There was no settings file: the first start on this PC (or profile).
        [JsonIgnore] public bool IsNew { get; private set; }

        public byte[] TokenBytes()
        {
            return Convert.FromBase64String(Token);
        }

        public static ClientSettings Load(string profile)
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WKRace");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, string.IsNullOrEmpty(profile) ? "client.json" : "client." + profile + ".json");

            ClientSettings s = null;
            string problem = null;
            bool isNew = !File.Exists(path);
            if (!isNew)
            {
                try
                {
                    s = JsonConvert.DeserializeObject<ClientSettings>(File.ReadAllText(path));
                    if (s == null) problem = "The file is empty.";
                    else if (!string.IsNullOrEmpty(s.Token) && !ValidToken(s.Token)) problem = "The saved identity key is damaged.";
                }
                catch (Exception e)
                {
                    problem = e.Message;
                }
            }

            string damagedCopy = null;
            if (problem != null)
            {
                // Keep the unreadable file: it holds the player's identity, which cannot be recovered any other way.
                damagedCopy = path + ".damaged-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                try { File.Copy(path, damagedCopy, true); }
                catch (IOException) { damagedCopy = null; }
                s = null;
            }

            if (s == null) s = new ClientSettings();
            if (s.Muted == null) s.Muted = new List<string>();
            if (s.VoiceMuted == null) s.VoiceMuted = new List<string>();
            if (string.IsNullOrEmpty(s.Token) || s.PlayerId == Guid.Empty)
            {
                s.PlayerId = Guid.NewGuid();
                var token = new byte[ProtocolConstants.TokenLength];
                using (RandomNumberGenerator random = RandomNumberGenerator.Create()) random.GetBytes(token);
                s.Token = Convert.ToBase64String(token);
            }
            s._path = path;
            s.LoadProblem = problem;
            s.IsNew = isNew;
            s.DamagedCopy = damagedCopy;
            if (problem == null) s.Save();
            return s;
        }

        // Keeps a copy of the current file (for example before the identity in it is replaced by a Steam sign-in).
        public void Backup(string label)
        {
            try
            {
                if (File.Exists(_path)) File.Copy(_path, _path + "." + label + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
            }
        }

        // Writes a new file next to the old one and then swaps it in, so a crash mid-save cannot leave a half-written file.
        public void Save()
        {
            string temp = _path + ".tmp";
            try
            {
                File.WriteAllText(temp, JsonConvert.SerializeObject(this, Formatting.Indented));
                if (File.Exists(_path)) File.Replace(temp, _path, null);
                else File.Move(temp, _path);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is PlatformNotSupportedException)
            {
                // Settings are a convenience; failing to write them must not stop a run.
            }
        }

        private static bool ValidToken(string token)
        {
            try { return Convert.FromBase64String(token).Length == ProtocolConstants.TokenLength; }
            catch (FormatException) { return false; }
        }
    }
}
