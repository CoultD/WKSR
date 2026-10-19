using System;
using System.Collections.Generic;
using System.Diagnostics;
using WKRace.Protocol;

namespace WKRace.Mod
{
    internal enum RacePhase
    {
        Banning,    // artifact bans before the world loads (only in matches with bans)
        Starting,   // match assigned, run not started yet
        Loading,    // seeded world is generating
        Waiting,    // loaded: the ready check, then waiting for everyone else
        Countdown,  // frozen until GO
        Racing,
        Finished,   // local finish (or death) reported, waiting for the verdict
        Done,       // verdict received
    }

    internal sealed class SplitRecord
    {
        public int Index;
        // Level name (+ "#n" for a repeat): what splits are compared by, see SplitKeys.
        public string Key;
        public string Level;
        public string Region;
        public int TimeMs;
    }

    // One other racer: the opponent of a 1v1, the best run replayed in practice, or one of a party.
    internal sealed class Rival
    {
        public readonly byte Racer;
        public string Name = "";
        public int Rating;
        public RacerStatus Status = RacerStatus.Racing;
        // When they went out (Out, Left), from the roster.
        public int OutMs = -1;
        public int Attempt = 1;
        public readonly Dictionary<int, SplitRecord> Splits = new Dictionary<int, SplitRecord>();
        public bool Finished;
        public int FinishMs = -1;
        public OpponentState Last;
        public float LastTime;
        public readonly Ghost Ghost = new Ghost();
        // The HUD's marker over this player's ghost, rebuilt only when the rounded distances change.
        public string MarkerText, MarkerName;
        public int MarkerMetres, MarkerRise;
        private Dictionary<string, SplitRecord> _byKey;

        public Rival(byte racer)
        {
            Racer = racer;
        }

        public bool IsOut { get { return Status == RacerStatus.Out || Status == RacerStatus.Left; } }

        public void SetSplit(SplitRecord record)
        {
            Splits[record.Index] = record;
            _byKey = null;
        }

        public SplitRecord Find(int index)
        {
            SplitRecord s;
            return Splits.TryGetValue(index, out s) ? s : null;
        }

        // Their split at this level (same key), or null if they have not been there.
        public SplitRecord FindAt(string key)
        {
            if (key == null) return null;
            BuildKeys();
            SplitRecord found;
            return _byKey.TryGetValue(key, out found) ? found : null;
        }

        public string KeyOf(SplitRecord record)
        {
            BuildKeys();
            return record.Key;
        }

        private void BuildKeys()
        {
            if (_byKey != null) return;
            List<SplitRecord> ordered = Ordered();
            List<string> keys = SplitKeys.Build(ordered.ConvertAll(r => r.Level));
            _byKey = new Dictionary<string, SplitRecord>();
            for (int i = 0; i < ordered.Count; i++)
            {
                ordered[i].Key = keys[i];
                _byKey[keys[i]] = ordered[i];
            }
        }

        public List<SplitRecord> Ordered()
        {
            var ordered = new List<SplitRecord>(Splits.Values);
            ordered.Sort((a, b) => a.Index.CompareTo(b.Index));
            return ordered;
        }

        // The level they reached last in their current attempt, or null.
        public SplitRecord Latest()
        {
            SplitRecord best = null;
            foreach (SplitRecord split in Splits.Values) if (best == null || split.Index > best.Index) best = split;
            return best;
        }

        public void Reset(int attempt)
        {
            Attempt = attempt;
            Splits.Clear();
            _byKey = null;
            Last = null;
        }

        public RacerProgress Progress()
        {
            var p = new RacerProgress { Racer = Racer, Name = Name, Finished = Finished, FinishMs = FinishMs, Out = !Finished && IsOut, OutMs = OutMs };
            foreach (SplitRecord s in Ordered()) p.AddSplit(s.Level, s.Region, s.TimeMs);
            return p;
        }
    }

