using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using WKRace.Protocol;

namespace WKRace.Mod
{
    using Message = WKRace.Protocol.Message;

    // Drives a match from the game thread: reacts to the client, the game and the hotkeys, and feeds the HUD and ghost.
    internal sealed class RaceController : MonoBehaviour
    {
        private const float StateInterval = 1f / 15f;
        private const float RetryInterval = 2f;
        private const float ConfirmWindow = 3f;
        private const float ResultSeconds = 25f;

        public static RaceController Current { get; private set; }

        private IModLink _link;
        private RaceSession _session;
        private ClientStatus _clientStatus;
        private const float RankingInterval = 0.25f;
        private const float GapSeconds = 6f;

        private readonly Hud _hud = new Hud();
        private readonly Hud _chatHud = new Hud(true);
        private readonly ChatBox _chat = new ChatBox();
        private readonly PlayerState _stateMessage = new PlayerState();
        private float _nextRanking;
        private float _nextState;
        private float _nextRetry;
        private float _nextAutoStart;
        private float _joinConfirmUntil;
        private float _forfeitConfirmUntil;
        // From WKSR's PracticeMark: the next MatchFound with this id is a practice run.
        private PracticeMark _practiceMark;
        // The player restarted a practice run: the fresh run WKSR sends next starts at once.
        private bool _practiceRestartPending;
        private bool _linkWasUp;
        private string _ghostColorText;
        private Color _ghostColor;
        private readonly List<KeyValuePair<float, string>> _shots = new List<KeyValuePair<float, string>>();

        private void Awake()
        {
            Current = this;
        }

        private void Start()
        {
            // WKSR runs inside the game.
            _link = new EmbeddedLink(PluginInfo.Version, GameBridge.GameVersion());
            _link.Start();
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (_link != null) _link.Stop();
            DestroyGhosts(_session);
            _hud.Destroy();
            _chatHud.Destroy();
            _chat.Close();
            GameBridge.ClearFreezeFlags();
            RaceRules.End();
            if (Current == this) Current = null;
        }

        // ---- frame loop ------------------------------------------------

        private void Update()
        {
            if (_link == null) return;
            EmbeddedClient.Update();

            bool up = _link.Connected;
            if (up != _linkWasUp)
            {
                _linkWasUp = up;
                if (!up)
                {
                    _clientStatus = null;
                    OnLinkLost();
                }
                else
                {
                    // The other mods in this game, for the match server's list of approved mods.
                    List<ModEntry> mods = ModList.Loaded();
                    foreach (LoadedMods part in LoadedMods.Split(mods)) _link.Send(part);
                    if (mods.Count > 0) Plugin.Log.LogInfo("Reported " + mods.Count + (mods.Count == 1 ? " other mod" : " other mods") + " to WKSR.");
                }
            }

            Message message;
            for (int budget = 256; budget > 0 && _link.TryReceive(out message); budget--)
            {
                try { Handle(message); }
                catch (Exception e) { Plugin.Log.LogError("Failed to handle " + message.Type + ": " + e); }
            }

            Perf.Frame();
            long started = Perf.Start();
            UpdateChat();
            HandleHotkeys();
            if (_session != null) UpdateSession(_session);
            RaceGuarantees.Update();
            InventoryLayout.Update();
            MenuUi.Update();
            UpdateGhost();
            UpdateShots();
            Perf.Update(started);
#if DEBUG
            DebugAutoAdvance();
#endif
        }

#if DEBUG
        private float _nextAdvance;
        private int _advances;

        // Debug builds only: walks the player up through levels so splits can be tested without playing.
        private void DebugAutoAdvance()
        {
            RaceSession s = _session;
            SpawnSeeding.Enabled = RaceConfig.DebugSeedLevelContents.Value;
            if (RaceRules.InRaceWorld)
            {
                for (int i = RaceConfig.DebugPerturbRandom.Value; i > 0; i--) UnityEngine.Random.value.GetHashCode();
            }
            DebugDump.Update();
            ReconDump.Update();

            if (!RaceConfig.DebugAutoAdvance.Value || s == null || s.Phase != RacePhase.Racing || s.Held || WorldLoader.instance == null) return;
            if (_nextAdvance == 0f) _nextAdvance = Time.unscaledTime + 8f;
            if (Time.unscaledTime < _nextAdvance || _advances >= RaceConfig.DebugAutoAdvanceLevels.Value) return;
            _nextAdvance = Time.unscaledTime + RaceConfig.DebugAutoAdvanceSeconds.Value;
            _advances++;
            WorldLoader.instance.TeleportPlayerToNextLevel(new string[0]);
        }
#endif

        private void Shot(string tag, float delay)
        {
            if (RaceConfig.AutoScreenshots.Value) _shots.Add(new KeyValuePair<float, string>(Time.unscaledTime + delay, tag));
        }

        private void UpdateShots()
        {
            for (int i = _shots.Count - 1; i >= 0; i--)
            {
                if (Time.unscaledTime < _shots[i].Key) continue;
                string dir = Path.Combine(Paths.BepInExRootPath, "WKRace-screenshots");
                Directory.CreateDirectory(dir);
                ScreenCapture.CaptureScreenshot(Path.Combine(dir, DateTime.Now.ToString("HHmmss") + "-" + _shots[i].Value + ".png"));
                _shots.RemoveAt(i);
            }
        }

        // After Update, so the ghost has moved before its marker is placed.
        private void LateUpdate()
        {
            long started = Perf.Start();
            _hud.Draw(_session, _chat, _clientStatus, _link != null && _link.Connected, GameBridge.InMenu, VersionMismatch());
            _chatHud.Draw(null, _chat, null, false, false, null);
            Perf.Hud(started);
        }

