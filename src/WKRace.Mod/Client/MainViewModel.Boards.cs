using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Input;
using System.Windows.Media;
using WKRace.Protocol;

namespace WKRace.Client
{
    using Message = WKRace.Protocol.Message;

    // One row of a leaderboard page.
    internal sealed class BoardRowModel
    {
        public string Rank { get; set; }
        public string Name { get; set; }
        public string Value { get; set; }
        public string Detail { get; set; }
        public Brush Brush { get; set; }
        // The row has a public replay (one of a time board's 20 fastest).
        public bool CanPlay { get; set; }
        public ICommand PlayCommand { get; set; }
    }

    // One saved replay in the SAVED REPLAYS list.
    internal sealed class ReplayRowModel
    {
        public string When { get; set; }
        public string Seed { get; set; }
        public string Result { get; set; }
        // The kind of run, the version it was played on, its modifiers and its bans.
        public string Detail { get; set; }
        public ICommand PlayCommand { get; set; }
        public ICommand DeleteCommand { get; set; }
    }

    // The leaderboards window, the replays of its fastest runs, and the player's own saved replays.
    internal sealed partial class MainViewModel
    {
        private const int BoardPageRows = 12;
        private const int ReplayPageRows = 8;

        // ---- leaderboards -----------------------------------------------------------------------------------

        private BoardKind _board = BoardKind.SoloTime;
        private int _boardOffset;
        private int _boardTotal;
        private int _boardYourRank;
        private string _boardSearch = "";
        private string _boardSearchShown = "";
        private string _boardStatus = "";

        public ObservableCollection<BoardRowModel> BoardRows { get; } = new ObservableCollection<BoardRowModel>();
        public ICommand BoardRatingCommand { get; private set; }
        public ICommand BoardDuelCommand { get; private set; }
        public ICommand BoardSoloCommand { get; private set; }
        public ICommand BoardUpCommand { get; private set; }
        public ICommand BoardDownCommand { get; private set; }
        public ICommand BoardTopCommand { get; private set; }
        public ICommand BoardFindMeCommand { get; private set; }
        public ICommand BoardSearchCommand { get; private set; }
        public ICommand BoardClearCommand { get; private set; }

        public event Action OpenLeaderboards;
        public event Action OpenReplays;

        private void InitBoards()
        {
            Func<bool> online = () => _link == LinkState.Connected;
            LeaderboardsCommand = new RelayCommand(() => { OpenLeaderboards?.Invoke(); RequestBoard(0, false); });
            BoardRatingCommand = new RelayCommand(() => ShowBoard(BoardKind.Rating), online);
            BoardDuelCommand = new RelayCommand(() => ShowBoard(BoardKind.DuelTime), online);
            BoardSoloCommand = new RelayCommand(() => ShowBoard(BoardKind.SoloTime), online);
            BoardUpCommand = new RelayCommand(() => RequestBoard(Math.Max(0, _boardOffset - BoardPageRows), false), () => online() && _boardOffset > 0);
            BoardDownCommand = new RelayCommand(() => RequestBoard(_boardOffset + BoardPageRows, false), () => online() && _boardOffset + BoardPageRows < _boardTotal);
            BoardTopCommand = new RelayCommand(() => RequestBoard(0, false), () => online() && _boardOffset > 0);
            BoardFindMeCommand = new RelayCommand(() => { _boardSearchShown = ""; BoardSearch = ""; RequestBoard(0, true); }, () => online() && _boardYourRank > 0);
            BoardSearchCommand = new RelayCommand(() => { _boardSearchShown = (_boardSearch ?? "").Trim(); RequestBoard(0, false); }, online);
            BoardClearCommand = new RelayCommand(() => { BoardSearch = ""; _boardSearchShown = ""; RequestBoard(0, false); }, () => online() && _boardSearchShown.Length > 0);

            ReplaysCardCommand = new RelayCommand(() => { Show(ConsoleCard.Replays); LoadReplays(); OpenReplays?.Invoke(); }, () => _phase != ClientPhase.InMatch);
            SaveReplayCommand = new RelayCommand(SaveEndedRun, () => _endedRun != null && !_endedRunSaved);
            ReplaysUpCommand = new RelayCommand(() => ShowReplayPage(_replayOffset - ReplayPageRows), () => _replayOffset > 0);
            ReplaysDownCommand = new RelayCommand(() => ShowReplayPage(_replayOffset + ReplayPageRows), () => _replayOffset + ReplayPageRows < _replays.Count);
            ReplaysFolderCommand = new RelayCommand(OpenReplaysFolder);
        }