    // Everything the HUD and ghosts need to know about the current match. Touched on the game thread only.
    internal sealed class RaceSession
    {
        public readonly Guid MatchId;
        public readonly int Seed;
        public readonly bool Competitive;
        public readonly int GraceSeconds;
        public readonly bool Ranked;
        public readonly MatchMode Mode;
        public readonly MatchRules Rules;
        public readonly byte YourRacer;
        public readonly int PlayerCount;
        // The ready check's length, or 0 when there is none (practice).
        public readonly int ReadySeconds;
        // A practice run (WKSR on its own); the "opponent" is the player's best run on the seed, when there is one.
        // A solo run: unranked (WKSR hosts it on this PC), or ranked (RankedSolo: the match server hosts it). Either
        // way one player, restarts allowed (each is a fresh run), and a pause stops the clock.
        public bool Practice;
        public bool RankedSolo;
        // A solo run of a segment (Segments): it ends where the segment does. 0: the whole climb.
        public readonly byte Segment;
        // "SILOS SEGMENT", or "" for the whole climb.
        public string SegmentLabel { get { return Segment != Segments.None ? Segments.Name(Segment) + " SEGMENT" : ""; } }
        // The ghost of a solo run is the player's own best on the seed (or there is none), not a run they picked
        // from a leaderboard.
        public bool GhostIsMine { get { return OpponentName == SoloHost.BestName || PracticeBestMs < 0; } }
        public int PracticeBestMs = -1;
        public bool PracticeBestFinished;
        public int PracticeBestLevels;
        // A practice run's result panel has closed; the session stays so a restart still means "fresh practice run".
        public bool ResultClosed;

        public bool Party { get { return Mode == MatchMode.Party; } }

        // "12:34.5" for a finished best, "OUT AFTER 17 LEVELS" for one that ended with a death.
        public string PracticeBestText
        {
            get
            {
                if (PracticeBestFinished) return RaceFormat.Time(PracticeBestMs, 1);
                return "OUT AFTER " + PracticeBestLevels + (PracticeBestLevels == 1 ? " LEVEL" : " LEVELS");
            }
        }

        // "RACE VS NAME (1000)", "FRIENDLY RACE VS NAME", "PARTY RACE (6 PLAYERS)", "PRACTICE VS YOUR BEST 12:34.5", ...
        public string MatchLabel(bool withRating)
        {
            if (Segment != Segments.None)
                return SegmentLabel + (RankedSolo ? "" : PracticeBestMs < 0 ? ", UNRANKED" : GhostIsMine ? " VS YOUR BEST " + PracticeBestText : " VS " + OpponentName.ToUpperInvariant() + " " + PracticeBestText);
            if (RankedSolo) return "RANKED SOLO";
            if (Practice) return PracticeBestMs < 0 ? "UNRANKED RUN" : GhostIsMine ? "UNRANKED VS YOUR BEST " + PracticeBestText : "UNRANKED VS " + OpponentName.ToUpperInvariant() + " " + PracticeBestText;
            if (Party) return "PARTY MATCH (" + PlayerCount + " PLAYERS)";
            return (Ranked ? "MATCH VS " : "FRIENDLY MATCH VS ") + OpponentName.ToUpperInvariant() + (withRating ? " (" + OpponentRating + ")" : "");
        }

        public RacePhase Phase = RacePhase.Starting;
        public bool AutoStartPending = true;
        public bool WorldInitialized;
        public bool ReadySent;
        public uint WorldHash;
        public int LevelCount;

        // The ready check: whether this player has pressed ready (sent, and as the roster confirms), and when it ends.
        public bool ReadyPressed;
        public bool ReadyConfirmed;
        public float ReadyDeadline = -1f;
        public MatchRoster Roster;

        // Attempt counting: the first BeginAttempt keeps Attempt as is, later ones (resets) increment it.
        public int Attempt;
        public bool AttemptBegun;
        public int AttemptStartMs;

        public bool HasGo;
        private long _goTimestamp;

        // While held the game is frozen and the race clock shows HeldRaceMs. A pending resume releases it at _resumeTimestamp.
        public bool Held;
        public int HeldRaceMs;
        public string HoldReason = "";
        public float HoldDeadline;
        public bool ResumePending;
        private long _resumeTimestamp;

