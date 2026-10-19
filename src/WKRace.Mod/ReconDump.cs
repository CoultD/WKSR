#if DEBUG
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using UnityEngine;

namespace WKRace.Mod
{
    // Debug builds only: writes what the game decides per level (spawn chances, spawners, vending stock, computers and
    // their perk pages) plus the perk table, so seed guarantees can be designed against real data.
    internal static class ReconDump
    {
        private static readonly List<KeyValuePair<float, M_Level>> Pending = new List<KeyValuePair<float, M_Level>>();
        private static readonly HashSet<int> Done = new HashSet<int>();
        private static string _file;

        public static void Begin(int seed)
        {
            Pending.Clear();
            Done.Clear();
            _perksWritten = false;
            string dir = Path.Combine(Paths.BepInExRootPath, "WKRace-recon");
            Directory.CreateDirectory(dir);
            _file = Path.Combine(dir, seed + "-" + DateTime.Now.ToString("HHmmss") + ".txt");
            Plugin.Log.LogInfo("Recon dump: " + _file);
        }

        public static void WorldReady()
        {
            if (_file == null) return;
            var sb = new StringBuilder();
            sb.AppendLine("#### WORLD");
            WorldLoader loader = WorldLoader.instance;
            if (loader != null && loader.GetCurrentBranch() != null)
            {
                int i = 0;
                foreach (WorldLoader.LevelInfo info in loader.GetCurrentBranch().levelTracker)
                {
                    M_Level l = info.GetLevel();
                    if (l == null) continue;
                    sb.AppendLine(string.Format("{0,3} {1}  region={2} sub={3} y={4:0}", i++, l.levelName,
                        l.region != null ? l.region.regionName : "-", l.subRegion != null ? l.subRegion.subregionName : "-", GameBridge.ToShared(l.transform.position).y));
                }
            }
            File.AppendAllText(_file, sb.ToString());
        }

        private static bool _perksWritten;

        // The asset database fills later than world start, so the perk table is written with the first level instead.
        private static void WritePerks()
        {
            if (_perksWritten) return;
            var perks = new List<Perk>();
            try { perks.AddRange(CL_AssetManager.GetFullCombinedAssetDatabase().perkAssets); } catch (Exception) { }
            if (perks.Count == 0) perks.AddRange(Resources.FindObjectsOfTypeAll<Perk>());
            if (perks.Count == 0) return;
            _perksWritten = true;
            var sb = new StringBuilder();
            sb.AppendLine("#### PERKS (" + perks.Count + ")");
            try
            {
                foreach (Perk p in perks)
                {
                    if (p == null) continue;
                    sb.AppendLine(string.Format("perk id={0} title=\"{1}\" cost={2} pool={3} type={4} competitive={5} tags=[{6}] chance={7}",
                        p.id, p.title, p.cost, p.spawnPool, p.perkType, p.competitive, string.Join(",", p.tags ?? new List<string>()),
                        p.spawnSettings != null ? p.spawnSettings.spawnChance.ToString(CultureInfo.InvariantCulture) : "?"));
                }
            }
            catch (Exception e) { sb.AppendLine("perk table failed: " + e.Message); }
            File.AppendAllText(_file, sb.ToString());
        }

        public static void LevelEntered(M_Level level)
        {
            if (_file != null && level != null && Done.Add(level.GetInstanceID()))
                Pending.Add(new KeyValuePair<float, M_Level>(Time.unscaledTime + 2.5f, level));
        }

        public static void Update()
        {
            for (int i = Pending.Count - 1; i >= 0; i--)
            {
                if (Time.unscaledTime < Pending[i].Key) continue;
                M_Level level = Pending[i].Value;
                Pending.RemoveAt(i);
                if (level == null) continue;
                try { WritePerks(); Write(level); }
                catch (Exception e) { Plugin.Log.LogWarning("Recon failed for " + level.levelName + ": " + e); }
            }
        }

