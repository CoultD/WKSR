using System;
using System.Globalization;
using WKRace.Protocol;

namespace WKRace.Server
{
    public sealed class ServerOptions
    {
        public int Port = ProtocolConstants.DefaultServerPort;
        public string DataDirectory = "data";
        public int MaxConnections = 500;
        public int MaxConnectionsPerIp = 8;
        public string CertificatePath;
        public string CertificatePassword;
        // Key for the encrypted database, read from a file (--db-key-file) or a systemd credential (--db-key-credential).
        // Without one the database is plain SQLite.
        public string DatabaseKey;
        public int SnapshotMinutes = 60;
        // Steam sign-in: the public HTTPS address Steam sends players back to (nginx proxies /steam/ to the local
        // port). Without it Steam sign-in is off. With it, ranked races need a linked account unless --steam-optional.
        public string SteamPublicUrl;
        public int SteamHttpPort = 47680;
        public bool SteamOptional;
        // Bug reports from WKSR at /report (served by the same local web side, so it needs --steam-public-url).
        public bool Reports = true;
        // Finish plausibility (0 = off): a finish faster than this, or with fewer levels reached, disqualifies.
        public int MinFinishSeconds;
        public int MinFinishSplits;
        // Admin commands (wkrace-admin) on 127.0.0.1, allowed with this key; without a key they are off.
        public string AdminKey;
        public int AdminPort = 47690;
        // Who to contact about personal data, shown on the privacy page.
        public string PrivacyContact = "the operator of this server";

        public int CountdownMs = 5000;
        public int ReadyTimeoutSeconds = 120;
        public int ReconnectGraceSeconds = 60;
        public int ResumeCountdownMs = 3000;
        public int MatchTimeLimitSeconds = 5400;
        public int FinishSettleMs = 1500;
        public bool Competitive = true;
        public string PublicAddress = "";
        public int LobbyTimeoutSeconds = 600;
        public int BanTurnSeconds = 20;
        // The modifiers of every ranked race (tests switch the bans off to skip the ban phase).
        public MatchRules RankedRules = MatchRules.Ranked;
        // Once every world has loaded, how long racers have to press ready (0: no ready check).
        public int ReadyCheckSeconds = 60;
        // The check for a timer that stands still while the player moves (TimerWatch). Switched off for now
        // (2026-10): it marked honest runs. --timer-check turns it back on.
        public bool TimerCheck;
        // A party race ends this long after its first finish.
        public int PartyFinishWindowSeconds = 300;
        // Chat lines a player may send in 10 seconds (0: no limit).
        // This many chat lines within five seconds silence the player for five seconds (0: no limit).
        public int ChatLinesPer5Seconds = 5;
        // Proximity voice: the UDP port of the voice relay (0: no voice), the port clients are told when a firewall
        // or NAT maps it to another one (0: the same), and how far a racer's voice carries in a race (0: everywhere).
        public int VoicePort = 47601;
        public int VoicePublicPort;
        public int VoiceRangeMeters = 65;
        // The newest WKSR release, which clients are told about so they can ask their player to update. Empty: this
        // server's own version.
        public string LatestVersion = "";
        // Players (id, or name on a test server) who can mark mods as approved or not from WKSR, besides the ones
        // the admin command "mods manager add" stores.
        public readonly System.Collections.Generic.List<string> ModsManagers = new System.Collections.Generic.List<string>();

        // Abuse limits. 0 turns a limit off.
        public int MaxPlayers = 100000;
        public int NewPlayersPerIpPerDay = 5;
        public int ConnectAttemptsPerMinute = 60;
        public int BanMinutes = 10;
        public int LobbyAttemptsPerMinute = 10;

