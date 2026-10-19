using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace WKRace.Mod
{
    // Gives the flare gun a fixed place in the inventory at the start of a run.
    //
    // The game drops every item a trinket grants on one spot (with a tiny random nudge) and then pushes overlapping
    // items apart, so the flare gun of the "hunted" trinket ends up somewhere different on every run, often on top of
    // another item. In a match world it is instead put just right of the middle. Every other item stays where the
    // game puts it; one that would overlap the flare gun's place is moved just clear of it.
    internal static class InventoryLayout
    {
        // The item's names as the game has them (prefab "Item_Flaregun"); its ammunition is a different item.
        private const string FlareGun = "flaregun";

        private const float Radius = 1.6f;          // items sit on a sphere of this size around the inventory's root
        private const float RightOfMiddle = 1.5f;   // in units of the game's "keep items this far apart" distance
        private const int GiveUpAfterFrames = 900;

        private static int _pendingSince = -1;
        private static readonly AccessTools.FieldRef<Inventory, List<Inventory.Pouch>> Pouches =
            AccessTools.FieldRefAccess<Inventory, List<Inventory.Pouch>>("pouches");

        public static void Apply(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(M_Gamemode), "StartFreshGamemode");
            if (target == null)
            {
                Plugin.Log.LogError("Patch target missing for the inventory layout. The game may have been updated.");
                return;
            }
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(InventoryLayout), nameof(AfterStartFresh)));
        }

        // The run's trinkets and starting items have just been handed out.
        private static void AfterStartFresh()
        {
            _pendingSince = RaceRules.Active ? Time.frameCount : -1;
        }

        // Once a frame. The flare gun is placed two frames after the items were handed out (so the game's own placing
        // is done), and only in a match world.
        public static void Update()
        {
            if (_pendingSince < 0) return;
            int waited = Time.frameCount - _pendingSince;
            if (waited < 2) return;
            if (!RaceRules.Active || waited > GiveUpAfterFrames) { _pendingSince = -1; return; }
            if (!RaceRules.InRaceWorld || Inventory.instance == null) return;
            _pendingSince = -1;
            try { Arrange(Inventory.instance); }
            catch (Exception e) { Plugin.Log.LogError("Inventory layout failed: " + e); }
        }

        private static void Arrange(Inventory inventory)
        {
            Item flareGun = null;
            var others = new List<Transform>();
            foreach (Item item in inventory.bagItems)
            {
                if (item == null || InPouch(inventory, item)) continue;
                Item_Object held = item.GetDropObject(false);
                if (held == null || !held.gameObject.activeSelf) continue;
                if (flareGun == null && IsFlareGun(item, held)) flareGun = item;
                else others.Add(held.transform);
            }
            if (flareGun == null) return;

            // Far enough from the middle that the game's own "push items apart" leaves it alone.
            float apart = Mathf.Max(0.2f, inventory.minDistance);
            float offset = Mathf.Tan(2f * Mathf.Asin(Mathf.Min(0.95f, apart * RightOfMiddle / (2f * Radius))));
            Vector3 spot = new Vector3(offset, 0f, 1f).normalized * Radius;

            Transform t = flareGun.GetDropObject(false).transform;
            t.localPosition = spot;
            flareGun.bagPosition = spot;

            // Whatever lies on that place moves just clear of it (straight away from the flare gun).
            int moved = 0;
            foreach (Transform other in others)
            {
                Vector3 away = other.localPosition - spot;
                if (away.magnitude >= apart) continue;
                if (away.sqrMagnitude < 0.0001f) away = Vector3.left;
                Vector3 clear = (spot + away.normalized * apart * 1.05f).normalized * Radius;
                other.localPosition = clear;
                Item_Object held = other.GetComponent<Item_Object>();
                if (held != null && held.itemData != null) held.itemData.bagPosition = clear;
                moved++;
            }
            Plugin.Log.LogInfo("Inventory: the flare gun is placed right of the middle" + (moved > 0 ? " (" + moved + " item" + (moved == 1 ? "" : "s") + " moved clear of it)." : "."));
        }

        private static bool IsFlareGun(Item item, Item_Object held)
        {
            return Named(item.itemName) || Named(item.prefabName) || Named(item.itemTag) || Named(held.gameObject.name);
        }

        // "Item_Flaregun", "Flaregun", "Item_Flaregun(Clone)": the flare gun. "Item_Flaregun_Ammo": not.
        private static bool Named(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string s = name.Replace("(Clone)", "").Trim().ToLowerInvariant();
            if (s.StartsWith("item_", StringComparison.Ordinal)) s = s.Substring(5);
            return s.Replace(" ", "").Replace("_", "") == FlareGun;
        }

        private static bool InPouch(Inventory inventory, Item item)
        {
            foreach (Inventory.Pouch pouch in Pouches(inventory) ?? new List<Inventory.Pouch>())
                if (pouch != null && pouch.pouchItems != null && pouch.pouchItems.Contains(item)) return true;
            return false;
        }
    }
}