        public bool IsRatingBoard { get { return _board == BoardKind.Rating; } }
        public bool IsDuelTimeBoard { get { return _board == BoardKind.DuelTime; } }
        public bool IsSoloTimeBoard { get { return _board == BoardKind.SoloTime; } }
        public bool IsTimeBoard { get { return _board != BoardKind.Rating; } }
        public string BoardValueHeader { get { return _board == BoardKind.Rating ? "RATING" : "TIME"; } }
        public string BoardDetailHeader { get { return _board == BoardKind.Rating ? "RECORD" : "VERSION"; } }
        public string BoardSearch { get { return _boardSearch; } set { Set(ref _boardSearch, value ?? ""); } }
        public string BoardStatus { get { return _boardStatus; } private set { Set(ref _boardStatus, value); } }

        public string BoardTitle
        {
            get
            {
                string segment = Segments.Name(Segments.OfBoard(_board));
                if (segment.Length > 0) return segment + " SEGMENT: FASTEST TIME";
                return _board == BoardKind.Rating ? "RANKED 1V1: RATING" : _board == BoardKind.DuelTime ? "RANKED 1V1: FASTEST TIME" : "RANKED SOLO: FASTEST TIME";
            }
        }

        public string BoardRangeText
        {
            get
            {
                if (_boardTotal == 0) return _boardSearchShown.Length > 0 ? "NOBODY FOUND" : "NO ENTRIES YET";
                int last = Math.Min(_boardTotal, _boardOffset + BoardPageRows);
                return (_boardOffset + 1) + "-" + last + " OF " + _boardTotal + (_boardSearchShown.Length > 0 ? " FOUND" : "");
            }
        }

        public string BoardYouText
        {
            get { return _boardYourRank > 0 ? "YOU: #" + _boardYourRank : "YOU ARE NOT ON THIS BOARD"; }
        }

        private void ShowBoard(BoardKind kind)
        {
            _board = kind;
            _boardSearchShown = "";
            BoardSearch = "";
            RaiseBoard();
            RequestBoard(0, false);
        }

        private void RequestBoard(int offset, bool findMe)
        {
            if (_server == null || _link != LinkState.Connected)
            {
                BoardRows.Clear();
                _boardTotal = 0;
                BoardStatus = "Not connected to the match server.";
                RaiseBoard();
                return;
            }
            BoardStatus = "";
            _server.Send(new BoardRequest { Board = _board, Offset = Math.Max(0, offset), Count = BoardPageRows, Search = _boardSearchShown, FindMe = findMe });
        }

        private void OnBoardPage(BoardPage page)
        {
            if (page.Board != _board) return;   // an answer for a board the player has already left
            _boardOffset = page.Offset;
            _boardTotal = page.Total;
            _boardYourRank = page.YourRank;
            BoardRows.Clear();
            foreach (BoardRow row in page.Rows)
            {
                BoardRow captured = row;
                BoardKind kind = page.Board;
                BoardRows.Add(new BoardRowModel
                {
                    Rank = "#" + row.Rank,
                    Name = row.Name,
                    Value = kind == BoardKind.Rating ? row.Value.ToString(CultureInfo.InvariantCulture) : RaceFormat.Time(row.Value),
                    Detail = row.Detail,
                    Brush = row.You ? Theme.Good : row.Rank <= 3 ? Theme.Warn : Theme.Accent,
                    CanPlay = row.Replay,
                    PlayCommand = new RelayCommand(() => RequestReplay(kind, captured), () => captured.Replay && !_matchActive && _replayWanted == null),
                });
            }
            RaiseBoard();
        }

        private void RaiseBoard()
        {
            Raise(nameof(IsRatingBoard));
            Raise(nameof(IsDuelTimeBoard));
            Raise(nameof(IsSoloTimeBoard));
            Raise(nameof(IsTimeBoard));
            Raise(nameof(BoardValueHeader));
            Raise(nameof(BoardDetailHeader));
            Raise(nameof(BoardTitle));
            Raise(nameof(BoardRangeText));
            Raise(nameof(BoardYouText));
            CommandManager.InvalidateRequerySuggested();
        }

        // After a ranked run that reached the end: what it did to the player's row.
        private void OnBoardNote(BoardNote note)
        {
            SegmentInfo segment = Segments.Find(Segments.OfBoard(note.Board));
            string board = segment != null ? segment.Title + " segment" : note.Board == BoardKind.DuelTime ? "1v1" : "solo";
            string text;
            if (!string.IsNullOrEmpty(note.Problem)) text = note.Problem;
            else if (note.PersonalBest) text = RaceFormat.Time(note.TimeMs) + " is your best on the " + board + " board, rank #" + note.Rank + ".";
            else if (note.BestMs >= 0) text = "Your best on the " + board + " board stays " + RaceFormat.Time(note.BestMs) + " (rank #" + note.Rank + ").";
            else return;
            Log("Leaderboard: " + text);
            BoardNoteText = "Leaderboard: " + text;
        }