        private static void Write(M_Level level)
        {
            var sb = new StringBuilder();
            Transform root = level.transform;
            sb.AppendLine("#### LEVEL " + level.levelName + "  region=" + (level.region != null ? level.region.regionName : "-") +
                          " sub=" + (level.subRegion != null ? level.subRegion.subregionName : "-"));

            foreach (UT_SpawnChance c in level.GetComponentsInChildren<UT_SpawnChance>(true))
            {
                sb.AppendLine(string.Format("chance {0} p={1} active={2} checkOnStart={3}", PathOf(root, c.transform),
                    c.spawnSettings != null ? c.spawnSettings.spawnChance.ToString(CultureInfo.InvariantCulture) : "?", c.gameObject.activeSelf, c.checkOnStart));
            }

            foreach (MonoBehaviour b in level.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (b == null) continue;
                string type = b.GetType().Name;
                if (type.Contains("Spawner") || type.StartsWith("UT_SpawnEntity", StringComparison.Ordinal) || type == "UT_RandomChildActivator")
                    sb.AppendLine("spawner " + type + " " + PathOf(root, b.transform) + " active=" + b.gameObject.activeInHierarchy + " :: " + Describe(b));
            }

            foreach (ENV_VendingMachine v in level.GetComponentsInChildren<ENV_VendingMachine>(true))
            {
                var stock = new List<string>();
                foreach (ENV_VendingMachine.Purchase p in v.purchaseList) stock.Add(p.name + (p.itemObject != null ? "(" + p.itemObject.name + ")" : "") + " $" + p.price + " c=" + p.chance);
                var shown = new List<string>();
                if (v.buttons != null) foreach (ENV_VendingMachine.VendingButton bt in v.buttons) shown.Add(bt.purchase != null ? bt.purchase.name : "-");
                sb.AppendLine("vending " + PathOf(root, v.transform) + " random=" + v.randomGeneration + " inOrder=" + v.fillFromPurchaseListInOrder +
                              " stock=[" + string.Join("; ", stock) + "] shown=[" + string.Join(", ", shown) + "]");
            }

            foreach (OS_Computer_Interface pc in level.GetComponentsInChildren<OS_Computer_Interface>(true))
            {
                sb.AppendLine("computer " + PathOf(root, pc.transform) + " id=" + pc.computerID + " facility=" + pc.facilityID + " seed=" + pc.GetSeed() +
                              " disk=" + (pc.desktopData != null ? pc.desktopData.name : "-"));
                if (pc.desktopData != null) DescribeDisk(sb, pc.desktopData, "  ", 0);
            }

            foreach (Item_Object item in level.GetComponentsInChildren<Item_Object>(false))
                sb.AppendLine("item " + PathOf(root, item.transform));
            foreach (GameEntity e in level.GetComponentsInChildren<GameEntity>(false))
                if (e.GetComponent<Item_Object>() == null) sb.AppendLine("entity " + PathOf(root, e.transform));

            File.AppendAllText(_file, sb.ToString());
        }

        private static void DescribeDisk(StringBuilder sb, OS_DiskData disk, string indent, int depth)
        {
            if (depth > 3) return;
            sb.AppendLine(indent + "disk " + disk.name + " type=" + disk.type + " randomSelector=" + disk.useRandomSelector);
            if (disk.files != null) foreach (OS_Filesystem.FileInfo f in disk.files) DescribeFile(sb, f, indent + "  ");
            if (disk.diskDataSelectors != null)
                foreach (OS_DiskData.DiskDataSelector s in disk.diskDataSelectors)
                {
                    sb.AppendLine(indent + "  selector " + s.name);
                    if (s.files != null) foreach (OS_Filesystem.FileInfo f in s.files) DescribeFile(sb, f, indent + "    ");
                }
            if (disk.randomDisks != null) foreach (OS_DiskData d in disk.randomDisks) if (d != null) DescribeDisk(sb, d, indent + "  ", depth + 1);
        }

        private static void DescribeFile(StringBuilder sb, OS_Filesystem.FileInfo f, string indent)
        {
            if (f == null) return;
            OS_Filesystem.FileInfo info = f.fileDataAsset != null ? f.fileDataAsset.fileInfo : f;
            string window = info.windowAsset != null ? info.windowAsset.name : (f.windowAsset != null ? f.windowAsset.name : "-");
            string line = indent + "file \"" + (f.name ?? info.name) + "\" type=" + info.type + " window=" + window + " data=\"" + (info.data ?? f.data ?? "").Replace("\n", " | ") + "\"";
            OS_Window w = info.windowAsset != null ? info.windowAsset : f.windowAsset;
            App_PerkPage page = w != null ? w.GetComponentInChildren<App_PerkPage>(true) : null;
            if (page != null) line += " PERKPAGE type=" + page.perkPageType + " cards=" + page.minCards + "-" + page.maxCards + " tags=[" + string.Join(",", page.requiredPerkTags ?? new List<string>()) + "]";
            sb.AppendLine(line);
            if (info.children != null) foreach (OS_Filesystem.FileInfo c in info.children) DescribeFile(sb, c, indent + "  ");
        }

