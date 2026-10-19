using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace WKRace.Mod
{
    // Matches are played with the game's competitive setting, which the game only lets a player switch on (and set
    // up: the two locker items) after they have unlocked it. While the mod is loaded that unlock counts as earned, so
    // every player can open the competitive options in the game's menu. Nothing else is unlocked.
    internal static class CompetitiveUnlock
    {
        private const string SettingId = "competitive";

        // The unlock objects of the game's "competitive" settings (one per game mode that has it).
        private static readonly HashSet<int> Unlocks = new HashSet<int>();
        private static float _nextSearch;

        public static void Apply(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(ProgressionUnlock), "CheckUnlock");
            if (target == null)
            {
                Plugin.Log.LogError("Patch target missing for the competitive unlock. The game may have been updated.");
                return;
            }
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(CompetitiveUnlock), nameof(AfterCheckUnlock)));
        }

        private static void AfterCheckUnlock(ProgressionUnlock __instance, ref bool __result)
        {
            if (__result || __instance == null) return;
            try
            {
                if (!IsCompetitive(__instance)) return;
                __instance.state = true;
                __result = true;
            }
            catch (Exception) { }
        }

        private static bool IsCompetitive(ProgressionUnlock unlock)
        {
            if (Unlocks.Count == 0 && Time.unscaledTime >= _nextSearch)
            {
                // The settings are assets the game loads with its menu: look again now and then until they are there.
                _nextSearch = Time.unscaledTime + 2f;
                foreach (GamemodeSetting setting in Resources.FindObjectsOfTypeAll<GamemodeSetting>())
                {
                    if (setting != null && setting.unlock != null && string.Equals(setting.id, SettingId, StringComparison.OrdinalIgnoreCase))
                        Unlocks.Add(setting.unlock.GetInstanceID());
                }
                if (Unlocks.Count > 0) Plugin.Log.LogInfo("Competitive mode is unlocked while the mod is loaded.");
            }
            return Unlocks.Contains(unlock.GetInstanceID());
        }
    }
}
