using System;
using System.Windows.Threading;
using WKRace.Client;
using WKRace.Protocol;

namespace WKRace.Mod
{
    using Message = WKRace.Protocol.Message;

    // What the race controller talks to: WKSR's logic running inside the game (EmbeddedLink). (Before 0.9.3 it was
    // the WKSR program, over a local socket.)
    internal interface IModLink
    {
        bool Connected { get; }
        bool SawUnreadable { get; }
        void Start();
        void Stop();
        bool TryReceive(out Message message);
        void Send(Message message);
    }

    // WKSR inside the game. Its logic is the same code that ran in the WKSR program (WKRace.Client.MainViewModel): it
    // keeps the connection to the match server, the player's account and every mode's rules. The in-game menu reads
    // its state and presses its commands; the race controller exchanges the same messages with it as it did with the
    // program, handed over in memory instead of over a socket.
    internal static class EmbeddedClient
    {
        public static MainViewModel Vm { get; private set; }
        public static string StartProblem { get; private set; }

        public static void Start()
        {
            if (Vm != null) return;
            Dispatcher.CurrentDispatcher.Attach();
            Dispatcher.Failed += e => Plugin.Log.LogError("WKSR: " + e);
            try
            {
                string profile = RaceConfig.Profile.Value.Trim();
                ClientSettings settings = ClientSettings.Load(profile);
                if (settings.LoadProblem != null)
                {
                    // The file holds the player's identity. It is left as it is (a copy was made); this session runs on a
                    // new identity, which is only written if the player changes a setting.
                    StartProblem = "Your WKSR settings file could not be read (" + settings.LoadProblem + "). A copy was kept"
                                   + (settings.DamagedCopy != null ? " as " + System.IO.Path.GetFileName(settings.DamagedCopy) : "") + ". You are playing as a new player.";
                    Plugin.Log.LogWarning(StartProblem);
                }
                Vm = new MainViewModel(settings, Endpoint(), false);
                Plugin.Log.LogInfo("WKSR runs inside the game: player " + settings.Name + (settings.IsNew ? " (new)" : "") + ".");
            }
            catch (Exception e)
            {
                StartProblem = "WKSR could not start: " + e.Message;
                Plugin.Log.LogError("WKSR could not start: " + e);
            }
        }

        // The official server, unless a test server is named in the config ("host:port", without TLS).
        private static ServerEndpoint Endpoint()
        {
            string text = RaceConfig.TestServer.Value.Trim();
            if (text.Length == 0) return OfficialServer.Endpoint();
            int colon = text.LastIndexOf(':');
            int port = ProtocolConstants.DefaultServerPort;
            string host = text;
            if (colon > 0 && int.TryParse(text.Substring(colon + 1), out port)) host = text.Substring(0, colon);
            Plugin.Log.LogWarning("WKSR uses the test server " + host + ":" + port + " (Debug/TestServer in the config).");
            return new ServerEndpoint { Host = host, Port = port, Tls = false, Custom = true };
        }

        // Once a frame, on the main thread: everything WKSR's logic queued (server messages, timers) runs here.
        public static void Update()
        {
            if (Vm != null) Dispatcher.CurrentDispatcher.Pump();
        }

        public static void Stop()
        {
            if (Vm == null) return;
            try { Vm.Shutdown(); }
            catch (Exception e) { Plugin.Log.LogWarning("WKSR shutdown: " + e.Message); }
            Vm = null;
        }
    }

    internal sealed class EmbeddedLink : IModLink
    {
        private readonly string _modVersion;
        private readonly string _gameVersion;
        private ModBridge _bridge;

        public EmbeddedLink(string modVersion, string gameVersion)
        {
            _modVersion = modVersion;
            _gameVersion = gameVersion;
        }

        public bool Connected { get { return _bridge != null && _bridge.Connected; } }
        public bool SawUnreadable { get { return false; } }

        public void Start()
        {
            EmbeddedClient.Start();
            _bridge = ModBridge.Current;
            if (_bridge == null) return;
            _bridge.SetConnected(true);
            // What the game said first over the socket: who it is, and its hotkeys.
            _bridge.FromMod(new ModHello { ModVersion = _modVersion, GameVersion = _gameVersion });
            _bridge.FromMod(new ModKeys { Keys = RaceConfig.CurrentKeys() });
        }

        public void Stop()
        {
            if (_bridge != null) _bridge.SetConnected(false);
            EmbeddedClient.Stop();
        }

        public bool TryReceive(out Message message)
        {
            message = null;
            return _bridge != null && _bridge.TryTake(out message);
        }

        public void Send(Message message)
        {
            if (_bridge != null && _bridge.Connected) _bridge.FromMod(message);
        }
    }
}
