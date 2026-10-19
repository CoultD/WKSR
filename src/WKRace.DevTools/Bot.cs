using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using WKRace.Protocol;

namespace WKRace.DevTools
{
    // A computer-controlled opponent that talks to the race server directly, for solo practice and testing.
    internal static class Bot
    {
        // The artifacts White Knuckle's artifact room offers, as the mod reads them from the game (0.61, October 2026).
        // The bot has no game to read them from; the server only keeps what both racers offer.
        private static readonly string[] BotPool =
        {
            "Item_Artifact_Remote", "Item_Artifact_Timepiece", "Item_Artifact_Translocator",
            "Item_Artifact_EVAGlove", "Item_Artifact_Rebar_Return", "Item_Artifact_Rapier",
        };

        private sealed class Options
        {
            public string Host = "127.0.0.1";
            public int Port = ProtocolConstants.DefaultServerPort;
            public string Name = "Pacer";
            public double Speed = 0.7;        // metres per second
            public double SplitEvery = 45;    // metres per split
            public int FinishAfter = 900;     // seconds
            public bool Loop;
            public int DropAt = -1;           // race seconds at which to drop the connection once, or -1
            public int DropFor = 10;          // seconds to stay away
            public uint LobbyId;              // join this lobby instead of queueing
            public bool HostParty;            // open a party lobby (prints its code) instead of queueing
            public int StartAt;               // as the party host: start the race once this many are in (0 = never)
            public int DieAt = -1;            // race seconds at which to die, or -1
            public bool Afk;                  // never press ready and never climb (stands at the bottom)
            public string Say;                // a chat line to send once in the lobby or race
            public double OffsetX, OffsetZ;   // where the bot climbs, relative to the start (to spread several out)
            public double VoicePitch;          // speak a test tone at this pitch (Hz) through the voice relay, or 0
        }

        // Progress that survives a dropped connection within one match.
        private sealed class Race
        {
            public Guid MatchId;
            public long GoServerMs;
            public long ResumeServerMs;
            public bool Started, Paused, Finished, Dropped;
            public int NextSplit;
            public long NextState;
        }

        private static bool InLobby(Options o)
        {
            return o.LobbyId != 0 || o.HostParty;
        }

        public static int Run(string[] args)
        {
            var o = new Options();
            for (int i = 1; i < args.Length; i++)
            {
                string next = i + 1 < args.Length ? args[i + 1] : null;
                switch (args[i])
                {
                    case "--server":
                        int colon = next.LastIndexOf(':');
                        o.Host = colon < 0 ? next : next.Substring(0, colon);
                        if (colon >= 0) o.Port = int.Parse(next.Substring(colon + 1), CultureInfo.InvariantCulture);
                        i++;
                        break;
                    case "--name": o.Name = next; i++; break;
                    case "--speed": o.Speed = double.Parse(next, CultureInfo.InvariantCulture); i++; break;
                    case "--split-every": o.SplitEvery = double.Parse(next, CultureInfo.InvariantCulture); i++; break;
                    case "--finish-after": o.FinishAfter = int.Parse(next, CultureInfo.InvariantCulture); i++; break;
                    case "--drop-at": o.DropAt = int.Parse(next, CultureInfo.InvariantCulture); i++; break;
                    case "--drop-for": o.DropFor = int.Parse(next, CultureInfo.InvariantCulture); i++; break;
                    case "--loop": o.Loop = true; break;
                    case "--host-party": o.HostParty = true; break;
                    case "--start-at": o.StartAt = int.Parse(next, CultureInfo.InvariantCulture); i++; break;
                    case "--die-at": o.DieAt = int.Parse(next, CultureInfo.InvariantCulture); i++; break;
                    case "--afk": o.Afk = true; break;
                    case "--say": o.Say = next; i++; break;
                    case "--voice": o.VoicePitch = next != null && !next.StartsWith("--") ? double.Parse(next, CultureInfo.InvariantCulture) : 220; if (next != null && !next.StartsWith("--")) i++; break;
                    case "--offset":
                        string[] xz = next.Split(',');
                        o.OffsetX = double.Parse(xz[0], CultureInfo.InvariantCulture);
                        o.OffsetZ = xz.Length > 1 ? double.Parse(xz[1], CultureInfo.InvariantCulture) : 0;
                        i++;
                        break;
                    case "--lobby":
                        LobbyCode code;
                        if (!LobbyCode.TryDecode(next, out code)) { Console.Error.WriteLine("Not a valid lobby code."); return 2; }
                        o.LobbyId = code.LobbyId;
                        i++;
                        break;
                    default:
                        Console.Error.WriteLine("Usage: WKRace.DevTools bot [--server host:port] [--name n] [--speed m/s] [--split-every m] [--finish-after s] [--drop-at s] [--drop-for s]\n" +
                            "    [--lobby code | --host-party [--start-at n]] [--die-at s] [--afk] [--say text] [--offset x,z] [--voice [hz]] [--loop]\n" +
                            "  A bot in a lobby stays in it after each race (for rematches) until it is stopped.");
                        return 2;
                }
            }

            Guid id = Guid.NewGuid();
            byte[] token = RandomNumberGenerator.GetBytes(ProtocolConstants.TokenLength);
            do
            {
                var race = new Race();
                try
                {
                    // Reconnects with the same identity until the match is over, like a real client would.
                    while (!Play(o, id, token, race))
                    {
                        Console.WriteLine("Bot offline for " + o.DropFor + " s.");
                        Thread.Sleep(o.DropFor * 1000);
                    }
                }
                catch (Exception e) { Console.WriteLine("Bot error: " + e.Message); Thread.Sleep(2000); }
            } while (o.Loop);
            return 0;
        }

