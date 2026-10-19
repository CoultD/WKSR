using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace WKRace.Server
{
    // Steam's sign-in is OpenID 2.0: we send the player's browser to Steam, Steam sends it back to our return URL with
    // a signed assertion naming the account, and we ask Steam to confirm that assertion (check_authentication).
    // No Steam Web API key is needed. This class holds the parts that need no network.
    public static class SteamOpenId
    {
        public const string Endpoint = "https://steamcommunity.com/openid/login";
        private const string Ns = "http://specs.openid.net/auth/2.0";
        private const string IdentifierSelect = "http://specs.openid.net/auth/2.0/identifier_select";
        // Individual accounts are 7656119 followed by ten digits.
        private static readonly Regex ClaimedId = new Regex(@"^https://steamcommunity\.com/openid/id/(7656119\d{10})$", RegexOptions.CultureInvariant);

        public static string ReturnUrl(string publicUrl, string nonce)
        {
            return publicUrl.TrimEnd('/') + "/steam/return?n=" + nonce;
        }

        public static string LoginUrl(string publicUrl, string nonce)
        {
            var q = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("openid.ns", Ns),
                new KeyValuePair<string, string>("openid.mode", "checkid_setup"),
                new KeyValuePair<string, string>("openid.return_to", ReturnUrl(publicUrl, nonce)),
                new KeyValuePair<string, string>("openid.realm", publicUrl.TrimEnd('/') + "/"),
                new KeyValuePair<string, string>("openid.identity", IdentifierSelect),
                new KeyValuePair<string, string>("openid.claimed_id", IdentifierSelect),
            };
            var sb = new StringBuilder(Endpoint).Append('?');
            for (int i = 0; i < q.Count; i++)
            {
                if (i > 0) sb.Append('&');
                sb.Append(Uri.EscapeDataString(q[i].Key)).Append('=').Append(Uri.EscapeDataString(q[i].Value));
            }
            return sb.ToString();
        }

        // Checks Steam's answer as far as possible without asking Steam: it must be a positive assertion from Steam's
        // endpoint, for exactly our return URL, signing the fields we rely on, about an individual Steam account.
        // Returns the 64-bit Steam ID, or 0 with the reason.
        public static ulong Validate(IReadOnlyDictionary<string, string> q, string expectedReturnUrl, out string error)
        {
            error = null;
            string Get(string key) { string v; return q.TryGetValue(key, out v) ? v : null; }

            if (Get("openid.mode") == "cancel") { error = "Sign-in was cancelled."; return 0; }
            if (Get("openid.ns") != Ns || Get("openid.mode") != "id_res") { error = "Not a Steam sign-in answer."; return 0; }
            if (Get("openid.op_endpoint") != Endpoint) { error = "The answer did not come from Steam."; return 0; }
            if (Get("openid.return_to") != expectedReturnUrl) { error = "The answer was for another address."; return 0; }

            string claimed = Get("openid.claimed_id");
            if (claimed == null || claimed != Get("openid.identity")) { error = "No Steam account in the answer."; return 0; }
            var m = ClaimedId.Match(claimed);
            if (!m.Success) { error = "Not a Steam account."; return 0; }

            var signed = new HashSet<string>((Get("openid.signed") ?? "").Split(','));
            foreach (string field in new[] { "op_endpoint", "claimed_id", "identity", "return_to", "response_nonce", "assoc_handle" })
            {
                if (!signed.Contains(field)) { error = "The answer is not signed properly."; return 0; }
            }
            if (string.IsNullOrEmpty(Get("openid.sig")) || string.IsNullOrEmpty(Get("openid.response_nonce"))) { error = "The answer is not signed."; return 0; }
            return ulong.Parse(m.Groups[1].Value);
        }

        // The form for Steam's check_authentication: the same openid.* fields with the mode changed.
        public static List<KeyValuePair<string, string>> CheckAuthenticationForm(IReadOnlyDictionary<string, string> q)
        {
            var form = new List<KeyValuePair<string, string>>();
            foreach (KeyValuePair<string, string> pair in q)
            {
                if (!pair.Key.StartsWith("openid.", StringComparison.Ordinal)) continue;
                form.Add(new KeyValuePair<string, string>(pair.Key, pair.Key == "openid.mode" ? "check_authentication" : pair.Value));
            }
            return form;
        }

        // Steam answers check_authentication with "key:value" lines; is_valid:true confirms the assertion.
        public static bool IsValidResponse(string body)
        {
            if (body == null) return false;
            foreach (string line in body.Split('\n'))
            {
                if (line.Trim() == "is_valid:true") return true;
            }
            return false;
        }

        // "a=1&b=x%20y" -> {a: 1, b: x y}. Later duplicates are ignored.
        public static Dictionary<string, string> ParseQuery(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(query)) return result;
            foreach (string part in query.TrimStart('?').Split('&'))
            {
                if (part.Length == 0) continue;
                int eq = part.IndexOf('=');
                string key = WebUtility.UrlDecode(eq < 0 ? part : part.Substring(0, eq));
                string value = eq < 0 ? "" : WebUtility.UrlDecode(part.Substring(eq + 1));
                if (!result.ContainsKey(key)) result[key] = value;
            }
            return result;
        }
    }
}
