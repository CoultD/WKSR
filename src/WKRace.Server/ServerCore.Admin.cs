using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using WKRace.Protocol;

namespace WKRace.Server
{
    // Admin commands, sent by "WKRace.Server admin ..." (wkrace-admin on the VPS) through the local admin port. They run
    // on the core thread like everything else, so the players in memory and the database stay in step.
    // A player is named by their full id, an id prefix of at least 6 characters, or their exact name.
    public sealed partial class ServerCore
    {
        public const string AdminUsage =
            "Commands:\n" +
            "  status                       players, matches and queue right now\n" +
            "  players [text]               list players (name or id containing text)\n" +
            "  show <player>                one player's details\n" +
            "  rename <player> <new name>   rename and lock the name (the player's client cannot change it back)\n" +
            "  ban <player> [reason]        refuse their sign-ins (ends their match as a loss)\n" +
            "  unban <player>\n" +
            "  ipban <ip> <minutes>         refuse connections from an address for a while\n" +
            "  export <player>              everything stored about a player, as JSON (data access requests)\n" +
            "  delete <player>              erase a player; their matches stay, anonymised (deletion requests)\n" +
            "  merge <from> <into>          fold one player into another (record, Steam link, matches); <from> is deleted\n" +
            "  board list <rating|1v1|solo> [n]   the top of a leaderboard\n" +
            "  board remove <1v1|solo> <player>   take a player's run off a time board (their next finish can replace it)\n" +
            "  board set <1v1|solo> <player> <time> [seed]   put a time on a board by hand, e.g. 15:28.75 (no replay)\n" +
            "  board flagged                runs the timer check flagged for a look (their times stand)\n" +
            "  mods list [all]              the mods that are not approved (all: every mod seen in a player's game)\n" +
            "  mods unapprove <mod>         mark a mod, every version of it, as not approved (mods approve <mod> undoes it)\n" +
            "  mods forget <mod>            drop a mod from the list\n" +
            "  mods manager add <player>    let a player mark mods from WKSR (also: manager remove <player>, manager list)\n";

        public string RunAdmin(string[] args)
        {
            if (args.Length == 0) return AdminUsage;
            string command = args[0].ToLowerInvariant();
            try
            {
                switch (command)
                {
                    case "help": return AdminUsage;
                    case "status": return AdminStatus();
                    case "players": return AdminPlayers(args.Length > 1 ? string.Join(" ", args.Skip(1)) : "");
                    case "show": return Describe(Resolve(args, 1));
                    case "rename": return AdminRename(Resolve(args, 1), string.Join(" ", args.Skip(2)));
                    case "ban": return AdminBan(Resolve(args, 1), string.Join(" ", args.Skip(2)));
                    case "unban": return AdminUnban(Resolve(args, 1));
                    case "export": return AdminExport(Resolve(args, 1));
                    case "delete": return AdminDelete(Resolve(args, 1));
                    case "merge": return AdminMerge(Resolve(args, 1), Resolve(args, 2));
                    case "board": return AdminBoard(args);
                    case "mods": return AdminMods(args);
                    default: return "Unknown command \"" + command + "\".\n" + AdminUsage;
                }
            }
            catch (AdminException e)
            {
                return "Error: " + e.Message + "\n";
            }
        }

        private sealed class AdminException : Exception
        {
            public AdminException(string message) : base(message) { }
        }

        private PlayerRecord Resolve(string[] args, int index)
        {
            if (args.Length <= index || string.IsNullOrWhiteSpace(args[index])) throw new AdminException("missing player.");
            string query = args[index].Trim();
            Guid id;
            if (Guid.TryParse(query, out id))
            {
                PlayerRecord exact = _store.Find(id);
                if (exact == null) throw new AdminException("no player with id " + id + ".");
                return exact;
            }
            List<PlayerRecord> all = _store.All();
            List<PlayerRecord> byName = all.Where(p => string.Equals(p.Name, query, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byName.Count == 1) return byName[0];
            if (query.Length >= 6)
            {
                List<PlayerRecord> byId = all.Where(p => p.Id.ToString("N").StartsWith(query.Replace("-", "").ToLowerInvariant(), StringComparison.Ordinal)).ToList();
                if (byId.Count == 1) return byId[0];
                if (byId.Count > 1) throw new AdminException("\"" + query + "\" matches " + byId.Count + " ids; use more of it.");
            }
            if (byName.Count > 1) throw new AdminException("several players are named \"" + query + "\"; use the id.");
            throw new AdminException("no player \"" + query + "\".");
        }

        private string AdminStatus()
        {
            int online = _players.Values.Count(p => p.Conn != null);
            return "Players: " + _store.Count + " stored, " + online + " online\n" +
                   "Matches running: " + _matches.Count + "\n" +
                   "Queue: " + _queue.Count + "\n" +
                   "Lobbies open: " + _lobbies.Count + "\n" +
                   "Steam sign-in: " + (SteamEnabled ? (SteamRequiredForRanked ? "on, required for ranked" : "on, optional") : "off") + "\n" +
                   "Finish checks: " + (_options.MinFinishSeconds > 0 || _options.MinFinishSplits > 0
                       ? "at least " + _options.MinFinishSeconds + " s and " + _options.MinFinishSplits + " levels" : "off") + "\n";
        }

        private string AdminPlayers(string filter)
        {
            string f = filter.Trim().ToLowerInvariant();
            List<PlayerRecord> found = _store.All()
                .Where(p => f.Length == 0 || p.Name.ToLowerInvariant().Contains(f) || p.Id.ToString("N").StartsWith(f.Replace("-", ""), StringComparison.Ordinal))
                .OrderByDescending(p => p.Rating).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var sb = new StringBuilder();
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0,-10} {1,-24} {2,6} {3,9} {4,-6} {5,-16} {6}", "ID", "NAME", "RATING", "W-L", "STEAM", "LAST SEEN (UTC)", "NOTES"));
            foreach (PlayerRecord p in found.Take(100)) sb.AppendLine(Line(p));
            if (found.Count > 100) sb.AppendLine("... and " + (found.Count - 100) + " more; narrow the search.");
            sb.AppendLine(found.Count + " player(s).");
            return sb.ToString();
        }