        private void UpdateSession(RaceSession s)
        {
            if (RaceRules.InRaceWorld && !s.Cheated && CommandConsole.hasCheated && s.Phase != RacePhase.Done && !RaceConfig.CheatsAllowed)
            {
                s.Cheated = true;
                _hud.Toast("Cheats detected: this run will be disqualified.", Hud.DeltaColor(1), 8f);
            }

            if (s.Held)
            {
                UpdateHold(s);
                // Frozen: nothing may start or move on, but a loading world must still be picked up (and frozen),
                // and a finish must still reach the server.
                if (s.Held && (s.Phase == RacePhase.Starting || s.Phase == RacePhase.Countdown || s.Phase == RacePhase.Racing)) return;
            }

            switch (s.Phase)
            {
                case RacePhase.Banning:
                    UpdateBans(s);
                    break;

                case RacePhase.Starting:
                    if (s.AutoStartPending && RaceConfig.AutoStartFromMenu.Value && GameBridge.InMenu && _link.Connected && Time.unscaledTime >= _nextAutoStart)
                    {
                        _nextAutoStart = Time.unscaledTime + 1f;
                        if (StartRun()) s.AutoStartPending = false;
                    }
                    break;

                case RacePhase.Loading:
                    if (s.WorldInitialized && GameBridge.IsWorldReady()) OnWorldReady(s);
                    break;

                case RacePhase.Waiting:
                    if (Time.unscaledTime >= _nextRetry)
                    {
                        _nextRetry = Time.unscaledTime + RetryInterval;
                        SendReady(s);
                        if (s.ReadyPressed && !s.ReadyConfirmed) _link.Send(new PlayerReady { MatchId = s.MatchId });
                    }
                    break;

                case RacePhase.Countdown:
                    if (s.RaceMs >= 0) GoNow(s);
                    break;

                case RacePhase.Racing:
                    // Pausing the game stops its timer. Solo that is the rule (as in the unmodded game); in a match
                    // everyone else keeps climbing, so there the clock keeps running through a pause.
                    if (!s.Practice && s.OnGameClock && RaceRules.InRaceWorld && GameBridge.GamePaused) GameBridge.AddPausedTime(Time.unscaledDeltaTime);
                    // The start split can be missing if the level was not resolved yet when the attempt began.
                    if (s.MySplits.Count == 0)
                    {
                        M_Level start = CurrentLevel();
                        if (start != null) AddSplit(s, start, true);
                    }
                    SendState(s);
                    if (s.Party && Time.unscaledTime >= _nextRanking)
                    {
                        _nextRanking = Time.unscaledTime + RankingInterval;
                        UpdateRanking(s);
                    }
                    break;

                case RacePhase.Finished:
                    if (Time.unscaledTime >= _nextRetry)
                    {
                        _nextRetry = Time.unscaledTime + RetryInterval;
                        if (s.Died) SendDied(s);
                        else SendFinish(s);
                    }
                    break;

                case RacePhase.Done:
                    if (Time.unscaledTime >= s.DoneUntil)
                    {
                        // A practice session stays (without its result panel) so that restarting in this world still
                        // means a fresh practice run. It ends when the player leaves for the menu or starts another.
                        if (s.Practice) s.ResultClosed = true;
                        else EndSession();
                    }
                    break;
            }
        }

        private void UpdateGhost()
        {
            if (_ghostColorText != RaceConfig.GhostColor.Value)
            {
                _ghostColorText = RaceConfig.GhostColor.Value;
                _ghostColor = RaceConfig.ParseGhostColor();
            }
            RaceSession s = _session;
            if (s == null) return;
            bool show = RaceConfig.ShowGhost.Value && s.Phase == RacePhase.Racing && RaceRules.InRaceWorld;
            ENT_Player player = ENT_Player.playerObject;
            foreach (Rival rival in s.Rivals.Values)
                rival.Ghost.Tick(show && !rival.IsOut, _ghostColor, RaceConfig.GhostOpacity.Value, player != null ? player.cam : null);
        }

        private static void ClearGhosts(RaceSession s)
        {
            if (s == null) return;
            foreach (Rival rival in s.Rivals.Values) rival.Ghost.Clear();
        }

        private static void DestroyGhosts(RaceSession s)
        {
            if (s == null) return;
            foreach (Rival rival in s.Rivals.Values) rival.Ghost.Destroy();
        }

        // Party races: where everyone stands, and who the split list compares with (the racer just ahead, or just
        // behind for the leader).
        private void UpdateRanking(RaceSession s)
        {
            List<RacerProgress> ranking = s.Ranking();
            int place = RaceSession.PlaceOf(ranking, s.YourRacer);
            _hud.SetPlace(place, ranking.Count, s.MyProgress().OnShortcut);
            int compare = place >= 2 ? place - 2 : 1;
            s.Compare = compare < ranking.Count && ranking[compare].Racer != s.YourRacer ? s.RivalFor((byte)ranking[compare].Racer) : null;
        }

        // ---- chat ------------------------------------------------------------

        private void UpdateChat()
        {
            string line = _chat.Update(_chatHud.VirtualWidth, _chatHud.VirtualHeight);
            // Enter in a text box must not press the menu button that happens to be highlighted.
            MenuQuiet.Update(_chat.Open || (MenuUi.IsOpen && GameBridge.InMenu));
            if (line == null) return;
            if (_link == null || !_link.Connected)
            {
                _hud.Toast("WKSR is not running, so chat cannot be sent.", 4f);
                return;
            }
            // WKSR sends it to the room that is selected in the chat.
            _link.Send(new ChatSend { Text = line });
        }

        private void OnModSettings(ModSettings settings)
        {
            float opacity = Mathf.Clamp01(settings.GhostOpacityPercent / 100f);
            if (Mathf.Abs(RaceConfig.GhostOpacity.Value - opacity) > 0.001f) RaceConfig.GhostOpacity.Value = opacity;
            if (settings.Keys != null) RaceConfig.ApplyKeys(settings.Keys);
        }

        // The roster: names and status of everyone in the match, and the ready check's clock.
        private void OnRoster(MatchRoster roster)
        {
            RaceSession s = _session;
            if (s == null || s.MatchId != roster.MatchId) return;
            s.Roster = roster;
            s.ReadyDeadline = roster.ReadySecondsLeft >= 0 ? Time.unscaledTime + roster.ReadySecondsLeft : -1f;
            foreach (RosterEntry e in roster.Racers)
            {
                if (e.Racer == s.YourRacer)
                {
                    if (e.Status == RacerStatus.Ready || e.Status == RacerStatus.Racing) s.ReadyConfirmed = true;
                    continue;
                }
                Rival rival = s.RivalFor(e.Racer);
                bool wasOut = rival.IsOut;
                rival.Name = e.Name;
                rival.Rating = e.Rating;
                rival.Status = e.Status;
                if (e.Status == RacerStatus.Out || e.Status == RacerStatus.Left) rival.OutMs = e.TimeMs;
                if (e.Status == RacerStatus.Finished && !rival.Finished)
                {
                    rival.Finished = true;
                    rival.FinishMs = e.TimeMs;
                }
                if (!wasOut && rival.IsOut) rival.Ghost.Clear();
            }
        }

