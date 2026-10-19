using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using WKRace.Protocol;

namespace WKRace.Mod
{
    // The race overlay. Drawn with TextMeshPro in the game's own UI font (Ticketing), on a canvas laid out as a
    // 1080-unit-high virtual screen. Each frame the layout code asks for labels and panels by position; they come from
    // pools, so nothing is allocated while racing unless the text itself changes.
    internal sealed class Hud
    {
        private const string GameFontName = "Ticketing SDF";

        // LiveSplit-style delta colours: bright when the gap grew in that direction since the previous split, pale otherwise.
        private static readonly Color AheadGaining = new Color(0.16f, 0.9f, 0.36f);
        private static readonly Color AheadLosing = new Color(0.5f, 0.86f, 0.6f);
        private static readonly Color BehindGaining = new Color(0.9f, 0.5f, 0.46f);
        private static readonly Color BehindLosing = new Color(1f, 0.22f, 0.16f);
        private static readonly Color Ahead = AheadGaining;
        private static readonly Color Behind = BehindLosing;
        private static readonly Color Neutral = new Color(0.92f, 0.92f, 0.92f);
        private static readonly Color Dim = new Color(1f, 1f, 1f, 0.5f);
        private static readonly Color Warning = new Color(1f, 0.8f, 0.3f);
        private static readonly Color CurrentRow = new Color(0.25f, 0.5f, 1f, 0.3f);

        private struct ToastEntry
        {
            public string Text;
            public Color Color;
            public float Until;
        }

        // A gap flashed on the right at a split (party races): to whom, and by how much (positive = behind them).
        public struct Gap
        {
            public string Name;
            public int Ms;
            // The gap of the racer behind us (shown as their deficit).
            public bool Behind;

            public Gap(string name, int ms, bool behind)
            {
                Name = name;
                Ms = ms;
                Behind = behind;
            }
        }

        private readonly List<ToastEntry> _toasts = new List<ToastEntry>();
        private Gap[] _gaps = new Gap[0];
        private float _gapsUntil;
        // Proximity voice (from WKSR): who is talking, and whether this player is.
        private VoiceActivity _voice;
        private int _place;
        private int _placeOf;
        private bool _placeUnknown;

        // The party position (1 = first) last worked out by the race controller.
        public int CurrentPlace { get { return _place; } }

        public void SetVoice(VoiceActivity voice)
        {
            _voice = voice;
        }

        private bool Talking(byte racer)
        {
            VoiceActivity v = _voice;
            return v != null && v.Racers.Contains(racer);
        }

        public void SetPlace(int place, int of, bool unknown)
        {
            _place = place;
            _placeOf = of;
            _placeUnknown = unknown;
        }

        public void Gaps(Gap[] gaps, float seconds)
        {
            _gaps = gaps ?? new Gap[0];
            _gapsUntil = Time.unscaledTime + seconds;
        }
        private string _flashTitle = "";
        private string _flashValue = "";
        private Color _flashColor;
        private float _flashUntil;

        // The overlay and the chat are two of these, each with its own canvas: the WKSR window (WKLib's canvas, order
        // 5000) is drawn over the overlay while it is open, so their texts never run through each other, and the chat
        // stays on top of both.
        private const int OrderOnTop = 30000, OrderUnderWindow = 4000, OrderChat = 30001;
        private readonly bool _chatOnly;
        private Canvas _canvasComponent;

        public Hud(bool chatOnly = false)
        {
            _chatOnly = chatOnly;
        }

        private GameObject _root;
        private RectTransform _canvas;
        private CanvasScaler _scaler;
        private TMP_FontAsset _font;
        private Material _material;
        private float _nextFontSearch;
        private readonly List<TextMeshProUGUI> _labels = new List<TextMeshProUGUI>();
        private readonly List<Image> _panels = new List<Image>();
        private int _labelsUsed;
        private int _panelsUsed;
        private Image _arrow;
        // Speaker symbols (voice): pooled like the panels. One sprite is the quiet speaker, the other has sound waves.
        private readonly List<Image> _icons = new List<Image>();
        private int _iconsUsed;
        private Sprite _speakerQuiet;
        private Sprite _speakerLoud;
        private static readonly Color SpeakerLit = new Color(1f, 0.85f, 0.4f, 1f);
        private static readonly Color SpeakerDim = new Color(1f, 1f, 1f, 0.3f);
        private float _vw;
        private float _vh;

        public void Toast(string text, float seconds = 5f)
        {
            Toast(text, Neutral, seconds);
        }

        public void Toast(string text, Color color, float seconds = 5f)
        {
            _toasts.Add(new ToastEntry { Text = text, Color = color, Until = Time.unscaledTime + seconds });
            if (_toasts.Count > 4) _toasts.RemoveAt(0);
        }

        // A short message in the middle of the screen, for the start and the finish.
        public void Flash(string title, string value, Color color)
        {
            _flashTitle = title;
            _flashValue = value;
            _flashColor = color;
            _flashUntil = Time.unscaledTime + 3f;
        }

        public static Color DeltaColor(int deltaMs)
        {
            return deltaMs <= 0 ? Ahead : Behind;
        }

        public void Destroy()
        {
            if (_root != null) UnityEngine.Object.Destroy(_root);
            if (_material != null) UnityEngine.Object.Destroy(_material);
            _root = null;
        }

        public void Draw(RaceSession s, ChatBox chat, ClientStatus client, bool linkUp, bool inMenu, string mismatch)
        {
            if (!EnsureCanvas()) return;
            Begin();
            try
            {
                if (_chatOnly)
                {
                    DrawChat(chat);
                    return;
                }
                bool fault = PlayerCheck.Fault != null && !inMenu;
                _topNotices = (mismatch != null ? 1 : 0) + (fault ? 1 : 0);
                _resultBottom = 0f;
                if (mismatch != null) DrawMismatch(mismatch);
                if (fault) DrawPlayerFault(PlayerCheck.Fault, mismatch != null ? 80 : 14);
                if (s == null)
                {
                    if (inMenu) DrawClientStatus(linkUp, client);
                }
                else
                {
                    DrawSession(s, linkUp, inMenu);
                }
                DrawVoice();
                DrawToasts();
            }
            finally
            {
                End();
            }
        }

        // ---- layout ---------------------------------------------------------

        // A version mismatch stops races from working, so it gets its own banner at the top, whatever else is shown.
        private void DrawMismatch(string text)
        {
            float center = _vw / 2f;
            Panel(center - 420, 14, 840, 58, new Color(0.35f, 0.02f, 0.02f, 0.85f));
            Label(center - 420, 18, 840, 26, ClientStatus.MismatchPrefix, 22, Behind, TextAlignmentOptions.Center);
            string detail = text.StartsWith(ClientStatus.MismatchPrefix, StringComparison.Ordinal) ? text.Substring(ClientStatus.MismatchPrefix.Length).TrimStart(':', ' ') : text;
            Label(center - 410, 44, 820, 22, detail, 14, Neutral, TextAlignmentOptions.Center);
        }

        // The character did not start (see PlayerCheck): the player cannot move, so this says why, on top like a mismatch.
        private void DrawPlayerFault(string text, float y)
        {
            float center = _vw / 2f;
            Panel(center - 420, y, 840, 58, new Color(0.35f, 0.02f, 0.02f, 0.85f));
            Label(center - 420, y + 4, 840, 26, "YOUR CHARACTER DID NOT START", 22, Behind, TextAlignmentOptions.Center);
            Label(center - 410, y + 30, 820, 22, text, 14, Neutral, TextAlignmentOptions.Center);
        }

