using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace WKRace.Protocol
{
    public static class RaceFormat
    {
        // "M1_Silos_SafeArea_01" -> "Safe Area 01"
        public static string LevelLabel(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string known;
            if (LevelLabels.TryGetValue(raw, out known)) return known;
            // Names come from the game's fixed set of levels, but the server also sees what clients send: never grow
            // without bound.
            if (LevelLabels.Count >= 4096) LevelLabels.Clear();
            return LevelLabels[raw] = BuildLevelLabel(raw);
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> LevelLabels =
            new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        private static string BuildLevelLabel(string raw)
        {
            string s = Regex.Replace(raw, @"^M\d+_", "");
            s = Regex.Replace(s, @"^(Silos|Pipeworks|Habitation)_", "");
            s = s.Replace("Campaign_Interlude_", "").Replace('_', ' ');
            return Regex.Replace(s, @"(?<=[a-z])(?=[A-Z])", " ").Trim();
        }

        // "Item_Artifact_Rebar_Return" -> "Rebar Return"
        public static string ItemLabel(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string s = Regex.Replace(raw, @"^Item_(Artifact_)?", "").Replace('_', ' ');
            return Regex.Replace(s, @"(?<=[a-z])(?=[A-Z])", " ").Trim();
        }

        // "m:ss.cc" or "h:mm:ss.cc".
        public static string Time(long ms, int decimals = 2)
        {
            if (ms < 0) ms = 0;
            long totalSeconds = ms / 1000;
            int millis = (int)(ms % 1000);
            long hours = totalSeconds / 3600;
            int minutes = (int)((totalSeconds / 60) % 60);
            int seconds = (int)(totalSeconds % 60);
            string fraction = Fraction(millis, decimals);
            if (hours > 0)
            {
                return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}{3}", hours, minutes, seconds, fraction);
            }
            return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}{2}", minutes, seconds, fraction);
        }

        // "+1.23" for short gaps, "-1:12.34" for longer ones.
        public static string Delta(long ms, int decimals = 2)
        {
            string sign = ms < 0 ? "-" : "+";
            long abs = Math.Abs(ms);
            if (abs < 60000)
            {
                return sign + (abs / 1000).ToString(CultureInfo.InvariantCulture) + Fraction((int)(abs % 1000), decimals);
            }
            return sign + Time(abs, decimals);
        }

        private static string Fraction(int millis, int decimals)
        {
            if (decimals <= 0) return string.Empty;
            string digits = millis.ToString("000", CultureInfo.InvariantCulture);
            return "." + digits.Substring(0, Math.Min(3, decimals));
        }
    }

    public static class Fnv1a
    {
        public const uint Offset = 2166136261;

        public static uint Add(uint hash, string value)
        {
            unchecked
            {
                if (value != null)
                {
                    for (int i = 0; i < value.Length; i++)
                    {
                        hash ^= value[i];
                        hash *= 16777619;
                    }
                }
                hash ^= 0xFF;
                hash *= 16777619;
                return hash;
            }
        }

        public static uint Add(uint hash, int value)
        {
            unchecked
            {
                for (int i = 0; i < 4; i++)
                {
                    hash ^= (byte)(value >> (8 * i));
                    hash *= 16777619;
                }
                return hash;
            }
        }
    }
}
