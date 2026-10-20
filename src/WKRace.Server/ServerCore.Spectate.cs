using System;
using System.Collections.Generic;
using System.Linq;
using WKRace.Protocol;

namespace WKRace.Server
{
    // Inside the game (the Steam lobby host) a global type of the game is also called Message.
    using Message = WKRace.Protocol.Message;

    // Spectating. A match can be watched from its start (GO): before it, its artifact bans (which shape the world) may
    // not be settled. A spectator joins the audience of a match (ranked, or a public lobby's) or of a ranked solo run; their
    // game builds the same world from the seed and follows the players with what their opponents get. A player who is
    // out of their own match can watch the rest of it the same way. Racers and spectators are told how many watch.
    // Shared with the Steam lobby host (lobby matches only; ranked solo runs are the official server's).
    public sealed partial class ServerCore
    {
        internal const string SpectateVersion = "0.9.6";

        // Whether `p` is in a match or ranked solo run that others may watch right now (racers only).
        private static bool Watchable(Player p)
        {
            if (p.Solo != null) return SoloWatchable(p);
            Match m = p.Match;
            if (m == null) return false;
            if (m.Lobby != null && !m.Lobby.Public) return false;
            if (m.Survivor != null) return m.Survivor.Player == p;
            return m.ClockStarted && m.SlotOf(p) != null && m.SlotOf(p).Active;
        }

        private void HandleSpectateRequest(Player player, SpectateRequest request)
        {
            // Out of their own match (or the loser of a 1v1 whose winner keeps running): watch the rest of it.
            if (request.OwnMatch != Guid.Empty)
            {
                Match own;
                if (!_matches.TryGetValue(request.OwnMatch, out own)) return;
                Slot slot = own.SlotOf(player);
                // Not theirs, still running in it, or the winner who keeps running.
                if (slot == null || own.Survivor == slot || (player.Match == own && slot.Active)) return;
                Watch(player, own.Audience, player.Match == own, Leader(own).Racer);
                return;
            }
            if (player.Match != null || player.Solo != null)
            {
                player.Send(new SpectateEnd { Reason = "Finish your match or run first." });
                return;
            }

            Player target = null;
            Match match = null;
            if (request.Tag != 0)
            {
                target = _players.Values.FirstOrDefault(p => p.Tag == request.Tag && p.Conn != null);
                if (target != null) match = target.Match;
            }
            else if (request.LobbyId != 0)
            {
                Lobby lobby;
                if (_lobbies.TryGetValue(request.LobbyId, out lobby) && lobby.Public) match = lobby.Match;
            }
            if (target != null && target.Solo != null && SoloWatchable(target))
            {
                Watch(player, SoloAudience(target), false);
                return;
            }
            bool can = match != null && (match.Lobby == null || match.Lobby.Public) && (match.ClockStarted || match.Survivor != null);
            if (!can)
            {
                player.Send(new SpectateEnd { Reason = target != null ? target.Record.Name + " is not in a match that can be watched." : "That match cannot be watched." });
                return;
            }
            if (player.Queue != null)
            {
                RemoveFromQueue(player);
                player.Send(new QueueStatus { Queued = false, PlayersQueued = _queue.Count });
            }
            byte follow = 0;
            if (target != null && match.SlotOf(target) != null) follow = match.SlotOf(target).Racer;
            Watch(player, match.Audience, false, follow);
        }

        private void Watch(Player player, Audience audience, bool own, byte follow = 0)
        {
            if (player.Watching == audience) return;
            StopWatching(player, null);
            SpectateStart start = audience.Match != null ? SpectateStartOf(audience.Match, follow) : SoloSpectateStart(audience.Solo);
            if (start == null) return;
            start.Own = own;
            audience.Spectators.Add(player);
            player.Watching = audience;
            player.Send(start);
            Match match = audience.Match;
            if (match != null && !own)
            {
                // Everything a racer who signed in again would get: the roster, the splits so far, the finishes.
                long now = _now();
                player.Send(RosterFor(match, null, now));
                foreach (Slot s in match.Slots)
                {
                    foreach (SplitReached split in s.Splits.Values.OrderBy(x => x.Index)) player.Send(ToOpponentSplit(split, s));
                    if (s.Finished) player.Send(new OpponentFinished { Attempt = s.Attempt, RaceTimeMs = s.FinishMs, Racer = s.Racer });
                }
            }
            _log(player.Record.Name + " watches " + AudienceName(audience));
            SendSpectatorCount(audience);
        }

