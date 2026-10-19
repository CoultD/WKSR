using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using WKRace.Protocol;

namespace WKRace.Server
{
    public struct SteamLinkOutcome
    {
        public bool Ok;
        public string Message;
    }

    // Steam sign-in (see SteamOpenId). A player asks for a sign-in link; after Steam confirms it in the browser, the web
    // endpoint calls CompleteSteamLink. Every successful sign-in issues a new token (so a stolen settings file stops
    // working). It links the account, or, if the Steam account already has a WKSR player and this PC's identity is
    // still empty, restores that player on this PC. Steam IDs are stored only as a keyed hash.
    public sealed partial class ServerCore
    {
        private const long SteamLinkTimeoutMs = 10 * 60 * 1000;
        private const int MaxPendingSteamLinks = 1000;

        private sealed class PendingSteamLink
        {
            public Guid PlayerId;
            public long ExpiresMs;
        }

        private readonly Dictionary<string, PendingSteamLink> _steamLinks = new Dictionary<string, PendingSteamLink>();
        private byte[] _steamSecret;

        private bool SteamEnabled { get { return !string.IsNullOrEmpty(_options.SteamPublicUrl); } }
        private bool SteamRequiredForRanked { get { return SteamEnabled && !_options.SteamOptional; } }

        private void HandleSteamLinkStart(Player player)
        {
            if (!SteamEnabled)
            {
                player.Send(new ErrorMessage(ErrorCode.Unknown, "This server does not offer Steam sign-in."));
                return;
            }
            long now = _now();
            PruneSteamLinks(now);
            // One sign-in at a time per player: a new request replaces the old link.
            foreach (string old in _steamLinks.Where(p => p.Value.PlayerId == player.Id).Select(p => p.Key).ToList()) _steamLinks.Remove(old);
            if (_steamLinks.Count >= MaxPendingSteamLinks)
            {
                player.Send(new ErrorMessage(ErrorCode.RateLimited, "Too many Steam sign-ins in progress. Try again in a few minutes."));
                return;
            }

            string nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            _steamLinks[nonce] = new PendingSteamLink { PlayerId = player.Id, ExpiresMs = now + SteamLinkTimeoutMs };
            player.Send(new SteamLinkUrl { Url = SteamOpenId.LoginUrl(_options.SteamPublicUrl, nonce) });
        }

        // For the web endpoint: is this a sign-in we started (checked before asking Steam)?
        public bool IsSteamLinkPending(string nonce)
        {
            PruneSteamLinks(_now());
            return !string.IsNullOrEmpty(nonce) && _steamLinks.ContainsKey(nonce);
        }

        // Called once Steam has confirmed the sign-in. Returns what the browser page says.
        public SteamLinkOutcome CompleteSteamLink(string nonce, ulong steamId)
        {
            PruneSteamLinks(_now());
            PendingSteamLink pending;
            if (string.IsNullOrEmpty(nonce) || !_steamLinks.TryGetValue(nonce, out pending))
                return Fail("This sign-in link has expired or was already used. Start it again from WKSR.");
            _steamLinks.Remove(nonce);

            Player player;
            if (!_players.TryGetValue(pending.PlayerId, out player) || player.Conn == null)
                return Fail("WKSR is no longer connected. Open it and start the Steam sign-in again.");
            return ApplySteamAccount(player, steamId, true);
        }

        // The game says which Steam account it runs under (0.9.3: WKSR is inside the game, and starting the game
        // through Steam is the sign-in). The account is linked without a word when that is all there is to do.
        private void HandleSteamIdentity(Player player, SteamIdentity identity)
        {
            if (!SteamEnabled || identity.SteamId == 0) return;
            long now = _now();
            if (now - player.LastSteamIdentityMs < 2000 && now >= player.LastSteamIdentityMs) return;
            player.LastSteamIdentityMs = now;
            ApplySteamAccount(player, identity.SteamId, false);
        }

