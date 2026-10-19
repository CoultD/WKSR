using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using WKRace.Protocol;

namespace WKRace.Server
{
    // The mods list. A player's game reports the plugins it has loaded (through WKSR); the server keeps a list of
    // every mod it has seen, each approved until its manager marks it otherwise. A mark is on the mod's id, so it
    // covers every version.
    //
    // A player with a mod that is not approved gets no ranked 1v1 and no ranked solo run, and only gets into lobbies
    // whose host ticked "allow unapproved mods". Who had which mod is not stored: a player's list lives in memory
    // while they are connected.
    public sealed partial class ServerCore
    {
        // Mods the list holds at most. A mod beyond that is not recorded, and so counts as approved.
        private const int MaxKnownMods = 4000;
        // New mods one player's lists may add to the server's list in an hour (a made-up list cannot fill it).
        private const int NewModsPerPlayerHour = 200;
        private const int ModsListGapMs = 500;
        private const int ModsRequestGapMs = 200;
        // An unchanged mod's "last seen" is written at most this often.
        private const long ModSeenWriteGapMs = 24L * 3600 * 1000;
        private const string OwnModId = "com.wkrace.mod";

        private Dictionary<string, ModRecord> _mods;
        private HashSet<Guid> _modManagers;
        private int _modsNotApproved;

        private void LoadMods()
        {
            if (_mods != null) return;
            _mods = _store.Database.LoadMods();
            _modManagers = _store.Database.LoadModManagers();
            _modsNotApproved = _mods.Values.Count(m => !m.Approved);
        }

        // Some mod is marked "not approved". Until then nothing is refused, and WKSR hides the list and the boxes.
        public bool AnyModMarked { get { LoadMods(); return _modsNotApproved > 0; } }

