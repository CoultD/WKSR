using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using WKRace.Protocol;

// STEAM LOBBIES: the match server's core (ServerCore) runs inside the lobby host's game. These stand in for what the
// core needs from the server's storage and from its parts that only make sense on the official server (ranked solo,
// leaderboards, the mods list, Steam sign-in, voice). Everything lives in memory for as long as the lobby does.

namespace WKRace.Server
{
    using Message = WKRace.Protocol.Message;

    // The players who signed in to this host. Nothing is written to disk.
    public sealed class PlayerStore
    {
        private readonly object _gate = new object();
        private readonly Dictionary<Guid, PlayerRecord> _players = new Dictionary<Guid, PlayerRecord>();

        public PlayerRecord Find(Guid id)
        {
            lock (_gate)
            {
                PlayerRecord record;
                return _players.TryGetValue(id, out record) ? record : null;
            }
        }

        public PlayerRecord Add(PlayerRecord record)
        {
            lock (_gate)
            {
                if (record.CreatedUnixMs == 0) record.CreatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _players[record.Id] = record;
            }
            return record;
        }

        public void MarkDirty(PlayerRecord record)
        {
        }

        public void Flush()
        {
        }

        public int Count
        {
            get { lock (_gate) return _players.Count; }
        }

        // Names are unique ignoring case and spacing, as on the official server.
        public PlayerRecord FindByName(string name, PlayerRecord except = null)
        {
            string key = NameKey(name);
            lock (_gate)
            {
                foreach (PlayerRecord p in _players.Values)
                    if (p != except && NameKey(p.Name) == key) return p;
                return null;
            }
        }

        public static string NameKey(string name)
        {
            return string.Join(" ", (name ?? "").ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }

        public static string HashToken(byte[] token)
        {
            using (SHA256 sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(token ?? new byte[0]));
        }

        public static bool TokenMatches(string expectedHash, string actualHash)
        {
            return string.Equals(expectedHash ?? "", actualHash ?? "", StringComparison.Ordinal);
        }
    }

    // Finished matches are not kept: each player's game keeps its own recent runs.
    public sealed class MatchLog
    {
        public void Append(MatchEntry entry)
        {
        }

        public static string BansJson(List<string> banned, List<string> bannedBy)
        {
            return null;
        }
    }

    // Ranked solo runs are hosted by the official server only.
    internal sealed class SoloRun
    {
    }

    public sealed partial class ServerCore
    {
        // The host is leaving: every lobby on this host closes, and everyone in it is told why.
        public void CloseAllLobbies(string reason)
        {
            foreach (Lobby lobby in new List<Lobby>(_lobbies.Values)) CloseLobby(lobby, reason);
        }

        private const string OfficialOnly = "That needs the match server (CONNECT): Steam lobbies only have lobbies and unranked runs.";

        private bool SteamEnabled { get { return false; } }
        private bool SteamRequiredForRanked { get { return false; } }

        // The ranked queue is refused through the mods check, the first thing it asks after the Steam link.
        private string RankedModsRefusal(Player player, string what)
        {
            return OfficialOnly;
        }

        private string LobbyModsRefusal(Player player, bool allowUnapproved, bool creating)
        {
            return null;
        }

        private void SendModVerdict(Player player)
        {
        }

        private void HandleLoadedMods(Player player, LoadedMods part)
        {
        }

        private void HandleModsRequest(Player player, ModsRequest request)
        {
        }

        private void HandleModMark(Player player, ModMark mark)
        {
        }

        private void HandleSoloStart(Player player, SoloStart start)
        {
            player.Send(new ErrorMessage(ErrorCode.ProtocolViolation, OfficialOnly));
        }

        private bool RouteSolo(Player player, Message message)
        {
            return false;
        }

        private void RestartEndedSolo(Player player, PracticeRestart restart)
        {
        }

        private void EndSolo(Player player, string why)
        {
        }

        private void TickSolo(long now)
        {
        }

        private void HandleBoardRequest(Player player, BoardRequest request)
        {
        }

        private void HandleReplayRequest(Player player, ReplayRequest request)
        {
        }

        private static RunRecorder NewReplayRecorder()
        {
            return new RunRecorder(190);
        }

        private void NoteRankedPlayed(PlayerRecord record)
        {
        }

        private void SubmitBoardRun(BoardKind kind, Player player, int timeMs, int seed, MatchRules rules, IList<string> bans, string gameBuild,
            RunRecorder recorder, TimerWatch watch)
        {
        }

        private void HandleSteamLinkStart(Player player)
        {
        }

        private void HandleSteamIdentity(Player player, SteamIdentity identity)
        {
        }

        private void GiveVoice(Player player)
        {
        }

        private void DropVoice(Player player)
        {
        }

        private void HandleVoiceState(Player player, VoiceState state)
        {
        }

        private void SendVoicePeers(Player player)
        {
        }

        private void SendVoicePeers(IEnumerable<Player> players)
        {
        }
    }
}