        // Links the player to the Steam account, or moves this PC over to the player the account already has.
        // proven: Steam confirmed the account in the browser; the player's key is renewed then, and every outcome is
        // answered. Otherwise the game reported the account: nothing is sent when the player is already linked to it.
        private SteamLinkOutcome ApplySteamAccount(Player player, ulong steamId, bool proven)
        {
            string hash = SteamHash(steamId);
            PlayerRecord me = player.Record;
            PlayerRecord owner = _store.FindBySteam(hash);

            if (owner == null)
            {
                if (me.SteamHash != null)
                    return Refuse(player, "This WKSR player is already linked to a different Steam account.");
                me.SteamHash = hash;
                _log("player " + me.Name + " linked a Steam account" + (proven ? "" : " (reported by their game)"));
                if (proven) return Grant(player, me, "Linked to your Steam account.");
                _store.MarkDirty(me);
                player.Send(new SteamLinkResult { Ok = true, Message = "Linked to your Steam account.", PlayerId = me.Id, Token = new byte[0], Name = me.Name, Rating = me.Rating, Wins = me.Wins, Losses = me.Losses });
                return new SteamLinkOutcome { Ok = true, Message = "Linked to your Steam account." };
            }
            if (owner == me)
            {
                if (!proven) return new SteamLinkOutcome { Ok = true, Message = "" };
                return Grant(player, me, "Signed in with Steam. Your WKSR key has been renewed.");
            }

            // The Steam account belongs to another WKSR player (a new PC, or a lost settings file). Move this PC over to
            // it, but only if the identity it uses now has nothing to lose.
            if (owner.Banned)
                return Refuse(player, "The WKSR player linked to this Steam account is banned from this server.");
            if (me.Wins + me.Losses > 0 || me.SteamHash != null)
                return Refuse(player, "This Steam account is linked to the WKSR player " + owner.Name + ", and this PC's player has ranked results of its own, so it is not replaced automatically.");
            if (player.Match != null || player.Queue != null || player.Lobby != null)
                return Refuse(player, "Finish your match, search or lobby first, then sign in with Steam again.");

            Player ownerOnline;
            if (_players.TryGetValue(owner.Id, out ownerOnline))
            {
                if (ownerOnline.Match != null)
                    return Refuse(player, owner.Name + " is in a match right now on another PC. Try again after it.");
                if (ownerOnline.Conn != null)
                {
                    IConnection elsewhere = ownerOnline.Conn;
                    _byConnection.Remove(elsewhere);
                    ownerOnline.Conn = null;
                    elsewhere.Send(new ErrorMessage(ErrorCode.BadToken, "Signed in with Steam on another PC."));
                    elsewhere.Close("restored elsewhere");
                }
                RemoveFromQueue(ownerOnline);
                LeaveLobby(ownerOnline, null);
                _players.Remove(owner.Id);
            }

            byte[] token = RandomNumberGenerator.GetBytes(ProtocolConstants.TokenLength);
            owner.TokenHash = PlayerStore.HashToken(token);
            _store.MarkDirty(owner);
            string message = "Welcome back, " + owner.Name + "! Your player was restored on this PC.";
            player.Send(Result(true, message, owner, token));

            // The empty identity this PC used until now is replaced; the client reconnects as the restored player.
            IConnection conn = player.Conn;
            _byConnection.Remove(conn);
            _players.Remove(me.Id);
            _store.Remove(me);
            conn.Close("restored another player");
            _log("player " + owner.Name + " restored on another PC with Steam (replaced the empty player " + me.Name + ")");
            return new SteamLinkOutcome { Ok = true, Message = message + " You can close this tab." };
        }

        private SteamLinkOutcome Grant(Player player, PlayerRecord record, string message)
        {
            byte[] token = RandomNumberGenerator.GetBytes(ProtocolConstants.TokenLength);
            record.TokenHash = PlayerStore.HashToken(token);
            _store.MarkDirty(record);
            player.Send(Result(true, message, record, token));
            return new SteamLinkOutcome { Ok = true, Message = message + " You can close this tab and return to WKSR." };
        }

        private static SteamLinkOutcome Refuse(Player player, string message)
        {
            player.Send(new SteamLinkResult { Ok = false, Message = message, PlayerId = player.Id });
            return new SteamLinkOutcome { Ok = false, Message = message };
        }

        private static SteamLinkOutcome Fail(string message)
        {
            return new SteamLinkOutcome { Ok = false, Message = message };
        }

        private static SteamLinkResult Result(bool ok, string message, PlayerRecord record, byte[] token)
        {
            return new SteamLinkResult
            {
                Ok = ok,
                Message = message,
                PlayerId = record.Id,
                Token = token,
                Name = record.Name,
                Rating = record.Rating,
                Wins = record.Wins,
                Losses = record.Losses,
            };
        }

        private void PruneSteamLinks(long now)
        {
            foreach (string old in _steamLinks.Where(p => p.Value.ExpiresMs <= now).Select(p => p.Key).ToList()) _steamLinks.Remove(old);
        }

        // HMAC-SHA256 of the Steam ID under a random secret kept in the (encrypted) database, so the column alone
        // does not name anyone's Steam account.
        private string SteamHash(ulong steamId)
        {
            if (_steamSecret == null)
            {
                ServerDatabase db = _store.Database;
                string stored = db.GetMeta("steam_secret");
                if (stored == null)
                {
                    stored = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                    db.SetMeta("steam_secret", stored);
                }
                _steamSecret = Convert.FromBase64String(stored);
            }
            using (var hmac = new HMACSHA256(_steamSecret))
            {
                return Convert.ToBase64String(hmac.ComputeHash(Encoding.ASCII.GetBytes("steam:" + steamId)));
            }
        }
    }
}