        // What the run that just ended did on its leaderboard; shown with the result.
        private string _boardNoteText = "";

        public bool HasBoardNote { get { return _boardNoteText.Length > 0; } }

        public string BoardNoteText
        {
            get { return _boardNoteText; }
            private set
            {
                if (!Set(ref _boardNoteText, value ?? "")) return;
                Raise(nameof(HasBoardNote));
            }
        }

        // ---- a replay from the leaderboard: download it, then run against it ----------------------------------

        // The replay being downloaded: which row, and the pieces so far.
        private BoardRow _replayWanted;
        private BoardKind _replayBoard;
        private byte[][] _replayPieces;
        private DateTime _replayAskedAt;

        private void RequestReplay(BoardKind kind, BoardRow row)
        {
            if (_server == null || _link != LinkState.Connected || _matchActive) return;
            _replayWanted = row;
            _replayBoard = kind;
            _replayPieces = null;
            _replayAskedAt = DateTime.UtcNow;
            BoardStatus = "Getting the replay of " + row.Name + "...";
            _server.Send(new ReplayRequest { Board = kind, Rank = row.Rank });
        }

        private void OnReplayChunk(ReplayChunk chunk)
        {
            BoardRow wanted = _replayWanted;
            if (wanted == null || chunk.Board != _replayBoard || chunk.Rank != wanted.Rank) return;
            if (chunk.Count == 0)
            {
                _replayWanted = null;
                BoardStatus = "That run has no replay any more. Only the 20 fastest keep one.";
                RequestBoard(_boardOffset, false);
                return;
            }
            if (_replayPieces == null || _replayPieces.Length != chunk.Count) _replayPieces = new byte[chunk.Count][];
            if (chunk.Index >= _replayPieces.Length) return;
            _replayPieces[chunk.Index] = chunk.Bytes;
            int have = _replayPieces.Count(p => p != null);
            if (have < _replayPieces.Length)
            {
                BoardStatus = "Getting the replay of " + wanted.Name + "... " + have * 100 / _replayPieces.Length + "%";
                return;
            }

            _replayWanted = null;
            RunRecording replay;
            try
            {
                replay = RunRecording.FromPacked(_replayPieces.SelectMany(p => p).ToArray());
            }
            catch (ProtocolException e)
            {
                BoardStatus = "The replay could not be read: " + e.Message;
                _replayPieces = null;
                return;
            }
            _replayPieces = null;
            BoardStatus = "Running against " + wanted.Name + " (" + RaceFormat.Time(replay.EndMs) + "), unranked, on seed " + replay.Seed + ".";
            RunAgainst(replay, wanted.Name + "'s " + RaceFormat.Time(replay.EndMs));
        }

        // A download that never finished (the connection dropped): let the player ask again.
        private void CheckReplayDownload()
        {
            if (_replayWanted == null || DateTime.UtcNow - _replayAskedAt < TimeSpan.FromSeconds(30)) return;
            _replayWanted = null;
            _replayPieces = null;
            BoardStatus = "The replay did not arrive. Try again.";
        }

        // An unranked solo run on the replay's seed, with its modifiers and bans, against it as the ghost.
        private void RunAgainst(RunRecording replay, string what)
        {
            if (_matchActive || _phase == ClientPhase.InMatch)
            {
                Log("Finish or end your current run first.");
                return;
            }
            Log("Unranked run against " + what + ": seed " + replay.Seed + ", " + RulesText.Describe(replay.Rules).ToLowerInvariant() + ".");
            _soloAgainst = replay;
            StartPractice(replay.Seed, new List<string>(replay.Bans ?? new List<string>()), replay.Rules, false, replay.Segment);
        }

        // ---- the player's own replays ---------------------------------------------------------------------------

        // The run that just ended, offered for saving (SAVE REPLAY), and whether it has been saved.
        private RunRecording _endedRun;
        private bool _endedRunSaved;
        // The replay an unranked run is played against instead of the player's own best on the seed (a leaderboard
        // replay or a saved one). It stays for restarts of that run.
        private RunRecording _soloAgainst;
        private List<SavedReplay> _replays = new List<SavedReplay>();
        private int _replayOffset;
        private string _replayStatus = "";

        public ObservableCollection<ReplayRowModel> ReplayRows { get; } = new ObservableCollection<ReplayRowModel>();
        public ICommand SaveReplayCommand { get; private set; }
        public ICommand ReplaysUpCommand { get; private set; }
        public ICommand ReplaysDownCommand { get; private set; }
        public ICommand ReplaysFolderCommand { get; private set; }

