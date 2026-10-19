using System.Globalization;
using WKRace.Protocol;

namespace WKRace.Client
{
    // Segment runs (see Segments): a solo run that ends where its segment does. With the leaderboard's modifiers, no
    // seed of the player's own and a connection, the match server hosts it as a ranked solo run and its time goes on
    // the segment's leaderboard. Anything else (other modifiers, a typed seed, offline) is an unranked run on this PC.
    internal sealed partial class MainViewModel
    {
        private RulesModel _segmentRules;
        private string _segmentSeed = "";

        // The SEGMENTS page's modifier picker.
        public RulesModel SegmentRules
        {
            get
            {
                if (_segmentRules == null)
                    _segmentRules = new RulesModel((MatchRules)_settings.SegmentRules, MatchMode.Duel, false, r =>
                    {
                        _settings.SegmentRules = (int)r.Stored;
                        _settings.Save();
                    });
                return _segmentRules;
            }
        }

        // The SEGMENTS page's seed box: blank for a random seed.
        public string SegmentSeed { get { return _segmentSeed; } set { _segmentSeed = value ?? ""; } }

        public bool ServerConnected { get { return _link == LinkState.Connected; } }
        public bool CanStartSegment { get { return _phase != ClientPhase.InMatch && !_matchActive; } }

        // The picker shows the modifiers the segment's leaderboard runs are played with, and no seed is typed in.
        public bool SegmentAsRanked(byte id)
        {
            return Segments.Rules(id, SegmentRules.Stored) == Segments.Rules(id, MatchRules.Ranked) && _segmentSeed.Trim().Length == 0;
        }

        // A run started now counts for the segment's leaderboard.
        public bool SegmentCounts(byte id)
        {
            // Steam lobbies have no leaderboards: every segment run there is unranked.
            return !SteamMode && ServerConnected && SegmentAsRanked(id);
        }

        // Back to what the leaderboard needs.
        public void ResetSegmentRules()
        {
            SegmentRules.Reset(MatchRules.Ranked);
            _settings.SegmentRules = (int)SegmentRules.Stored;
            _settings.Save();
            _segmentSeed = "";
        }

        public void StartSegment(byte id)
        {
            SegmentInfo segment = Segments.Find(id);
            if (segment == null || !CanStartSegment) return;
            if (SegmentCounts(id))
            {
                StartRankedSolo(id);
                return;
            }
            int? seed = null;
            string text = _segmentSeed.Trim();
            if (text.Length > 0)
            {
                int typed;
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out typed))
                {
                    Log("A seed is a whole number (or leave the box blank for a random one).");
                    return;
                }
                seed = typed;
            }
            _soloAgainst = null;
            StartPractice(seed, null, Segments.Rules(id, SegmentRules.Stored), false, id);
        }

        // ---- the segments' leaderboards ---------------------------------------------------------------------------

        public bool IsSegmentBoard { get { return Segments.OfBoard(_board) != Segments.None; } }
        public byte BoardSegment { get { return Segments.OfBoard(_board); } }

        public void ShowSegmentBoard(byte id)
        {
            if (!ServerConnected || Segments.Find(id) == null) return;
            ShowBoard(Segments.Board(id));
        }
    }
}