        // Public fields that say what a spawner can produce.
        private static string Describe(MonoBehaviour b)
        {
            var parts = new List<string>();
            foreach (FieldInfo f in b.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                object v = f.GetValue(b);
                if (v == null) continue;
                if (v is SpawnTable t) parts.Add(f.Name + "=table:" + t.name + "{" + Table(t) + "}");
                else if (v is GameObject g) parts.Add(f.Name + "=" + g.name);
                else if (v is SpawnTable.SpawnSettings s) parts.Add(f.Name + ".p=" + s.spawnChance.ToString(CultureInfo.InvariantCulture));
                else if (v is IList list && f.FieldType.IsGenericType && typeof(UnityEngine.Object).IsAssignableFrom(f.FieldType.GetGenericArguments()[0]))
                {
                    var names = new List<string>();
                    foreach (object o in list) names.Add(o is UnityEngine.Object uo && uo != null ? uo.name : "null");
                    if (names.Count > 0) parts.Add(f.Name + "=[" + string.Join(",", names) + "]");
                }
            }
            return string.Join(" ", parts);
        }

        private static string Table(SpawnTable t)
        {
            var entries = new List<string>();
            if (t.spawnList != null)
                foreach (SpawnTable.SpawnTableAsset a in t.spawnList)
                {
                    var prefabs = new List<string>();
                    if (a.prefabs != null) foreach (GameObject g in a.prefabs) prefabs.Add(g != null ? g.name : "null");
                    entries.Add(a.name + ":" + (a.spawnSettings != null ? a.spawnSettings.spawnChance.ToString(CultureInfo.InvariantCulture) : "?") + "[" + string.Join("/", prefabs) + "]");
                }
            return string.Join("; ", entries);
        }

        // What can be pressed in the levels whose name contains `part`, and what each press sets off: read from the
        // level prefabs, so it works from the main menu.
        public static void DumpEvents(string part)
        {
            string dir = Path.Combine(Paths.BepInExRootPath, "WKRace-recon");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "events-" + part + ".txt");
            var sb = new StringBuilder();
            WKAssetDatabase db = CL_AssetManager.GetFullCombinedAssetDatabase();
            foreach (M_Level.LevelAssetHolder holder in db.levelAssets)
            {
                M_Level level = holder != null ? holder.GetLevel() : null;
                if (level == null || level.levelName == null || level.levelName.IndexOf(part, StringComparison.OrdinalIgnoreCase) < 0) continue;
                sb.AppendLine("#### LEVEL " + level.levelName);
                foreach (MonoBehaviour b in level.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (b == null) continue;
                    var events = new List<string>();
                    for (Type t = b.GetType(); t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                        foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        {
                            if (!typeof(UnityEngine.Events.UnityEventBase).IsAssignableFrom(f.FieldType)) continue;
                            var ev = f.GetValue(b) as UnityEngine.Events.UnityEventBase;
                            if (ev == null) continue;
                            for (int i = 0; i < ev.GetPersistentEventCount(); i++)
                            {
                                UnityEngine.Object target = ev.GetPersistentTarget(i);
                                var c = target as Component;
                                string where = c != null ? PathOf(level.transform, c.transform) : target is GameObject g ? PathOf(level.transform, g.transform) : "?";
                                events.Add(f.Name + " -> " + (target != null ? target.GetType().Name : "null") + "." + ev.GetPersistentMethodName(i) + " @ " + where);
                            }
                        }
                    string name = b.GetType().Name;
                    bool pressable = b is Clickable;
                    string lower = b.gameObject.name.ToLowerInvariant();
                    bool named = lower.Contains("elevator") || lower.Contains("lift") || lower.Contains("button");
                    if (events.Count == 0 && !pressable && !named) continue;
                    sb.AppendLine((pressable ? "PRESS " : "comp  ") + name + " " + PathOf(level.transform, b.transform) + " active=" + b.gameObject.activeSelf);
                    foreach (string e in events) sb.AppendLine("        " + e);
                }
            }
            File.WriteAllText(file, sb.ToString());
            Plugin.Log.LogInfo("Recon events: " + file);
        }

        private static string PathOf(Transform root, Transform t)
        {
            string path = "";
            for (; t != null && t != root; t = t.parent) path = "/" + t.name.Replace("(Clone)", "") + path;
            return path;
        }
    }
}
#endif
