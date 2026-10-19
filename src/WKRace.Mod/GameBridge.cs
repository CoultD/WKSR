using System;
using System.Globalization;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using WKRace.Protocol;

namespace WKRace.Mod
{
    // Thin layer over the game's own classes; everything that can break on a game update lives here or in Patches.
    internal static class GameBridge
    {
        public const string MenuScene = "Main-Menu";
        public const string RunScene = "Game-Main";
        public const string CampaignModeName = "Campaign";
        private const string CampaignAssetName = "GM_Campaign";

        public static bool Frozen;
        private static bool _lockedByUs;
        private static Vector3 _savedVelocity;
        private static readonly AccessTools.FieldRef<ENT_Player, Vector3> PlayerVelocity = SafeFieldRef("vel");

        public static bool InMenu { get { return SceneManager.GetActiveScene().name == MenuScene; } }
        public static bool InRunScene { get { return SceneManager.GetActiveScene().name == RunScene; } }

        // The Steam account the game runs under, or 0 when Steam is not there (the game's Steamworks has not come up
        // yet, or this copy was not started through Steam).
        public static ulong SteamId()
        {
            try { return Steamworks.SteamClient.IsValid ? Steamworks.SteamClient.SteamId.Value : 0UL; }
            catch (Exception) { return 0UL; }
        }

        // The player's Steam name, or null when Steam is not there (yet).
        public static string SteamName()
        {
            try { return Steamworks.SteamClient.IsValid ? Steamworks.SteamClient.Name : null; }
            catch (Exception) { return null; }
        }

        public static string GameVersion()
        {
            try { return DarkMachineFunctions.GetVersionName("").Trim(); }
            catch (Exception) { return Application.version; }
        }

        // ---- the game's own run timer -----------------------------------------------------------------------
        //
        // CL_GameManager adds "frame time x factor" to it every frame. The factor is 1 on the main route and 3 in the
        // shortcut sections (0.15 in part of one), set by checkpoints in the levels; the timer stands still while the
        // game is paused, loading, or the player is dead. From GO on it is the match clock, so every time the mod
        // shows or reports is the time the game itself shows.

        private static readonly AccessTools.FieldRef<CL_GameManager, float> GameTime = SafeFloatRef("gameTime");
        private static readonly AccessTools.FieldRef<CL_GameManager, float> TimerFactor = SafeFloatRef("checkpointSpacewarp");
        private static int _lastGameMs;

        // The run timer of the match world in ms. Outside it (the menu, another world) the last value stays.
        public static int GameTimeMs()
        {
            CL_GameManager gm = CL_GameManager.gMan;
            if (gm == null || !RaceRules.InRaceWorld) return _lastGameMs;
            double ms = gm.GetGameTime() * 1000.0;
            return _lastGameMs = ms <= 0 ? 0 : ms >= int.MaxValue ? int.MaxValue : (int)ms;
        }

        // The timer at a finish, to the hundredth like the game's own result screen (which rounds it the same way).
        public static int FinishTimeMs()
        {
            CL_GameManager gm = CL_GameManager.gMan;
            if (gm == null || !RaceRules.InRaceWorld) return _lastGameMs;
            double hundredths = Math.Round(gm.GetGameTime() * 100.0, MidpointRounding.AwayFromZero);
            return _lastGameMs = hundredths <= 0 ? 0 : (int)Math.Min(int.MaxValue, hundredths * 10.0);
        }

        public static void SetGameTime(int ms)
        {
            CL_GameManager gm = CL_GameManager.gMan;
            if (gm == null || GameTime == null) return;
            GameTime(gm) = Math.Max(0, ms) / 1000f;
            _lastGameMs = Math.Max(0, ms);
        }

        public static bool GamePaused
        {
            get { return CL_GameManager.gMan != null && CL_GameManager.gMan.isPaused; }
        }

        // The game's timer stops while the game is paused. In a match that would be a free rest, so there the paused
        // time is added back, at the speed the timer was running at.
        public static void AddPausedTime(float realSeconds)
        {
            CL_GameManager gm = CL_GameManager.gMan;
            if (gm == null || GameTime == null || realSeconds <= 0f) return;
            float factor = TimerFactor != null ? TimerFactor(gm) : 1f;
            if (factor <= 0f) factor = 1f;
            GameTime(gm) += realSeconds * factor;
        }