        private string Line(PlayerRecord p)
        {
            var notes = new List<string>();
            if (p.Banned) notes.Add("BANNED");
            if (p.NameLocked) notes.Add("name locked");
            if (p.Provisional) notes.Add("provisional");
            Player online;
            if (_players.TryGetValue(p.Id, out online) && online.Conn != null) notes.Add(online.Match != null ? "in a match" : "online");
            return string.Format(CultureInfo.InvariantCulture, "{0,-10} {1,-24} {2,6} {3,9} {4,-6} {5,-16} {6}",
                p.Id.ToString("N").Substring(0, 8), p.Name, p.Rating, p.Wins + "-" + p.Losses, p.SteamHash != null ? "yes" : "no",
                p.LastSeenUnixMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(p.LastSeenUnixMs).UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "-",
                string.Join(", ", notes));
        }

        private string Describe(PlayerRecord p)
        {
            return "Id:        " + p.Id + "\n" +
                   "Name:      " + p.Name + (p.NameLocked ? " (locked by an admin)" : "") + "\n" +
                   "Rating:    " + p.Rating + (p.Provisional ? " (provisional)" : "") + "   " + p.Wins + " wins, " + p.Losses + " losses" +
                   (p.BestTimeMs >= 0 ? "   best " + RaceFormat.Time(p.BestTimeMs) : "") + "\n" +
                   "Steam:     " + (p.SteamHash != null ? "linked" : "not linked") + "\n" +
                   "Created:   " + Utc(p.CreatedUnixMs) + "   last seen " + Utc(p.LastSeenUnixMs) + "\n" +
                   (p.Banned ? "BANNED:    " + (p.BanReason ?? "") + "\n" : "");
        }

        private static string Utc(long ms)
        {
            return ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC" : "-";
        }

        private string AdminRename(PlayerRecord p, string requested)
        {
            string name = NameSanitizer.CleanName(requested);
            if (name == null) throw new AdminException("that name is not usable (at least 2 letters or digits).");
            PlayerRecord holder = _store.FindByName(name, p);
            if (holder != null) throw new AdminException("\"" + name + "\" belongs to " + holder.Id.ToString("N").Substring(0, 8) + ".");
            string old = p.Name;
            p.Name = name;
            p.NameLocked = true;
            _store.MarkDirty(p);
            // An online player picks the new name up when they next connect; disconnect them so that is now.
            Player online;
            if (_players.TryGetValue(p.Id, out online) && online.Conn != null && online.Match == null) Kick(online, "Your name was changed by the server admin.", ErrorCode.Unknown);
            _log("admin renamed " + old + " to " + name);
            return "Renamed " + old + " to " + name + " (locked).\n";
        }

        private string AdminBan(PlayerRecord p, string reason)
        {
            p.Banned = true;
            p.BanReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
            _store.MarkDirty(p);
            Player online;
            if (_players.TryGetValue(p.Id, out online))
            {
                RemoveFromMatch(online, "was removed from the server.");
                RemoveFromQueue(online);
                LeaveLobby(online, null);
                if (online.Conn != null) Kick(online, "This player is banned from the server" + (p.BanReason == null ? "." : ": " + p.BanReason));
            }
            _log("admin banned " + p.Name + (p.BanReason == null ? "" : ": " + p.BanReason));
            return "Banned " + p.Name + ".\n";
        }

