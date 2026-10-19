using System;
using System.Collections.Generic;
using System.Linq;
using WKRace.Protocol;

namespace WKRace.Server
{
    // Artifact bans, run while a match is Preparing and before either game loads its world. Both mods report the
    // artifacts their game's artifact rooms can offer; then the racers take one turn each (the race seed decides who
    // goes first) to ban one, seeing the other's ban. A turn that runs out passes without a ban.
    public sealed partial class ServerCore
    {
        private const int TurnsPerMatch = 2;

        private void HandleBanReady(Player player, BanReady ready)
        {
            Match match = player.Match;
            if (match == null || match.Id != ready.MatchId || !match.ArtifactBans || match.Phase != MatchPhase.Preparing) return;
            Slot slot = match.SlotOf(player);
            // A game that restarted asks again: just tell it where things stand.
            if (match.BansDone || match.BanPool != null || slot.BanPool != null)
            {
                SendBanState(match, slot);
                return;
            }

            slot.BanPool = CleanPool(ready.Pool);
            if (match.Other(slot).BanPool == null)
            {
                SendBanState(match, slot);
                return;
            }

            // Both games run the same build, so their pools agree; keep what both offer, in the first player's order.
            match.BanPool = match.A.BanPool.Where(item => match.B.BanPool.Contains(item)).ToList();
            if (match.BanPool.Count == 0)
            {
                FinishBans(match);
                return;
            }
            match.BanTurn = (match.Seed & 1) == 0 ? match.A : match.B;
            match.BanDeadlineMs = _now() + _options.BanTurnSeconds * 1000L;
            _log("match " + Short(match) + ": bans open, " + match.BanTurn.Name + " first");
            BroadcastBans(match);
        }

        private void HandleBanPick(Player player, BanPick pick)
        {
            Match match = player.Match;
            if (match == null || match.Id != pick.MatchId || match.BansDone || match.BanPool == null) return;
            Slot slot = match.SlotOf(player);
            string item = pick.Item ?? "";
            if (match.BanTurn != slot || (item.Length > 0 && (!match.BanPool.Contains(item) || match.Banned.Contains(item))))
            {
                // Out of turn or not a valid choice: show the player the real state again.
                SendBanState(match, slot);
                return;
            }

            if (item.Length > 0)
            {
                match.Banned.Add(item);
                match.BannedBy.Add(slot.Name);
                _log("match " + Short(match) + ": " + slot.Name + " banned " + item);
            }
            else
            {
                _log("match " + Short(match) + ": " + slot.Name + " passed");
            }
            NextBanTurn(match);
        }

        private void NextBanTurn(Match match)
        {
            match.BanTurnsTaken++;
            if (match.BanTurnsTaken >= TurnsPerMatch || match.Banned.Count >= match.BanPool.Count)
            {
                FinishBans(match);
                return;
            }
            match.BanTurn = match.Other(match.BanTurn);
            match.BanDeadlineMs = _now() + _options.BanTurnSeconds * 1000L;
            BroadcastBans(match);
        }

        private void FinishBans(Match match)
        {
            match.BansDone = true;
            match.BanTurn = null;
            // The games load their worlds now; the loading timeout starts here.
            match.PrepareSinceMs = _now();
            BroadcastBans(match);
        }

        // Called while Preparing: a turn that runs out passes.
        private void TickBans(Match match, long now)
        {
            if (match.BansDone || match.BanTurn == null || now < match.BanDeadlineMs) return;
            _log("match " + Short(match) + ": " + match.BanTurn.Name + " ran out of time to ban");
            NextBanTurn(match);
        }

        private void BroadcastBans(Match match)
        {
            SendBanState(match, match.A);
            SendBanState(match, match.B);
        }

        private void SendBanState(Match match, Slot to)
        {
            long left = match.BanTurn == null ? 0 : Math.Max(0, match.BanDeadlineMs - _now());
            to.Player.Send(new BanState
            {
                MatchId = match.Id,
                Pool = match.BanPool != null ? new List<string>(match.BanPool) : new List<string>(),
                Banned = new List<string>(match.Banned),
                BannedBy = new List<string>(match.BannedBy),
                TurnName = match.BanTurn != null ? match.BanTurn.Name : "",
                YourTurn = match.BanTurn == to,
                SecondsLeft = (int)((left + 999) / 1000),
                Done = match.BansDone,
            });
        }

        // Item names are prefab names: letters, digits and underscores. Anything else is dropped.
        private static List<string> CleanPool(List<string> pool)
        {
            var clean = new List<string>();
            if (pool == null) return clean;
            foreach (string item in pool)
            {
                if (clean.Count >= ProtocolConstants.MaxBanPool) break;
                if (string.IsNullOrEmpty(item) || item.Length > ProtocolConstants.MaxItemNameLength || clean.Contains(item)) continue;
                if (item.All(c => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_')) clean.Add(item);
            }
            return clean;
        }
    }
}
