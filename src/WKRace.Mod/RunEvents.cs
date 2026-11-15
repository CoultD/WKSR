using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using WKRace.Protocol;

namespace WKRace.Mod
{
    // What happens in a racer's world that spectators' copies cannot know by themselves (RunEventKind): items used,
    // buttons pressed, hold buttons finished, trigger and event zones walked into, shortcut branches generated.
    //
    // Racing, each one is reported (RunEvent) with the object's name and where it is. Spectating, the same object is
    // looked for in this world (same name, within a step of the same place: both worlds come from the seed) and set off
    // too, for the player being followed (another player's events wait until they are followed). An item used is a
    // line on the screen instead.
    internal static class RunEvents
    {
        // How close the same object must be in the spectator's world (shared coordinates).
        private const float SameObjectMeters = 1.5f;
        // The same report is not sent twice within this long (a zone walked through back and forth).
        private const float RepeatSeconds = 1f;

        private static readonly AccessTools.FieldRef<CL_Button, int> ButtonPresses = Ref<CL_Button, int>("presses");
        private static readonly AccessTools.FieldRef<HandItem, bool> ItemInteracting = Ref<HandItem, bool>("isCurrentlyInteracting");
        private static readonly AccessTools.FieldRef<UT_TriggerZone, bool> ZoneHasRun = Ref<UT_TriggerZone, bool>("hasRun");
        private static readonly AccessTools.FieldRef<UT_EventZone, bool> EventZoneHasRun = Ref<UT_EventZone, bool>("hasRun");

        private static readonly Dictionary<string, float> LastSent = new Dictionary<string, float>();

        public static void Apply(Harmony harmony)
        {
            Patch(harmony, AccessTools.Method(typeof(HandItem), "TryUse"), nameof(ItemPrefix), null);
            Patch(harmony, AccessTools.Method(typeof(CL_Button), "Interact", Type.EmptyTypes), nameof(ButtonPrefix), nameof(ButtonPostfix));
            Patch(harmony, AccessTools.Method(typeof(CL_HoldButton), "Update"), nameof(HoldPrefix), nameof(HoldPostfix));
            Patch(harmony, AccessTools.Method(typeof(UT_TriggerZone), "OnTriggerEnter"), nameof(ZonePrefix), null);
            Patch(harmony, AccessTools.Method(typeof(UT_EventZone), "OnTriggerEnter"), nameof(EventZonePrefix), null);
            Patch(harmony, AccessTools.Method(typeof(UT_BranchGenerator), "Generate"), nameof(BranchPrefix), null);
        }

        // ---- racing: reporting -------------------------------------------------------------------------------

        private static void ItemPrefix(HandItem __instance)
        {
            if (!Reporting || __instance == null || __instance.item == null) return;
            if (ItemInteracting != null && ItemInteracting(__instance)) return;
            Report(RunEventKind.ItemUsed, ItemLabel(__instance.item), Vector3.zero);
        }

        private static void ButtonPrefix(CL_Button __instance, out int __state)
        {
            __state = __instance != null && ButtonPresses != null ? ButtonPresses(__instance) : 0;
        }

        private static void ButtonPostfix(CL_Button __instance, int __state)
        {
            if (!Reporting || __instance == null || ButtonPresses == null || ButtonPresses(__instance) <= __state) return;
            Report(RunEventKind.Button, __instance.name, __instance.transform.position);
        }

        private static void HoldPrefix(CL_HoldButton __instance, out bool __state)
        {
            __state = __instance != null && __instance.hasFinished;
        }

        private static void HoldPostfix(CL_HoldButton __instance, bool __state)
        {
            if (__state || __instance == null || !__instance.hasFinished || !Reporting) return;
            Report(RunEventKind.HoldFinished, __instance.name, __instance.transform.position);
        }

        private static void ZonePrefix(UT_TriggerZone __instance, Collider other)
        {
            if (!Reporting || __instance == null || other == null || !IsLocalPlayer(other)) return;
            if (__instance.runOnce && ZoneHasRun != null && ZoneHasRun(__instance)) return;
            ObjectTagger tagger = other.GetComponent<ObjectTagger>();
            if (tagger == null || !tagger.HasTagInList(__instance.entityTags)) return;
            Report(RunEventKind.TriggerZone, __instance.name, __instance.transform.position);
        }

        private static void EventZonePrefix(UT_EventZone __instance, Collider other)
        {
            if (!Reporting || __instance == null || other == null || !__instance.active || !IsLocalPlayer(other)) return;
            if (EventZoneHasRun != null && EventZoneHasRun(__instance)) return;
            if (Array.IndexOf(__instance.triggerTags, other.tag) < 0) return;
            Report(RunEventKind.EventZone, __instance.name, __instance.transform.position);
        }

        private static void BranchPrefix(UT_BranchGenerator __instance)
        {
            if (!Reporting || __instance == null) return;
            Report(RunEventKind.Branch, __instance.name, __instance.transform.position);
        }

        private static bool Reporting
        {
            get { return RaceController.Current != null && RaceController.Current.ReportsRunEvents; }
        }

        private static bool IsLocalPlayer(Collider other)
        {
            ENT_Player me = ENT_Player.playerObject;
            return me != null && other.GetComponentInParent<ENT_Player>() == me;
        }

        private static void Report(byte kind, string name, Vector3 position)
        {
            try
            {
                Vector3 shared = GameBridge.ToShared(position);
                string key = kind + "|" + name + "|" + Mathf.RoundToInt(shared.x) + "," + Mathf.RoundToInt(shared.y) + "," + Mathf.RoundToInt(shared.z);
                float last;
                if (LastSent.TryGetValue(key, out last) && Time.unscaledTime - last < RepeatSeconds) return;
                LastSent[key] = Time.unscaledTime;
                if (LastSent.Count > 500) LastSent.Clear();
                RaceController.Current.SendRunEvent(new RunEvent { Kind = kind, Name = name ?? "", X = shared.x, Y = shared.y, Z = shared.z });
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Could not report a world event: " + e.Message);
            }
        }