        public static ServerOptions Parse(string[] args)
        {
            var o = new ServerOptions();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string next = i + 1 < args.Length ? args[i + 1] : null;
                switch (a)
                {
                    case "--port": o.Port = ParseInt(a, next, 0, 65535); i++; break;
                    case "--data": o.DataDirectory = Require(a, next); i++; break;
                    case "--max-connections": o.MaxConnections = ParseInt(a, next, 1, 100000); i++; break;
                    case "--max-per-ip": o.MaxConnectionsPerIp = ParseInt(a, next, 1, 1000); i++; break;
                    case "--cert": o.CertificatePath = Require(a, next); i++; break;
                    case "--db-key-file": o.DatabaseKey = ReadKey(a, Require(a, next)); i++; break;
                    case "--db-key-credential":
                        // systemd LoadCredential=<name>:<file> puts the key in $CREDENTIALS_DIRECTORY, readable only by the service.
                        string dir = Environment.GetEnvironmentVariable("CREDENTIALS_DIRECTORY");
                        if (string.IsNullOrEmpty(dir)) throw new ArgumentException(a + " needs systemd's LoadCredential (CREDENTIALS_DIRECTORY is not set)");
                        o.DatabaseKey = ReadKey(a, System.IO.Path.Combine(dir, Require(a, next)));
                        i++;
                        break;
                    case "--snapshot-minutes": o.SnapshotMinutes = ParseInt(a, next, 0, 10080); i++; break;
                    case "--steam-public-url":
                        string url = Require(a, next).TrimEnd('/');
                        if (!url.StartsWith("https://", StringComparison.Ordinal)) throw new ArgumentException(a + " must be an https:// address");
                        o.SteamPublicUrl = url;
                        i++;
                        break;
                    case "--steam-http-port": o.SteamHttpPort = ParseInt(a, next, 1, 65535); i++; break;
                    case "--steam-optional": o.SteamOptional = true; break;
                    case "--no-reports": o.Reports = false; break;
                    case "--min-finish-seconds": o.MinFinishSeconds = ParseInt(a, next, 0, 86400); i++; break;
                    case "--min-finish-splits": o.MinFinishSplits = ParseInt(a, next, 0, 1000); i++; break;
                    case "--admin-port": o.AdminPort = ParseInt(a, next, 1, 65535); i++; break;
                    case "--admin-key-file": o.AdminKey = ReadKey(a, Require(a, next)); i++; break;
                    case "--admin-key-credential":
                        string adminDir = Environment.GetEnvironmentVariable("CREDENTIALS_DIRECTORY");
                        if (string.IsNullOrEmpty(adminDir)) throw new ArgumentException(a + " needs systemd's LoadCredential (CREDENTIALS_DIRECTORY is not set)");
                        o.AdminKey = ReadKey(a, System.IO.Path.Combine(adminDir, Require(a, next)));
                        i++;
                        break;
                    case "--privacy-contact": o.PrivacyContact = Require(a, next).Trim(); i++; break;
                    case "--cert-password-env":
                        o.CertificatePassword = Environment.GetEnvironmentVariable(Require(a, next));
                        i++;
                        break;
                    case "--countdown-ms": o.CountdownMs = ParseInt(a, next, 0, 60000); i++; break;
                    case "--time-limit": o.MatchTimeLimitSeconds = ParseInt(a, next, 60, 86400); i++; break;
                    case "--reconnect-grace": o.ReconnectGraceSeconds = ParseInt(a, next, 5, 3600); i++; break;
                    case "--ban-turn-seconds": o.BanTurnSeconds = ParseInt(a, next, 5, 300); i++; break;
                    case "--ready-seconds": o.ReadyCheckSeconds = ParseInt(a, next, 0, 600); i++; break;
                    case "--timer-check": o.TimerCheck = true; break;
                    case "--party-finish-window": o.PartyFinishWindowSeconds = ParseInt(a, next, 10, 7200); i++; break;
                    case "--voice-port": o.VoicePort = ParseInt(a, next, 0, 65535); i++; break;
                    case "--voice-public-port": o.VoicePublicPort = ParseInt(a, next, 0, 65535); i++; break;
                    case "--voice-range": o.VoiceRangeMeters = ParseInt(a, next, 0, 100000); i++; break;
                    case "--latest-version": o.LatestVersion = (next ?? "").Trim(); i++; break;
                    case "--mods-manager": if (!string.IsNullOrWhiteSpace(next)) o.ModsManagers.Add(next.Trim()); i++; break;
                    case "--public-address":
                        string host;
                        int port;
                        if (!LobbyCode.TryParseAddress(Require(a, next), out host, out port)) throw new ArgumentException(a + " must look like host:port");
                        o.PublicAddress = next.Trim();
                        i++;
                        break;
                    case "--no-competitive": o.Competitive = false; break;
                    case "--max-players": o.MaxPlayers = ParseInt(a, next, 0, 10000000); i++; break;
                    case "--new-players-per-ip": o.NewPlayersPerIpPerDay = ParseInt(a, next, 0, 100000); i++; break;
                    case "--connects-per-minute": o.ConnectAttemptsPerMinute = ParseInt(a, next, 0, 100000); i++; break;
                    case "--ban-minutes": o.BanMinutes = ParseInt(a, next, 1, 10080); i++; break;
                    case "--lobby-attempts-per-minute": o.LobbyAttemptsPerMinute = ParseInt(a, next, 0, 10000); i++; break;
                    case "-h":
                    case "--help":
                        throw new ArgumentException(Usage);
                    default:
                        throw new ArgumentException("Unknown option: " + a + "\n" + Usage);
                }
            }
            return o;
        }