        private void DrawClientStatus(bool linkUp, ClientStatus client)
        {
            string text;
            if (!linkUp) text = "WKSR is not running";
            else if (client == null) text = "WKSR is starting";
            else
            {
                if (!ReferenceEquals(_statusFor, client))
                {
                    _statusFor = client;
                    // Steam lobbies have no ratings.
                    bool steam = EmbeddedClient.Vm != null && EmbeddedClient.Vm.SteamMode;
                    _statusText = "WKSR: " + client.PlayerName + (steam ? "" : " (" + client.Rating + ")") + " - " + (string.IsNullOrEmpty(client.Info) ? client.Phase.ToString() : client.Info);
                }
                text = _statusText;
            }
            Label(16, 10, 700, 20, text, 14, new Color(1f, 1f, 1f, 0.55f), TextAlignmentOptions.Left);
        }

        private void DrawSession(RaceSession s, bool linkUp, bool inMenu)
        {
            float center = _vw / 2f;

            switch (s.Phase)
            {
                case RacePhase.Banning:
                    DrawBans(s, center);
                    break;

                case RacePhase.Starting:
                    Panel(center - 290, 56, 580, 70, new Color(0f, 0f, 0f, 0.5f));
                    Label(center - 290, 62, 580, 30, s.MatchLabel(true), 22, Neutral, TextAlignmentOptions.Center);
                    string hint;
                    if (s.Practice && s.AttemptBegun) hint = "Press " + RaceConfig.JoinKey.Value + " for a fresh run";
                    else if (!s.Practice && s.AttemptBegun) hint = "You left the match world. Restarts are off in matches: " + RaceConfig.ForfeitKey.Value + " twice forfeits";
                    else hint = inMenu ? "Press " + RaceConfig.JoinKey.Value + " to start your run" : "Press " + RaceConfig.JoinKey.Value + " twice to leave this run and join the match";
                    Label(center - 290, 94, 580, 24, hint, 15, Warning, TextAlignmentOptions.Center);
                    break;

                case RacePhase.Loading:
                    Label(center - 400, _vh * 0.4f, 800, 36, "LOADING MATCH WORLD...", 26, Neutral, TextAlignmentOptions.Center);
                    break;

                case RacePhase.Waiting:
                    if (s.ReadySeconds > 0) DrawReadyCheck(s, center);
                    else
                    {
                        Label(center - 500, _vh * 0.36f, 1000, 40, "GET READY", 28, Neutral, TextAlignmentOptions.Center);
                        Label(center - 500, _vh * 0.36f + 42, 1000, 24, "Your world is loaded. The run starts in a moment.", 15, new Color(1, 1, 1, 0.7f), TextAlignmentOptions.Center);
                    }
                    break;

                case RacePhase.Countdown:
                    if (s.Held) break;
                    int seconds = Mathf.Max(1, Mathf.CeilToInt(-s.RaceMs / 1000f));
                    Label(center - 150, _vh * 0.3f, 300, 120, seconds.ToString(), 100, Warning, TextAlignmentOptions.Center);
                    Label(center - 300, _vh * 0.3f + 116, 600, 30, "GET READY", 22, Neutral, TextAlignmentOptions.Center);
                    break;
            }

            if (s.Phase == RacePhase.Racing || s.Phase == RacePhase.Finished || s.Phase == RacePhase.Done)
            {
                if (RaceConfig.ShowSplits.Value || s.Phase == RacePhase.Done) DrawSplits(s);
                DrawFlash(center);
            }

            if (s.Phase == RacePhase.Done && !s.ResultClosed) DrawResult(s, center);
            if (s.Held && s.IsActive) DrawPause(s, center);
            DrawGhostMarkers(s);
            DrawBanners(s, linkUp, center);
            DrawFooter(s);
        }

        // The ready check: press F9 when ready; who is ready; the time left.
        private void DrawReadyCheck(RaceSession s, float center)
        {
            float y = _vh * 0.3f;
            MatchRoster roster = s.Roster;
            bool ready = s.ReadyPressed || s.ReadyConfirmed;
            int seconds = s.ReadyDeadline >= 0f ? Mathf.Max(0, Mathf.CeilToInt(s.ReadyDeadline - Time.unscaledTime)) : -1;
            int total = roster != null ? roster.Racers.Count : s.PlayerCount;
            int readyCount = 0, loaded = 0;
            if (roster != null)
            {
                foreach (RosterEntry e in roster.Racers)
                {
                    if (e.Status == RacerStatus.Ready) readyCount++;
                    if (e.Status == RacerStatus.Ready || e.Status == RacerStatus.Loaded) loaded++;
                }
            }

            int rows = roster != null ? Mathf.Min(roster.Racers.Count, 15) : 0;
            float height = 132f + rows * 21f;
            Panel(center - 330, y, 660, height, new Color(0f, 0f, 0f, 0.6f));
            Label(center - 330, y + 10, 660, 40, ready ? "READY" : "PRESS " + RaceConfig.JoinKey.Value + " WHEN READY", 32, ready ? Ahead : Warning, TextAlignmentOptions.Center);
            string line;
            if (seconds >= 0) line = readyCount + " of " + total + " ready.  " + seconds + " s left" + (s.Party ? " (then the match starts anyway)" : "");
            else line = "Waiting for every game to load (" + loaded + " of " + total + ")...";
            Label(center - 330, y + 54, 660, 24, line, 17, Neutral, TextAlignmentOptions.Center);
            Label(center - 330, y + 80, 660, 22, s.MatchLabel(false) + "  -  " + RulesText.Describe(s.Rules), 14, Dim, TextAlignmentOptions.Center);

            if (roster == null) return;
            float ry = y + 112f;
            for (int i = 0; i < rows; i++)
            {
                RosterEntry e = roster.Racers[i];
                string name = e.Racer == s.YourRacer ? e.Name + " (you)" : e.Name;
                string status;
                Color color;
                switch (e.Status)
                {
                    case RacerStatus.Ready: status = "READY"; color = Ahead; break;
                    case RacerStatus.Loaded: status = "LOADED"; color = Warning; break;
                    case RacerStatus.Loading: status = "LOADING"; color = Dim; break;
                    default: status = "LEFT"; color = Behind; break;
                }
                Label(center - 250, ry, 340, 21, Shorten(name, 28), 15, Neutral, TextAlignmentOptions.Left);
                Label(center + 90, ry, 160, 21, status, 15, color, TextAlignmentOptions.Right);
                ry += 21f;
            }
        }

        // The artifact ban phase: the pool with each item's number, who banned what, and whose turn it is.
        private void DrawBans(RaceSession s, float center)
        {
            const float width = 620f, rowHeight = 30f;
            float x = center - width / 2f;
            float y = _vh * 0.18f;
            BanState state = s.Bans;
            int count = state != null ? state.Pool.Count : 0;
            float height = 118f + Mathf.Max(1, count) * rowHeight;

            Panel(x, y, width, height, new Color(0f, 0f, 0f, 0.93f));
            Label(x, y + 12, width, 34, "ARTIFACT BANS", 28, Neutral, TextAlignmentOptions.Center);
            Label(x, y + 46, width, 22, s.MatchLabel(false), 15, Dim, TextAlignmentOptions.Center);
            float rows = y + 78f;

            if (count == 0)
            {
                Label(x, rows, width, rowHeight, state != null && state.Done ? "Nothing to ban." : "Waiting for both games to be ready...", 18, Neutral, TextAlignmentOptions.Center);
                return;
            }

            for (int i = 0; i < count; i++)
            {
                string item = state.Pool[i];
                int banned = state.Banned.IndexOf(item);
                float ry = rows + i * rowHeight;
                if (state.YourTurn && !s.BanPickSent && banned < 0 && i == s.BanCursor) Panel(x + 20, ry, width - 40, rowHeight - 2, CurrentRow);
                string key = i < 9 ? (i + 1).ToString() : " ";
                Label(x + 34, ry + 2, 30, rowHeight, key, 18, banned < 0 ? Warning : Dim, TextAlignmentOptions.Left);
                Label(x + 70, ry + 2, 300, rowHeight, ArtifactBans.Label(item), 19, banned < 0 ? Neutral : Dim, TextAlignmentOptions.Left);
                if (banned >= 0)
                    Label(x + 330, ry + 2, width - 360, rowHeight, "BANNED BY " + state.BannedBy[banned].ToUpperInvariant(), 15, Behind, TextAlignmentOptions.Right);
            }

            float footer = rows + count * rowHeight + 8f;
            int seconds = Mathf.Max(0, state.SecondsLeft - Mathf.FloorToInt(Time.unscaledTime - s.BansReceivedAt));
            string line;
            Color color = Neutral;
            if (state.Done) line = "Bans done. Loading the match...";
            else if (state.YourTurn && s.BanPickSent) line = "Sending your ban...";
            else if (state.YourTurn)
            {
                line = "YOUR BAN: press 1-" + Mathf.Min(count, 9) + " (or Up/Down + Enter). 0 = no ban.  " + seconds + " s";
                color = Warning;
            }
            else if (state.TurnName.Length > 0) line = "Waiting for " + state.TurnName + " to ban...  " + seconds + " s";
            else line = "Waiting for both games to be ready...";
            Label(x, footer, width, 24, line, 16, color, TextAlignmentOptions.Center);
        }