        // ---- messages from the client -----------------------------------

        private void Handle(Message message)
        {
            switch (message)
            {
                case ClientStatus status: _clientStatus = status; break;
                case MatchFound found: OnMatchFound(found); break;
                case StartCountdown countdown: OnStartCountdown(countdown); break;
                case OpponentState state: OnOpponentState(state); break;
                case OpponentSplit split: OnOpponentSplit(split); break;
                case OpponentAttempt attempt: OnOpponentAttempt(attempt); break;
                case OpponentFinished finished: OnOpponentFinished(finished); break;
                case RacePaused paused: OnRacePaused(paused); break;
                case RaceResumed resumed: OnRaceResumed(resumed); break;
                case MatchResult result: OnMatchResult(result); break;
                case BanState bans: OnBanState(bans); break;
                case PracticeMark mark: _practiceMark = mark; break;
                case MatchRoster roster: OnRoster(roster); break;
                case ChatMessage _: break;   // the chat reads WKSR's rooms itself
                case ModSettings settings: OnModSettings(settings); break;
                case VoiceActivity voice: _hud.SetVoice(voice); break;
                case ErrorMessage error: _hud.Toast(error.Text, Hud.DeltaColor(1), 6f); break;
            }
        }

        private void OnMatchFound(MatchFound found)
        {
            if (_session != null && _session.MatchId == found.MatchId)
            {
                // The client or server reconnected: they may have missed anything we sent meanwhile, so send our run again.
                if (!_session.Party)
                {
                    _session.OpponentName = found.OpponentName;
                    _session.OpponentRating = found.OpponentRating;
                }
                if (_session.ReadyPressed) _session.ReadyConfirmed = false;
                ReplayRun(_session);
                return;
            }

            DestroyGhosts(_session);
            _session = new RaceSession(found);
            // A match or run begins: the WKSR window has done its part and makes room for what the game shows next
            // (the artifact bans, then the world loading).
            MenuUi.Close();
            RaceGuarantees.Rules = found.Rules;
            PracticeMark mark = _practiceMark;
            if (mark != null && found.MatchId == mark.MatchId)
            {
                _session.Practice = true;
                _session.RankedSolo = mark.Ranked;
                _session.PracticeBestMs = mark.BestTimeMs;
                _session.PracticeBestFinished = mark.BestFinished;
                _session.PracticeBestLevels = mark.BestLevels;
            }
            ArtifactBans.Clear();
            GameBridge.ClearFreezeFlags();
            M_Gamemode campaign = GameBridge.FindCampaign();
            if (campaign != null) RaceRules.Begin(campaign, found.Seed, found.Competitive);
            if (_session.Segment != Segments.None)
                _hud.Toast(Segments.Find(_session.Segment).Title + " segment on seed " + found.Seed + (_session.RankedSolo ? ", for the leaderboard." : ", unranked.") + " The clock stops at the end of the segment.", 7f);
            else if (_session.RankedSolo)
                _hud.Toast("Ranked solo run on seed " + found.Seed + ".", 6f);
            else if (_session.Practice)
                _hud.Toast("Unranked run on seed " + found.Seed + (_session.PracticeBestMs >= 0 ? ", against your best (" + _session.PracticeBestText.ToLowerInvariant() + ")" : ""), 6f);
            else if (found.Party)
                _hud.Toast("Party match with " + found.PlayerCount + " players. " + RulesText.Describe(found.Rules) + ".", 6f);
            else
                _hud.Toast((found.Ranked ? "Match found vs " : "Friendly match vs ") + found.OpponentName + " (" + found.OpponentRating + "). " + RulesText.Describe(found.Rules) + ".", 6f);
            Plugin.Log.LogInfo((_session.RankedSolo ? "Ranked solo run " : _session.Practice ? "Unranked solo run " : "Match ") + found.MatchId.ToString("N").Substring(0, 8) + " " + (found.Party ? "party of " + found.PlayerCount : "vs " + found.OpponentName)
                + ", seed " + found.Seed + ", rules " + found.Rules + (found.ResumeElapsedMs >= 0 ? " (resumed)" : ""));

            // A restarted practice run goes straight back into a fresh world (after its bans, which WKSR repeats).
            if (!_session.Practice) _practiceRestartPending = false;
            else if (_practiceRestartPending && !found.ArtifactBans) StartRestartedRun();
        }

        private void StartRestartedRun()
        {
            _practiceRestartPending = false;
            if (!StartRun()) _hud.Toast("Could not find the Campaign gamemode.", Hud.DeltaColor(1), 5f);
        }

        private void OnStartCountdown(StartCountdown countdown)
        {
            RaceSession s = _session;
            if (s == null || s.MatchId != countdown.MatchId) return;
            s.SetGoIn(countdown.MsUntilGo);
            if (s.Phase == RacePhase.Waiting)
            {
                s.Phase = RacePhase.Countdown;
                Shot("countdown", 1.5f);
            }
        }

        // The racer an Opponent* message is about, or null for one about ourselves (never expected).
        private static Rival RivalOf(RaceSession s, byte racer)
        {
            if (s == null || (racer == s.YourRacer && s.Party)) return null;
            if (!s.Party) return s.Primary;
            return s.RivalFor(racer);
        }

        private void OnOpponentState(OpponentState state)
        {
            Rival rival = RivalOf(_session, state.Racer);
            if (rival == null || state.Attempt < rival.Attempt) return;
            if (state.Attempt > rival.Attempt) rival.Reset(state.Attempt);
            rival.Last = state;
            rival.LastTime = Time.unscaledTime;
            rival.Ghost.SetTarget(state);
        }