        public const string Usage =
            "WKRace.Server options:\n" +
            "  --port <n>               TCP port (default 47600)\n" +
            "  --data <dir>             Directory for players.json and matches.jsonl (default ./data)\n" +
            "  --max-connections <n>    Maximum simultaneous connections (default 500)\n" +
            "  --max-per-ip <n>         Maximum simultaneous connections per address (default 8)\n" +
            "  --cert <pfx>             Serve TLS using this certificate\n" +
            "  --cert-password-env <v>  Environment variable holding the certificate password\n" +
            "  --db-key-file <file>     Encrypt the database (wkrace.db) with the key in this file\n" +
            "  --db-key-credential <n>  Same, from the systemd credential <n> (LoadCredential=<n>:<file>)\n" +
            "  --snapshot-minutes <n>   How often to write the backup snapshot wkrace-snapshot.db (default 60, 0 = never)\n" +
            "  --steam-public-url <u>   Turn on Steam sign-in; <u> is this server's public https:// address (proxied to the port below)\n" +
            "  --steam-http-port <n>    Local port for the Steam sign-in page, 127.0.0.1 only (default 47680)\n" +
            "  --steam-optional         Let players play ranked without a linked Steam account\n" +
            "  --no-reports             Refuse bug reports (otherwise kept in <data>/reports for the mailer)\n" +
            "  --min-finish-seconds <n> Disqualify finishes faster than this (default 0 = off)\n" +
            "  --min-finish-splits <n>  Disqualify finishes with fewer levels reached (default 0 = off)\n" +
            "  --admin-key-file <file>  Allow admin commands (WKRace.Server admin ...) with this key\n" +
            "  --admin-key-credential <n>  Same, from the systemd credential <n>\n" +
            "  --admin-port <n>         Local port for admin commands, 127.0.0.1 only (default 47690)\n" +
            "  --privacy-contact <text> Contact for data requests, shown on the privacy page\n" +
            "  --countdown-ms <n>       Pre-match countdown length (default 5000)\n" +
            "  --time-limit <seconds>   Maximum match length (default 5400)\n" +
            "  --reconnect-grace <s>    How long a match stays paused for a missing player before they forfeit (default 60)\n" +
            "  --ban-turn-seconds <s>   Time each player gets to ban an artifact (default 20)\n" +
            "  --timer-check            Mark runs whose timer stood still while the player moved (off by default)\n" +
            "  --ready-seconds <s>      Ready check once every world has loaded (default 60, 0 = none)\n" +
            "  --party-finish-window <s> A party match ends this long after its first finish (default 300)\n" +
            "  --voice-port <n>         UDP port of the proximity voice relay (default 47601, 0 = no voice)\n" +
            "  --voice-public-port <n>  Voice port clients are told, when a firewall maps it (default: the same)\n" +
            "  --voice-range <m>        How far a voice carries in a match, in metres (default 65, 0 = everywhere)\n" +
            "  --latest-version <v>     The newest WKSR release clients are told about (default: this server's version)\n" +
            "  --mods-manager <player>  A player (id; a name only on a test server) who can mark mods from WKSR. Repeatable\n" +
            "  --public-address <h:p>   Put into lobby codes for clients older than 0.3.0 (newer codes carry no address)\n" +
            "  --no-competitive         Do not require the game's competitive mode\n" +
            "  --max-players <n>        Stop registering new players beyond this many (default 100000, 0 = no limit)\n" +
            "  --new-players-per-ip <n> New players one address may register per 24 h (default 5, 0 = no limit)\n" +
            "  --connects-per-minute <n> Connection attempts per address per minute before a temporary ban (default 60, 0 = no limit)\n" +
            "  --ban-minutes <n>        Length of a temporary ban (default 10)\n" +
            "  --lobby-attempts-per-minute <n>  Lobby creates/joins per player per minute (default 10, 0 = no limit)";

        private static string ReadKey(string option, string path)
        {
            string key;
            try { key = System.IO.File.ReadAllText(path).Trim(); }
            catch (Exception e) { throw new ArgumentException(option + ": cannot read " + path + " (" + e.Message + ")"); }
            if (key.Length < 16) throw new ArgumentException(option + ": the key in " + path + " is too short (at least 16 characters)");
            return key;
        }

        private static string Require(string name, string value)
        {
            if (string.IsNullOrEmpty(value)) throw new ArgumentException(name + " needs a value");
            return value;
        }

        private static int ParseInt(string name, string value, int min, int max)
        {
            int n;
            if (!int.TryParse(Require(name, value), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) || n < min || n > max)
                throw new ArgumentException(name + " must be an integer between " + min + " and " + max);
            return n;
        }
    }
}