        // LiveSplit-like list on the right: recent splits with the gap to the opponent, the split being run now
        // (highlighted, with a live gap once the opponent's time for it has passed), what comes next, and the race clock.
        private void DrawSplits(RaceSession s)
        {
            const float width = 340f, rowHeight = 23f, nameWidth = 180f, deltaWidth = 80f, size = 16f;
            float x = _vw - width - 22f;
            float y = 130f;
            int raceMs = Math.Max(0, s.RaceMs);

            if (s.Party)
            {
                y = DrawPosition(s, x, y, width);
            }
            else
            {
                Color headerColor = s.OpponentFinished ? Behind : Dim;
                long headerKey = ((long)s.OpponentRating << 34) ^ ((long)(s.OpponentFinished ? s.OpponentFinishMs + 1 : 0) << 3)
                                 ^ (s.Practice ? 4 : 0) ^ (s.Ranked ? 2 : 0) ^ (s.PracticeBestMs >= 0 ? 1 : 0) ^ (s.RankedSolo ? 1L << 62 : 0) ^ ((long)s.Segment << 54);
                if (!_header.Is(s.OpponentName, s.PracticeBestText, headerKey))
                {
                    string header = s.Practice ? (s.RankedSolo ? (s.Segment != Segments.None ? s.SegmentLabel : "RANKED SOLO") : s.PracticeBestMs < 0 && s.Segment != Segments.None ? s.SegmentLabel + ", UNRANKED" : s.PracticeBestMs >= 0 ? (s.GhostIsMine ? "VS YOUR BEST " : "VS " + s.OpponentName.ToUpperInvariant() + " ") + s.PracticeBestText : "UNRANKED")
                        : "VS " + s.OpponentName.ToUpperInvariant() + "  " + s.OpponentRating + (s.Ranked ? "" : "  FRIENDLY");
                    if (s.OpponentFinished) header = s.OpponentName.ToUpperInvariant() + " FINISHED " + RaceFormat.Time(s.OpponentFinishMs);
                    _header.Set(s.OpponentName, s.PracticeBestText, headerKey, header);
                }
                Label(x, y, width, 20, _header.Text, 13, headerColor, TextAlignmentOptions.Left);
                y += 18f;

                // Where the opponent is. Splits only compare levels both racers went through, so on different routes
                // this line is what says how far along they are.
                SplitRecord latest = s.Compare != null ? s.Compare.Latest() : null;
                if (!s.OpponentFinished && latest != null && latest.Index > 0)
                {
                    Label(x, y, width, 18, Labels.At(latest.Level), 12, Dim, TextAlignmentOptions.Left);
                }
                y += 20f;
            }

            // Split 0 is the start of the attempt (0:00 for both), so the list begins at split 1.
            int done = s.MySplits.Count;
            // A party race keeps the list short (the previous split, the current one and the next) to cut the clutter.
            int back = s.Party ? 1 : RaceConfig.SplitRows.Value;
            int upcoming = s.Party ? 1 : RaceConfig.UpcomingSplits.Value;
            int first = Math.Max(1, done - back);
            int previousDelta = int.MinValue;
            if (first > 1)
            {
                SplitRecord before = s.FindMySplit(first - 1);
                SplitRecord beforeOpp = before != null ? s.FindOpponentAt(before.Key) : null;
                if (before != null && beforeOpp != null) previousDelta = before.TimeMs - beforeOpp.TimeMs;
            }

            int lastDelta = int.MinValue;
            for (int i = first; i < done; i++)
            {
                SplitRecord mine = s.MySplits[i];
                SplitRecord opp = s.FindOpponentAt(mine.Key);
                Label(x + 6, y, nameWidth, rowHeight, Labels.Short(mine.Level), size, Neutral, TextAlignmentOptions.Left);
                if (opp != null)
                {
                    int delta = mine.TimeMs - opp.TimeMs;
                    Label(x + nameWidth, y, deltaWidth, rowHeight, DeltaText(delta), size, GapColor(delta, previousDelta), TextAlignmentOptions.Right);
                    previousDelta = delta;
                    lastDelta = delta;
                }
                Label(x + nameWidth + deltaWidth, y, width - nameWidth - deltaWidth - 6, rowHeight, TimeText(mine.TimeMs), size, Neutral, TextAlignmentOptions.Right);
                y += rowHeight;
            }

            // The split being run right now ends when the next level is reached.
            bool running = s.Phase == RacePhase.Racing;
            int liveDelta = int.MinValue;
            if (running)
            {
                // Our own route decides what comes next; the opponent's time is shown where they went through it too.
                string name = GameBridge.LevelNameAhead(1);
                SplitRecord oppNext = string.IsNullOrEmpty(name) ? null : s.FindOpponentAt(s.NextKey(name));
                Panel(x, y, width, rowHeight, CurrentRow);
                Label(x + 6, y, nameWidth, rowHeight, Labels.Short(name), size, Color.white, TextAlignmentOptions.Left);
                if (oppNext != null && raceMs > oppNext.TimeMs && !s.Held)
                {
                    liveDelta = raceMs - oppNext.TimeMs;
                    Label(x + nameWidth, y, deltaWidth, rowHeight, DeltaText(liveDelta), size, BehindLosing, TextAlignmentOptions.Right);
                }
                string target = oppNext != null ? TimeText(oppNext.TimeMs) : "-";
                Label(x + nameWidth + deltaWidth, y, width - nameWidth - deltaWidth - 6, rowHeight, target, size, Dim, TextAlignmentOptions.Right);
                y += rowHeight;

                // What comes after, with the opponent's time where they have already been.
                for (int ahead = 1; ahead <= upcoming; ahead++)
                {
                    string later = GameBridge.LevelNameAhead(1 + ahead);
                    if (string.IsNullOrEmpty(later)) break;
                    SplitRecord oppLater = s.FindOpponentAt(s.NextKey(later));
                    Label(x + 6, y, nameWidth, rowHeight, Labels.Short(later), size, Dim, TextAlignmentOptions.Left);
                    Label(x + nameWidth + deltaWidth, y, width - nameWidth - deltaWidth - 6, rowHeight, oppLater != null ? TimeText(oppLater.TimeMs) : "-", size, Dim, TextAlignmentOptions.Right);
                    y += rowHeight;
                }
            }

            // Race clock, coloured by where you stand against the opponent.
            int clockMs;
            if (s.Phase == RacePhase.Done) clockMs = s.Result != null && s.Result.YourTimeMs >= 0 ? s.Result.YourTimeMs : s.EndRaceMs;
            else if (s.Phase == RacePhase.Finished && s.MyFinishMs >= 0) clockMs = s.MyFinishMs;
            else clockMs = raceMs;

            Color clock = Neutral;
            if (s.Held) clock = Warning;
            else if (liveDelta != int.MinValue) clock = BehindLosing;
            else if (lastDelta != int.MinValue) clock = lastDelta <= 0 ? AheadGaining : BehindLosing;
            y += 4f;
            Label(x, y, width - 6, 44, ClockText(clockMs), 38, clock, TextAlignmentOptions.Right, true);
            if (s.Attempt > 1)
            {
                if (!_attempt.Is(null, null, s.Attempt)) _attempt.Set(null, null, s.Attempt, "ATTEMPT " + s.Attempt);
                Label(x + 6, y + 8, 140, 20, _attempt.Text, 13, Dim, TextAlignmentOptions.Left);
            }
        }