        private void OnOpponentSplit(OpponentSplit split)
        {
            RaceSession s = _session;
            Rival rival = RivalOf(s, split.Racer);
            if (rival == null || split.Attempt < rival.Attempt) return;
            if (split.Attempt > rival.Attempt) rival.Reset(split.Attempt);

            SplitRecord known = rival.Find(split.Index);
            var record = new SplitRecord { Index = split.Index, Level = split.Level, Region = split.Region, TimeMs = split.RaceTimeMs };
            rival.SetSplit(record);
            if (known != null && known.TimeMs == split.RaceTimeMs) return;
            if (s.Phase != RacePhase.Racing || split.Index <= 0) return;
            string key = rival.KeyOf(record);
            if (s.Party)
            {
                // The leader sees how far back the racer in second is, as they reach each level the leader passed.
                if (Time.unscaledTime >= _nextRanking) { _nextRanking = Time.unscaledTime + RankingInterval; UpdateRanking(s); }
                SplitRecord mine = FindMine(s, key);
                if (mine != null && _hud.CurrentPlace == 1 && s.Compare == rival)
                    _hud.Gaps(new[] { new Hud.Gap(rival.Name + " (2nd)", split.RaceTimeMs - mine.TimeMs, true) }, GapSeconds);
                return;
            }
            // Announce levels the opponent reached that we have not (ahead of us, or on another route).
            if (!s.HaveReached(key))
            {
                _hud.Toast(rival.Name + " reached " + Labels.Level(split.Level) + " at " + RaceFormat.Time(split.RaceTimeMs), 4f);
            }
        }

        private static SplitRecord FindMine(RaceSession s, string key)
        {
            foreach (SplitRecord m in s.MySplits) if (m.Key == key) return m;
            return null;
        }

        private void OnOpponentAttempt(OpponentAttempt attempt)
        {
            RaceSession s = _session;
            Rival rival = RivalOf(s, attempt.Racer);
            if (rival == null || attempt.Attempt <= rival.Attempt) return;
            rival.Reset(attempt.Attempt);
            rival.Ghost.Clear();
            _hud.Toast(rival.Name + " reset (attempt " + attempt.Attempt + ")", 5f);
        }

        private void OnOpponentFinished(OpponentFinished finished)
        {
            RaceSession s = _session;
            Rival rival = RivalOf(s, finished.Racer);
            if (rival == null || rival.Finished) return;
            rival.Finished = true;
            rival.FinishMs = finished.RaceTimeMs;
            rival.Status = RacerStatus.Finished;
            if (s.Party)
            {
                int place = 0;
                foreach (Rival r in s.Rivals.Values) if (r.Finished) place++;
                if (s.MyFinishMs >= 0 && !s.Died && (s.Phase == RacePhase.Finished || s.Phase == RacePhase.Done)) place++;
                _hud.Toast(rival.Name + " finished " + Standings.Ordinal(place) + " in " + RaceFormat.Time(finished.RaceTimeMs), 6f);
                return;
            }
            _hud.Toast(rival.Name + " finished in " + RaceFormat.Time(finished.RaceTimeMs), Hud.DeltaColor(1), 8f);
        }

        // ---- pausing -------------------------------------------------------

        private void OnRacePaused(RacePaused paused)
        {
            RaceSession s = _session;
            if (s == null || s.MatchId != paused.MatchId || !s.IsActive) return;
            bool first = !s.Held;
            s.Hold(paused.Reason, paused.SecondsLeft, paused.RaceTimeMs, paused.RaceTimeMs != -1);
            ApplyHold(s);
            if (first)
            {
                Plugin.Log.LogInfo("Match paused: " + paused.Reason + " (" + paused.SecondsLeft + " s left)");
                Shot("paused", 1f);
            }
        }

        private void OnRaceResumed(RaceResumed resumed)
        {
            RaceSession s = _session;
            if (s == null || s.MatchId != resumed.MatchId || !s.IsActive) return;
            if (!s.Held)
            {
                // Not frozen here (nothing was missed); just keep the clock in step with the server.
                if (resumed.MsUntilResume <= 0) s.SetGoIn(resumed.MsUntilGo);
                return;
            }
            s.ScheduleResume(resumed.MsUntilGo, resumed.MsUntilResume);
            Plugin.Log.LogInfo("Match resumes in " + resumed.MsUntilResume + " ms at " + RaceFormat.Time(Math.Max(0, s.HeldRaceMs)));
            Shot("resuming", 0.5f);
        }

        // Without the client nothing reaches the server, so freeze rather than race on unseen. The client unfreezes us when it is back.
        private void OnLinkLost()
        {
            RaceSession s = _session;
            if (s == null || !s.IsActive || !s.HasGo || s.Phase == RacePhase.Finished) return;
            s.Hold("Lost connection to the WKSR client", s.GraceSeconds, 0, false);
            ApplyHold(s);
            Plugin.Log.LogInfo("Match paused: lost the WKSR client.");
        }

        private void UpdateHold(RaceSession s)
        {
            ApplyHold(s);
            if (!s.TryRelease()) return;
            Plugin.Log.LogInfo("Match resumed at " + RaceFormat.Time(Math.Max(0, s.RaceMs)));
            if (s.Phase == RacePhase.Racing && RaceRules.InRaceWorld) GameBridge.ThawWorld();
        }

        // Keeps the race world frozen for as long as the session is held. Menus are never frozen.
        private static void ApplyHold(RaceSession s)
        {
            if (!s.Held || !RaceRules.InRaceWorld || !GameBridge.IsWorldReady()) return;
            if (s.Phase == RacePhase.Racing || s.Phase == RacePhase.Countdown || s.Phase == RacePhase.Waiting) GameBridge.FreezeWorld();
        }

        // Sends our side of the run again after a reconnect. The server ignores anything it already has.
        private void ReplayRun(RaceSession s)
        {
            if (!s.AttemptBegun || (s.Phase != RacePhase.Racing && s.Phase != RacePhase.Finished)) return;
            if (s.Attempt > 1) _link.Send(new AttemptStarted { Attempt = s.Attempt, RaceTimeMs = s.AttemptStartMs });
            foreach (SplitRecord split in s.MySplits)
            {
                _link.Send(new SplitReached { Attempt = s.Attempt, Index = split.Index, Level = split.Level, Region = split.Region, RaceTimeMs = split.TimeMs });
            }
            if (RaceConfig.Verbose) Plugin.Log.LogDebug("Replayed attempt " + s.Attempt + " with " + s.MySplits.Count + " splits.");
        }

