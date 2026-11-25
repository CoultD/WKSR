using System;
using HarmonyLib;

namespace WKRace.Mod
{
    // Since The Core update (Beta 0.70) how often a perk page can be rerolled depends on facility upgrades bought on
    // the player's save, and unlike the break room upgrades it is not switched on for everyone in competitive mode.
    // In a match every reroll upgrade counts as owned, so all players get the same rerolls at the same costs.
    internal static class PerkRerolls
    {
        // True while a perk page counts its reroll upgrades.
        private static bool _counting;

        public static void Apply(Harmony harmony)
        {
            var refresh = AccessTools.Method(typeof(App_PerkPage), "UpdateRefreshUI");
            var owned = AccessTools.Method(typeof(FacilityUpgrade), "IsOwned", new[] { typeof(string) });
            if (refresh == null || owned == null)
            {
                HookHealth.Fail("perk rerolls", (refresh == null ? "App_PerkPage.UpdateRefreshUI" : "FacilityUpgrade.IsOwned") + " not found");
                return;
            }
            harmony.Patch(owned, postfix: new HarmonyMethod(typeof(PerkRerolls), nameof(AfterIsOwned)));
            harmony.Patch(refresh, prefix: new HarmonyMethod(typeof(PerkRerolls), nameof(BeforeRefreshUI)),
                          finalizer: new HarmonyMethod(typeof(PerkRerolls), nameof(AfterRefreshUI)));
        }

        private static void BeforeRefreshUI()
        {
            _counting = RaceRules.InRaceWorld;
        }

        private static Exception AfterRefreshUI(Exception __exception)
        {
            _counting = false;
            return __exception;
        }

        private static void AfterIsOwned(ref bool __result)
        {
            if (_counting) __result = true;
        }
    }
}