        // Party races: a big Mario Kart style position ("?" while on a shortcut), the gaps flashed at the last split,
        // and who the split list compares with. Returns where the split list starts.
        private float DrawPosition(RaceSession s, float x, float y, float width)
        {
            if (!_placeText.Is(null, null, _placeUnknown ? -1 : _place))
                _placeText.Set(null, null, _placeUnknown ? -1 : _place, _placeUnknown ? "?" : _place > 0 ? Standings.Ordinal(_place).ToUpperInvariant() : "-");
            if (!_partyText.Is(null, null, _placeOf)) _partyText.Set(null, null, _placeOf, "PARTY  " + _placeOf + " PLAYERS");
            Color color = _placeUnknown ? Warning : _place == 1 ? Ahead : Neutral;
            Label(x, y - 18, width - 6, 64, _placeText.Text, 58, color, TextAlignmentOptions.Right);
            Label(x, y + 2, width - 130, 20, _partyText.Text, 13, Dim, TextAlignmentOptions.Left);
            if (_placeUnknown) Label(x, y + 22, width - 130, 18, "SHORTCUT: PLACE UNKNOWN", 12, Warning, TextAlignmentOptions.Left);
            y += 50f;

            float remaining = _gapsUntil - Time.unscaledTime;
            if (remaining > 0f)
            {
                float alpha = Mathf.Clamp01(remaining / 0.6f);
                foreach (Gap g in _gaps)
                {
                    Color c = g.Behind ? Ahead : (g.Ms <= 0 ? Ahead : Behind);
                    c.a = alpha;
                    string text = g.Behind ? Shorten(g.Name, 20) + " BEHIND BY " + RaceFormat.Time(Math.Max(0, g.Ms), 1)
                        : RaceFormat.Delta(g.Ms, 1) + "  TO " + Shorten(g.Name, 22).ToUpperInvariant();
                    Label(x, y, width - 6, 24, text, 18, c, TextAlignmentOptions.Right);
                    y += 24f;
                }
            }
            if (s.Compare != null)
            {
                if (!_versus.Is(s.Compare.Name, null, 0)) _versus.Set(s.Compare.Name, null, 0, "SPLITS VS " + Shorten(s.Compare.Name, 20).ToUpperInvariant());
                Label(x, y, width, 18, _versus.Text, 12, Dim, TextAlignmentOptions.Left);
            }
            return y + 20f;
        }

        private static Color GapColor(int delta, int previousDelta)
        {
            bool gaining = previousDelta == int.MinValue || delta <= previousDelta;
            if (delta <= 0) return gaining ? AheadGaining : AheadLosing;
            return gaining ? BehindGaining : BehindLosing;
        }

        // "8:30.46" with the hundredths smaller, like LiveSplit.
        private static string ClockText(int ms)
        {
            if (ms < 0) ms = 0;
            if (ms / 10 == _clockFor) return _clockText;
            _clockFor = ms / 10;
            int total = ms / 1000, hundredths = (ms % 1000) / 10;
            int hours = total / 3600, minutes = (total / 60) % 60, seconds = total % 60;
            int n = 0;
            if (hours > 0)
            {
                n = Digits(ClockChars, n, hours, 1);
                ClockChars[n++] = ':';
                n = Digits(ClockChars, n, minutes, 2);
            }
            else n = Digits(ClockChars, n, minutes, 1);
            ClockChars[n++] = ':';
            n = Digits(ClockChars, n, seconds, 2);
            const string open = "<size=65%>.", close = "</size>";
            for (int i = 0; i < open.Length; i++) ClockChars[n++] = open[i];
            n = Digits(ClockChars, n, hundredths, 2);
            for (int i = 0; i < close.Length; i++) ClockChars[n++] = close[i];
            return _clockText = new string(ClockChars, 0, n);
        }

        private static readonly char[] ClockChars = new char[48];
        private static int _clockFor = -1;
        private static string _clockText = "";

        private static int Digits(char[] buffer, int at, int value, int minDigits)
        {
            int digits = 1;
            for (int v = value; v >= 10; v /= 10) digits++;
            if (digits < minDigits) digits = minDigits;
            for (int i = digits - 1; i >= 0; i--)
            {
                buffer[at + i] = (char)('0' + value % 10);
                value /= 10;
            }
            return at + digits;
        }

        // Split times ("8:30") and gaps ("+1.2") as the split list shows them. The list is redrawn every frame, so the
        // texts are kept: a finished split's never change, and a running gap changes ten times a second.
        private static readonly Dictionary<long, string> TimeTexts = new Dictionary<long, string>();
        private static readonly Dictionary<long, string> DeltaTexts = new Dictionary<long, string>();

        private static string TimeText(int ms)
        {
            long key = Math.Max(0, ms) / 1000;
            string text;
            if (TimeTexts.TryGetValue(key, out text)) return text;
            if (TimeTexts.Count > 2000) TimeTexts.Clear();
            return TimeTexts[key] = RaceFormat.Time(ms, 0);
        }

        private static string DeltaText(int ms)
        {
            long tenths = Math.Abs((long)ms) / 100;
            long key = ms < 0 ? -tenths - 1 : tenths;
            string text;
            if (DeltaTexts.TryGetValue(key, out text)) return text;
            if (DeltaTexts.Count > 4000) DeltaTexts.Clear();
            return DeltaTexts[key] = RaceFormat.Delta(ms, 1);
        }

        // A text that is drawn every frame but only changes with its inputs.
        private struct Memo
        {
            private object _a, _b;
            private long _n;
            private bool _set;
            public string Text;

            public bool Is(object a, object b, long n)
            {
                return _set && ReferenceEquals(_a, a) && ReferenceEquals(_b, b) && _n == n;
            }

            public void Set(object a, object b, long n, string text)
            {
                _a = a; _b = b; _n = n; _set = true; Text = text;
            }
        }

        private Memo _header, _attempt, _placeText, _partyText, _versus;
        private ClientStatus _statusFor;
        private string _statusText = "";

        private void DrawFlash(float center)
        {
            float remaining = _flashUntil - Time.unscaledTime;
            if (remaining <= 0f) return;
            float alpha = Mathf.Clamp01(remaining / 0.6f);
            Color title = Neutral;
            title.a = alpha;
            Color value = _flashColor;
            value.a = alpha;
            Label(center - 400, _vh * 0.6f, 800, 26, _flashTitle, 18, title, TextAlignmentOptions.Center);
            Label(center - 400, _vh * 0.6f + 26, 800, 50, _flashValue, 40, value, TextAlignmentOptions.Center);
        }