        private void OnMatchResult(MatchResult result)
        {
            RaceSession s = _session;
            if (s == null || s.MatchId != result.MatchId) return;
            s.EndRaceMs = Math.Max(0, s.RaceMs);
            s.Result = result;
            s.Phase = RacePhase.Done;
            s.Held = false;
            s.ResumePending = false;
            s.DoneUntil = Time.unscaledTime + ResultSeconds;
            GameBridge.ThawWorld();
            ClearGhosts(s);
            Shot("result", 1f);
            Plugin.Log.LogInfo("Match over: " + result.Outcome + " - " + result.Reason);
        }

        // ---- run lifecycle ----------------------------------------------

        private bool StartRun()
        {
            RaceSession s = _session;
            M_Gamemode campaign = GameBridge.FindCampaign();
            if (s == null || campaign == null) return false;

            RaceRules.Begin(campaign, s.Seed, s.Competitive);
            RaceGuarantees.Rules = s.Rules;
            SetStatic(typeof(CL_SaveManager), "isSavedRun", false);
            SetStatic(typeof(CL_SaveManager), "loadSessionSaveOnLoad", false);
            GameBridge.ClearFreezeFlags();
            s.WorldInitialized = false;
            s.Phase = RacePhase.Loading;
            GameBridge.BeginSeededRun(campaign, s.Seed);
            return true;
        }

        // Called from WorldLoader.Initialize, before the world is generated.
        public void OnWorldInitializing()
        {
            RaceRules.InRaceWorld = false;
            RaceSession s = _session;
            if (s == null || s.Phase == RacePhase.Done)
            {
                RaceRules.End();
                return;
            }

            M_Gamemode mode = CL_GameManager.gamemode;
            if (mode == null || !string.Equals(mode.gamemodeName, GameBridge.CampaignModeName, StringComparison.OrdinalIgnoreCase))
            {
                _hud.Toast("Matches are played on the Campaign gamemode.", Hud.DeltaColor(1), 6f);
                return;
            }

            RaceRules.Begin(mode, s.Seed, s.Competitive);
            RaceGuarantees.Rules = s.Rules;
            GameBridge.ApplySeed(s.Seed);
            RaceRules.InRaceWorld = true;
#if DEBUG
            if (RaceConfig.DebugDumpLevelContents.Value) DebugDump.Begin(s.Seed);
            if (RaceConfig.DebugReconDump.Value) ReconDump.Begin(s.Seed);
#endif
            GameBridge.ClearFreezeFlags();
            s.AutoStartPending = false;
            s.WorldInitialized = true;
            if (s.Phase != RacePhase.Finished) s.Phase = RacePhase.Loading;
        }

        private void OnWorldReady(RaceSession s)
        {
            s.WorldInitialized = false;
            int levels;
            s.WorldHash = GameBridge.WorldFingerprint(out levels);
            s.LevelCount = levels;
            Plugin.Log.LogInfo("World ready: " + levels + " levels, fingerprint " + s.WorldHash.ToString("X8"));
            RaceGuarantees.LogFirstLevel();
#if DEBUG
            ReconDump.WorldReady();
#endif

            if (!s.ReadySent && !s.HasGo)
            {
                s.Phase = RacePhase.Waiting;
                GameBridge.FreezeWorld();
                _nextRetry = 0f;
                SendReady(s);
                if (s.ReadySeconds > 0) Shot("ready-check", 2f);
            }
            else if (s.HasGo && s.RaceMs >= 0)
            {
                BeginAttempt(s);
            }
            else
            {
                s.Phase = s.HasGo ? RacePhase.Countdown : RacePhase.Waiting;
                GameBridge.FreezeWorld();
            }
            ApplyHold(s);
        }

        private void GoNow(RaceSession s)
        {
            GameBridge.ThawWorld();
            _hud.Flash(s.Segment != Segments.None ? s.SegmentLabel : s.RankedSolo ? "RANKED SOLO" : s.Practice ? "UNRANKED" : "MATCH STARTED", "GO!", Hud.DeltaColor(-1));
            BeginAttempt(s);
            Shot("racing", 5f);
            Shot("racing-later", 20f);
        }

        private void BeginAttempt(RaceSession s)
        {
            bool announce = s.AttemptBegun;
            if (announce) s.Attempt++;
            s.AttemptBegun = true;
            s.Phase = RacePhase.Racing;
            s.ClearMySplits();
            s.SeenLevels.Clear();
            s.MyFinishMs = -1;
            // From here on the clock is the game's own timer. It starts at the real time since GO: zero at a normal
            // start, and the time already gone for a world that only became ready after GO.
            GameBridge.SetGameTime(Math.Max(0, s.RealMs));
            s.OnGameClock = true;
            s.AttemptStartMs = Math.Max(0, s.RaceMs);
            _nextState = 0f;

            if (announce) _link.Send(new AttemptStarted { Attempt = s.Attempt, RaceTimeMs = s.AttemptStartMs });

            M_Level start = CurrentLevel();
            if (start != null) AddSplit(s, start, true);
#if DEBUG
            // Called directly rather than through the console, so it is not recorded as a cheat.
            if (RaceConfig.DebugNoclip.Value && GameBridge.Player != null) GameBridge.Player.Noclip(new[] { "true" });
#endif
        }

        public void OnLevelEntered(M_Level level)
        {
            RaceSession s = _session;
            if (s == null || s.Phase != RacePhase.Racing || level == null || !RaceRules.InRaceWorld) return;
#if DEBUG
            DebugDump.LevelEntered(level);
            ReconDump.LevelEntered(level);
#endif
            AddSplit(s, level, false);
        }

        private void AddSplit(RaceSession s, M_Level level, bool isStart)
        {
            if (!s.SeenLevels.Add(level.GetInstanceID())) return;

            var record = new SplitRecord
            {
                Index = s.MySplits.Count,
                Level = level.levelName,
                Region = level.subRegion != null ? level.subRegion.subregionName : (level.region != null ? level.region.regionName : ""),
                TimeMs = Math.Max(0, s.RaceMs),
            };
            s.AddMySplit(record);
            _link.Send(new SplitReached { Attempt = s.Attempt, Index = record.Index, Level = record.Level, Region = record.Region, RaceTimeMs = record.TimeMs });
            if (!isStart) Shot("split", 0.8f);
            if (!isStart && s.Party) ShowPartyGaps(s, record);
        }