        // The bot's "world" has loaded; it is ready at once (an --afk bot never is).
        private static void Loaded(Options o, Race race, Action<Message> send)
        {
            send(new MatchReady { MatchId = race.MatchId, WorldHash = 0, LevelCount = 0 });
            if (!o.Afk) send(new PlayerReady { MatchId = race.MatchId });
        }

        // Returns true when the match is over, false when the connection was dropped on purpose.
        private static bool Play(Options o, Guid id, byte[] token, Race race)
        {
            using (var tcp = new TcpClient(o.Host, o.Port) { NoDelay = true })
            {
                var stream = tcp.GetStream();
                Action<Message> send = m => FrameIO.WriteFrame(stream, MessageCodec.Encode(m));
                Func<Message> read = () =>
                {
                    byte[] frame = FrameIO.ReadFrame(stream);
                    return frame == null ? null : MessageCodec.Decode(frame);
                };

                send(new ClientHello { PlayerId = id, Token = token, Name = o.Name, ClientVersion = "bot" });
                bool said = false;
                if (race.MatchId == Guid.Empty)
                {
                    if (o.HostParty)
                    {
                        Console.WriteLine("Bot " + o.Name + " connected, opening a party lobby.");
                        send(new LobbyCreate { Mode = MatchMode.Party, Rules = MatchRules.CuratedSeeds });
                    }
                    else if (o.LobbyId != 0)
                    {
                        Console.WriteLine("Bot " + o.Name + " connected, joining the lobby.");
                        send(new LobbyJoin { LobbyId = o.LobbyId });
                    }
                    else
                    {
                        Console.WriteLine("Bot " + o.Name + " connected, queueing.");
                        send(new QueueJoin());
                    }
                }
                else
                {
                    Console.WriteLine("Bot " + o.Name + " reconnected.");
                }

                BotVoice voice = null;
                long nextPing = 0, nextVoiceReport = 0;
                const double startX = 0, startY = 2.4, startZ = 4.7;
                stream.ReadTimeout = 50;
                while (true)
                {
                    Message m = null;
                    try { m = read(); if (m == null) return true; }
                    catch (IOException e) when (e.InnerException is SocketException se && se.SocketErrorCode == SocketError.TimedOut) { }

                    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (now >= nextPing)
                    {
                        nextPing = now + 2000;
                        send(new Ping { ClientTimeMs = now });
                    }
                    if (voice != null && now >= nextVoiceReport)
                    {
                        nextVoiceReport = now + 10000;
                        Console.WriteLine("Voice: " + voice.Received + " packets heard from others so far.");
                    }

                    switch (m)
                    {
                        case LobbyCreated created:
                            Console.WriteLine("Party lobby open: " + LobbyCode.Encode(created.LobbyId));
                            break;
                        case LobbyState lobby:
                            Console.WriteLine("Lobby: " + string.Join(", ", lobby.Members.Select(x => x.Name + (x.Host ? "*" : ""))) + (lobby.Racing ? " (racing)" : ""));
                            if (o.HostParty && lobby.YouAreHost && !lobby.Racing && o.StartAt > 1 && lobby.Members.Count >= o.StartAt && race.MatchId == Guid.Empty)
                            {
                                Console.WriteLine("Starting the race.");
                                send(new LobbyStart());
                            }
                            if (!said && o.Say != null) { said = true; send(new ChatSend { Text = o.Say }); }
                            break;
                        case ChatMessage chat:
                            Console.WriteLine(chat.System ? "* " + chat.Text : chat.From + ": " + chat.Text);
                            break;
                        case VoiceHello hello:
                            if (o.VoicePitch > 0 && hello.Port > 0)
                            {
                                voice?.Dispose();
                                voice = new BotVoice(hello, o.Host, o.VoicePitch);
                                send(new VoiceState { Enabled = true });
                                Console.WriteLine("Voice on: a " + o.VoicePitch + " Hz tone every 4 s.");
                            }
                            break;
                        case VoicePeers peers:
                            Console.WriteLine("Voice peers: " + string.Join(", ", peers.Peers.Select(x => x.Name)) + (voice != null ? "  (voice packets heard so far: " + voice.Received + ")" : ""));
                            break;
                        case MatchRoster roster:
                            Console.WriteLine("Roster: " + string.Join(", ", roster.Racers.Select(r => r.Name + " " + r.Status)) + (roster.ReadySecondsLeft >= 0 ? "  (ready check " + roster.ReadySecondsLeft + " s)" : ""));
                            break;
                        case MatchFound found:
                            if (found.MatchId != race.MatchId)
                            {
                                race.MatchId = found.MatchId;
                                Console.WriteLine((found.Party ? "Party race of " + found.PlayerCount : (found.Ranked ? "Match" : "Friendly race") + " vs " + found.OpponentName)
                                    + ", seed " + found.Seed + ", " + RulesText.Describe(found.Rules) + (found.ArtifactBans ? ". Artifact bans first." : "."));
                                // With bans the world only "loads" once they are done; the bot offers the game's usual pool.
                                if (found.ArtifactBans) send(new BanReady { MatchId = race.MatchId, Pool = new List<string>(BotPool) });
                                else Loaded(o, race, send);
                            }
                            break;
                        case BanState bans:
                            if (bans.MatchId != race.MatchId) break;
                            if (bans.Done)
                            {
                                Console.WriteLine("Bans done: " + (bans.Banned.Count == 0 ? "none" : string.Join(", ", bans.Banned)) + ".");
                                Loaded(o, race, send);
                            }
                            else if (bans.YourTurn)
                            {
                                // Bans the last artifact nobody has banned yet, so a player can predict it.
                                string pick = bans.Pool.LastOrDefault(item => !bans.Banned.Contains(item)) ?? "";
                                Console.WriteLine("Bot bans " + (pick.Length > 0 ? pick : "nothing") + ".");
                                send(new BanPick { MatchId = race.MatchId, Item = pick });
                            }
                            break;
                        case MatchStart start:
                            race.GoServerMs = start.GoServerTimeMs;
                            if (!race.Started) Console.WriteLine("Countdown started.");
                            race.Started = true;
                            break;
                        case MatchPaused paused:
                            if (!race.Paused) Console.WriteLine("Paused: " + paused.AwayName + " away, " + paused.SecondsLeft + " s left.");
                            race.Paused = true;
                            break;
                        case MatchResumed resumed:
                            if (race.Paused) Console.WriteLine("Resuming.");
                            race.Paused = false;
                            race.GoServerMs = resumed.GoServerTimeMs;
                            race.ResumeServerMs = resumed.ResumeServerTimeMs;
                            break;
                        case MatchResult result:
                            Console.WriteLine("Match over: " + result.Outcome + " - " + result.Reason);
                            if (result.Standings.Count > 0) Console.WriteLine("Standings: " + string.Join(", ", result.Standings.Select(s => s.Place + ". " + s.Name + " " + s.Status)));
                            if (!InLobby(o)) return true;
                            // Stay in the lobby for the next race.
                            race.MatchId = Guid.Empty;
                            race.Started = race.Paused = race.Finished = race.Dropped = false;
                            race.NextSplit = 0;
                            race.NextState = 0;
                            race.ResumeServerMs = 0;
                            break;
                        case LobbyClosed closed:
                            Console.WriteLine("Lobby closed: " + closed.Reason);
                            return true;
                        case ErrorMessage error:
                            Console.WriteLine("Server: " + error.Text);
                            if (!InLobby(o) || race.MatchId == Guid.Empty && error.Code != ErrorCode.NotInMatch) return true;
                            break;
                    }

                    if (!race.Started || race.Finished || race.Paused || o.Afk || now < race.ResumeServerMs) continue;
                    long raceMs = now - race.GoServerMs;
                    if (raceMs < 0) continue;

                    if (o.DropAt >= 0 && !race.Dropped && raceMs >= o.DropAt * 1000L)
                    {
                        race.Dropped = true;
                        Console.WriteLine("Dropping the connection at " + RaceFormat.Time(raceMs) + ".");
                        return false;
                    }

                    double climbed = o.Speed * raceMs / 1000.0;
                    if (raceMs >= race.NextState)
                    {
                        race.NextState = raceMs + 66;
                        send(new PlayerState { Attempt = 1, RaceTimeMs = (int)raceMs, X = (float)(startX + o.OffsetX), Y = (float)(startY + climbed), Z = (float)(startZ + o.OffsetZ), Yaw = 0f });
                    }
                    if (climbed >= race.NextSplit * o.SplitEvery && race.NextSplit < 1000)
                    {
                        send(new SplitReached { Attempt = 1, Index = race.NextSplit, Level = race.NextSplit == 0 ? "Start" : "Bot " + race.NextSplit, Region = "pacer", RaceTimeMs = (int)raceMs });
                        race.NextSplit++;
                    }
                    if (o.DieAt >= 0 && raceMs >= o.DieAt * 1000L)
                    {
                        race.Finished = true;
                        send(new PlayerDied { Attempt = 1, RaceTimeMs = (int)raceMs });
                        Console.WriteLine("Bot died.");
                        continue;
                    }
                    if (raceMs >= o.FinishAfter * 1000L)
                    {
                        race.Finished = true;
                        send(new PlayerFinished { Attempt = 1, RaceTimeMs = (int)raceMs });
                        Console.WriteLine("Bot finished.");
                    }
                }
            }
        }
    }
}