        private void DrawResult(RaceSession s, float center)
        {
            MatchResult r = s.Result;
            if (r == null) return;
            if (s.Party && r.Outcome != MatchOutcome.Aborted)
            {
                DrawPartyResult(s, r, center);
                return;
            }
            string title;
            Color color;
            switch (r.Outcome)
            {
                case MatchOutcome.Win: title = !s.Practice ? "VICTORY" : r.Reason.StartsWith("New best", StringComparison.Ordinal) ? "NEW BEST" : "FINISHED"; color = Ahead; break;
                case MatchOutcome.Loss: title = !s.Practice ? "DEFEAT" : r.End == MatchEnd.Death ? "DIED" : "NOT COUNTED"; color = Behind; break;
                default: title = s.Practice ? "RUN ENDED" : "MATCH ABORTED"; color = Warning; break;
            }

            // At the top, narrow and dark: the game's own end screen is in the middle and must stay readable.
            float x = center - 220, y = ResultTop, w = 440;
            List<string> reason = WrapChat(r.Reason ?? "", 50);
            string you = r.YourTimeMs >= 0 ? RaceFormat.Time(r.YourTimeMs) : "-";
            string opp = r.OpponentTimeMs >= 0 ? RaceFormat.Time(r.OpponentTimeMs) : "-";
            bool rival = !s.Practice || r.OpponentTimeMs >= 0;
            string theirs = !s.Practice ? Shorten(s.OpponentName, 30) + " " + opp : "Best " + opp;

            string note;
            Color noteColor = Dim;
            if (s.Practice)
            {
                note = !s.RankedSolo ? "Unranked run: nothing is uploaded" : s.Segment != Segments.None ? "Segment run: your best time counts for its leaderboard" : "Ranked solo: your best finished time counts";
            }
            else if (r.Outcome == MatchOutcome.Aborted) note = null;
            else if (!s.Ranked) note = "Friendly match: no rating change";
            else
            {
                int change = r.NewRating - r.OldRating;
                note = "Rating " + r.OldRating + " > " + r.NewRating + "  (" + (change >= 0 ? "+" : "") + change + ")";
                noteColor = change >= 0 ? Ahead : Behind;
            }
            int closesIn = Mathf.Max(0, Mathf.CeilToInt(s.DoneUntil - Time.unscaledTime));
            string next = s.Practice ? "Press " + RaceConfig.JoinKey.Value + " (or your restart bind) for a fresh run." : "For another match: WKSR, in the pause menu or the main menu.";

            float height = 62 + reason.Count * 20 + 8 + (rival ? 48 : 24) + (note != null ? 26 : 0) + 6 + 36 + 8;
            Panel(x, y, w, height, new Color(0f, 0f, 0f, 0.88f));
            _resultBottom = y + height;
            Label(x, y + 10, w, 48, title, 42, color, TextAlignmentOptions.Center);
            float ly = y + 62;
            foreach (string row in reason)
            {
                Label(x + 12, ly, w - 24, 20, row, 15, new Color(1, 1, 1, 0.8f), TextAlignmentOptions.Center);
                ly += 20;
            }
            ly += 8;
            Label(x, ly, w, 24, "You " + you, 18, Neutral, TextAlignmentOptions.Center);
            ly += 24;
            if (rival)
            {
                Label(x, ly, w, 24, theirs, 18, Neutral, TextAlignmentOptions.Center);
                ly += 24;
            }
            if (note != null)
            {
                Label(x, ly + 4, w, 22, note, 16, noteColor, TextAlignmentOptions.Center);
                ly += 26;
            }
            ly += 6;
            Label(x, ly, w, 18, next, 13, new Color(1, 1, 1, 0.55f), TextAlignmentOptions.Center);
            Label(x, ly + 18, w, 18, "This closes in " + closesIn + " s.", 13, new Color(1, 1, 1, 0.55f), TextAlignmentOptions.Center);
        }

        // Where the result panel starts: at the top of the screen, under the red notices when one is shown.
        private float ResultTop
        {
            get { return _topNotices > 0 ? 14 + _topNotices * 66 : 14; }
        }

        private int _topNotices;
        // The bottom edge of the result panel while it is shown (toasts go under it), else 0.
        private float _resultBottom;

        private void DrawPartyResult(RaceSession s, MatchResult r, float center)
        {
            int rows = Mathf.Min(r.Standings.Count, 15);
            float x = center - 320, y = ResultTop, w = 640;
            Panel(x, y, w, 150 + rows * 23, new Color(0f, 0f, 0f, 0.88f));
            _resultBottom = y + 150 + rows * 23;
            string title = r.Place == 1 ? "1ST PLACE!" : Standings.Ordinal(r.Place).ToUpperInvariant() + " OF " + r.Standings.Count;
            Label(x, y + 10, w, 48, title, 42, r.Place == 1 ? Ahead : r.Place <= 3 ? Warning : Neutral, TextAlignmentOptions.Center);
            Label(x + 16, y + 62, w - 32, 22, r.Reason, 14, new Color(1, 1, 1, 0.8f), TextAlignmentOptions.Center);
            float ry = y + 94;
            for (int i = 0; i < rows; i++)
            {
                Standing st = r.Standings[i];
                bool me = st.Racer == s.YourRacer;
                if (me) Panel(x + 20, ry, w - 40, 22, CurrentRow);
                string detail;
                switch (st.Status)
                {
                    case RacerStatus.Finished: detail = RaceFormat.Time(st.TimeMs, 1); break;
                    case RacerStatus.Out: detail = (string.IsNullOrEmpty(st.Note) ? "OUT" : Shorten(st.Note, 26)) + "  " + st.Levels + " LV"; break;
                    case RacerStatus.Left: detail = "LEFT  " + st.Levels + " LV"; break;
                    default: detail = "DNF  " + st.Levels + " LV"; break;
                }
                Label(x + 30, ry, 60, 22, Standings.Ordinal(st.Place), 16, st.Place == 1 ? Ahead : Neutral, TextAlignmentOptions.Left);
                Label(x + 90, ry, 300, 22, Shorten(st.Name, 26), 16, Neutral, TextAlignmentOptions.Left);
                Label(x + 330, ry, w - 360, 22, detail, 16, Dim, TextAlignmentOptions.Right);
                ry += 23;
            }
            int closesIn = Mathf.Max(0, Mathf.CeilToInt(s.DoneUntil - Time.unscaledTime));
            Label(x, ry + 10, w, 20, "Party match: no rating change. Back to the lobby in WKSR for a rematch. This closes in " + closesIn + " s.", 13, new Color(1, 1, 1, 0.55f), TextAlignmentOptions.Center);
        }

        private void DrawPause(RaceSession s, float center)
        {
            float x = center - 290, y = _vh * 0.32f, w = 580;
            Panel(x, y, w, 128, new Color(0f, 0f, 0f, 0.55f));
            if (s.ResumePending)
            {
                int seconds = Mathf.Max(1, Mathf.CeilToInt(s.MsUntilResume / 1000f));
                Label(x, y + 10, w, 40, "RESUMING", 32, Ahead, TextAlignmentOptions.Center);
                Label(x, y + 52, w, 66, seconds.ToString(), 58, Warning, TextAlignmentOptions.Center);
                return;
            }
            Label(x, y + 10, w, 40, "MATCH PAUSED", 32, Warning, TextAlignmentOptions.Center);
            Label(x + 16, y + 56, w - 32, 26, s.HoldReason, 18, Neutral, TextAlignmentOptions.Center);
            int left = Mathf.Max(0, Mathf.CeilToInt(s.HoldDeadline - Time.unscaledTime));
            string footer = left > 0
                ? "The match clock is stopped. Not back within " + left + " s counts as a forfeit."
                : "Time is up. Waiting for the server's decision...";
            Label(x + 16, y + 92, w - 32, 22, footer, 14, new Color(1, 1, 1, 0.75f), TextAlignmentOptions.Center);
        }

        // A name label over every ghost. In a 1v1 an arrow on the screen's edge points to an opponent out of view;
        // a party has too many racers for arrows, and above 8 players the labels shrink.
        private void DrawGhostMarkers(RaceSession s)
        {
            if (s.Phase != RacePhase.Racing || s.Held) return;
            float size = s.PlayerCount > 8 ? 14f * 0.6f : 14f;
            foreach (Rival rival in s.Rivals.Values)
            {
                if (rival.Ghost.Visible) DrawGhostMarker(rival, !s.Party, size, Talking(rival.Racer));
            }
        }