        // At each level: the gap to the racer just ahead and to the leader (both of whom passed it already). The
        // leader instead sees the gap to second place when second gets there (OnOpponentSplit).
        private void ShowPartyGaps(RaceSession s, SplitRecord mine)
        {
            _nextRanking = Time.unscaledTime + RankingInterval;
            UpdateRanking(s);
            List<RacerProgress> ranking = s.Ranking();
            int place = RaceSession.PlaceOf(ranking, s.YourRacer);
            if (place <= 1) return;
            var gaps = new List<Hud.Gap>();
            Rival ahead = s.RivalFor((byte)ranking[place - 2].Racer);
            SplitRecord at = ahead.FindAt(mine.Key);
            if (at != null) gaps.Add(new Hud.Gap(ahead.Name + " (" + Standings.Ordinal(place - 1) + ")", mine.TimeMs - at.TimeMs, false));
            if (place > 2)
            {
                Rival leader = s.RivalFor((byte)ranking[0].Racer);
                SplitRecord lead = leader.FindAt(mine.Key);
                if (lead != null) gaps.Add(new Hud.Gap(leader.Name + " (1st)", mine.TimeMs - lead.TimeMs, false));
            }
            if (gaps.Count > 0) _hud.Gaps(gaps.ToArray(), GapSeconds);
        }

        public void OnLocalWin()
        {
            RaceSession s = _session;
            if (s == null || s.Phase != RacePhase.Racing) return;
            FinishRun(s, "FINISHED");
        }

        // A button in the game took a press. A segment run ends when it is the button its segment ends at.
        public void OnButtonPressed(CL_Button button)
        {
            RaceSession s = _session;
            if (s == null || s.Segment == Segments.None || s.Phase != RacePhase.Racing) return;
            if (!SegmentEnd.Is(button, Segments.Find(s.Segment))) return;
            Plugin.Log.LogInfo("Segment end reached: " + button.name + " pressed.");
            FinishRun(s, s.SegmentLabel + " FINISHED");
        }

        private void FinishRun(RaceSession s, string title)
        {
            s.MyFinishMs = s.OnGameClock ? GameBridge.FinishTimeMs() : Math.Max(0, s.RaceMs);
            s.MyFinishRealMs = Math.Max(0, s.RealMs);
            s.Phase = RacePhase.Finished;
            _nextRetry = Time.unscaledTime + RetryInterval;
            SendFinish(s);
            _hud.Flash(title, RaceFormat.Time(s.MyFinishMs), Hud.DeltaColor(-1));
        }

        // A death ends the run: in a race the other racer wins, in practice the run is kept if it is the best so far.
        public void OnLocalDeath()
        {
            RaceSession s = _session;
            if (s == null || s.Phase != RacePhase.Racing || !RaceRules.InRaceWorld || s.Held) return;
            s.Died = true;
            s.DeathCause = Patches.LastDeathCause ?? "";
            s.DeathText = GameBridge.DeathText(s.DeathCause);
            s.MyFinishMs = Math.Max(0, s.RaceMs);
            s.Phase = RacePhase.Finished;
            _nextRetry = Time.unscaledTime + RetryInterval;
            SendDied(s);
            _hud.Flash(string.IsNullOrEmpty(s.DeathText) ? "YOU DIED" : s.DeathText, RaceFormat.Time(s.MyFinishMs), Hud.DeltaColor(1));
            Plugin.Log.LogInfo("Died at " + RaceFormat.Time(s.MyFinishMs) + " (" + s.DeathCause + ": " + s.DeathText + ")");
        }

        // Whether the game may restart the scene now. In a race world during a race: no (one run each, finish or
        // die). In a practice world: the restart becomes a fresh practice run from WKSR.
        public bool AllowGameRestart()
        {
            RaceSession s = _session;
            if (s == null || !RaceRules.InRaceWorld) return true;
            if (s.Practice)
            {
                RequestPracticeRestart(s);
                return false;
            }
            if (s.Phase == RacePhase.Done) return true;
            _hud.Toast("Restarts are off in matches: one run, finish it or die trying. " + RaceConfig.ForfeitKey.Value + " twice forfeits.", Hud.DeltaColor(1), 5f);
            return false;
        }

        private void RequestPracticeRestart(RaceSession s)
        {
            if (!_link.Connected)
            {
                _hud.Toast("WKSR is not connected, so the run cannot restart.", Hud.DeltaColor(1), 4f);
                return;
            }
            if (s.Phase == RacePhase.Banning)
            {
                _hud.Toast("Artifact bans first.", Hud.DeltaColor(1), 3f);
                return;
            }
            _practiceRestartPending = true;
            _link.Send(new PracticeRestart { MatchId = s.MatchId });
            Plugin.Log.LogInfo("Restart requested for " + (s.RankedSolo ? "the ranked solo run" : "the unranked run") + " on seed " + s.Seed + ".");
            _hud.Toast("Restarting the run...", 3f);
        }

        private void EndSession()
        {
            DestroyGhosts(_session);
            _session = null;
            GameBridge.ThawWorld();
        }

        internal ChatBox Chat { get { return _chat; } }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // A run starting (or ending) puts the chat away: the game gets its input back.
            _chat.Close();
            if (scene.name != GameBridge.MenuScene) return;

            RaceRules.InRaceWorld = false;
            GameBridge.ClearFreezeFlags();
            ClearGhosts(_session);
            RaceSession s = _session;
            if (s == null || s.Phase == RacePhase.Done)
            {
                _session = null;
                RaceRules.End();
            }
            else if (s.Phase != RacePhase.Banning && s.Phase != RacePhase.Starting && s.Phase != RacePhase.Finished)
            {
                s.Phase = RacePhase.Starting;
                s.AutoStartPending = false;
                if (s.Practice) _hud.Toast("Back on the menu. Press " + RaceConfig.JoinKey.Value + " for a fresh practice run.", 8f);
                else if (s.AttemptBegun) _hud.Toast("You left the match world, and restarts are off in matches. Press " + RaceConfig.ForfeitKey.Value + " twice to forfeit.", Hud.DeltaColor(1), 10f);
                else _hud.Toast("Back on the menu. Press " + RaceConfig.JoinKey.Value + " to rejoin the match.", 8f);
            }
        }

        // ---- outgoing ----------------------------------------------------