        // "Syringe" rather than "Item_Syringe".
        public static string ItemLabel(Item item)
        {
            string name = !string.IsNullOrEmpty(item.itemName) ? item.itemName : item.prefabName ?? "";
            if (name.StartsWith("Item_", StringComparison.Ordinal)) name = name.Substring(5);
            return name.Replace('_', ' ');
        }

        // ---- spectating: replaying ---------------------------------------------------------------------------

        // Sets the event off in this world. False while its object is not there (its level not loaded yet).
        public static bool Replay(OpponentEvent e)
        {
            Vector3 at = new Vector3(e.X, e.Y, e.Z);
            Transform player = ENT_Player.playerObject != null ? ENT_Player.playerObject.transform : null;
            switch (e.Kind)
            {
                case RunEventKind.Button:
                    CL_Button button = Find<CL_Button>(e.Name, at);
                    if (button == null) return false;
                    button.Interact();
                    return true;
                case RunEventKind.HoldFinished:
                    CL_HoldButton hold = Find<CL_HoldButton>(e.Name, at);
                    if (hold == null) return false;
                    if (!hold.hasFinished)
                    {
                        hold.hasFinished = true;
                        hold.finishEvent?.Invoke();
                        hold.stopEvent?.Invoke();
                    }
                    return true;
                case RunEventKind.TriggerZone:
                    UT_TriggerZone zone = Find<UT_TriggerZone>(e.Name, at);
                    if (zone == null) return false;
                    if (!(zone.runOnce && ZoneHasRun != null && ZoneHasRun(zone)))
                    {
                        if (ZoneHasRun != null) ZoneHasRun(zone) = true;
                        zone.onEnter?.Invoke(player);
                    }
                    return true;
                case RunEventKind.EventZone:
                    UT_EventZone eventZone = Find<UT_EventZone>(e.Name, at);
                    if (eventZone == null) return false;
                    if (eventZone.active && !(EventZoneHasRun != null && EventZoneHasRun(eventZone))) eventZone.enterEvent?.Invoke(player);
                    return true;
                case RunEventKind.Branch:
                    UT_BranchGenerator branch = Find<UT_BranchGenerator>(e.Name, at);
                    if (branch == null) return false;
                    branch.Generate();
                    return true;
            }
            return true;
        }

        // The object of that type and name nearest to `shared` (within SameObjectMeters), among those loaded.
        private static T Find<T>(string name, Vector3 shared) where T : Component
        {
            T best = null;
            float bestDistance = SameObjectMeters;
            foreach (T candidate in UnityEngine.Object.FindObjectsByType<T>(FindObjectsSortMode.None))
            {
                if (candidate == null || candidate.name != name) continue;
                float d = Vector3.Distance(GameBridge.ToShared(candidate.transform.position), shared);
                if (d <= bestDistance)
                {
                    best = candidate;
                    bestDistance = d;
                }
            }
            return best;
        }

#if DEBUG
        // Testing: the nearest button or zone to the local player, replayed through the same lookup a spectator uses.
        internal static string ReplayNearestForTest()
        {
            ENT_Player p = ENT_Player.playerObject;
            if (p == null) return "no player";
            Component best = null;
            byte kind = 0;
            float bestDistance = float.MaxValue;
            void Consider(Component c, byte k)
            {
                float d = Vector3.Distance(c.transform.position, p.transform.position);
                if (d < bestDistance) { best = c; kind = k; bestDistance = d; }
            }
            foreach (CL_Button c in UnityEngine.Object.FindObjectsByType<CL_Button>(FindObjectsSortMode.None)) Consider(c, RunEventKind.Button);
            foreach (UT_TriggerZone c in UnityEngine.Object.FindObjectsByType<UT_TriggerZone>(FindObjectsSortMode.None)) Consider(c, RunEventKind.TriggerZone);
            foreach (UT_EventZone c in UnityEngine.Object.FindObjectsByType<UT_EventZone>(FindObjectsSortMode.None)) Consider(c, RunEventKind.EventZone);
            if (best == null) return "nothing found";
            Vector3 shared = GameBridge.ToShared(best.transform.position);
            var e = new OpponentEvent { Kind = kind, Name = best.name, X = shared.x, Y = shared.y, Z = shared.z };
            return "kind " + kind + " '" + best.name + "' " + bestDistance.ToString("0.0") + " m away: replayed " + Replay(e);
        }
#endif

        // ---- plumbing ----------------------------------------------------------------------------------------

        private static void Patch(Harmony harmony, System.Reflection.MethodBase target, string prefix, string postfix)
        {
            if (target == null)
            {
                HookHealth.Fail("world events " + (prefix ?? postfix), "not found in the game; spectators see less of that");
                return;
            }
            try
            {
                harmony.Patch(target,
                    prefix: prefix == null ? null : new HarmonyMethod(typeof(RunEvents), prefix),
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(RunEvents), postfix));
            }
            catch (Exception e)
            {
                HookHealth.Fail("world events " + target.Name, e.Message);
            }
        }

        private static AccessTools.FieldRef<TObject, TField> Ref<TObject, TField>(string field)
        {
            try { return AccessTools.FieldRefAccess<TObject, TField>(field); }
            catch (Exception e) { HookHealth.Fail(typeof(TObject).Name + "." + field, e.Message); return null; }
        }
    }
}
