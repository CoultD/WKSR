using System;
using System.Collections.Generic;
using System.Linq;
using WKRace.Protocol;

namespace WKRace.Server
{
    // The leaderboards.
    //
    //   Rating     ranked 1v1: the players' ratings. Only players who are past their provisional matches and have
    //              played a ranked match since the boards exist are listed (older players never agreed to a public
    //              list; their first ranked match on this version puts them on it).
    //   DuelTime   ranked 1v1: each player's fastest finished run.
    //   SoloTime   ranked solo: each player's fastest finished run.
    //   segments   one board per segment (Segments.Board): each player's fastest ranked run of that segment.
    //
    // One row per player; a faster run replaces the row. The 20 fastest runs of a time board keep their replay (the
    // server records every ranked run itself, from the positions the game reports), which anyone can download and
    // run against. A replay is deleted when its run is no longer among the 20.
    public sealed partial class ServerCore
    {
        public const int ReplayPlaces = 20;
        // A packed replay larger than this is not kept (hours of play; the run keeps its row).
        private const int MaxReplayBytes = ReplayChunk.MaxBytes * ReplayChunk.MaxChunks / 2;
        // The server keeps about five positions a second of a run.
        private const int ReplayStateGapMs = 190;
        private const int BoardViewMs = 5000;
        private const int BoardRequestGapMs = 100;
        private const int ReplayRequestGapMs = 3000;

        // What a board shows for one player, in order.
        private sealed class BoardEntry
        {
            public Guid Id;
            public string Name;
            public int Value;
            public string Detail;
            public bool Replay;
        }

        // The time boards' rows, fastest first.
        private Dictionary<int, List<BoardRun>> _boardRuns;
        private HashSet<Guid> _boardPlayers;
        private readonly Dictionary<BoardKind, List<BoardEntry>> _boardViews = new Dictionary<BoardKind, List<BoardEntry>>();
        private readonly Dictionary<BoardKind, long> _boardViewAtMs = new Dictionary<BoardKind, long>();

        private static readonly Comparison<BoardRun> FastestFirst = (a, b) =>
        {
            int c = a.TimeMs.CompareTo(b.TimeMs);
            if (c == 0) c = a.SetUnixMs.CompareTo(b.SetUnixMs);
            return c != 0 ? c : a.PlayerId.CompareTo(b.PlayerId);
        };

        private void LoadBoards()
        {
            if (_boardRuns != null) return;
            _boardRuns = new Dictionary<int, List<BoardRun>>
            {
                { (int)BoardKind.DuelTime, new List<BoardRun>() },
                { (int)BoardKind.SoloTime, new List<BoardRun>() },
            };
            foreach (SegmentInfo segment in Segments.All) _boardRuns[(int)Segments.Board(segment.Id)] = new List<BoardRun>();
            foreach (BoardRun run in _store.Database.LoadBoardRuns())
            {
                List<BoardRun> list;
                if (_boardRuns.TryGetValue(run.Board, out list)) list.Add(run);
            }
            foreach (List<BoardRun> list in _boardRuns.Values) list.Sort(FastestFirst);
            _boardPlayers = _store.Database.LoadBoardPlayers();
        }

        // ---- recording a ranked run -----------------------------------------------------------------------

        private static RunRecorder NewReplayRecorder()
        {
            return new RunRecorder(ReplayStateGapMs);
        }