        // The world has loaded (MatchReady). The ready check (PlayerReady) is the player's own press.
        private void SendReady(RaceSession s)
        {
            s.ReadySent = true;
            _link.Send(new MatchReady { MatchId = s.MatchId, WorldHash = s.WorldHash, LevelCount = s.LevelCount });
        }

        private void PressReady(RaceSession s)
        {
            if (!_link.Connected)
            {
                _hud.Toast("WKSR is not connected.", Hud.DeltaColor(1), 3f);
                return;
            }
            s.ReadyPressed = true;
            _nextRetry = Time.unscaledTime + RetryInterval;
            _link.Send(new PlayerReady { MatchId = s.MatchId });
            _hud.Toast("Ready! The countdown starts when everyone is ready.", Hud.DeltaColor(-1), 3f);
        }

        private void SendDied(RaceSession s)
        {
            _link.Send(new PlayerDied { Attempt = s.Attempt, RaceTimeMs = s.MyFinishMs, Cause = s.DeathCause ?? "", Text = s.DeathText ?? "" });
        }

        private void SendFinish(RaceSession s)
        {
            _link.Send(new PlayerFinished { Attempt = s.Attempt, RaceTimeMs = s.MyFinishMs, RealTimeMs = s.MyFinishRealMs, Cheated = s.Cheated });
        }

        private void SendState(RaceSession s)
        {
            if (Time.unscaledTime < _nextState || !_link.Connected) return;
            _nextState = Time.unscaledTime + StateInterval;
            if (!GameBridge.TryReadPlayer(_stateMessage)) return;
            _stateMessage.Attempt = s.Attempt;
            _stateMessage.RaceTimeMs = Math.Max(0, s.RaceMs);
            _link.Send(_stateMessage);
        }

        // ---- artifact bans -------------------------------------------------

        private static readonly KeyCode[] NumberKeys =
        {
            KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.Alpha3, KeyCode.Alpha4, KeyCode.Alpha5,
            KeyCode.Alpha6, KeyCode.Alpha7, KeyCode.Alpha8, KeyCode.Alpha9,
        };
        private static readonly KeyCode[] KeypadKeys =
        {
            KeyCode.Keypad1, KeyCode.Keypad2, KeyCode.Keypad3, KeyCode.Keypad4, KeyCode.Keypad5,
            KeyCode.Keypad6, KeyCode.Keypad7, KeyCode.Keypad8, KeyCode.Keypad9,
        };

        private void OnBanState(BanState state)
        {
            RaceSession s = _session;
            if (s == null || s.MatchId != state.MatchId) return;
            bool wasMyTurn = s.Bans != null && s.Bans.YourTurn;
            s.Bans = state;
            s.BansReceivedAt = Time.unscaledTime;
            s.BanPickSent = false;
            if (state.YourTurn && !wasMyTurn)
            {
                s.BanCursor = FirstBannable(state, 0, 1);
                _hud.Toast("Your turn to ban an artifact.", Hud.DeltaColor(-1), 4f);
            }
            if (!state.Done) return;

            ArtifactBans.Set(state.Banned);
            if (s.Phase != RacePhase.Banning) return;
            s.Phase = RacePhase.Starting;
            if (s.Practice && _practiceRestartPending) StartRestartedRun();
            var parts = new List<string>();
            for (int i = 0; i < state.Banned.Count; i++) parts.Add(ArtifactBans.Label(state.Banned[i]) + " (" + state.BannedBy[i] + ")");
            string summary = parts.Count == 0 ? "No artifacts banned." : "Banned: " + string.Join(", ", parts);
            _hud.Toast(summary, 8f);
            Plugin.Log.LogInfo("Artifact bans done. " + summary);
        }

