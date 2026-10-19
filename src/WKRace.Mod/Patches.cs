using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace WKRace.Mod
{
    // Every hook into the game. Each patch is applied on its own so one game update cannot take the others down.
    internal static class Patches
    {
        public static void Apply(Harmony harmony)
        {
            Patch(harmony, AccessTools.Method(typeof(StatManager.SaveData), "IsSettingActive"), null, nameof(IsSettingActivePostfix));
            Patch(harmony, AccessTools.Method(typeof(WorldLoader), "Initialize"), nameof(WorldInitializePrefix), null);
            Patch(harmony, AccessTools.Method(typeof(M_Level), "OnSpawn"), null, nameof(LevelSpawnPostfix));
            Patch(harmony, AccessTools.Method(typeof(M_Level), "OnEnter"), null, nameof(LevelEnterPostfix));
            Patch(harmony, AccessTools.Method(typeof(CL_GameManager), "RefreshTimescale"), null, nameof(RefreshTimescalePostfix));
            Patch(harmony, AccessTools.Method(typeof(CL_GameManager), "Win"), nameof(WinPrefix), null);
            Patch(harmony, AccessTools.Method(typeof(CL_GameManager), "Die"), nameof(DiePrefix), nameof(DiePostfix));
            Patch(harmony, AccessTools.Method(typeof(CL_GameManager), "RestartScene"), nameof(RestartScenePrefix), null);
            Patch(harmony, AccessTools.Method(typeof(CL_SaveManager), "DeleteSessionSave"), nameof(KeepSavesPrefix), null);
            Patch(harmony, AccessTools.Method(typeof(CL_SaveManager), "SaveSessionToFile"), nameof(KeepSavesPrefix), null);

            Patch(harmony, AccessTools.Method(typeof(Imui.IO.UGUI.ImuiUnityGUIBackend), "OnScroll"), nameof(MenuScrollPrefix), nameof(MenuScrollPostfix));

            Guarded(() => RaceUnlocks.Apply(harmony));
            Guarded(() => SpawnSeeding.Apply(harmony));
            Guarded(() => RaceGuarantees.Apply(harmony));
            Guarded(() => ArtifactBans.Apply(harmony));
            Guarded(() => SegmentEnd.Apply(harmony));
            Guarded(() => ChatInputBlock.Apply(harmony));
            Guarded(() => InventoryLayout.Apply(harmony));
            Guarded(() => CompetitiveUnlock.Apply(harmony));
            Guarded(() => PlayerCheck.Apply(harmony));
        }

        // Imui scrolls half a line of text for a notch of the mouse wheel, which makes the options window and the
        // WKSR lists crawl. The wheel counts several times over while Imui reads it, and is put back afterwards.
        private const float MenuScrollFactor = 6f;

        private static void MenuScrollPrefix(UnityEngine.EventSystems.PointerEventData eventData, out Vector2 __state)
        {
            __state = eventData.scrollDelta;
            eventData.scrollDelta = __state * MenuScrollFactor;
        }

        private static void MenuScrollPostfix(UnityEngine.EventSystems.PointerEventData eventData, Vector2 __state)
        {
            eventData.scrollDelta = __state;
        }

        private static void Patch(Harmony harmony, MethodBase target, string prefix, string postfix)
        {
            string name = (prefix ?? postfix);
            if (target == null)
            {
                Plugin.Log.LogError("Patch target missing for " + name + ". The game may have been updated.");
                return;
            }
            try
            {
                harmony.Patch(target,
                    prefix: prefix == null ? null : new HarmonyMethod(typeof(Patches), prefix),
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(Patches), postfix));
                if (RaceConfig.Verbose) Plugin.Log.LogDebug("Patched " + target.DeclaringType.Name + "." + target.Name);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not patch " + target.DeclaringType.Name + "." + target.Name + ": " + e.Message);
            }
        }

        // Races always run with the same rule set: competitive on (or off if the server says so), every other modifier off.
        private static void IsSettingActivePostfix(string gameModeName, string settingID, ref bool __result)
        {
            if (!RaceRules.Active) return;
            if (!string.Equals(gameModeName, GameBridge.CampaignModeName, StringComparison.OrdinalIgnoreCase)) return;
            __result = RaceRules.Competitive && settingID == "competitive";
        }

        private static void WorldInitializePrefix()
        {
            SpawnSeeding.Reset();
            RaceGuarantees.Reset();
            Guarded(() => { if (RaceController.Current != null) RaceController.Current.OnWorldInitializing(); });
        }

        // The game flips levels using the shared random stream, which other systems also draw from between frames.
        // Reseeding per level name makes every player see the same mirrored/unmirrored layout.
        private static void LevelSpawnPostfix(M_Level __instance)
        {
            if (!RaceRules.InRaceWorld || __instance == null) return;
            UnityEngine.Random.InitState(MixSeed(RaceRules.Seed, __instance.levelName));
        }

        private static void LevelEnterPostfix(M_Level __instance)
        {
            Guarded(() => { if (RaceController.Current != null) RaceController.Current.OnLevelEntered(__instance); });
        }

        private static void RefreshTimescalePostfix()
        {
            if (GameBridge.Frozen) Time.timeScale = 0f;
        }

        private static void WinPrefix()
        {
            Guarded(() => { if (RaceController.Current != null) RaceController.Current.OnLocalWin(); });
        }

        // Die returns at once when the player is already dead, and revives instead when a saved checkpoint exists
        // (never in a race world, where saving is off). Only a real, new death counts.
        // What the game says killed the player ("deathfloor", "falling", ...), from the last real death.
        public static string LastDeathCause = "";

        private static void DiePrefix(string type, out bool __state)
        {
            __state = CL_GameManager.isDead();
            if (!__state) LastDeathCause = type ?? "";
        }

        private static void DiePostfix(CL_GameManager __instance, bool __state)
        {
            if (__state || !CL_GameManager.isDead() || __instance.IsReviving()) return;
            Guarded(() => { if (RaceController.Current != null) RaceController.Current.OnLocalDeath(); });
        }

        // Every restart goes through here: binds such as "rr restart", the restart command, the pause and death menus.
        // In a race they are off; in practice they become a fresh practice run.
        private static bool RestartScenePrefix()
        {
            bool allow = true;
            Guarded(() => { if (RaceController.Current != null) allow = RaceController.Current.AllowGameRestart(); });
            return allow;
        }

        // The game deletes and rewrites the player's saved campaign run at the end of every run; a race must not touch it.
        private static bool KeepSavesPrefix()
        {
            return !RaceRules.InRaceWorld;
        }

        internal static int MixSeed(int seed, string name)
        {
            unchecked
            {
                uint h = 2166136261;
                if (name != null)
                {
                    for (int i = 0; i < name.Length; i++)
                    {
                        h ^= name[i];
                        h *= 16777619;
                    }
                }
                h ^= (uint)seed;
                h *= 16777619;
                return Mix(h);
            }
        }

        // Spreads a hash over all bits before it is used as a seed.
        internal static int Mix(uint h)
        {
            unchecked
            {
                h ^= h >> 15;
                h *= 0x2c1b3c6d;
                h ^= h >> 12;
                return (int)(h & 0x7fffffff);
            }
        }

        private static void Guarded(Action action)
        {
            try { action(); }
            catch (Exception e) { Plugin.Log.LogError("Match hook failed: " + e); }
        }
    }
}
