using System;

namespace WKRace.Client
{
    // WKSR opens a link the server sends for linking Steam, so it checks the link really is Steam's OpenID page.
    internal static class SteamLinks
    {
        // https://steamcommunity.com/openid/... and nothing else (no other host, no plain http, no tricks with "@").
        public static bool IsSteamOpenIdPage(string url)
        {
            Uri uri;
            return Uri.TryCreate(url, UriKind.Absolute, out uri)
                && uri.Scheme == Uri.UriSchemeHttps
                && uri.IsDefaultPort
                && string.IsNullOrEmpty(uri.UserInfo)
                && string.Equals(uri.Host, "steamcommunity.com", StringComparison.OrdinalIgnoreCase)
                && uri.AbsolutePath.StartsWith("/openid/", StringComparison.Ordinal);
        }
    }
}