        private void DrawGhostMarker(Rival rival, bool arrow, float size, bool talking)
        {
            Ghost ghost = rival.Ghost;
            Camera cam = Viewer();
            if (cam == null) return;

            Vector3 world = ghost.EyeWorld + Vector3.up * 0.45f;
            Vector3 sp = cam.WorldToScreenPoint(world);
            float distance = Vector3.Distance(cam.transform.position, ghost.EyeWorld);
            float dy = ghost.EyeWorld.y - cam.transform.position.y;
            int metres = Mathf.RoundToInt(distance), rise = Mathf.RoundToInt(dy);
            if (rival.MarkerText == null || rival.MarkerMetres != metres || rival.MarkerRise != rise || !ReferenceEquals(rival.MarkerName, rival.Name))
            {
                rival.MarkerMetres = metres;
                rival.MarkerRise = rise;
                rival.MarkerName = rival.Name;
                rival.MarkerText = rival.Name + "  " + metres + " m" + (rise != 0 ? ", " + Mathf.Abs(rise) + " m " + (rise > 0 ? "above" : "below") : "");
            }
            string label = rival.MarkerText;
            Color tint = talking ? new Color(1f, 0.85f, 0.4f, 1f) : new Color(0.7f, 0.9f, 1f, 0.95f);

            float k = _vh / Screen.height;
            float gx = sp.x * k;
            float gy = (Screen.height - sp.y) * k;
            bool onScreen = sp.z > 0f && gx > 30 && gx < _vw - 30 && gy > 30 && gy < _vh - 30;
            if (onScreen)
            {
                Label(gx - 200, gy - 22, 400, 20, label, size, tint, TextAlignmentOptions.Center);
                // Whoever is talking has the speaker symbol over their head.
                if (talking)
                {
                    float icon = size >= 14f ? 26f : 18f;
                    Speaker(gx - icon / 2f, gy - 24f - icon, icon, true);
                }
                return;
            }
            if (!arrow) return;

            // Off screen: an arrow on the edge pointing toward the opponent.
            Vector2 dir = new Vector2(gx - _vw / 2f, gy - _vh / 2f);
            if (sp.z < 0f) dir = -dir;
            if (dir.sqrMagnitude < 1f) dir = Vector2.down;
            dir.Normalize();
            Vector2 pos = new Vector2(_vw / 2f + dir.x * (_vw / 2f - 50f), _vh / 2f + dir.y * (Mathf.Min(_vw, _vh) / 2f - 50f));
            Arrow(pos, dir, 28f, tint);
            Label(pos.x - 150, pos.y + 18, 300, 20, label, 13, tint, TextAlignmentOptions.Center);
        }

        private void DrawBanners(RaceSession s, bool linkUp, float center)
        {
            float y = _vh - 86f;
            if (s.Cheated && s.IsActive)
            {
                Label(center - 600, y, 1200, 22, "CHEATS DETECTED - THIS RUN WILL BE DISQUALIFIED", 16, Behind, TextAlignmentOptions.Center);
                y -= 24f;
            }
            if (!linkUp && s.IsActive && !s.Held)
            {
                Label(center - 600, y, 1200, 22, "LOST CONNECTION TO WKSR", 15, Behind, TextAlignmentOptions.Center);
            }
        }

        private void DrawFooter(RaceSession s)
        {
            // (The open chat has its input line there.)
            if (s.Phase == RacePhase.Done || ChatBox.AnyOpen) return;
            string first = s.Phase == RacePhase.Waiting && s.ReadySeconds > 0 ? " ready   " : s.Practice ? " restart   " : " start   ";
            string keys = RaceConfig.JoinKey.Value + first + RaceConfig.ForfeitKey.Value + " x2 forfeit   " +
                          RaceConfig.SplitsKey.Value + " splits   " + RaceConfig.GhostKey.Value + " ghost   " + RaceConfig.ChatKey.Value + " chat";
            Label(46, _vh - 26, 900, 18, keys, 12, new Color(1, 1, 1, 0.35f), TextAlignmentOptions.Left);
        }

        // Voice chat: a speaker symbol in the bottom left corner while voice is on. It lights up (with sound waves)
        // while this player's voice is being sent.
        private void DrawVoice()
        {
            VoiceActivity v = _voice;
            if (v == null || !v.On) return;
            Speaker(14f, _vh - 36f, 26f, v.Me);
        }

        private void Speaker(float x, float y, float size, bool lit)
        {
            if (_speakerQuiet == null)
            {
                _speakerQuiet = MakeSpeakerSprite(false);
                _speakerLoud = MakeSpeakerSprite(true);
            }
            Image icon;
            if (_iconsUsed < _icons.Count)
            {
                icon = _icons[_iconsUsed];
            }
            else
            {
                var go = new GameObject("Speaker", typeof(RectTransform));
                go.transform.SetParent(_canvas, false);
                icon = go.AddComponent<Image>();
                icon.raycastTarget = false;
                Place(icon.rectTransform);
                _icons.Add(icon);
            }
            _iconsUsed++;
            icon.rectTransform.anchoredPosition = new Vector2(x, -y);
            icon.rectTransform.sizeDelta = new Vector2(size, size);
            icon.sprite = lit ? _speakerLoud : _speakerQuiet;
            icon.color = lit ? SpeakerLit : SpeakerDim;
            if (!icon.enabled) icon.enabled = true;
        }

        private static readonly Color ChatGold = new Color(0.92f, 0.63f, 0.26f);
        private static readonly Color ChatRed = new Color(0.95f, 0.3f, 0.25f);
        private Image _chatBlocker;

        public float VirtualWidth { get { return _vw; } }
        public float VirtualHeight { get { return _vh; } }