        private SpectateStart SpectateStartOf(Match match, byte follow)
        {
            // Follow the asked player, else the leader.
            Slot followed = match.Slots.FirstOrDefault(s => s.Racer == follow);
            if (followed == null || !(followed.Active || followed == match.Survivor)) followed = Leader(match);
            follow = followed.Racer;
            return new SpectateStart
            {
                MatchId = match.Id,
                Seed = match.Seed,
                Mode = match.Mode,
                Rules = match.Rules,
                Ranked = match.Ranked,
                Competitive = _options.Competitive,
                Segment = match.Segment,
                GoServerTimeMs = match.GoMs,
                WatchRacer = follow,
                Bans = new List<string>(match.Banned),
            };
        }

        // The one still running who is furthest up (most levels reached), or the winner who keeps running.
        private static Slot Leader(Match match)
        {
            if (match.Survivor != null) return match.Survivor;
            Slot best = null;
            foreach (Slot s in match.Slots)
                if (s.Active && (best == null || s.Splits.Count > best.Splits.Count)) best = s;
            return best ?? match.Slots[0];
        }

        private void HandleSpectateStop(Player player)
        {
            StopWatching(player, null);
        }

        // reason: told to the spectator (null: they asked, or left).
        private void StopWatching(Player player, string reason)
        {
            Audience audience = player.Watching;
            if (audience == null) return;
            player.Watching = null;
            audience.Spectators.Remove(player);
            if (reason != null) player.Send(new SpectateEnd { MatchId = AudienceId(audience), Reason = reason });
            _log(player.Record.Name + " stopped watching " + AudienceName(audience));
            SendSpectatorCount(audience);
        }

        // The match or run is over: everyone watching is told.
        private void EndAudience(Audience audience, string reason)
        {
            if (audience == null || audience.Spectators.Count == 0) return;
            foreach (Player p in audience.Spectators.ToList())
            {
                p.Watching = null;
                p.Send(new SpectateEnd { MatchId = AudienceId(audience), Reason = reason });
            }
            audience.Spectators.Clear();
        }

        // What the players of a match get, passed on to those who watch it (but not to its own players, who have it).
        private void ToAudience(Match match, Message message)
        {
            foreach (Player p in match.Audience.Spectators)
                if (p.Match != match) p.Send(message);
        }

        private void RosterToAudience(Match match)
        {
            if (match.Audience.Spectators.Count == 0) return;
            ToAudience(match, RosterFor(match, null, _now()));
        }

        private void SendSpectatorCount(Audience audience)
        {
            var count = new SpectatorCount { MatchId = AudienceId(audience), Count = (ushort)Math.Min(audience.Spectators.Count, ushort.MaxValue) };
            IEnumerable<Player> players = audience.Match != null ? audience.Match.Slots.Select(s => s.Player) : new[] { audience.Solo };
            foreach (Player p in players.Concat(audience.Spectators).Distinct())
                if (p != null && p.Conn != null && BuildTag.AtLeast(p.ClientVersion, SpectateVersion)) p.Send(count);
        }

        // Everyone in the Match chat room of what a spectator watches: its players and its audience.
        private static IEnumerable<Player> AudienceRoom(Audience audience)
        {
            IEnumerable<Player> players = audience.Match != null ? audience.Match.Slots.Select(s => s.Player) : new[] { audience.Solo };
            return players.Where(p => p != null).Concat(audience.Spectators).Distinct().ToList();
        }

        private Guid AudienceId(Audience audience)
        {
            if (audience.Match != null) return audience.Match.Id;
            return SoloMatchId(audience.Solo);
        }

        private static string AudienceName(Audience audience)
        {
            if (audience.Match != null) return "match " + Short(audience.Match);
            return audience.Solo != null ? audience.Solo.Record.Name + "'s ranked solo run" : "a run";
        }
    }
}
