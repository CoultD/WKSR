using System;
using System.Windows;
using System.Windows.Media;
using WKRace.Protocol;

namespace WKRace.Client
{
    using Message = WKRace.Protocol.Message;

    // The modifier picker behind CREATE LOBBY (mode and modifiers) and SOLO (modifiers only). Artifact bans are a 1v1
    // modifier; curated seeds come in three parts that can be picked separately.
    internal sealed class RulesModel : ObservableObject
    {
        private MatchRules _rules;
        private MatchMode _mode;
        private readonly Action<RulesModel> _changed;

        public RulesModel(MatchRules rules, MatchMode mode, bool showMode, Action<RulesModel> changed)
        {
            _rules = rules & MatchRules.All;
            _mode = mode == MatchMode.Party ? MatchMode.Party : MatchMode.Duel;
            ShowMode = showMode;
            _changed = changed;
        }

        public bool ShowMode { get; private set; }

        public MatchMode Mode
        {
            get { return _mode; }
            set
            {
                if (_mode == value) return;
                _mode = value;
                RaiseAll();
            }
        }

        // Two check boxes acting as one choice: clicking the ticked one keeps it ticked.
        public bool IsDuel
        {
            get { return _mode == MatchMode.Duel; }
            set { if (value) Mode = MatchMode.Duel; else Later(nameof(IsDuel)); }
        }

        public bool IsParty
        {
            get { return _mode == MatchMode.Party; }
            set { if (value) Mode = MatchMode.Party; else Later(nameof(IsParty)); }
        }

        public bool BansAvailable { get { return _mode == MatchMode.Duel; } }

        public bool Bans
        {
            get { return BansAvailable && Has(MatchRules.ArtifactBans); }
            set { Flag(MatchRules.ArtifactBans, value); }
        }

        public bool Curated
        {
            get { return (_rules & MatchRules.CuratedSeeds) == MatchRules.CuratedSeeds; }
            set
            {
                _rules = value ? _rules | MatchRules.CuratedSeeds : _rules & ~MatchRules.CuratedSeeds;
                RaiseAll();
            }
        }

        public bool Food { get { return Has(MatchRules.DeepStorageFood); } set { Flag(MatchRules.DeepStorageFood, value); } }
        public bool Perks { get { return Has(MatchRules.GuaranteedPerks); } set { Flag(MatchRules.GuaranteedPerks, value); } }
        public bool Items { get { return Has(MatchRules.GuaranteedItems); } set { Flag(MatchRules.GuaranteedItems, value); } }

        // What the race is played with (a party never has bans).
        public MatchRules Rules { get { return _mode == MatchMode.Party ? _rules & ~MatchRules.ArtifactBans : _rules; } }
        // The raw choice, kept in the settings (a party remembers the bans choice for the next 1v1).
        public MatchRules Stored { get { return _rules; } }
        public string Summary { get { return RulesText.Describe(Rules); } }
        public bool IsRanked { get { return Rules == MatchRules.Ranked; } }

        public void Reset(MatchRules rules)
        {
            _rules = rules & MatchRules.All;
            RaiseAll();
        }

        private bool Has(MatchRules flag)
        {
            return (_rules & flag) != 0;
        }

        private void Flag(MatchRules flag, bool on)
        {
            _rules = on ? _rules | flag : _rules & ~flag;
            RaiseAll();
        }

        private void RaiseAll()
        {
            Raise(nameof(Mode));
            Raise(nameof(IsDuel));
            Raise(nameof(IsParty));
            Raise(nameof(BansAvailable));
            Raise(nameof(Bans));
            Raise(nameof(Curated));
            Raise(nameof(Food));
            Raise(nameof(Perks));
            Raise(nameof(Items));
            Raise(nameof(Rules));
            Raise(nameof(Summary));
            Raise(nameof(IsRanked));
            _changed?.Invoke(this);
        }

        // A binding that just wrote a value does not pick up a change event raised while it writes; raise it after.
        private void Later(string name)
        {
            Application app = Application.Current;
            if (app != null) app.Dispatcher.BeginInvoke(new Action(() => Raise(name)));
        }
    }

    internal sealed class LobbyMemberRow : ObservableObject
    {
        private bool _speaking;
        private bool _muted;

        public string Name { get; set; }
        public string PlainName { get; set; }
        public string Detail { get; set; }
        public Brush Brush { get; set; }
        // Voice: whether this row can be muted (not yourself), is muted, and is talking right now.
        public bool CanMute { get; set; }
        public System.Windows.Input.ICommand MuteCommand { get; set; }
        public bool Muted { get { return _muted; } set { if (Set(ref _muted, value)) Raise(nameof(MuteText)); } }
        public string MuteText { get { return _muted ? "MUTED" : "MUTE"; } }
        public bool Speaking { get { return _speaking; } set { if (Set(ref _speaking, value)) Raise(nameof(SpeakText)); } }
        public string SpeakText { get { return _speaking ? ">" : ""; } }
    }

    // One line of the party standings in the race view.
    internal sealed class StandingRow
    {
        public string Place { get; set; }
        public string Name { get; set; }
        public string Detail { get; set; }
        public Brush Brush { get; set; }
    }

    internal sealed class ChatLine
    {
        public string Text { get; set; }
        public Brush Brush { get; set; }
    }
}