        // A ranked 1v1 was played to a result: both players are listed on the rating board from now on.
        private void NoteRankedPlayed(PlayerRecord record)
        {
            LoadBoards();
            if (!_boardPlayers.Add(record.Id)) return;
            _store.Database.AddBoardPlayer(record.Id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _boardViews.Remove(BoardKind.Rating);
        }

        // A ranked run reached the end. Puts its time on the board if it is the player's fastest, and tells the player
        // where they stand. recorder/watch: the server's own recording of the run and its check of the timer.
        private void SubmitBoardRun(BoardKind kind, Player player, int timeMs, int seed, MatchRules rules, IList<string> bans, string gameBuild,
            RunRecorder recorder, TimerWatch watch)
        {
            LoadBoards();
            PlayerRecord record = player.Record;
            var note = new BoardNote { Board = kind, TimeMs = timeMs };
            List<BoardRun> list = _boardRuns[(int)kind];
            BoardRun had = list.Find(r => r.PlayerId == record.Id);

            if (record.Banned) return;
            // The timer check is a note for the admin, not a verdict. The game itself slows or stops its timer while
            // the player still moves (slow-time buffs such as the Calming Buddy trinket's while the bag is open), and
            // the server cannot tell that from a tampered timer. The time stands; "board flagged" lists these runs
            // and "board remove" takes one off.
            if (_options.TimerCheck && watch != null && watch.Failed) FlagForReview(kind, record, timeMs, seed, watch);
            if (had != null && had.TimeMs <= timeMs)
            {
                note.Rank = VisibleRank(kind, record.Id);
                note.BestMs = had.TimeMs;
                player.Send(note);
                return;
            }

            if (had != null) list.Remove(had);
            string mod = BuildTag.ModVersion(gameBuild);
            var run = new BoardRun
            {
                Board = (int)kind,
                PlayerId = record.Id,
                TimeMs = timeMs,
                Seed = seed,
                Rules = (int)rules,
                Bans = bans != null ? string.Join(",", bans.Where(b => !string.IsNullOrEmpty(b))) : "",
                ModVersion = mod.Length > 0 ? mod : BuildTag.ShortVersion(ServerVersion),
                GameBuild = gameBuild ?? "",
                SetUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            int index = list.BinarySearch(run, Comparer<BoardRun>.Create(FastestFirst));
            if (index < 0) index = ~index;
            list.Insert(index, run);

            // Among the fastest: its replay is kept, and whoever it pushed out of them loses theirs.
            byte[] replay = null;
            if (index < ReplayPlaces && recorder != null)
            {
                try
                {
                    RunRecording recording = recorder.Finish(seed, timeMs, true, kind == BoardKind.DuelTime);
                    recording.Rules = rules;
                    recording.Bans = bans != null ? bans.Where(b => !string.IsNullOrEmpty(b)).Take(RunRecording.MaxBans).ToList() : new List<string>();
                    recording.Player = record.Name;
                    recording.Version = run.ModVersion;
                    recording.Kind = kind == BoardKind.DuelTime ? RunKind.Ranked1v1 : RunKind.RankedSolo;
                    recording.Segment = Segments.OfBoard(kind);
                    replay = recording.ToPacked();
                    if (replay.Length > MaxReplayBytes) replay = null;
                }
                catch (Exception e) when (e is ProtocolException || e is InvalidOperationException)
                {
                    replay = null;
                }
            }
            run.HasReplay = replay != null;
            _store.Database.SaveBoardRun(run, replay);
            if (list.Count > ReplayPlaces && list[ReplayPlaces].HasReplay)
            {
                list[ReplayPlaces].HasReplay = false;
                _store.Database.ClearBoardReplay((int)kind, list[ReplayPlaces].PlayerId);
            }
            _boardViews.Remove(kind);

            note.PersonalBest = true;
            note.BestMs = timeMs;
            note.Rank = VisibleRank(kind, record.Id);
            _log(record.Name + " is " + Standings.Ordinal(Math.Max(1, note.Rank)) + " on the " + BoardName(kind) + " board with " + RaceFormat.Time(timeMs)
                 + (had != null ? " (was " + RaceFormat.Time(had.TimeMs) + ")" : "") + (replay != null ? ", replay kept (" + replay.Length / 1024 + " KB)" : ""));
            player.Send(note);
        }

        // Runs the timer check flagged since the server started, newest last (the log has them too).
        private readonly List<string> _boardFlags = new List<string>();
        private const int MaxBoardFlags = 200;

        public int FlaggedRunCount { get { return _boardFlags.Count; } }

        private void FlagForReview(BoardKind kind, PlayerRecord record, int timeMs, int seed, TimerWatch watch)
        {
            string stopped = RaceFormat.Time(watch.MovedWhileStoppedMs, 1);
            if (_boardFlags.Count >= MaxBoardFlags) _boardFlags.RemoveAt(0);
            _boardFlags.Add(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) + " UTC  " + record.Name + "  "
                            + BoardName(kind) + " board  " + RaceFormat.Time(timeMs) + "  seed " + seed + "  timer nearly stopped for " + stopped + " of movement");
            _log("REVIEW " + record.Name + "'s time of " + RaceFormat.Time(timeMs) + " on the " + BoardName(kind) + " board (seed " + seed
                 + "): the game timer ran under a tenth of real time for " + stopped + " while the player moved. The time stands.");
        }

        // "15:28.75", "1:02:03.4" or plain seconds, as milliseconds; -1 if it is not a time.
        private static int ParseBoardTime(string text)
        {
            string[] parts = (text ?? "").Trim().Split(':');
            if (parts.Length > 3) return -1;
            double seconds;
            if (!double.TryParse(parts[parts.Length - 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out seconds) || seconds < 0) return -1;
            long total = 0;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                int whole;
                if (!int.TryParse(parts[i], out whole) || whole < 0) return -1;
                total = total * 60 + whole;
            }
            double ms = Math.Round((total * 60 + seconds) * 1000.0);
            return ms > 0 && ms < int.MaxValue ? (int)ms : -1;
        }

        private static string BoardName(BoardKind kind)
        {
            SegmentInfo segment = Segments.Find(Segments.OfBoard(kind));
            if (segment != null) return segment.Title + " segment";
            return kind == BoardKind.Rating ? "rating" : kind == BoardKind.DuelTime ? "1v1 time" : "solo time";
        }

        // ---- what a board shows -----------------------------------------------------------------------------

        // A board's rows in order. Banned players are left out. Built again every few seconds, or after a change.
        private List<BoardEntry> BoardView(BoardKind kind, long now)
        {
            LoadBoards();
            List<BoardEntry> view;
            long at;
            if (_boardViews.TryGetValue(kind, out view) && _boardViewAtMs.TryGetValue(kind, out at) && now - at < BoardViewMs && now >= at) return view;

            view = new List<BoardEntry>();
            if (kind == BoardKind.Rating)
            {
                foreach (PlayerRecord p in _store.All().Where(p => !p.Banned && !p.Provisional && _boardPlayers.Contains(p.Id))
                             .OrderByDescending(p => p.Rating).ThenByDescending(p => p.Wins).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
                    view.Add(new BoardEntry { Id = p.Id, Name = p.Name, Value = p.Rating, Detail = p.Wins + "W " + p.Losses + "L" });
            }
            else
            {
                foreach (BoardRun run in _boardRuns[(int)kind])
                {
                    PlayerRecord p = _store.Find(run.PlayerId);
                    if (p == null || p.Banned) continue;
                    view.Add(new BoardEntry { Id = p.Id, Name = p.Name, Value = run.TimeMs, Detail = run.ModVersion, Replay = run.HasReplay });
                }
            }
            _boardViews[kind] = view;
            _boardViewAtMs[kind] = now;
            return view;
        }

        // 1 for first place; 0 when the player has no row that is shown.
        private int VisibleRank(BoardKind kind, Guid player)
        {
            _boardViews.Remove(kind);
            return BoardView(kind, _now()).FindIndex(e => e.Id == player) + 1;
        }

        private void HandleBoardRequest(Player player, BoardRequest request)
        {
            long now = _now();
            if (now - player.LastBoardRequestMs < BoardRequestGapMs && now >= player.LastBoardRequestMs) return;
            player.LastBoardRequestMs = now;

            List<BoardEntry> view = BoardView(request.Board, now);
            int count = Math.Max(1, Math.Min((int)request.Count, BoardRequest.MaxRows));
            string search = (request.Search ?? "").Trim();
            int yours = view.FindIndex(e => e.Id == player.Id);

            // The rows to page through: the whole board, or the rows a search finds (each keeps its real rank).
            List<int> rows = null;
            if (search.Length > 0)
            {
                rows = new List<int>();
                for (int i = 0; i < view.Count; i++)
                    if (view[i].Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0) rows.Add(i);
            }
            int total = rows != null ? rows.Count : view.Count;
            int offset = request.FindMe && rows == null && yours >= 0 ? yours / count * count : Math.Max(0, request.Offset);
            if (offset >= total) offset = total == 0 ? 0 : (total - 1) / count * count;

            var page = new BoardPage { Board = request.Board, Offset = offset, Total = total, YourRank = yours + 1, Search = search };
            for (int i = offset; i < total && page.Rows.Count < count; i++)
            {
                int at = rows != null ? rows[i] : i;
                BoardEntry e = view[at];
                page.Rows.Add(new BoardRow { Rank = at + 1, Name = e.Name, Value = e.Value, Detail = e.Detail ?? "", Replay = e.Replay, You = at == yours });
            }
            player.Send(page);
        }

        // Sends the replay of the run at a rank, in pieces. A rank that has none gets one empty piece.
        private void HandleReplayRequest(Player player, ReplayRequest request)
        {
            long now = _now();
            if (now - player.LastReplayRequestMs < ReplayRequestGapMs && now >= player.LastReplayRequestMs) return;
            player.LastReplayRequestMs = now;

            byte[] replay = null;
            if (request.Board != BoardKind.Rating)
            {
                List<BoardEntry> view = BoardView(request.Board, now);
                if (request.Rank >= 1 && request.Rank <= view.Count && view[request.Rank - 1].Replay)
                    replay = _store.Database.LoadBoardReplay((int)request.Board, view[request.Rank - 1].Id);
            }
            if (replay == null || replay.Length == 0)
            {
                player.Send(new ReplayChunk { Board = request.Board, Rank = request.Rank });
                return;
            }
            int pieces = (replay.Length + ReplayChunk.MaxBytes - 1) / ReplayChunk.MaxBytes;
            for (int i = 0; i < pieces; i++)
            {
                int from = i * ReplayChunk.MaxBytes;
                var bytes = new byte[Math.Min(ReplayChunk.MaxBytes, replay.Length - from)];
                Buffer.BlockCopy(replay, from, bytes, 0, bytes.Length);
                player.Send(new ReplayChunk { Board = request.Board, Rank = request.Rank, Index = (ushort)i, Count = (ushort)pieces, Bytes = bytes });
            }
        }

        // ---- changes from outside: a deleted or merged player, an admin removing a run ---------------------------

        // A player's rows on the time boards, for the copy of their data (the admin "export" command).
        private List<object> BoardExport(Guid player)
        {
            LoadBoards();
            var rows = new List<object>();
            foreach (KeyValuePair<int, List<BoardRun>> pair in _boardRuns)
            {
                BoardRun run = pair.Value.Find(r => r.PlayerId == player);
                if (run == null) continue;
                rows.Add(new
                {
                    board = pair.Key == (int)BoardKind.DuelTime ? "ranked 1v1, fastest time" : pair.Key == (int)BoardKind.SoloTime ? "ranked solo, fastest time" : BoardName((BoardKind)pair.Key) + ", fastest time",
                    timeMs = run.TimeMs,
                    seed = run.Seed,
                    modifiers = ((MatchRules)run.Rules).ToString(),
                    bans = run.Bans,
                    modVersion = run.ModVersion,
                    gameBuild = run.GameBuild,
                    setUtc = DateTimeOffset.FromUnixTimeMilliseconds(run.SetUnixMs).UtcDateTime,
                    replayKept = run.HasReplay,
                });
            }
            return rows;
        }

        // The player has played ranked since the leaderboards exist (the rating board lists only those).
        private bool OnRatingBoardList(Guid player)
        {
            LoadBoards();
            return _boardPlayers.Contains(player);
        }

        // The player's rows are gone from every board (board < 0), or from one.
        private void ForgetBoardRuns(Guid player, int board = -1)
        {
            LoadBoards();
            foreach (KeyValuePair<int, List<BoardRun>> pair in _boardRuns)
                if (board < 0 || board == pair.Key) pair.Value.RemoveAll(r => r.PlayerId == player);
            if (board < 0) _boardPlayers.Remove(player);
            _store.Database.DeleteBoardRuns(board, player);
            _boardViews.Clear();
        }

        // One player's records pass to another (an admin merge): of two rows on a board the faster one stays.
        private void MoveBoardRuns(Guid from, Guid into)
        {
            LoadBoards();
            foreach (KeyValuePair<int, List<BoardRun>> pair in _boardRuns)
            {
                BoardRun theirs = pair.Value.Find(r => r.PlayerId == from);
                if (theirs == null) continue;
                BoardRun mine = pair.Value.Find(r => r.PlayerId == into);
                byte[] replay = theirs.HasReplay ? _store.Database.LoadBoardReplay(pair.Key, from) : null;
                pair.Value.Remove(theirs);
                _store.Database.DeleteBoardRuns(pair.Key, from);
                if (mine != null && mine.TimeMs <= theirs.TimeMs) continue;
                if (mine != null) pair.Value.Remove(mine);
                theirs.PlayerId = into;
                pair.Value.Add(theirs);
                pair.Value.Sort(FastestFirst);
                _store.Database.SaveBoardRun(theirs, replay);
            }
            if (_boardPlayers.Contains(from)) NoteRankedPlayedById(into);
            _boardPlayers.Remove(from);
            _boardViews.Clear();
        }

        private void NoteRankedPlayedById(Guid id)
        {
            if (!_boardPlayers.Add(id)) return;
            _store.Database.AddBoardPlayer(id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        // "board list <rating|1v1|solo> [n]" and "board remove <1v1|solo> <player>". A segment's board goes by the
        // segment's name ("silos").
        private string AdminBoard(string[] args)
        {
            const string usage = "Usage: board list <rating|1v1|solo|silos> [how many]\n       board remove <1v1|solo|silos> <player>\n       board set <1v1|solo|silos> <player> <time> [seed]\n       board flagged";
            if (args.Length > 1 && args[1] == "flagged")
            {
                if (_boardFlags.Count == 0) return "No run has been flagged by the timer check since the server started. (Older ones: search the log for REVIEW.)\n";
                return "Runs the timer check flagged since the server started (their times stand unless you remove them):\n  " + string.Join("\n  ", _boardFlags) + "\n";
            }
            if (args.Length < 3) throw new AdminException(usage);
            BoardKind kind;
            switch (args[2].ToLowerInvariant())
            {
                case "rating": kind = BoardKind.Rating; break;
                case "1v1": kind = BoardKind.DuelTime; break;
                case "solo": kind = BoardKind.SoloTime; break;
                default:
                {
                    SegmentInfo segment = Array.Find(Segments.All, s => string.Equals(s.Name, args[2], StringComparison.OrdinalIgnoreCase));
                    if (segment == null) throw new AdminException("Unknown board \"" + args[2] + "\": rating, 1v1, solo, or a segment's name (silos).");
                    kind = Segments.Board(segment.Id);
                    break;
                }
            }
            if (args[1] == "list")
            {
                int n = 20;
                if (args.Length > 3 && (!int.TryParse(args[3], out n) || n < 1)) throw new AdminException("How many rows? A whole number.");
                List<BoardEntry> view = BoardView(kind, _now());
                var text = new System.Text.StringBuilder(BoardName(kind) + " board: " + view.Count + (view.Count == 1 ? " row\n" : " rows\n"));
                for (int i = 0; i < view.Count && i < n; i++)
                    text.Append(string.Format("{0,4}  {1,-24}  {2,10}  {3}{4}\n", i + 1, view[i].Name,
                        kind == BoardKind.Rating ? view[i].Value.ToString() : RaceFormat.Time(view[i].Value), view[i].Detail, view[i].Replay ? "  (replay)" : ""));
                return text.ToString();
            }
            if (args[1] == "remove")
            {
                if (kind == BoardKind.Rating) throw new AdminException("The rating board has no runs to remove. Ban the player to take them off it.");
                PlayerRecord p = Resolve(args, 3);
                LoadBoards();
                BoardRun run = _boardRuns[(int)kind].Find(r => r.PlayerId == p.Id);
                if (run == null) throw new AdminException(p.Name + " has no run on the " + BoardName(kind) + " board.");
                ForgetBoardRuns(p.Id, (int)kind);
                _log("admin removed " + p.Name + "'s run of " + RaceFormat.Time(run.TimeMs) + " from the " + BoardName(kind) + " board");
                return "Removed " + p.Name + "'s run of " + RaceFormat.Time(run.TimeMs) + " from the " + BoardName(kind) + " board. Their next finished run can take its place.\n";
            }
            if (args[1] == "set")
            {
                // Puts a time on a board by hand (a run the server did not keep). It has no replay.
                if (kind == BoardKind.Rating) throw new AdminException("The rating board has no times to set.");
                if (args.Length < 5) throw new AdminException(usage);
                PlayerRecord p = Resolve(args, 3);
                int timeMs = ParseBoardTime(args[4]);
                if (timeMs < 0) throw new AdminException("\"" + args[4] + "\" is not a time. Write it like 15:28.75");
                int seed = 0;
                if (args.Length > 5 && !int.TryParse(args[5], out seed)) throw new AdminException("The seed is a whole number.");
                LoadBoards();
                List<BoardRun> list = _boardRuns[(int)kind];
                BoardRun had = list.Find(r => r.PlayerId == p.Id);
                if (had != null) list.Remove(had);
                var run = new BoardRun
                {
                    Board = (int)kind, PlayerId = p.Id, TimeMs = timeMs, Seed = seed, Rules = (int)_options.RankedRules, Bans = "",
                    ModVersion = BuildTag.ShortVersion(ServerVersion), GameBuild = "", SetUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                };
                int index = list.BinarySearch(run, Comparer<BoardRun>.Create(FastestFirst));
                if (index < 0) index = ~index;
                list.Insert(index, run);
                _store.Database.SaveBoardRun(run, null);
                if (list.Count > ReplayPlaces && list[ReplayPlaces].HasReplay)
                {
                    list[ReplayPlaces].HasReplay = false;
                    _store.Database.ClearBoardReplay((int)kind, list[ReplayPlaces].PlayerId);
                }
                _boardViews.Remove(kind);
                string was = had != null ? " (was " + RaceFormat.Time(had.TimeMs) + ")" : "";
                _log("admin set " + p.Name + "'s time on the " + BoardName(kind) + " board to " + RaceFormat.Time(timeMs) + was);
                return p.Name + " now has " + RaceFormat.Time(timeMs) + " on the " + BoardName(kind) + " board" + was + ", without a replay.\n";
            }
            throw new AdminException(usage);
        }
    }
}
