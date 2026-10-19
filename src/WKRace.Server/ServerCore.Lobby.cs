using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using WKRace.Protocol;

namespace WKRace.Server
{
    // Friendly races: a host opens a lobby (1v1 or party, with the modifiers they pick) and shares its code; others
    // join with it. A 1v1 lobby races as soon as the second player is in; a party lobby when the host presses START.
    // After a race everyone is back in the lobby for a rematch. A host who leaves hands the lobby to the next player.
    public sealed partial class ServerCore
    {
        private readonly Dictionary<uint, Lobby> _lobbies = new Dictionary<uint, Lobby>();

        public int LobbyCount { get { return _lobbies.Count; } }

        // Lobby ids are random 32-bit numbers, so guessing one is hopeless anyway; this also stops lobby spam.
        private bool LobbyAttemptAllowed(Player player)
        {
            int limit = _options.LobbyAttemptsPerMinute;
            if (limit <= 0) return true;
            long now = _now();
            Queue<long> times = player.LobbyAttempts;
            while (times.Count > 0 && times.Peek() <= now - 60000) times.Dequeue();
            if (times.Count >= limit)
            {
                player.Send(new ErrorMessage(ErrorCode.RateLimited, "Too many lobby attempts. Wait a minute and try again."));
                return false;
            }
            times.Enqueue(now);
            return true;
        }

        private void HandleLobbyCreate(Player player, LobbyCreate create)
        {
            if (!LobbyAttemptAllowed(player)) return;
            EndSolo(player, "opened a lobby");
            if (player.Match != null)
            {
                player.Send(new ErrorMessage(ErrorCode.AlreadyInMatch, "Finish or forfeit your current match first."));
                return;
            }
            string modsRefusal = LobbyModsRefusal(player, create.AllowUnapproved, true);
            if (modsRefusal != null)
            {
                player.Send(new ErrorMessage(ErrorCode.ModsNotApproved, modsRefusal));
                return;
            }
            RemoveFromQueue(player);
            LeaveLobby(player, null);

            uint id;
            do { id = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue) ^ ((uint)RandomNumberGenerator.GetInt32(0, 2) << 31); }
            while (_lobbies.ContainsKey(id));

            MatchRules rules = create.Rules & MatchRules.All;
            if (create.Mode == MatchMode.Party) rules &= ~MatchRules.ArtifactBans;
            long now = _now();
            var lobby = new Lobby
            {
                Id = id, Host = player, Mode = create.Mode, Rules = rules, AllowUnapproved = create.AllowUnapproved,
                GameBuild = create.GameBuild ?? "", CreatedMs = now, AloneSinceMs = now,
            };
            lobby.Members.Add(player);
            _lobbies[id] = lobby;
            player.Lobby = lobby;
            player.GameBuild = create.GameBuild ?? "";
            _log(player.Record.Name + " opened a " + RulesText.Mode(lobby.Mode) + " lobby (" + rules + (lobby.AllowUnapproved ? ", unapproved mods allowed" : "") + ")");
            player.Send(new LobbyCreated { LobbyId = id, PublicAddress = _options.PublicAddress ?? "", ExpiresInSeconds = _options.LobbyTimeoutSeconds });
            SendLobbyState(lobby, player, now);
        }

        private void HandleLobbyJoin(Player player, LobbyJoin join)
        {
            if (!LobbyAttemptAllowed(player)) return;
            EndSolo(player, "joined a lobby");
            Lobby lobby;
            if (!_lobbies.TryGetValue(join.LobbyId, out lobby))
            {
                player.Send(new ErrorMessage(ErrorCode.LobbyNotFound, "That lobby code is not valid or the lobby has closed."));
                return;
            }
            if (player.Lobby == lobby)
            {
                if (lobby.Host == player && lobby.Members.Count == 1)
                    player.Send(new ErrorMessage(ErrorCode.LobbyUnavailable, "That is your own lobby. Send the code to your friends."));
                else
                    SendLobbyState(lobby, player, _now());
                return;
            }
            if (player.Match != null)
            {
                player.Send(new ErrorMessage(ErrorCode.AlreadyInMatch, "Finish or forfeit your current match first."));
                return;
            }
            if (lobby.Match != null)
            {
                player.Send(new ErrorMessage(ErrorCode.LobbyBusy, "That lobby is in a match right now. Try the code again when it is over."));
                return;
            }
            if (lobby.Members.Count >= lobby.MaxPlayers)
            {
                player.Send(new ErrorMessage(ErrorCode.LobbyFull, lobby.Mode == MatchMode.Party
                    ? "That party lobby is full (" + lobby.MaxPlayers + " players)." : "That 1v1 lobby already has two players."));
                return;
            }
            if (lobby.Host.Conn == null)
            {
                player.Send(new ErrorMessage(ErrorCode.LobbyUnavailable, "The lobby's host is not available right now."));
                return;
            }
            if (!BuildsPlayTogether(join.GameBuild, lobby.GameBuild))
            {
                player.Send(new ErrorMessage(ErrorCode.LobbyBuildMismatch, BuildTag.CompatibleBesidesGame(join.GameBuild, lobby.GameBuild)
                    ? "You and the host are on different versions of White Knuckle, and those two make different worlds. Both need the same one."
                    : "You and the host have different versions of the WK Race mod. Both need the latest."));
                return;
            }
            player.GameBuild = join.GameBuild ?? "";
            string modsRefusal = LobbyModsRefusal(player, lobby.AllowUnapproved, false);
            if (modsRefusal != null)
            {
                player.Send(new ErrorMessage(ErrorCode.ModsNotApproved, modsRefusal));
                return;
            }

            RemoveFromQueue(player);
            LeaveLobby(player, null);
            lobby.Members.Add(player);
            lobby.AloneSinceMs = -1;
            player.Lobby = lobby;
            _log(player.Record.Name + " joined the lobby of " + lobby.Host.Record.Name);
            BroadcastLobby(lobby);
            SendChatHistory(player, lobby.Chat);
            SystemChat(lobby.Members, player.Record.Name + " joined.");

            if (lobby.Mode == MatchMode.Duel && lobby.Members.Count == 2) StartLobbyRace(lobby);
        }