        private string AdminUnban(PlayerRecord p)
        {
            p.Banned = false;
            p.BanReason = null;
            _store.MarkDirty(p);
            _log("admin unbanned " + p.Name);
            return "Unbanned " + p.Name + ".\n";
        }

        private string AdminExport(PlayerRecord p)
        {
            List<MatchEntry> matches = _store.Database.LoadMatches().Where(m => m.A.Id == p.Id || m.B.Id == p.Id).ToList();
            var export = new
            {
                exportedUtc = DateTime.UtcNow,
                player = new
                {
                    id = p.Id,
                    name = p.Name,
                    rating = p.Rating,
                    wins = p.Wins,
                    losses = p.Losses,
                    bestTimeMs = p.BestTimeMs,
                    createdUtc = p.CreatedUnixMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(p.CreatedUnixMs).UtcDateTime : (DateTime?)null,
                    lastSeenUtc = p.LastSeenUnixMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(p.LastSeenUnixMs).UtcDateTime : (DateTime?)null,
                    steamLinked = p.SteamHash != null,
                    banned = p.Banned,
                    banReason = p.BanReason,
                },
                matches = matches.Select(m => new
                {
                    id = m.Id,
                    endedUtc = DateTimeOffset.FromUnixTimeMilliseconds(m.EndedUnixMs).UtcDateTime,
                    seed = m.Seed,
                    ranked = m.Ranked,
                    result = m.Reason,
                    you = m.A.Id == p.Id ? m.A : m.B,
                    opponent = (m.A.Id == p.Id ? m.B : m.A).Name,
                    bans = m.BansJson,
                }),
                leaderboardTimes = BoardExport(p.Id),
                playedRankedSinceLeaderboards = OnRatingBoardList(p.Id),
                notStored = "Your key is stored only as a one-way hash, your Steam account only as a keyed hash. IP addresses are kept in server logs for 21 days.",
            };
            return JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }) + "\n";
        }

        private string AdminDelete(PlayerRecord p)
        {
            Player online;
            if (_players.TryGetValue(p.Id, out online))
            {
                if (online.Match != null && online.Match.Phase != MatchPhase.Completed) throw new AdminException(p.Name + " is in a match; try again after it.");
                RemoveFromQueue(online);
                LeaveLobby(online, null);
                if (online.Conn != null) Kick(online, "This player was deleted from the server.");
                _players.Remove(p.Id);
            }
            _store.Database.AnonymizeMatches(p.Id, p.Name);
            ForgetBoardRuns(p.Id);
            ForgetModManager(p.Id);
            _store.Remove(p);
            _log("admin deleted a player (data erasure)");
            return "Deleted " + p.Name + "; their matches now show \"deleted player\".\n";
        }

        private string AdminMerge(PlayerRecord from, PlayerRecord into)
        {
            if (from == into) throw new AdminException("that is the same player.");
            foreach (PlayerRecord p in new[] { from, into })
            {
                Player online;
                if (_players.TryGetValue(p.Id, out online) && online.Match != null && online.Match.Phase != MatchPhase.Completed)
                    throw new AdminException(p.Name + " is in a match; try again after it.");
            }
            if (from.SteamHash != null && into.SteamHash != null && from.SteamHash != into.SteamHash)
                throw new AdminException("both players are linked to different Steam accounts.");

            Player fromOnline;
            if (_players.TryGetValue(from.Id, out fromOnline))
            {
                RemoveFromQueue(fromOnline);
                LeaveLobby(fromOnline, null);
                if (fromOnline.Conn != null) Kick(fromOnline, "This player was merged into " + into.Name + ".");
                _players.Remove(from.Id);
            }

            into.Wins += from.Wins;
            into.Losses += from.Losses;
            if (from.BestTimeMs >= 0 && (into.BestTimeMs < 0 || from.BestTimeMs < into.BestTimeMs)) into.BestTimeMs = from.BestTimeMs;
            string steam = into.SteamHash ?? from.SteamHash;
            _store.Database.ReassignMatches(from.Id, into.Id);
            MoveBoardRuns(from.Id, into.Id);
            ForgetModManager(from.Id);
            _store.Remove(from);          // first, so the Steam link is free to move
            into.SteamHash = steam;
            _store.MarkDirty(into);
            _log("admin merged " + from.Name + " into " + into.Name);
            return "Merged " + from.Name + " into " + into.Name + " (rating " + into.Rating + ", " + into.Wins + "-" + into.Losses + ").\n";
        }

        // BadToken makes the client stop reconnecting (ban, delete, merge); other codes let it come straight back.
        private void Kick(Player player, string message, ErrorCode code = ErrorCode.BadToken)
        {
            IConnection conn = player.Conn;
            if (conn == null) return;
            _byConnection.Remove(conn);
            player.Conn = null;
            conn.Send(new ErrorMessage(code, message));
            conn.Close("admin");
        }
    }
}
