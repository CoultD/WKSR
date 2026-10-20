using System;
using UnityEngine;
using WKRace.Protocol;

namespace WKRace.Mod
{
    // Spectating, in the game (see Spectator). Two ways in:
    //   - watching a match from outside (the online list, the lobby browser): a world is built from its seed, with its
    //     artifact bans, and the player watches in it as a spectator;
    //   - being out of one's own match (a death) while others still run: no death screen; the player becomes a
    //     spectator in their own world, following the leader. The server confirms (SpectateStart); if it does not (the
    //     match is over, or an older server), the death screen comes after all.
    internal sealed partial class RaceController
    {
        // How long a death waits for the server to confirm that there is something left to watch.
        private const float OwnSpectateWait = 4f;
        // How long the view stays on a player who went out before it moves to the leader.
        private const float HoldOnOutSeconds = 3f;
        // A watching from outside that ended: the world is left after this long.
        private const float LeaveAfterEndSeconds = 5f;

        private Spectator _spectator;
        // The spectating began with the player's own death in the match (it ends with the death screen).
        private bool _ownSpectating;
        // The view last used (kept for the next spectating).
        private static bool _firstPerson;
        // A death that may become spectating: until when the server's answer is waited for, and what killed the player.
        private float _ownSpectateUntil = -1f;
        private string _deathType = "";
        // When the player followed went out (to move on after a moment).
        private float _watchedOutAt = -1f;
        private int _spectators;
        private Guid _spectatorsFor;
        // The game's own death runs this once (after an own spectating that did not happen).
        internal static bool AllowDeath;

        internal Spectator Spectating { get { return _spectator; } }

        // ---- what happens in the players' worlds (RunEvents) ------------------------------------------------------

        // A racer reports what happens in their world while their run counts: a match (not watched by this player) or
        // a ranked solo run; never an unranked run, which nobody can watch.
        internal bool ReportsRunEvents
        {
            get
            {
                RaceSession s = _session;
                return s != null && !s.Spectating && _spectator == null && (!s.Practice || s.RankedSolo) && s.Phase == RacePhase.Racing
                       && RaceRules.InRaceWorld && _link != null && _link.Connected;
            }
        }

        internal void SendRunEvent(RunEvent e)
        {
            RaceSession s = _session;
            if (s == null) return;
            e.RaceTimeMs = Math.Max(0, s.RaceMs);
            _link.Send(e);
        }

        private float _nextReplay;

        private void OnOpponentEvent(OpponentEvent e)
        {
            RaceSession s = _session;
            if (s == null) return;
            if (e.Kind == RunEventKind.ItemUsed)
            {
                Rival who;
                if (!e.History && (_spectator != null || s.Spectating) && s.Rivals.TryGetValue(e.Racer, out who))
                    _hud.Feed(who.Name + " used " + e.Name);
                return;
            }
            s.PendingEvents.Add(e);
        }

        // A few times a second while spectating: what the followed player set off in their world, set off here too
        // (as soon as its level is loaded).
        private void ReplayEvents()
        {
            Spectator sp = _spectator;
            RaceSession s = _session;
            if (sp == null || s == null || s.PendingEvents.Count == 0 || Time.unscaledTime < _nextReplay) return;
            _nextReplay = Time.unscaledTime + 0.25f;
            for (int i = 0; i < s.PendingEvents.Count; i++)
            {
                OpponentEvent e = s.PendingEvents[i];
                if (e.Racer != sp.Racer) continue;
                bool done;
                try { done = RunEvents.Replay(e); }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning("Could not replay " + e.Name + ": " + ex.Message);
                    done = true;
                }
                if (!done) continue;
                if (RaceConfig.Verbose) Plugin.Log.LogDebug("Replayed " + e.Kind + " " + e.Name);
                s.PendingEvents.RemoveAt(i--);
            }
        }
        internal RacePhase SessionPhase { get { return _session != null ? _session.Phase : RacePhase.Done; } }

        // How many watch this player's match or run (0 when none, or another match's count).
        internal int SpectatorsWatching
        {
            get { return _session != null && _session.MatchId == _spectatorsFor ? _spectators : 0; }
        }

        private void OnSpectatorCount(SpectatorCount count)
        {
            _spectators = count.Count;
            _spectatorsFor = count.MatchId;
        }

