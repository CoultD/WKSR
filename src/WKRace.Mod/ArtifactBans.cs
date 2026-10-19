using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;

namespace WKRace.Mod
{
    // Artifact bans for the race world (see BanMessages). The pool is every artifact any artifact room in the game
    // can offer, read from the level prefabs, so artifacts added by a game update show up without a mod update.
    // The bans then come off every artifact room's list before it draws, which the race seed already makes
    // identical for both racers.
    internal static class ArtifactBans
    {
        private static List<string> _pool;
        private static readonly Dictionary<string, string> Labels = new Dictionary<string, string>();
        private static readonly HashSet<string> Banned = new HashSet<string>();

        public static void Apply(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(ENV_ArtifactDevice), "Start");
            if (target == null)
            {
                Plugin.Log.LogError("Patch target missing for artifact bans. The game may have been updated.");
                return;
            }
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(ArtifactBans), nameof(BeforeDeviceStart)));
        }

        // Null until the game's asset database can be read (it is ready once the game has booted to the menu).
        public static List<string> Pool()
        {
            if (_pool != null) return _pool;
            try
            {
                var names = new List<string>();
                WKAssetDatabase db = CL_AssetManager.GetFullCombinedAssetDatabase();
                if (db == null || db.levelAssets == null || db.levelAssets.Count == 0) return null;
                foreach (M_Level.LevelAssetHolder holder in db.levelAssets)
                {
                    M_Level level = holder != null ? holder.GetLevel() : null;
                    if (level == null) continue;
                    foreach (ENV_ArtifactDevice device in level.GetComponentsInChildren<ENV_ArtifactDevice>(true))
                    {
                        if (device.artifacts == null) continue;
                        foreach (Item_Object item in device.artifacts)
                        {
                            if (item == null || names.Contains(item.name)) continue;
                            names.Add(item.name);
                            string display = item.itemData != null ? item.itemData.itemName : null;
                            if (!string.IsNullOrEmpty(display)) Labels[item.name] = display;
                        }
                    }
                }
                _pool = names;
                Plugin.Log.LogInfo("Artifact pool: " + string.Join(", ", names));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Could not read the artifact pool: " + e.Message);
            }
            return _pool;
        }

        // The game's own item name if known, else a tidied prefab name ("Item_Artifact_Rebar_Return" -> "Rebar Return").
        public static string Label(string item)
        {
            string display;
            if (item != null && Labels.TryGetValue(item, out display)) return display;
            return WKRace.Protocol.RaceFormat.ItemLabel(item);
        }

        public static void Set(IEnumerable<string> items)
        {
            Banned.Clear();
            if (items != null) foreach (string item in items) Banned.Add(item);
        }

        public static void Clear()
        {
            Banned.Clear();
        }

        // Sorted, for the world fingerprint.
        public static IEnumerable<string> Current()
        {
            return Banned.OrderBy(b => b, StringComparer.Ordinal);
        }

        private static void BeforeDeviceStart(ENV_ArtifactDevice __instance)
        {
            try
            {
                if (!RaceRules.InRaceWorld || Banned.Count == 0 || __instance.artifacts == null) return;
                List<Item_Object> kept = __instance.artifacts.Where(a => a != null && !Banned.Contains(a.name)).ToList();
                if (kept.Count == __instance.artifacts.Count) return;
                // Never leave fewer artifacts than pedestals: the game would draw from an empty list.
                if (__instance.artifactSpawners != null && kept.Count < __instance.artifactSpawners.Count)
                    __instance.artifactSpawners = __instance.artifactSpawners.Take(kept.Count).ToList();
                __instance.artifacts = kept;
                Plugin.Log.LogInfo("Artifact bans applied: " + string.Join(", ", Banned) + " (" + kept.Count + " left)");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Artifact bans failed: " + e.Message);
            }
        }
    }
}
