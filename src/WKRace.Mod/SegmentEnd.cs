using System;
using HarmonyLib;
using UnityEngine;
using WKRace.Protocol;

namespace WKRace.Mod
{
    // Where a segment run ends (see Segments): the press of one button in the game. Every button press that goes
    // through is passed to the run in progress, which checks whether it is its segment's button.
    internal static class SegmentEnd
    {
        public static void Apply(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(CL_Button), "Interact", new Type[0]);
            if (target == null)
            {
                Plugin.Log.LogError("Patch target missing for segment runs. The game may have been updated.");
                return;
            }
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(SegmentEnd), nameof(BeforePress)), postfix: new HarmonyMethod(typeof(SegmentEnd), nameof(AfterPress)));
        }

        // A press only counts when the button takes it (not on its cooldown, not switched off).
        private static void BeforePress(CL_Button __instance, out bool __state)
        {
            __state = false;
            try { __state = RaceRules.InRaceWorld && __instance != null && __instance.CanInteract(); }
            catch (Exception) { }
        }

        private static void AfterPress(CL_Button __instance, bool __state)
        {
            if (!__state) return;
            try { if (RaceController.Current != null) RaceController.Current.OnButtonPressed(__instance); }
            catch (Exception e) { Plugin.Log.LogError("Segment end check failed: " + e); }
        }

        // The button is the one the segment ends at: it is in the segment's last level, and pressing it acts on the
        // object the segment names (the control that seals the shaft, for the Silos).
        public static bool Is(CL_Button button, SegmentInfo segment)
        {
            if (button == null || segment == null || button.activeEvent == null) return false;
            M_Level level = button.GetComponentInParent<M_Level>();
            if (level == null || level.levelName == null || !level.levelName.StartsWith(segment.EndLevel, StringComparison.OrdinalIgnoreCase)) return false;
            for (int i = 0; i < button.activeEvent.GetPersistentEventCount(); i++)
            {
                UnityEngine.Object target = button.activeEvent.GetPersistentTarget(i);
                var component = target as Component;
                GameObject acted = component != null ? component.gameObject : target as GameObject;
                if (acted != null && acted.name.StartsWith(segment.EndTarget, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }
}
