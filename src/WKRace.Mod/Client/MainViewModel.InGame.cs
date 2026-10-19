using System;
using System.Security.Cryptography;
using WKRace.Protocol;

namespace WKRace.Client
{
    // What WKSR's logic does differently inside the game. (The rest of the class is the WKSR program's own source,
    // linked into the mod.)
    internal sealed partial class MainViewModel
    {
        // ---- online only when the player wants it ------------------------------------------------------------------

        // The game connects to the match server only after CONNECT in the WKSR window, and DISCONNECT takes it
        // offline again; the choice is kept for the next start. Offline, unranked solo runs still work.
        public bool WantsOnline { get { return _settings.Online; } }

        public void GoOnline()
        {
            if (_settings.Online) return;
            // CONNECT and STEAM LOBBIES are one or the other.
            if (SteamMode) LeaveSteam();
            _settings.Online = true;
            _settings.Save();
            Raise(nameof(WantsOnline));
            _connectDue = DateTime.UtcNow;
        }

        public void GoOffline()
        {
            if (!_settings.Online) return;
            _settings.Online = false;
            _settings.Save();
            Raise(nameof(WantsOnline));
            _connectDue = DateTime.MaxValue;
            if (_server != null) _server.Stop();
            _server = null;
            _refusedStatus = null;
            CloseChatRooms();
            OnLinkState(LinkState.Disconnected, "");
            Log("Disconnected from the match server. Unranked runs work offline.");
        }

        // ---- the player's name is their Steam name -----------------------------------------------------------------

        private DateTime _connectDue = DateTime.MaxValue;
        // Read on the main thread, used by the connection's thread when it signs in.
        private volatile string _steamName;

        private void ConnectWhenSteamIsUp()
        {
            string name = WKRace.Mod.GameBridge.SteamName();
            if (!string.IsNullOrEmpty(name)) _steamName = name;
            if (_connectDue == DateTime.MaxValue) return;
            if (_steamName == null && DateTime.UtcNow < _connectDue) return;
            _connectDue = DateTime.MaxValue;
            ConnectToServer();
        }

        // What the player is called when signing in: their Steam name (the server tidies it and numbers it if someone
        // else has it), or the stored name when Steam is not there.
        private string HelloName()
        {
            string steam = _steamName;
            return string.IsNullOrEmpty(steam) || steam.Trim().Length < 2 ? _playerName : steam;
        }

        // The key (a Unity key code) of one hotkey, for the options page.
        public int KeyCodeOf(string action)
        {
            switch (action)
            {
                case "Ready": return _keys.Ready;
                case "Forfeit": return _keys.Forfeit;
                case "Splits": return _keys.Splits;
                case "Ghost": return _keys.Ghost;
                case "Chat": return _keys.Chat;
                default: return 0;
            }
        }

        // ---- Steam: the account the game runs under is the sign-in ------------------------------------------------

        private bool _steamIdentitySent;
        private bool _startedOver;

        // Once signed in to the match server, tells it which Steam account the game runs under. Nothing is sent when
        // the player is already linked, or while Steam has not come up yet (it is tried again on the next tick).
        private void SendSteamIdentityIfDue()
        {
            if (_steamIdentitySent || _server == null || _link != LinkState.Connected || _welcomeName == null) return;
            if (!_steamAvailable || _steamLinked)
            {
                _steamIdentitySent = true;
                return;
            }
            ulong id = WKRace.Mod.GameBridge.SteamId();
            if (id == 0)
            {
                SteamStatus = "Start the game through Steam";
                return;
            }
            _steamIdentitySent = true;
            _server.Send(new SteamIdentity { SteamId = id });
        }

        // The server no longer takes this PC's key: the player was signed in with Steam on another PC since. With the
        // game running under Steam, this PC starts over as an empty player and the Steam account brings the real one
        // back (the server moves it here). Once per session, so a refusal for another reason cannot loop.
        private bool StartOverForSteam()
        {
            if (_startedOver || WKRace.Mod.GameBridge.SteamId() == 0) return false;
            _startedOver = true;
            _settings.Backup("replaced-key");
            _settings.PlayerId = Guid.NewGuid();
            var token = new byte[ProtocolConstants.TokenLength];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create()) random.GetBytes(token);
            _settings.Token = Convert.ToBase64String(token);
            // A name of its own for the moment: the player's real name is taken (by the player being restored).
            _settings.Name = NameGenerator.Next();
            _settings.Save();
            PlayerName = _settings.Name;
            Log("This PC's key was replaced by a sign-in on another PC. Signing in again through Steam...");
            if (_server != null) _server.Stop();
            _server = null;
            ConnectToServer();
            return true;
        }
    }
}