        private static AccessTools.FieldRef<CL_GameManager, float> SafeFloatRef(string name)
        {
            try { return AccessTools.FieldRefAccess<CL_GameManager, float>(name); }
            catch (Exception) { return null; }
        }

        public static M_Gamemode FindCampaign()
        {
            M_Gamemode fallback = null;
            foreach (M_Gamemode gm in Resources.FindObjectsOfTypeAll<M_Gamemode>())
            {
                if (gm.name == CampaignAssetName) return gm;
                if (fallback == null && gm.baseGamemode && !gm.devOnly && gm.allowCompetitive &&
                    string.Equals(gm.gamemodeName, CampaignModeName, StringComparison.OrdinalIgnoreCase))
                {
                    fallback = gm;
                }
            }
            return fallback;
        }

        // Same entry point the game's own menu uses, minus the step that deletes the player's saved run.
        public static void BeginSeededRun(M_Gamemode gamemode, int seed)
        {
            WorldLoader.SetPresetSeed(seed.ToString(CultureInfo.InvariantCulture));
            CL_GameManager.gamemode = gamemode;
            SceneManager.LoadScene(gamemode.gamemodeScene);
        }

        public static void ApplySeed(int seed)
        {
            WorldLoader.SetPresetSeed(seed.ToString(CultureInfo.InvariantCulture));
        }

        public static bool IsWorldReady()
        {
            return InRunScene && WorldLoader.instance != null && WorldLoader.isLoaded &&
                   CL_GameManager.gMan != null && CL_GameManager.gMan.localPlayer != null && !CL_GameManager.IsLoading();
        }

        public static ENT_Player Player { get { return ENT_Player.playerObject; } }

        // The game's death-screen line for a cause ("FUSED INTO THE MASS"), as the game itself picks it: the cause's
        // own line, else the "basic" one. "" when the game has no localization loaded.
        public static string DeathText(string cause)
        {
            try
            {
                string text = CL_LocalizationManager.GetLocalization("deathmessages", cause ?? "");
                if (string.IsNullOrEmpty(text)) text = CL_LocalizationManager.GetLocalization("deathmessages", "basic");
                return text ?? "";
            }
            catch (Exception)
            {
                return "";
            }
        }

        // Added to positions that were sent without the game's floating-origin offset.
        public static Vector3 OriginOffset()
        {
            CL_GameManager gm = CL_GameManager.gMan;
            return gm == null ? Vector3.zero : -gm.GetCorrectedPosition(Vector3.zero);
        }

        public static Vector3 ToShared(Vector3 local)
        {
            CL_GameManager gm = CL_GameManager.gMan;
            return gm == null ? local : gm.GetCorrectedPosition(local);
        }

        // Name of the level `steps` levels above the one the player is in, or "" if unknown.
        public static string LevelNameAhead(int steps)
        {
            WorldLoader loader = WorldLoader.instance;
            if (loader == null) return "";
            WorldLoader.BranchInfo branch = loader.GetCurrentBranch();
            WorldLoader.LevelInfo current = loader.GetCurrentLevel();
            if (branch == null || branch.levelTracker == null || current == null) return "";
            int index = branch.levelTracker.IndexOf(current) + steps;
            if (index < steps || index >= branch.levelTracker.Count) return "";
            M_Level level = branch.levelTracker[index].GetLevel();
            return level != null ? level.levelName : "";
        }

        public static uint WorldFingerprint(out int levelCount)
        {
            levelCount = 0;
            WorldLoader loader = WorldLoader.instance;
            if (loader == null) return 0;
            WorldLoader.BranchInfo branch = loader.GetCurrentBranch();
            if (branch == null || branch.levelTracker == null) return 0;

            // The guarantee rules shape the world too, so racers on different rule versions get different fingerprints.
            uint hash = Fnv1a.Add(Fnv1a.Offset, RaceGuarantees.RulesVersion);
            hash = Fnv1a.Add(hash, (int)RaceGuarantees.Rules);
            // Both racers must have applied the same artifact bans.
            foreach (string ban in ArtifactBans.Current()) hash = Fnv1a.Add(hash, ban);
            foreach (WorldLoader.LevelInfo info in branch.levelTracker)
            {
                M_Level level = info.GetLevel();
                if (level == null) continue;
                M_Level.LevelAssetHolder holder = info.GetLevelAssetReference();
                Vector3 p = ToShared(level.transform.position);
                hash = Fnv1a.Add(hash, holder != null ? holder.id : level.levelName);
                hash = Fnv1a.Add(hash, Mathf.RoundToInt(p.x * 10f));
                hash = Fnv1a.Add(hash, Mathf.RoundToInt(p.y * 10f));
                hash = Fnv1a.Add(hash, Mathf.RoundToInt(p.z * 10f));
                hash = Fnv1a.Add(hash, level.transform.localScale.x < 0f ? 1 : 0);
                levelCount++;
            }
            return hash == 0 ? 1u : hash;
        }