        private void UpdateBans(RaceSession s)
        {
            // Until the server has both pools, keep telling it ours (the client may not have been connected yet).
            bool waiting = s.Bans == null || (!s.Bans.Done && s.Bans.Pool.Count == 0);
            if (waiting && Time.unscaledTime >= s.NextBanReady && _link.Connected)
            {
                List<string> pool = ArtifactBans.Pool();
                s.NextBanReady = Time.unscaledTime + 3f;
                if (pool != null) _link.Send(new BanReady { MatchId = s.MatchId, Pool = new List<string>(pool) });
            }

            BanState state = s.Bans;
            if (state == null || state.Done || !state.YourTurn || s.BanPickSent || state.Pool.Count == 0 || ChatInputBlock.Active) return;

            int count = state.Pool.Count;
            for (int i = 0; i < NumberKeys.Length && i < count; i++)
            {
                if (Input.GetKeyDown(NumberKeys[i]) || Input.GetKeyDown(KeypadKeys[i])) { PickBan(s, i); return; }
            }
            if (Input.GetKeyDown(KeyCode.Alpha0) || Input.GetKeyDown(KeyCode.Keypad0) || Input.GetKeyDown(KeyCode.Backspace))
            {
                SendBan(s, "");
                return;
            }
            if (Input.GetKeyDown(KeyCode.UpArrow)) s.BanCursor = FirstBannable(state, s.BanCursor - 1, -1);
            if (Input.GetKeyDown(KeyCode.DownArrow)) s.BanCursor = FirstBannable(state, s.BanCursor + 1, 1);
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) PickBan(s, s.BanCursor);
        }

        private void PickBan(RaceSession s, int index)
        {
            BanState state = s.Bans;
            if (index < 0 || index >= state.Pool.Count || state.Banned.Contains(state.Pool[index])) return;
            SendBan(s, state.Pool[index]);
        }

        private void SendBan(RaceSession s, string item)
        {
            s.BanPickSent = true;
            _link.Send(new BanPick { MatchId = s.MatchId, Item = item });
        }

        // The nearest item from `from` in `step` direction that is not banned yet (wrapping around).
        private static int FirstBannable(BanState state, int from, int step)
        {
            int count = state.Pool.Count;
            for (int n = 0; n < count; n++)
            {
                int i = ((from + n * step) % count + count) % count;
                if (!state.Banned.Contains(state.Pool[i])) return i;
            }
            return 0;
        }

        // ---- hotkeys -----------------------------------------------------

        private void HandleHotkeys()
        {
            if (_chat.Typing || ChatInputBlock.Active) return;
            // The chat key types nothing, so it works whatever has the keyboard.
            if (Input.GetKeyDown(RaceConfig.ChatKey.Value) && !CommandConsole.IsConsoleVisible())
            {
                _chat.OpenPanel();
                Shot("chat-typing", 1.5f);
                return;
            }
            // Typing in one of the WKSR window's text boxes (a lobby code, a seed, a name) or one of the game's own is
            // not pressing hotkeys.
            if (MenuUi.Typing) return;
            UnityEngine.EventSystems.EventSystem events = UnityEngine.EventSystems.EventSystem.current;
            GameObject focused = events != null ? events.currentSelectedGameObject : null;
            if (focused != null && focused.GetComponent<TMPro.TMP_InputField>() != null) return;
            if (Input.GetKeyDown(RaceConfig.SplitsKey.Value)) RaceConfig.ShowSplits.Value = !RaceConfig.ShowSplits.Value;
            if (Input.GetKeyDown(RaceConfig.GhostKey.Value))
            {
                RaceConfig.ShowGhost.Value = !RaceConfig.ShowGhost.Value;
                _hud.Toast("Ghost " + (RaceConfig.ShowGhost.Value ? "on" : "off"), 2f);
            }
            if (Input.GetKeyDown(RaceConfig.JoinKey.Value)) OnJoinKey();
            if (Input.GetKeyDown(RaceConfig.ForfeitKey.Value)) OnForfeitKey();
        }

        private void OnJoinKey()
        {
            RaceSession s = _session;
            if (s != null && s.Practice)
            {
                OnPracticeJoinKey(s);
                return;
            }
            if (s == null || s.Phase == RacePhase.Done)
            {
                _hud.Toast("No active match. Open WKSR in the main menu to find one.", 4f);
                return;
            }
            if (s.Phase == RacePhase.Finished) return;
            if (s.Phase == RacePhase.Banning)
            {
                _hud.Toast("Artifact bans first. The match world loads when they are done.", Hud.DeltaColor(1), 3f);
                return;
            }
            // Loaded: F9 is the ready button.
            if (s.Phase == RacePhase.Waiting && s.ReadySeconds > 0)
            {
                if (!s.ReadyPressed) PressReady(s);
                else _hud.Toast("You are ready. Waiting for the others.", 2f);
                return;
            }
            if (s.Phase == RacePhase.Countdown) return;
            if (s.Held)
            {
                _hud.Toast("The match is paused. You can restart once it resumes.", Hud.DeltaColor(1), 3f);
                return;
            }

            // A race run starts once: after that it ends by finishing or dying (a game that crashed rejoins on its own).
            if (s.AttemptBegun)
            {
                _hud.Toast("Restarts are off in matches: one run, finish it or die trying. " + RaceConfig.ForfeitKey.Value + " twice forfeits.", Hud.DeltaColor(1), 4f);
                Plugin.Log.LogInfo("Restart key pressed during a match: restarts are off.");
                return;
            }
            if (GameBridge.InMenu || Time.unscaledTime < _joinConfirmUntil)
            {
                _joinConfirmUntil = 0f;
                if (!StartRun()) _hud.Toast("Could not find the Campaign gamemode.", Hud.DeltaColor(1), 5f);
                return;
            }
            _joinConfirmUntil = Time.unscaledTime + ConfirmWindow;
            _hud.Toast("Press " + RaceConfig.JoinKey.Value + " again to leave this run and join the match.", Hud.DeltaColor(1), ConfirmWindow);
        }

        // Practice: the first press from the menu starts the run; after that F9 (twice while running) is a fresh run.
        private void OnPracticeJoinKey(RaceSession s)
        {
            if (s.Phase == RacePhase.Banning)
            {
                _hud.Toast("Artifact bans first. The world loads when they are done.", Hud.DeltaColor(1), 3f);
                return;
            }
            if (!s.AttemptBegun && (GameBridge.InMenu || Time.unscaledTime < _joinConfirmUntil))
            {
                _joinConfirmUntil = 0f;
                if (!StartRun()) _hud.Toast("Could not find the Campaign gamemode.", Hud.DeltaColor(1), 5f);
                return;
            }
            if (s.AttemptBegun && (GameBridge.InMenu || s.Phase == RacePhase.Done || s.Phase == RacePhase.Finished || Time.unscaledTime < _joinConfirmUntil))
            {
                _joinConfirmUntil = 0f;
                RequestPracticeRestart(s);
                return;
            }
            _joinConfirmUntil = Time.unscaledTime + ConfirmWindow;
            _hud.Toast("Press " + RaceConfig.JoinKey.Value + " again for a fresh run.", ConfirmWindow);
        }

        private void OnForfeitKey()
        {
            RaceSession s = _session;
            if (s == null || s.Phase == RacePhase.Done) return;

            if (Time.unscaledTime < _forfeitConfirmUntil)
            {
                _forfeitConfirmUntil = 0f;
                _link.Send(new PlayerForfeit());
                _hud.Toast(s.Practice ? "Ending the practice run..." : "Forfeiting...", Hud.DeltaColor(1), 4f);
                return;
            }
            _forfeitConfirmUntil = Time.unscaledTime + ConfirmWindow;
            _hud.Toast("Press " + RaceConfig.ForfeitKey.Value + (s.Practice ? " again to end the practice run." : " again to forfeit the match."), Hud.DeltaColor(1), ConfirmWindow);
        }

        // What to warn about when WKSR and this mod do not match, or null. WKSR says so itself when it knows (from our
        // hello); unreadable messages give it away when WKSR is too old to say.
        private string VersionMismatch()
        {
            if (_link == null || !_link.Connected) return null;
            ClientStatus client = _clientStatus;
            if (client != null && client.Info != null && client.Info.StartsWith(ClientStatus.MismatchPrefix, StringComparison.Ordinal)) return client.Info;
            if (_link.SawUnreadable)
                return ClientStatus.MismatchPrefix + ": this WK Race mod (" + PluginInfo.Version + ") cannot read what WKSR sends. Install the same version of both.";
            return null;
        }

        // ---- helpers -------------------------------------------------------

        private static M_Level CurrentLevel()
        {
            WorldLoader loader = WorldLoader.instance;
            if (loader == null) return null;
            WorldLoader.LevelInfo info = loader.GetCurrentLevel();
            return info == null ? null : info.GetLevel();
        }

        private static void SetStatic(Type type, string field, object value)
        {
            FieldInfo f = AccessTools.Field(type, field);
            if (f != null) f.SetValue(null, value);
        }
    }
}
