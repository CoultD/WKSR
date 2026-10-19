using System.Security.Cryptography;
using System.Text;
using WKRace.Protocol;

namespace WKRace.Client
{
    // Random racer names such as "RustyRebar417", offered on first start so nobody races under their Windows account
    // name by accident. Letters and digits only, so the server always accepts them.
    internal static class NameGenerator
    {
        private static readonly string[] Adjectives =
        {
            "Rusty", "Brittle", "Frantic", "Silent", "Hollow", "Grim", "Swift", "Crooked", "Feral", "Lucky",
            "Restless", "Gritty", "Nimble", "Stubborn", "Weary", "Daring", "Shaky", "Clumsy", "Bold", "Quiet",
            "Lanky", "Plucky", "Hasty", "Sly", "Sturdy", "Wiry", "Pale", "Dusty", "Rapid", "Brave",
            "Sooty", "Frayed", "Giddy", "Jumpy", "Murky", "Rowdy", "Steady", "Tireless", "Wobbly", "Zealous",
        };

        private static readonly string[] Nouns =
        {
            "Climber", "Grub", "Piston", "Rebar", "Ladder", "Crank", "Lantern", "Pipe", "Hook", "Ratchet",
            "Rivet", "Girder", "Chain", "Ascender", "Spider", "Moth", "Beetle", "Crow", "Gecko", "Ferret",
            "Goat", "Lizard", "Badger", "Weasel", "Wren", "Rook", "Mantis", "Hatch", "Valve", "Bolt",
            "Gauge", "Winch", "Pulley", "Sprocket", "Grapple", "Piton", "Cable", "Furnace", "Shaft", "Vent",
        };

        // Same characters the server keeps (NameSanitizer): letters, digits, single spaces and _-.[]()!'
        private const string AllowedPunctuation = "_-.[]()!'";

        // The name as the server will keep it, or null when it is not usable.
        public static string Clean(string raw)
        {
            var sb = new StringBuilder();
            bool lastWasSpace = true;
            foreach (char c in raw ?? "")
            {
                if (char.IsLetterOrDigit(c) || AllowedPunctuation.IndexOf(c) >= 0)
                {
                    sb.Append(c);
                    lastWasSpace = false;
                }
                else if (c == ' ')
                {
                    // Runs of spaces become one, as on the server.
                    if (!lastWasSpace) sb.Append(' ');
                    lastWasSpace = true;
                }
                else return null;
            }
            string name = sb.ToString().Trim();
            return name.Length >= 2 && name.Length <= ProtocolConstants.MaxNameLength ? name : null;
        }

        public static string Next()
        {
            var b = new StringBuilder();
            b.Append(Adjectives[RandomNumberGenerator.GetInt32(Adjectives.Length)]);
            b.Append(Nouns[RandomNumberGenerator.GetInt32(Nouns.Length)]);
            b.Append(RandomNumberGenerator.GetInt32(10, 1000));
            string name = b.ToString();
            return name.Length <= ProtocolConstants.MaxNameLength ? name : name.Substring(0, ProtocolConstants.MaxNameLength);
        }
    }
}
