#if DEBUG
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx;
using UnityEngine;

namespace WKRace.Mod
{
    // Debug builds only: writes what each level contains so two runs on the same seed can be compared line by line.
    internal static class DebugDump
    {
        private static readonly List<KeyValuePair<float, M_Level>> Pending = new List<KeyValuePair<float, M_Level>>();
        private static string _file;

        public static void Begin(int seed)
        {
            Pending.Clear();
            string dir = Path.Combine(Paths.BepInExRootPath, "WKRace-dumps");
            Directory.CreateDirectory(dir);
            string mode = (SpawnSeeding.Enabled ? "seeded" : "unseeded") + "-perturb" + RaceConfig.DebugPerturbRandom.Value;
            _file = Path.Combine(dir, seed + "-" + mode + "-" + DateTime.Now.ToString("HHmmss") + ".txt");
            Plugin.Log.LogInfo("Dumping level contents to " + _file);
        }

        // Contents are written a few seconds after entering, once the level's objects have run Start.
        public static void LevelEntered(M_Level level)
        {
            if (_file != null && level != null) Pending.Add(new KeyValuePair<float, M_Level>(Time.unscaledTime + 3f, level));
        }

        public static void Update()
        {
            for (int i = Pending.Count - 1; i >= 0; i--)
            {
                if (Time.unscaledTime < Pending[i].Key) continue;
                M_Level level = Pending[i].Value;
                Pending.RemoveAt(i);
                if (level != null) Write(level);
            }
        }

        private static void Write(M_Level level)
        {
            var lines = new List<string>();
            Transform root = level.transform;
            foreach (Item_Object item in level.GetComponentsInChildren<Item_Object>(false))
            {
                lines.Add("item   " + Clean(item.name) + " @ " + Local(root, item.transform.position));
            }
            foreach (GameEntity entity in level.GetComponentsInChildren<GameEntity>(false))
            {
                if (entity.GetComponent<Item_Object>() != null) continue;
                lines.Add("entity " + Clean(entity.name));
            }
            foreach (UT_SpawnChance chance in level.GetComponentsInChildren<UT_SpawnChance>(true))
            {
                lines.Add("chance " + PathOf(root, chance.transform) + " = " + (chance.gameObject.activeSelf ? "on" : "off"));
            }
            lines.Sort(StringComparer.Ordinal);

            var text = new System.Text.StringBuilder();
            text.AppendLine("== " + level.levelName + " (" + lines.Count + " lines, " + SpawnSeeding.SeededCalls + " seeded calls so far)");
            foreach (string line in lines) text.AppendLine(line);
            File.AppendAllText(_file, text.ToString());
        }

        private static string Clean(string name)
        {
            return name.Replace("(Clone)", "").Trim();
        }

        private static string Local(Transform root, Vector3 world)
        {
            Vector3 p = root.InverseTransformPoint(world);
            return string.Format(CultureInfo.InvariantCulture, "{0:0.0},{1:0.0},{2:0.0}", Round(p.x), Round(p.y), Round(p.z));
        }

        // Half-metre steps: items that settle a few centimetres differently still compare equal.
        private static float Round(float v)
        {
            return Mathf.Round(v * 2f) / 2f;
        }

        private static string PathOf(Transform root, Transform t)
        {
            string path = "";
            for (; t != null && t != root; t = t.parent) path = "/" + Clean(t.name) + path;
            return path;
        }
    }
}
#endif
