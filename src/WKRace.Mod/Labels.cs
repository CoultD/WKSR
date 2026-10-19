using WKRace.Protocol;

namespace WKRace.Mod
{
    internal static class Labels
    {
        public static string Level(string raw)
        {
            return RaceFormat.LevelLabel(raw);
        }

        // The HUD draws these every frame, so each form of a level's name is built once.
        private static readonly System.Collections.Generic.Dictionary<string, string> Shorts = new System.Collections.Generic.Dictionary<string, string>();
        private static readonly System.Collections.Generic.Dictionary<string, string> Ats = new System.Collections.Generic.Dictionary<string, string>();

        // The label cut to the split list's column ("Storage 03", "Interlude Silo T.").
        public static string Short(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string text;
            if (Shorts.TryGetValue(raw, out text)) return text;
            text = Level(raw);
            if (text.Length > 16) text = text.Substring(0, 15) + ".";
            if (Shorts.Count > 2000) Shorts.Clear();
            return Shorts[raw] = text;
        }

        // "AT STORAGE 03"
        public static string At(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string text;
            if (Ats.TryGetValue(raw, out text)) return text;
            if (Ats.Count > 2000) Ats.Clear();
            return Ats[raw] = "AT " + Level(raw).ToUpperInvariant();
        }
    }
}