        private bool IsModManager(Player player)
        {
            LoadMods();
            if (_modManagers.Contains(player.Id)) return true;
            foreach (string named in _options.ModsManagers)
            {
                Guid id;
                if (Guid.TryParse(named, out id) ? id == player.Id : string.Equals(named, player.Record.Name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // Printable ASCII only, trimmed: these strings come from clients and are shown to other players.
        private static string CleanModText(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var clean = new StringBuilder(Math.Min(text.Length, max));
            foreach (char c in text)
            {
                if (clean.Length >= max) break;
                clean.Append(c >= ' ' && c < 127 ? c : '?');
            }
            return clean.ToString().Trim();
        }

        // ---- a player's list ------------------------------------------------------------------------------------

        private void HandleLoadedMods(Player player, LoadedMods part)
        {
            if (part.Parts == 0)
            {
                // No game: no mods.
                player.PendingMods = null;
                SetMods(player, new List<ModEntry>(), false);
                return;
            }
            if (part.Part == 0)
            {
                long now = _now();
                if (now - player.LastModsListMs < ModsListGapMs && now >= player.LastModsListMs)
                {
                    player.PendingMods = null;
                    return;
                }
                player.LastModsListMs = now;
                player.PendingMods = new List<ModEntry>();
                player.PendingModParts = part.Parts;
                player.PendingModNext = 0;
            }
            // The parts come in order over one connection; anything else is not a list.
            if (player.PendingMods == null || part.Parts != player.PendingModParts || part.Part != player.PendingModNext)
            {
                player.PendingMods = null;
                return;
            }
            player.PendingMods.AddRange(part.Mods);
            player.PendingModNext++;
            if (player.PendingModNext < part.Parts) return;

            List<ModEntry> list = player.PendingMods;
            player.PendingMods = null;
            SetMods(player, list, part.Cut);
        }

        private void SetMods(Player player, List<ModEntry> reported, bool cut)
        {
            LoadMods();
            long now = _now();
            var mods = new Dictionary<string, ModEntry>(StringComparer.Ordinal);
            foreach (ModEntry entry in reported)
            {
                string id = CleanModText(entry.Id, ModEntry.MaxIdLength);
                string key = id.ToLowerInvariant();
                if (key.Length == 0 || key == OwnModId || mods.ContainsKey(key)) continue;
                string name = CleanModText(entry.Name, ModEntry.MaxNameLength);
                mods[key] = new ModEntry { Id = id, Name = name.Length > 0 ? name : id, Version = CleanModText(entry.Version, ModEntry.MaxVersionLength) };
            }
            bool changed = cut != player.ModsCut || mods.Count != player.Mods.Count || mods.Keys.Any(k => !player.Mods.ContainsKey(k));
            player.Mods = mods;
            player.ModsCut = cut;
            foreach (KeyValuePair<string, ModEntry> pair in mods) NoteMod(player, pair.Key, pair.Value, now);
            if (changed && mods.Count > 0)
                _log(player.Record.Name + "'s game has " + mods.Count + (mods.Count == 1 ? " mod" : " mods") + (cut ? " (list cut)" : "") + ": "
                     + string.Join(", ", mods.Values.Take(12).Select(m => m.Name + " " + m.Version)) + (mods.Count > 12 ? ", ..." : ""));
            SendModVerdict(player);
            EnforceMods(player);
        }

        // The server's list learns the mod, or that it is still around. The name is the one first reported.
        private void NoteMod(Player player, string key, ModEntry mod, long now)
        {
            ModRecord record;
            if (!_mods.TryGetValue(key, out record))
            {
                if (_mods.Count >= MaxKnownMods) return;
                Queue<long> added = player.NewModTimes;
                while (added.Count > 0 && added.Peek() <= now - 3600000) added.Dequeue();
                if (added.Count >= NewModsPerPlayerHour) return;
                added.Enqueue(now);
                record = new ModRecord { Id = mod.Id, Name = mod.Name, Version = mod.Version, Approved = true, FirstSeenMs = WallClockMs(), LastSeenMs = WallClockMs() };
                _mods[key] = record;
                _store.Database.SaveMod(key, record);
                _log("New mod on the list: " + record.Name + " " + record.Version + " (" + record.Id + "), from " + player.Record.Name);
                return;
            }
            long wall = WallClockMs();
            if (record.Version == mod.Version && wall - record.LastSeenMs < ModSeenWriteGapMs) return;
            record.Version = mod.Version;
            record.LastSeenMs = wall;
            _store.Database.SaveMod(key, record);
        }

        private static long WallClockMs()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        // The mods in the player's game that are not approved, by name. A list that was cut hides mods, so it counts
        // as one once anything is marked.
        private List<string> UnapprovedMods(Player player)
        {
            LoadMods();
            var names = new List<string>();
            if (_modsNotApproved == 0) return names;
            foreach (KeyValuePair<string, ModEntry> pair in player.Mods)
            {
                ModRecord record;
                if (_mods.TryGetValue(pair.Key, out record) && !record.Approved) names.Add(record.Name);
            }
            if (player.ModsCut) names.Add("more mods than can be listed (over " + LoadedMods.MaxMods + ")");
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        private static string ModNames(List<string> names)
        {
            const int shown = 4;
            return string.Join(", ", names.Take(shown)) + (names.Count > shown ? " and " + (names.Count - shown) + " more" : "");
        }

        private void SendModVerdict(Player player)
        {
            if (player.Conn == null) return;
            List<string> names = UnapprovedMods(player);
            var verdict = new ModVerdict { AnyMarked = _modsNotApproved > 0, Manager = IsModManager(player), Unapproved = names.Count };
            verdict.Names.AddRange(names.Take(ModVerdict.MaxNames));
            player.Send(verdict);
        }

        // ---- the rules --------------------------------------------------------------------------------------------

        // Why the player cannot play ranked (`what`: "Ranked matches", "Ranked solo runs") with their game as it
        // is, or null when they can.
        private string RankedModsRefusal(Player player, string what)
        {
            List<string> names = UnapprovedMods(player);
            if (names.Count == 0) return null;
            return what + " only take approved mods. Not approved in your game: " + ModNames(names) + ". Remove them and start the game again.";
        }

        // Why the player cannot be in this lobby, or null when they can.
        private string LobbyModsRefusal(Player player, bool allowUnapproved, bool creating)
        {
            if (allowUnapproved) return null;
            List<string> names = UnapprovedMods(player);
            if (names.Count == 0) return null;
            return creating
                ? "Your game has mods that are not approved (" + ModNames(names) + "). Tick ALLOW UNAPPROVED MODS to open a lobby with them."
                : "This lobby does not allow mods that are not approved, and your game has: " + ModNames(names) + ".";
        }

        // The player's list changed, or a mark did: whatever they are in that their mods do not allow ends for them.
        private void EnforceMods(Player player)
        {
            List<string> names = UnapprovedMods(player);
            if (names.Count == 0) return;
            string have = "Not approved in your game: " + ModNames(names) + ".";

            if (player.Queue != null)
            {
                RemoveFromQueue(player);
                player.Send(new QueueStatus { Queued = false, PlayersQueued = _queue.Count });
                player.Send(new ErrorMessage(ErrorCode.ModsNotApproved, "You were taken out of the ranked queue: ranked matches only take approved mods. " + have));
                _log(player.Record.Name + " left the queue: unapproved mods (" + ModNames(names) + ")");
            }
            SoloRun solo = player.Solo;
            if (solo != null)
            {
                _log(player.Record.Name + "'s ranked solo run ends: unapproved mods (" + ModNames(names) + ")");
                solo.Host.OnMessage(new PlayerForfeit());
                player.Send(new ErrorMessage(ErrorCode.ModsNotApproved, "Your ranked solo run ended: ranked solo runs only take approved mods. " + have));
            }
            Match match = player.Match;
            if (match != null && match.Phase != MatchPhase.Completed && match.Ranked)
            {
                _log(player.Record.Name + " is out of their ranked match: unapproved mods (" + ModNames(names) + ")");
                player.Send(new ErrorMessage(ErrorCode.ModsNotApproved, "Your ranked match ended: ranked matches only take approved mods. " + have));
                HandleForfeit(player);
            }
            Lobby lobby = player.Lobby;
            if (lobby != null && !lobby.AllowUnapproved)
            {
                _log(player.Record.Name + " is out of the lobby of " + lobby.Host.Record.Name + ": unapproved mods (" + ModNames(names) + ")");
                if (lobby.Match != null && player.Match == lobby.Match) HandleForfeit(player);
                LeaveLobby(player, "This lobby does not allow mods that are not approved, and your game has: " + ModNames(names) + ".");
            }
        }

        // ---- the list, for WKSR -------------------------------------------------------------------------------------

        private static int ModOrder(ModRecord a, ModRecord b)
        {
            if (a.Approved != b.Approved) return a.Approved ? 1 : -1;
            int byName = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return byName != 0 ? byName : string.CompareOrdinal(a.Id, b.Id);
        }

        // The rows a player sees: the mods that are not approved and the ones in their own game. The manager sees
        // every mod the server knows.
        private List<KeyValuePair<string, ModRecord>> ModRowsFor(Player player)
        {
            LoadMods();
            bool manager = IsModManager(player);
            var rows = new List<KeyValuePair<string, ModRecord>>();
            foreach (KeyValuePair<string, ModRecord> pair in _mods)
                if (manager || !pair.Value.Approved || player.Mods.ContainsKey(pair.Key)) rows.Add(pair);
            // A mod of theirs the list never recorded (the list was full) still shows, as approved.
            foreach (KeyValuePair<string, ModEntry> pair in player.Mods)
                if (!_mods.ContainsKey(pair.Key))
                    rows.Add(new KeyValuePair<string, ModRecord>(pair.Key, new ModRecord { Id = pair.Value.Id, Name = pair.Value.Name, Version = pair.Value.Version, Approved = true }));
            rows.Sort((a, b) => ModOrder(a.Value, b.Value));
            return rows;
        }

        private void HandleModsRequest(Player player, ModsRequest request)
        {
            long now = _now();
            if (now - player.LastModsRequestMs < ModsRequestGapMs && now >= player.LastModsRequestMs) return;
            player.LastModsRequestMs = now;

            List<KeyValuePair<string, ModRecord>> rows = ModRowsFor(player);
            bool manager = IsModManager(player);
            int count = Math.Max(1, Math.Min((int)request.Count, ModsRequest.MaxRows));
            int offset = Math.Max(0, Math.Min(request.Offset, Math.Max(0, (rows.Count - 1) / count * count)));
            var page = new ModsPage { Offset = offset, Total = rows.Count, NotApproved = rows.Count(r => !r.Value.Approved) };
            foreach (KeyValuePair<string, ModRecord> pair in rows.Skip(offset).Take(count))
            {
                bool yours = player.Mods.ContainsKey(pair.Key);
                page.Rows.Add(new ModRow
                {
                    // The player's own version of their own mods; else the one last reported by anyone.
                    Mod = new ModEntry { Id = pair.Value.Id, Name = pair.Value.Name, Version = yours ? player.Mods[pair.Key].Version : pair.Value.Version },
                    Approved = pair.Value.Approved,
                    Yours = yours,
                    Players = manager ? _byConnection.Values.Count(p => p.Mods.ContainsKey(pair.Key)) : 0,
                });
            }
            player.Send(page);
        }

        private void HandleModMark(Player player, ModMark mark)
        {
            if (!IsModManager(player))
            {
                player.Send(new ErrorMessage(ErrorCode.Unknown, "Only the mods list's manager can mark mods."));
                return;
            }
            LoadMods();
            string key = CleanModText(mark.Id, ModEntry.MaxIdLength).ToLowerInvariant();
            ModRecord record;
            if (!_mods.TryGetValue(key, out record))
            {
                player.Send(new ErrorMessage(ErrorCode.Unknown, "That mod is not on the list any more."));
                return;
            }
            SetModApproved(key, record, mark.Approved, player.Record.Name);
        }

        // Marks a mod and applies the mark to everyone connected.
        private void SetModApproved(string key, ModRecord record, bool approved, string by)
        {
            if (record.Approved == approved) return;
            record.Approved = approved;
            _store.Database.SaveMod(key, record);
            _modsNotApproved = _mods.Values.Count(m => !m.Approved);
            _log(by + " marked the mod " + record.Name + " (" + record.Id + ") as " + (approved ? "approved" : "NOT approved"));
            foreach (Player online in _byConnection.Values.ToArray())
            {
                SendModVerdict(online);
                EnforceMods(online);
            }
        }

        private void ForgetModManager(Guid player)
        {
            LoadMods();
            if (_modManagers.Remove(player)) _store.Database.SetModManager(player, false);
        }

        // ---- admin: "mods ..." ----------------------------------------------------------------------------------------

        private const string ModsUsage =
            "Usage: mods list [all]              the mods that are not approved (all: every mod seen)\n" +
            "       mods unapprove <mod>         mark a mod (every version) as not approved\n" +
            "       mods approve <mod>\n" +
            "       mods forget <mod>            drop a mod from the list (it comes back, approved, when seen again)\n" +
            "       mods manager list | add <player> | remove <player>     who can mark mods from WKSR\n" +
            "A mod is named by its id, a part of its id that fits only one mod, or its exact name.";

        private KeyValuePair<string, ModRecord> ResolveMod(string[] args, int index)
        {
            if (args.Length <= index || string.IsNullOrWhiteSpace(args[index])) throw new AdminException("missing mod.\n" + ModsUsage);
            string query = string.Join(" ", args.Skip(index)).Trim();
            string key = query.ToLowerInvariant();
            LoadMods();
            ModRecord exact;
            if (_mods.TryGetValue(key, out exact)) return new KeyValuePair<string, ModRecord>(key, exact);
            List<KeyValuePair<string, ModRecord>> byName = _mods.Where(m => string.Equals(m.Value.Name, query, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byName.Count == 1) return byName[0];
            List<KeyValuePair<string, ModRecord>> byPart = _mods.Where(m => m.Key.Contains(key)).ToList();
            if (byPart.Count == 1) return byPart[0];
            if (byName.Count > 1 || byPart.Count > 1) throw new AdminException("\"" + query + "\" fits several mods; use the whole id:\n  " + string.Join("\n  ", byName.Concat(byPart).Select(m => m.Value.Id).Distinct()));
            throw new AdminException("no mod \"" + query + "\" on the list.");
        }

        private string AdminMods(string[] args)
        {
            LoadMods();
            string verb = args.Length > 1 ? args[1].ToLowerInvariant() : "list";
            switch (verb)
            {
                case "list":
                {
                    bool all = args.Length > 2 && args[2].ToLowerInvariant() == "all";
                    List<ModRecord> rows = _mods.Values.Where(m => all || !m.Approved).ToList();
                    rows.Sort(ModOrder);
                    var text = new StringBuilder(_mods.Count + (_mods.Count == 1 ? " mod seen, " : " mods seen, ") + _modsNotApproved + " not approved\n");
                    foreach (ModRecord m in rows)
                        text.Append(string.Format("  {0,-12}  {1,-32}  {2,-12}  {3}   last seen {4:yyyy-MM-dd}\n", m.Approved ? "approved" : "NOT APPROVED", m.Name, m.Version, m.Id,
                            DateTimeOffset.FromUnixTimeMilliseconds(m.LastSeenMs).UtcDateTime));
                    if (!all && rows.Count == 0) text.Append("  Every mod is approved. \"mods list all\" shows the mods seen.\n");
                    return text.ToString();
                }
                case "approve":
                case "unapprove":
                {
                    KeyValuePair<string, ModRecord> mod = ResolveMod(args, 2);
                    bool approved = verb == "approve";
                    if (mod.Value.Approved == approved) return mod.Value.Name + " (" + mod.Value.Id + ") is already " + (approved ? "approved" : "not approved") + ".\n";
                    SetModApproved(mod.Key, mod.Value, approved, "admin");
                    return mod.Value.Name + " (" + mod.Value.Id + ") is now " + (approved ? "approved." : "NOT approved: players with it get no ranked match or ranked solo run, and only lobbies that allow unapproved mods.") + "\n";
                }
                case "forget":
                {
                    KeyValuePair<string, ModRecord> mod = ResolveMod(args, 2);
                    bool wasMarked = !mod.Value.Approved;
                    if (wasMarked) SetModApproved(mod.Key, mod.Value, true, "admin");
                    _mods.Remove(mod.Key);
                    _store.Database.DeleteMod(mod.Key);
                    return "Forgot " + mod.Value.Name + " (" + mod.Value.Id + ")." + (wasMarked ? " It was not approved; it is approved again if it shows up." : "") + "\n";
                }
                case "manager":
                {
                    string what = args.Length > 2 ? args[2].ToLowerInvariant() : "list";
                    if (what == "list")
                    {
                        var text = new StringBuilder("Players who can mark mods from WKSR:\n");
                        foreach (Guid id in _modManagers)
                        {
                            PlayerRecord p = _store.Find(id);
                            text.Append("  " + (p != null ? p.Name : "(deleted player)") + "  " + id + "\n");
                        }
                        foreach (string named in _options.ModsManagers) text.Append("  " + named + "  (from --mods-manager)\n");
                        if (_modManagers.Count == 0 && _options.ModsManagers.Count == 0) text.Append("  nobody\n");
                        return text.ToString();
                    }
                    if (what != "add" && what != "remove") throw new AdminException(ModsUsage);
                    PlayerRecord player = Resolve(args, 3);
                    bool add = what == "add";
                    if (add ? !_modManagers.Add(player.Id) : !_modManagers.Remove(player.Id))
                        return player.Name + (add ? " already manages the mods list.\n" : " does not manage the mods list.\n");
                    _store.Database.SetModManager(player.Id, add);
                    Player online;
                    if (_players.TryGetValue(player.Id, out online)) SendModVerdict(online);
                    _log("admin " + (add ? "made " + player.Name + " a manager of the mods list" : "took the mods list away from " + player.Name));
                    return player.Name + (add ? " can now mark mods from WKSR (the MODS button)." : " can no longer mark mods.") + "\n";
                }
                default:
                    throw new AdminException(ModsUsage);
            }
        }
    }
}