        // The chat. Closed: the last line of the selected room, bottom left. Open: the panel over the lower part of
        // the screen (ChatBox says what it is). A panel drawn earlier lies over one drawn later, so the dark
        // background comes last.
        private void DrawChat(ChatBox chat)
        {
            if (chat == null) return;
            chat.Hits.Clear();
            WKRace.Client.MainViewModel vm = EmbeddedClient.Vm;
            float top = _vh * 0.56f;
            BlockClicks(chat.Open, top, _vh - top);
            if (vm == null) return;
            WKRace.Client.ChatRoomModel room = vm.Room(vm.SelectedRoom);
            if (room != null && !room.Joined) room = null;

            if (!chat.Open)
            {
                if (room == null || room.Lines.Count == 0 || !vm.ChatConnected) return;
                WKRace.Client.ChatRoomLine last = room.Lines[room.Lines.Count - 1];
                Label(20, _vh - 60f, 1100, 21, Shorten(last.Short(), 120), 15, last.System ? Warning : Neutral, TextAlignmentOptions.Left);
                return;
            }

            // The tabs: the joined rooms, each with an x to leave it, then the + for the list of rooms.
            const float tabHeight = 28f, lineHeight = 22f;
            float x = 8f, y = top + 6f;
            foreach (WKRace.Client.ChatRoomModel tab in vm.Rooms)
            {
                if (!tab.Joined) continue;
                bool selected = room != null && tab.Id == room.Id;
                string name = tab.Name + (tab.Unread > 0 ? " (" + tab.Unread + ")" : "");
                float width = 22f + name.Length * 9.5f + 30f;
                byte id = tab.Id;
                Panel(x, y, width, tabHeight, selected ? new Color(0.3f, 0.2f, 0.08f, 0.98f) : new Color(0.13f, 0.13f, 0.13f, 0.98f));
                Label(x + 11, y + 4, width - 40, 20, name, 15, selected ? ChatGold : Neutral, TextAlignmentOptions.Left);
                Label(x + width - 27, y + 4, 22, 20, "x", 15, ChatRed, TextAlignmentOptions.Center);
                chat.Hits.Add(new ChatBox.Hit { Area = new Rect(x, y, width - 30, tabHeight), Do = () => { vm.SelectRoom(id); chat.Scroll = 0; chat.ShowRooms = false; } });
                chat.Hits.Add(new ChatBox.Hit { Area = new Rect(x + width - 30, y, 30, tabHeight), Do = () => { vm.LeaveRoom(id); chat.Scroll = 0; } });
                x += width + 5f;
            }
            Panel(x, y, 32, tabHeight, chat.ShowRooms ? new Color(0.3f, 0.2f, 0.08f, 0.98f) : new Color(0.13f, 0.13f, 0.13f, 0.98f));
            Label(x, y + 3, 32, 20, "+", 18, ChatGold, TextAlignmentOptions.Center);
            chat.Hits.Add(new ChatBox.Hit { Area = new Rect(x, y, 32, tabHeight), Do = () => chat.ShowRooms = !chat.ShowRooms });
            Label(_vw - 620, y + 4, 604, 20, "ENTER send    " + RaceConfig.ChatKey.Value + " close", 13, Dim, TextAlignmentOptions.Right);

            // The input line, at the bottom.
            float inputY = _vh - 36f;
            float areaTop = y + tabHeight + 12f;
            Panel(8, inputY, _vw - 16, 28, new Color(0.12f, 0.12f, 0.12f, 0.98f));
            string caret = ((int)(Time.unscaledTime * 2f)) % 2 == 0 ? "_" : " ";
            string target = room == null ? "" : "[" + room.Name + "] ";
            // How many characters fit on a row (the font is not fixed-width, so this stays on the safe side).
            int perRow = Mathf.Max(40, Mathf.FloorToInt((_vw - 70f) / 8f));
            // A long line shows its end, where the typing is.
            int fits = perRow - 40;
            string draft = chat.Draft.Length > fits ? "..." + chat.Draft.Substring(chat.Draft.Length - fits) : chat.Draft;
            Label(18, inputY + 4, _vw - 220, 21, target + draft + caret, 16, Color.white, TextAlignmentOptions.Left);
            // How many characters are left for this line.
            int left = ProtocolConstants.MaxChatLength - chat.Draft.Length;
            // Silenced for sending too fast: the seconds left, counting down, in place of the characters left.
            int silenced = EmbeddedClient.Vm != null ? EmbeddedClient.Vm.ChatSilencedSeconds : 0;
            if (silenced > 0)
                Label(_vw - 260, inputY + 4, 240, 21, "SILENCED: " + silenced, 16, ChatRed, TextAlignmentOptions.Right);
            else
                Label(_vw - 200, inputY + 5, 180, 20, left + " / " + ProtocolConstants.MaxChatLength + " left", 13, left == 0 ? ChatRed : left <= 50 ? Warning : Dim, TextAlignmentOptions.Right);

            int rows = Mathf.Max(1, Mathf.FloorToInt((inputY - 6f - areaTop) / lineHeight));
            if (chat.ShowRooms)
            {
                // The list of rooms: + joins, - leaves.
                float ry = areaTop;
                Label(20, ry, 600, 21, "CHAT ROOMS", 15, ChatGold, TextAlignmentOptions.Left);
                ry += lineHeight + 6f;
                foreach (WKRace.Client.ChatRoomModel listed in vm.Rooms)
                {
                    byte id = listed.Id;
                    bool can = listed.Joined || (listed.Available && vm.ChatConnected);
                    string sign = listed.Joined ? "-" : "+";
                    Panel(16, ry - 2, 34, lineHeight + 4, new Color(0.13f, 0.13f, 0.13f, 0.98f));
                    Label(16, ry, 34, 21, sign, 18, can ? ChatGold : Dim, TextAlignmentOptions.Center);
                    Label(62, ry, 1100, 21, listed.Name + "    " + listed.About + (can ? "" : "  (not open right now)"), 15, listed.Joined ? Neutral : Dim, TextAlignmentOptions.Left);
                    if (can)
                    {
                        bool joined = listed.Joined;
                        chat.Hits.Add(new ChatBox.Hit { Area = new Rect(16, ry - 2, 700, lineHeight + 4), Do = () => { if (joined) vm.LeaveRoom(id); else vm.JoinRoom(id); chat.Scroll = 0; } });
                    }
                    ry += lineHeight + 8f;
                }
            }
            else if (!vm.ChatConnected)
            {
                Label(20, areaTop, 1200, 21, vm.WantsOnline ? "Not connected to the match server." : "The chat needs a connection: press CONNECT in the WKSR menu.", 15, Warning, TextAlignmentOptions.Left);
            }
            else if (room == null)
            {
                Label(20, areaTop, 1200, 21, "No chat room is open. Press + to add one.", 15, Warning, TextAlignmentOptions.Left);
            }
            else
            {
                // The newest line sits just above the input line; older ones go up, as far as the panel has room.
                chat.Scroll = Mathf.Clamp(chat.Scroll, 0, Mathf.Max(0, room.Lines.Count - rows));
                float ly = inputY - 6f - lineHeight;
                for (int i = room.Lines.Count - 1 - chat.Scroll, shown = 0; i >= 0 && shown < rows; i--)
                {
                    WKRace.Client.ChatRoomLine line = room.Lines[i];
                    // A long line takes several rows; the rows after the first are set in a little.
                    List<string> parts = WrapChat(line.Display(), perRow);
                    for (int p = parts.Count - 1; p >= 0 && shown < rows; p--, shown++)
                    {
                        Label(p == 0 ? 20 : 44, ly, _vw - 64, 21, parts[p], 15, line.System ? Warning : Neutral, TextAlignmentOptions.Left);
                        ly -= lineHeight;
                    }
                }
                if (chat.Scroll > 0) Label(_vw - 420, inputY - 6f - lineHeight, 400, 21, "older lines (scroll down for the newest)", 13, Dim, TextAlignmentOptions.Right);
            }

            // A line under the tabs, and the dark background (darker than the game behind it, so the text reads).
            Panel(0, y + tabHeight + 4, _vw, 2, new Color(1f, 1f, 1f, 0.55f));
            Panel(0, top, _vw, _vh - top, new Color(0f, 0f, 0f, 0.975f));
        }

        private static readonly List<string> OneRow = new List<string>(1) { "" };

        // Breaks a line into rows of at most max characters, at spaces where it can.
        private static List<string> WrapChat(string text, int max)
        {
            if (text.Length <= max)
            {
                OneRow[0] = text;
                return OneRow;
            }
            var rows = new List<string>();
            while (text.Length > max)
            {
                int cut = text.LastIndexOf(' ', max - 1, max);
                if (cut <= max / 2) cut = max;
                rows.Add(text.Substring(0, cut));
                text = text.Substring(cut).TrimStart();
            }
            if (text.Length > 0) rows.Add(text);
            return rows;
        }

        // While the chat panel is open, clicks on it must not reach what is behind it (the main menu's QUIT is there).
        private void BlockClicks(bool on, float y, float height)
        {
            if (_chatBlocker == null)
            {
                if (!on) return;
                if (_root.GetComponent<GraphicRaycaster>() == null) _root.AddComponent<GraphicRaycaster>();
                var go = new GameObject("Chat click blocker", typeof(RectTransform));
                go.transform.SetParent(_canvas, false);
                go.transform.SetAsFirstSibling();
                _chatBlocker = go.AddComponent<Image>();
                _chatBlocker.color = new Color(0f, 0f, 0f, 0f);
                _chatBlocker.raycastTarget = true;
                Place(_chatBlocker.rectTransform);
            }
            if (_chatBlocker.enabled != on) _chatBlocker.enabled = on;
            if (!on) return;
            _chatBlocker.rectTransform.anchoredPosition = new Vector2(0f, -y);
            _chatBlocker.rectTransform.sizeDelta = new Vector2(_vw, height);
        }

        private void DrawToasts()
        {
            float now = Time.unscaledTime;
            _toasts.RemoveAll(t => t.Until < now);
            float y = _resultBottom > 0f ? _resultBottom + 8f : 22f;
            for (int i = 0; i < _toasts.Count; i++)
            {
                ToastEntry t = _toasts[i];
                Color c = t.Color;
                c.a = Mathf.Clamp01((t.Until - now) / 0.6f);
                Label(_vw / 2f - 520, y, 1040, 22, t.Text, 16, c, TextAlignmentOptions.Center);
                y += 22f;
            }
        }

        private static Camera Viewer()
        {
            ENT_Player player = ENT_Player.playerObject;
            if (player != null && player.cam != null) return player.cam;
            return Camera.main;
        }

        private static string Shorten(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Length <= max ? text : text.Substring(0, max - 1) + ".";
        }

        // ---- pooled elements --------------------------------------------------