        // The host starts the race: a party with two players or more, or a 1v1 rematch.
        private void HandleLobbyStart(Player player)
        {
            Lobby lobby = player.Lobby;
            if (lobby == null)
            {
                player.Send(new ErrorMessage(ErrorCode.LobbyNotFound, "You are not in a lobby."));
                return;
            }
            if (lobby.Host != player)
            {
                player.Send(new ErrorMessage(ErrorCode.NotHost, "Only the host (" + lobby.Host.Record.Name + ") can start the match."));
                return;
            }
            if (lobby.Match != null) return;
            int ready = lobby.Members.Count(m => m.Conn != null && m.Match == null);
            if (ready < 2)
            {
                player.Send(new ErrorMessage(ErrorCode.LobbyUnavailable, "A match needs at least two players. Send the code to your friends."));
                return;
            }
            StartLobbyRace(lobby);
        }

        private void StartLobbyRace(Lobby lobby)
        {
            List<Player> racers = lobby.Members.Where(m => m.Conn != null && m.Match == null).ToList();
            if (racers.Count < 2) return;
            foreach (Player p in racers) RemoveFromQueue(p);
            lobby.Match = CreateMatch(racers, false, lobby.Mode, lobby.Rules, lobby);
            BroadcastLobby(lobby);
        }

        // After a lobby's race: everyone is back in the lobby. Players who left during the race are let go now.
        private void ReturnToLobby(Match match)
        {
            Lobby lobby = match.Lobby;
            if (lobby == null || lobby.Match != match) return;
            lobby.Match = null;
            foreach (Player gone in lobby.Members.Where(m => m.Conn == null).ToList()) LeaveLobby(gone, null);
            if (_lobbies.ContainsKey(lobby.Id)) BroadcastLobby(lobby);
        }

        private void HandleLobbyCancel(Player player)
        {
            LeaveLobby(player, null);
        }

        // Takes the player out of their lobby. `reason` (when not null) is sent to them as LobbyClosed.
        private void LeaveLobby(Player player, string reason)
        {
            Lobby lobby = player.Lobby;
            if (lobby == null) return;
            player.Lobby = null;
            lobby.Members.Remove(player);
            if (reason != null) player.Send(new LobbyClosed { LobbyId = lobby.Id, Reason = reason });
            SendVoicePeers(player);
            if (lobby.Members.Count == 0)
            {
                _lobbies.Remove(lobby.Id);
                return;
            }
            SystemChat(lobby.Members, player.Record.Name + " left.");
            if (lobby.Host == player)
            {
                lobby.Host = lobby.Members.FirstOrDefault(m => m.Conn != null) ?? lobby.Members[0];
                SystemChat(lobby.Members, lobby.Host.Record.Name + " is the host now.");
            }
            if (lobby.Members.Count == 1) lobby.AloneSinceMs = _now();
            BroadcastLobby(lobby);
        }

        // Closes the lobby for everyone in it. reason == null closes silently.
        private void CloseLobby(Lobby lobby, string reason)
        {
            if (lobby == null) return;
            _lobbies.Remove(lobby.Id);
            List<Player> members = lobby.Members.ToList();
            foreach (Player m in members)
            {
                if (m.Lobby == lobby) m.Lobby = null;
                if (reason != null) m.Send(new LobbyClosed { LobbyId = lobby.Id, Reason = reason });
            }
            lobby.Members.Clear();
            SendVoicePeers(members);
        }

        // A lobby nobody joined closes after LobbyTimeoutSeconds.
        private void ExpireLobbies(long now)
        {
            long limit = _options.LobbyTimeoutSeconds * 1000L;
            foreach (Lobby lobby in _lobbies.Values.Where(l => l.Match == null && l.Members.Count <= 1 && l.AloneSinceMs >= 0 && now - l.AloneSinceMs > limit).ToArray())
            {
                CloseLobby(lobby, "Your lobby expired. Open a new one to get a fresh code.");
            }
        }

        private void BroadcastLobby(Lobby lobby)
        {
            long now = _now();
            foreach (Player m in lobby.Members) SendLobbyState(lobby, m, now);
            SendVoicePeers(lobby.Members);
        }

        private void SendLobbyState(Lobby lobby, Player to, long now)
        {
            var state = new LobbyState
            {
                LobbyId = lobby.Id,
                Mode = lobby.Mode,
                Rules = lobby.Rules,
                MaxPlayers = lobby.MaxPlayers,
                YouAreHost = lobby.Host == to,
                Racing = lobby.Match != null,
                AllowUnapproved = lobby.AllowUnapproved,
                ExpiresInSeconds = lobby.Match == null && lobby.Members.Count <= 1 && lobby.AloneSinceMs >= 0
                    ? (int)Math.Max(0, (_options.LobbyTimeoutSeconds * 1000L - (now - lobby.AloneSinceMs)) / 1000) : -1,
            };
            foreach (Player m in lobby.Members)
            {
                state.Members.Add(new LobbyMember
                {
                    Name = m.Record.Name,
                    Rating = m.Record.Rating,
                    Host = m == lobby.Host,
                    Racing = lobby.Match != null && m.Match == lobby.Match,
                });
            }
            to.Send(state);
        }
    }
}