        public static bool TryReadPlayer(PlayerState state)
        {
            ENT_Player player = ENT_Player.playerObject;
            if (player == null || CL_GameManager.gMan == null) return false;

            Transform head = player.cam != null ? player.cam.transform : player.transform;
            Vector3 pos = ToShared(head.position);
            state.X = pos.x; state.Y = pos.y; state.Z = pos.z;

            Vector3 euler = head.eulerAngles;
            state.Yaw = euler.y;
            state.Pitch = euler.x > 180f ? euler.x - 360f : euler.x;

            StateFlags flags = StateFlags.None;
            if (player.dead) flags |= StateFlags.Dead;
            if (!IsWorldReady()) flags |= StateFlags.Loading;

            ENT_Player.Hand[] hands = player.hands;
            if (hands != null && hands.Length >= 2)
            {
                if (hands[0] != null && hands[0].IsHolding())
                {
                    Vector3 h = ToShared(hands[0].GetHoldWorldPosition());
                    state.LeftX = h.x; state.LeftY = h.y; state.LeftZ = h.z;
                    flags |= StateFlags.LeftHold;
                }
                if (hands[1] != null && hands[1].IsHolding())
                {
                    Vector3 h = ToShared(hands[1].GetHoldWorldPosition());
                    state.RightX = h.x; state.RightY = h.y; state.RightZ = h.z;
                    flags |= StateFlags.RightHold;
                }
            }
            state.Flags = flags;
            return true;
        }

        // Stops time and input. The player's momentum is kept so a pause mid-jump resumes mid-jump.
        public static void FreezeWorld()
        {
            if (Frozen) return;
            Frozen = true;
            ENT_Player player = ENT_Player.playerObject;
            if (player != null)
            {
                if (PlayerVelocity != null) _savedVelocity = PlayerVelocity(player);
                player.Lock();
                _lockedByUs = true;
            }
            CL_GameManager.RefreshTimescale();
        }

        public static void ThawWorld()
        {
            Frozen = false;
            ENT_Player player = ENT_Player.playerObject;
            if (_lockedByUs && player != null)
            {
                player.UnLock();
                if (PlayerVelocity != null) PlayerVelocity(player) = _savedVelocity;
            }
            _lockedByUs = false;
            _savedVelocity = Vector3.zero;
            if (CL_GameManager.gMan != null) CL_GameManager.RefreshTimescale();
        }

        private static AccessTools.FieldRef<ENT_Player, Vector3> SafeFieldRef(string name)
        {
            try { return AccessTools.FieldRefAccess<ENT_Player, Vector3>(name); }
            catch (Exception) { return null; }
        }

        public static void ClearFreezeFlags()
        {
            Frozen = false;
            _lockedByUs = false;
            _savedVelocity = Vector3.zero;
        }
    }

    // Rules that apply for as long as a match is in progress, even while the player is back on the menu.
    internal static class RaceRules
    {
        public static bool Active;
        public static bool Competitive = true;
        public static int Seed;
        // True while the loaded world is the seeded race world (not a normal run started during a match).
        public static bool InRaceWorld;
        private static M_Gamemode _gamemode;
        private static bool _originalAllowCheats;

        public static void Begin(M_Gamemode gamemode, int seed, bool competitive)
        {
            if (Active && _gamemode == gamemode)
            {
                Seed = seed;
                Competitive = competitive;
                return;
            }
            End();
            Active = true;
            Seed = seed;
            Competitive = competitive;
            _gamemode = gamemode;
            _originalAllowCheats = gamemode.allowCheats;
            if (!RaceConfig.CheatsAllowed) gamemode.allowCheats = false;
        }

        public static void End()
        {
            if (!Active) return;
            Active = false;
            if (_gamemode != null) _gamemode.allowCheats = _originalAllowCheats;
            _gamemode = null;
        }
    }
}