        public bool ShowSaveRow { get { return _endedRun != null && HasResult; } }
        public bool ShowSavePrompt { get { return ShowSaveRow && !_endedRunSaved; } }

        public string SavePromptText
        {
            get { return _endedRunSaved ? "Replay saved. Find it under UNRANKED, SAVED REPLAYS." : "Save this run's replay? It stays on this PC."; }
        }
        public string ReplayStatus { get { return _replayStatus; } private set { Set(ref _replayStatus, value); } }

        public string ReplayRangeText
        {
            get
            {
                if (_replays.Count == 0) return "NO SAVED REPLAYS";
                return (_replayOffset + 1) + "-" + Math.Min(_replays.Count, _replayOffset + ReplayPageRows) + " OF " + _replays.Count;
            }
        }

        // The run that ended can be saved, or (null) there is nothing to save.
        private void OfferReplay(RunRecording run)
        {
            _endedRun = run;
            _endedRunSaved = false;
            RaiseSaveReplay();
        }

        private void RaiseSaveReplay()
        {
            Raise(nameof(ShowSaveRow));
            Raise(nameof(ShowSavePrompt));
            Raise(nameof(SavePromptText));
            CommandManager.InvalidateRequerySuggested();
        }

        private void SaveEndedRun()
        {
            RunRecording run = _endedRun;
            if (run == null || _endedRunSaved) return;
            try
            {
                _replayStore.Save(run);
                _endedRunSaved = true;
                Log("Replay saved. It is under UNRANKED, SAVED REPLAYS, and stays on this PC.");
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Log("Could not save the replay: " + e.Message);
            }
            RaiseSaveReplay();
        }

        private void LoadReplays()
        {
            try { _replays = _replayStore.List(); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { _replays = new List<SavedReplay>(); ReplayStatus = "Could not read the replays folder: " + e.Message; }
            ShowReplayPage(0);
        }

        private void ShowReplayPage(int offset)
        {
            _replayOffset = Math.Max(0, Math.Min(offset, Math.Max(0, (_replays.Count - 1) / ReplayPageRows * ReplayPageRows)));
            ReplayRows.Clear();
            foreach (SavedReplay saved in _replays.Skip(_replayOffset).Take(ReplayPageRows))
            {
                SavedReplay captured = saved;
                RunRecording run = saved.Run;
                string bans = run.Bans != null && run.Bans.Count > 0 ? "  -  BANS: " + string.Join(", ", run.Bans.Select(RaceFormat.ItemLabel)).ToUpperInvariant() : "";
                ReplayRows.Add(new ReplayRowModel
                {
                    When = run.RecordedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                    Seed = "SEED " + run.Seed.ToString(CultureInfo.InvariantCulture),
                    Result = run.Finished ? RaceFormat.Time(run.EndMs) : "DIED, " + run.Levels + (run.Levels == 1 ? " LEVEL" : " LEVELS"),
                    Detail = (run.Segment != Segments.None ? Segments.Name(run.Segment) + " SEGMENT, " : "") + KindText(run.Kind) + (string.IsNullOrEmpty(run.Version) ? "" : " " + run.Version) + "  -  " + RulesText.Describe(run.Rules).ToUpperInvariant() + bans,
                    PlayCommand = new RelayCommand(() => RunAgainst(captured.Run, "your saved run"), () => !_matchActive && _phase != ClientPhase.InMatch),
                    DeleteCommand = new RelayCommand(() => DeleteReplay(captured)),
                });
            }
            Raise(nameof(ReplayRangeText));
            CommandManager.InvalidateRequerySuggested();
        }

        private static string KindText(RunKind kind)
        {
            switch (kind)
            {
                case RunKind.RankedSolo: return "RANKED SOLO";
                case RunKind.Ranked1v1: return "RANKED 1V1";
                case RunKind.Friendly: return "LOBBY MATCH";
                default: return "UNRANKED SOLO";
            }
        }

        private void DeleteReplay(SavedReplay saved)
        {
            try
            {
                _replayStore.Delete(saved.Path);
                _replays.Remove(saved);
                ReplayStatus = "Replay deleted.";
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                ReplayStatus = "Could not delete the replay: " + e.Message;
            }
            ShowReplayPage(_replayOffset);
        }

        private void OpenReplaysFolder()
        {
            try
            {
                Directory.CreateDirectory(_replayStore.Folder);
                UnityEngine.Application.OpenURL(new Uri(_replayStore.Folder).AbsoluteUri);
            }
            catch (Exception e)
            {
                ReplayStatus = "Could not open the folder: " + e.Message;
            }
        }
    }
}