        public bool Cheated;
        // The run ended with the player's death (MyFinishMs is then the time of death).
        public bool Died;
        // What killed the player and the game's line for it.
        public string DeathCause = "";
        public string DeathText = "";
        public int MyFinishMs = -1;
        // The shared real-time clock at the finish (who finished first), next to MyFinishMs (the game's timer).
        public int MyFinishRealMs = -1;
        public MatchResult Result;
        // The race clock when the result arrived, for showing a stopped clock afterwards.
        public int EndRaceMs;
        public float DoneUntil;

        public readonly List<SplitRecord> MySplits = new List<SplitRecord>();
        public readonly HashSet<int> SeenLevels = new HashSet<int>();

        // Every other racer by racer number. Primary is the one a 1v1 or practice run is against (null in a party).
        public readonly Dictionary<byte, Rival> Rivals = new Dictionary<byte, Rival>();
        public readonly Rival Primary;
        // Who the split list compares with: the 1v1 opponent, or in a party the racer just ahead (just behind when first).
        public Rival Compare;

        // Artifact bans: the latest state from the server, when it arrived, and the local selection.
        public readonly bool ArtifactBans;
        public BanState Bans;
        public float BansReceivedAt;
        public float NextBanReady;
        public bool BanPickSent;
        public int BanCursor;

        public RaceSession(MatchFound found)
        {
            MatchId = found.MatchId;
            ArtifactBans = found.ArtifactBans;
            if (ArtifactBans) Phase = RacePhase.Banning;
            Seed = found.Seed;
            Competitive = found.Competitive;
            GraceSeconds = found.GraceSeconds;
            Ranked = found.Ranked;
            Mode = found.Mode;
            Rules = found.Rules;
            YourRacer = found.YourRacer;
            PlayerCount = Math.Max(1, found.PlayerCount);
            ReadySeconds = found.ReadySeconds;
            Segment = Segments.Find(found.Segment) != null ? found.Segment : Segments.None;
            Attempt = Math.Max(1, found.Attempt);
            AttemptBegun = found.ResumeElapsedMs >= 0;
            if (!Party)
            {
                Primary = RivalFor((byte)(YourRacer == 0 ? 1 : 0));
                Primary.Name = found.OpponentName;
                Primary.Rating = found.OpponentRating;
                Compare = Primary;
            }
        }

        public Rival RivalFor(byte racer)
        {
            Rival r;
            if (!Rivals.TryGetValue(racer, out r))
            {
                r = new Rival(racer) { Name = "Player " + (racer + 1) };
                Rivals[racer] = r;
            }
            return r;
        }

        public string OpponentName { get { return Primary != null ? Primary.Name : ""; } set { if (Primary != null) Primary.Name = value; } }
        public int OpponentRating { get { return Primary != null ? Primary.Rating : 0; } set { if (Primary != null) Primary.Rating = value; } }
        public bool OpponentFinished { get { return Compare != null && Compare.Finished; } }
        public int OpponentFinishMs { get { return Compare != null ? Compare.FinishMs : -1; } }

        public bool IsActive
        {
            get { return Phase != RacePhase.Done; }
        }

        // Set at GO. From then on the match clock is the game's own run timer (GameBridge.GameTimeMs): it counts the
        // shortcut sections three times over, and stands still while the game loads, is frozen by a hold, or (solo
        // only) is paused. Before GO the clock is the countdown on the shared real-time clock.
        public bool OnGameClock;

        // The match clock: negative while the countdown is running; stands still while held.
        public int RaceMs
        {
            get
            {
                if (OnGameClock) return GameBridge.GameTimeMs();
                if (Held) return HeldRaceMs;
                return RealMs;
            }
        }

        // The real time since GO on the clock every player of the match shares (negative before GO). It only says
        // who finished first; every time that is shown comes from RaceMs.
        public int RealMs
        {
            get
            {
                if (!HasGo) return 0;
                return (int)((Stopwatch.GetTimestamp() - _goTimestamp) * 1000L / Stopwatch.Frequency);
            }
        }

        // Freezes the clock. raceMs < 0 with unknownTime keeps the clock where it is (the client does not know the server's value).
        public void Hold(string reason, int secondsLeft, int raceMs, bool knownTime)
        {
            if (!Held) HeldRaceMs = RaceMs;
            if (knownTime) HeldRaceMs = raceMs;
            Held = true;
            ResumePending = false;
            HoldReason = reason ?? "";
            HoldDeadline = UnityEngine.Time.unscaledTime + Math.Max(0, secondsLeft);
        }