        private bool EnsureCanvas()
        {
            if (_root == null)
            {
                _root = new GameObject(_chatOnly ? "WKRace chat" : "WKRace HUD");
                UnityEngine.Object.DontDestroyOnLoad(_root);
                var canvas = _root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = _chatOnly ? OrderChat : OrderOnTop;
                _canvasComponent = canvas;
                _scaler = _root.AddComponent<CanvasScaler>();
                _scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                _scaler.matchWidthOrHeight = 1f;
                _canvas = (RectTransform)_root.transform;
                _labels.Clear();
                _panels.Clear();
                _icons.Clear();
                _arrow = null;
            }

            int order = _chatOnly ? OrderChat : MenuUi.IsOpen ? OrderUnderWindow : OrderOnTop;
            if (_canvasComponent.sortingOrder != order) _canvasComponent.sortingOrder = order;

            float scale = Mathf.Max(0.1f, RaceConfig.HudScale.Value);
            _vh = 1080f / scale;
            _vw = Screen.height > 0 ? Screen.width * _vh / Screen.height : 1920f / scale;
            _scaler.referenceResolution = new Vector2(_vw, _vh);

            if (_font == null && Time.unscaledTime >= _nextFontSearch) FindFont();
            return true;
        }

        // The game's UI font is loaded with its first scene; until then TextMeshPro's default font stands in.
        private void FindFont()
        {
            _nextFontSearch = Time.unscaledTime + 1f;
            foreach (TMP_FontAsset font in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
            {
                if (font.name != GameFontName) continue;
                _font = font;
                _material = new Material(font.material) { name = "WKRace HUD text" };
                // Plain text with a soft drop shadow, so it reads over bright walls without a background box.
                _material.DisableKeyword("GLOW_ON");
                _material.EnableKeyword("UNDERLAY_ON");
                _material.SetColor("_UnderlayColor", new Color(0f, 0f, 0f, 0.85f));
                _material.SetFloat("_UnderlayOffsetX", 0.7f);
                _material.SetFloat("_UnderlayOffsetY", -0.7f);
                _material.SetFloat("_UnderlayDilate", 0.3f);
                _material.SetFloat("_UnderlaySoftness", 0.25f);
                foreach (TextMeshProUGUI label in _labels) ApplyFont(label);
                return;
            }
        }

        private void ApplyFont(TextMeshProUGUI label)
        {
            if (_font == null) return;
            label.font = _font;
            label.fontSharedMaterial = _material;
        }

        private void Begin()
        {
            _labelsUsed = 0;
            _panelsUsed = 0;
            _iconsUsed = 0;
            if (_arrow != null) _arrow.enabled = false;
        }

        private void End()
        {
            for (int i = _labelsUsed; i < _labels.Count; i++) if (_labels[i].enabled) _labels[i].enabled = false;
            for (int i = _panelsUsed; i < _panels.Count; i++) if (_panels[i].enabled) _panels[i].enabled = false;
            for (int i = _iconsUsed; i < _icons.Count; i++) if (_icons[i].enabled) _icons[i].enabled = false;
        }

        // x/y are the top-left corner in virtual 1080p units, y growing downward. Text is shown as plain text: names,
        // level names and reasons come from other players, and '<' would let them inject rich-text tags. Only our
        // own formatted text (the clock) passes rich = true.
        private void Label(float x, float y, float w, float h, string text, float size, Color color, TextAlignmentOptions alignment, bool rich = false)
        {
            if (!rich && text != null && text.IndexOf('<') >= 0) text = text.Replace("<", "");
            TextMeshProUGUI label;
            if (_labelsUsed < _labels.Count)
            {
                label = _labels[_labelsUsed];
            }
            else
            {
                var go = new GameObject("Label", typeof(RectTransform));
                go.transform.SetParent(_canvas, false);
                label = go.AddComponent<TextMeshProUGUI>();
                label.raycastTarget = false;
                label.textWrappingMode = TextWrappingModes.NoWrap;
                label.overflowMode = TextOverflowModes.Overflow;
                label.richText = true;
                ApplyFont(label);
                Place(label.rectTransform);
                _labels.Add(label);
            }
            _labelsUsed++;

            label.rectTransform.anchoredPosition = new Vector2(x, -y);
            label.rectTransform.sizeDelta = new Vector2(w, h);
            if (label.text != text) label.text = text;
            label.fontSize = size;
            label.color = color;
            label.alignment = alignment;
            if (!label.enabled) label.enabled = true;
        }

        private void Panel(float x, float y, float w, float h, Color color)
        {
            Image panel;
            if (_panelsUsed < _panels.Count)
            {
                panel = _panels[_panelsUsed];
            }
            else
            {
                var go = new GameObject("Panel", typeof(RectTransform));
                go.transform.SetParent(_canvas, false);
                go.transform.SetAsFirstSibling();
                panel = go.AddComponent<Image>();
                panel.raycastTarget = false;
                Place(panel.rectTransform);
                _panels.Add(panel);
            }
            _panelsUsed++;
            panel.rectTransform.anchoredPosition = new Vector2(x, -y);
            panel.rectTransform.sizeDelta = new Vector2(w, h);
            panel.color = color;
            if (!panel.enabled) panel.enabled = true;
        }

        // dir is in screen space with y pointing down.
        private void Arrow(Vector2 center, Vector2 dir, float size, Color color)
        {
            if (_arrow == null)
            {
                var go = new GameObject("Arrow", typeof(RectTransform));
                go.transform.SetParent(_canvas, false);
                _arrow = go.AddComponent<Image>();
                _arrow.raycastTarget = false;
                _arrow.sprite = MakeArrowSprite();
                var rt = _arrow.rectTransform;
                rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
                rt.pivot = new Vector2(0.5f, 0.5f);
            }
            _arrow.rectTransform.anchoredPosition = new Vector2(center.x, -center.y);
            _arrow.rectTransform.sizeDelta = new Vector2(size, size);
            _arrow.rectTransform.localEulerAngles = new Vector3(0f, 0f, Mathf.Atan2(-dir.x, -dir.y) * Mathf.Rad2Deg);
            _arrow.color = color;
            _arrow.enabled = true;
        }

        private static void Place(RectTransform rt)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
        }

        // A loudspeaker seen from the side: a small box, a cone opening to the right and, when loud, two sound waves.
        private static Sprite MakeSpeakerSprite(bool loud)
        {
            const int n = 64;
            var texture = new Texture2D(n, n, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            var pixels = new Color32[n * n];
            var on = new Color32(255, 255, 255, 255);
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    float dy = Mathf.Abs(y - 31.5f);
                    bool box = x >= 6 && x < 18 && dy <= 9f;
                    bool cone = x >= 18 && x < 34 && dy <= 9f + (x - 18) * (14f / 16f);
                    bool wave = false;
                    if (loud && x > 36)
                    {
                        float r = Mathf.Sqrt((x - 30f) * (x - 30f) + (y - 31.5f) * (y - 31.5f));
                        bool inArc = dy <= (x - 30f) * 1.2f;
                        wave = inArc && (Mathf.Abs(r - 16f) <= 2f || Mathf.Abs(r - 26f) <= 2f);
                    }
                    pixels[y * n + x] = box || cone || wave ? on : new Color32(0, 0, 0, 0);
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply();
            return Sprite.Create(texture, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f));
        }

        // A filled triangle pointing up.
        private static Sprite MakeArrowSprite()
        {
            const int n = 64;
            var texture = new Texture2D(n, n, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            var pixels = new Color32[n * n];
            for (int y = 0; y < n; y++)
            {
                float half = (n - 1 - y) / (float)(n - 1) * (n / 2f);
                for (int x = 0; x < n; x++)
                {
                    pixels[y * n + x] = Mathf.Abs(x - (n - 1) / 2f) <= half ? new Color32(255, 255, 255, 255) : new Color32(0, 0, 0, 0);
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply();
            return Sprite.Create(texture, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f));
        }
    }
}