        private void OnSpectateStart(SpectateStart start)
        {
            RaceSession s = _session;
            // Out of one's own match: the same world, now as a spectator.
            if (s != null && s.MatchId == start.MatchId && !s.Spectating)
            {
                if (_spectator != null) return;
                _ownSpectateUntil = -1f;
                _ownSpectating = true;
                _spectator = new Spectator(s, start.WatchRacer, _firstPerson);
                _spectator.Enter();
                _watchedOutAt = -1f;
                _hud.Toast("Spectating the rest of the match. Left / right click switch player, " + RaceConfig.SpectateViewKey.Value + " switches the view.", 7f);
                Plugin.Log.LogInfo("Spectating the rest of the match, following " + _spectator.WatchedName + ".");
                return;
            }
            if (s != null && s.Spectating && s.MatchId == start.MatchId) return;

            // A match watched from outside (or the next run of a ranked solo run being watched): its own world.
            if (_spectator != null) _spectator.Exit();
            _spectator = null;
            DestroyGhosts(s);
            var found = new MatchFound
            {
                MatchId = start.MatchId,
                Seed = start.Seed,
                Competitive = start.Competitive,
                Ranked = start.Ranked,
                Rules = start.Rules,
                Mode = start.Mode,
                YourRacer = ServerSpectatorRacer,
                PlayerCount = 0,
                Segment = start.Segment,
            };
            _session = new RaceSession(found) { Spectating = true, WatchStart = start.WatchRacer };
            MenuUi.Close();
            RaceGuarantees.Rules = start.Rules;
            ArtifactBans.Clear();
            ArtifactBans.Set(start.Bans);
            GameBridge.ClearFreezeFlags();
            _session.Phase = RacePhase.Starting;
            _session.AutoStartPending = false;
            Plugin.Log.LogInfo("Spectating match " + start.MatchId.ToString("N").Substring(0, 8) + ", seed " + start.Seed + ", rules " + start.Rules);
            if (!StartRun()) _hud.Toast("Could not find the Campaign gamemode.", Hud.DeltaColor(1), 5f);
        }

        // The racer number the server gives a spectator (none of the match's).
        private const byte ServerSpectatorRacer = 255;

        private void OnSpectateEnd(SpectateEnd end)
        {
            RaceSession s = _session;
            if (_ownSpectateUntil >= 0f && s != null && !s.Spectating)
            {
                // Nothing left to watch: the death after all.
                RunDeferredDeath();
                return;
            }
            if (_spectator == null && (s == null || !s.Spectating)) return;
            if (_spectator != null) _spectator.Exit();
            _spectator = null;
            _spectators = 0;
            if (!string.IsNullOrEmpty(end.Reason)) _hud.Toast(end.Reason, 6f);
            Plugin.Log.LogInfo("Spectating ended: " + end.Reason);
            if (_ownSpectating)
            {
                // Out of one's own match, and now it is over: the game's death screen ends the run as usual.
                _ownSpectating = false;
                RunDeferredDeath();
                return;
            }
            if (s != null && s.Spectating)
            {
                s.Phase = RacePhase.Done;
                s.ResultClosed = true;
                s.DoneUntil = Time.unscaledTime + LeaveAfterEndSeconds;
            }
        }

        // The spectator's world is ready: in they go (no ready check, nothing reported).
        private void BeginWatching(RaceSession s)
        {
            _ownSpectating = false;
            s.Phase = RacePhase.Spectating;
            _spectator = new Spectator(s, s.WatchStart, _firstPerson);
            _spectator.Enter();
            _watchedOutAt = -1f;
            _hud.Flash("SPECTATING", "", Hud.DeltaColor(0));
        }

        // Every frame while spectating: move on from a player who went out, after a moment.
        private void UpdateSpectator()
        {
            Spectator sp = _spectator;
            if (sp == null) return;
            Rival watched = sp.Watched;
            bool gone = watched == null || watched.IsOut || watched.Finished;
            if (!gone) _watchedOutAt = -1f;
            else if (_watchedOutAt < 0f) _watchedOutAt = Time.unscaledTime;
            else if (Time.unscaledTime - _watchedOutAt >= HoldOnOutSeconds)
            {
                sp.FollowLeaderIfOut();
                _watchedOutAt = -1f;
            }
            ReplayEvents();
            if (MenuUi.IsOpen || GameBridge.GamePaused || ChatInputBlock.Active) return;
            if (Input.GetMouseButtonDown(0)) sp.Next(1);
            if (Input.GetMouseButtonDown(1)) sp.Next(-1);
            if (Input.GetKeyDown(RaceConfig.SpectateViewKey.Value))
            {
                sp.ToggleView();
                _firstPerson = sp.FirstPerson;
            }
        }

        // ---- a death that becomes spectating -------------------------------------------------------------------

        // Called instead of the game's death when there may be something left to watch: a 1v1 or a party in which
        // someone else still runs. The death is reported as usual; the death screen waits for the server.
        internal bool DeathBecomesSpectating(string type)
        {
            RaceSession s = _session;
            if (AllowDeath || s == null || s.Spectating || s.Practice || s.Phase != RacePhase.Racing || !RaceRules.InRaceWorld || !_link.Connected) return false;
            bool someone = false;
            foreach (Rival r in s.Rivals.Values)
                if (!r.IsOut && !r.Finished) someone = true;
            if (!someone) return false;
            _deathType = type ?? "";
            OnLocalDeath();
            _ownSpectateUntil = Time.unscaledTime + OwnSpectateWait;
            _link.Send(new SpectateRequest { OwnMatch = s.MatchId });
            return true;
        }

        private void UpdateOwnSpectateWait()
        {
            if (_ownSpectateUntil < 0f || Time.unscaledTime < _ownSpectateUntil) return;
            RunDeferredDeath();
        }

        // The game's own death, after all: the death screen and the end of the run.
        private void RunDeferredDeath()
        {
            _ownSpectateUntil = -1f;
            CL_GameManager game = CL_GameManager.gMan;
            if (game == null) return;
            AllowDeath = true;
            try { game.Die(_deathType); }
            catch (Exception e) { Plugin.Log.LogWarning("The death screen could not be shown: " + e.Message); }
            finally { AllowDeath = false; }
        }
    }
}