        // GO moves to msUntilGo from now; the hold ends msUntilResume from now, at which point the clock reads what it read when held.
        public void ScheduleResume(int msUntilGo, int msUntilResume)
        {
            SetGoIn(msUntilGo);
            if (!Held) return;
            HeldRaceMs = msUntilResume - msUntilGo;
            ResumePending = true;
            _resumeTimestamp = Stopwatch.GetTimestamp() + Math.Max(0, msUntilResume) * Stopwatch.Frequency / 1000L;
        }

        public int MsUntilResume
        {
            get { return (int)((_resumeTimestamp - Stopwatch.GetTimestamp()) * 1000L / Stopwatch.Frequency); }
        }

        // True once a scheduled resume time has passed; the hold is lifted.
        public bool TryRelease()
        {
            if (!Held || !ResumePending || MsUntilResume > 0) return false;
            Held = false;
            ResumePending = false;
            return true;
        }

        // msFromNow may be negative when the race is already under way.
        public void SetGoIn(int msFromNow)
        {
            _goTimestamp = Stopwatch.GetTimestamp() + msFromNow * Stopwatch.Frequency / 1000L;
            HasGo = true;
        }

        public SplitRecord FindMySplit(int index)
        {
            return index >= 0 && index < MySplits.Count ? MySplits[index] : null;
        }

        // ---- splits matched by level --------------------------------------------------------------------------

        private readonly Dictionary<string, int> _myLevelCounts = new Dictionary<string, int>();

        public void AddMySplit(SplitRecord record)
        {
            string name = record.Level ?? "";
            int n;
            _myLevelCounts.TryGetValue(name, out n);
            _myLevelCounts[name] = ++n;
            record.Key = SplitKeys.Key(name, n);
            MySplits.Add(record);
        }

        public void ClearMySplits()
        {
            MySplits.Clear();
            _myLevelCounts.Clear();
        }

        public bool HaveReached(string key)
        {
            foreach (SplitRecord split in MySplits) if (split.Key == key) return true;
            return false;
        }

        // The key the next visit of this level would get on our run.
        public string NextKey(string level)
        {
            int n;
            _myLevelCounts.TryGetValue(level ?? "", out n);
            // Asked every frame by the HUD: the same level and count give the same key.
            if (ReferenceEquals(level, _nextKeyLevel) && n == _nextKeyCount && _nextKey != null) return _nextKey;
            _nextKeyLevel = level;
            _nextKeyCount = n;
            return _nextKey = SplitKeys.Key(level, n + 1);
        }

        private string _nextKeyLevel, _nextKey;
        private int _nextKeyCount;

        // The compared racer's split at the same level, or null if they have not been there.
        public SplitRecord FindOpponentAt(string key)
        {
            return Compare != null ? Compare.FindAt(key) : null;
        }

        // ---- party standings --------------------------------------------------------------------------------

        public RacerProgress MyProgress()
        {
            var p = new RacerProgress
            {
                Racer = YourRacer,
                Name = "You",
                Finished = MyFinishMs >= 0 && !Died && (Phase == RacePhase.Finished || Phase == RacePhase.Done),
                FinishMs = Died ? -1 : MyFinishMs,
                Out = Died,
                OutMs = Died ? MyFinishMs : -1,
            };
            foreach (SplitRecord s in MySplits) p.AddSplit(s.Level, s.Region, s.TimeMs);
            return p;
        }

        // Everyone, best first, with this player among them (Racer == YourRacer).
        public List<RacerProgress> Ranking()
        {
            var all = new List<RacerProgress> { MyProgress() };
            foreach (Rival r in Rivals.Values) all.Add(r.Progress());
            return Standings.Rank(all, Math.Max(0, RaceMs));
        }

        // This player's place (1 = first), from the ranking.
        public static int PlaceOf(List<RacerProgress> ranking, int racer)
        {
            for (int i = 0; i < ranking.Count; i++) if (ranking[i].Racer == racer) return i + 1;
            return ranking.Count;
        }
    }
}
