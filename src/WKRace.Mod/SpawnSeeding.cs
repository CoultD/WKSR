using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;
using WKRace.Protocol;

namespace WKRace.Mod
{
    // Makes what a level contains (items, creatures, props, events) the same for both racers.
    //
    // The game rolls level contents from Unity's one shared random stream when a level first switches on, which
    // happens when the player gets near it. By then the stream has been drawn from by everything else that ran,
    // so two players get different rolls. Each wrapped method instead rolls from its own stream, seeded by the race
    // seed, the object's level and its place in that level, and the shared stream is put back afterwards.
    internal static class SpawnSeeding
    {
        // Methods that set up or spawn level contents. Only those that actually roll random numbers get wrapped.
        private static readonly HashSet<string> EntryPoints = new HashSet<string>
        {
            "Awake", "Start", "OnEnable", "Initialize", "Setup", "Randomize", "Check",
            "Spawn", "SpawnCustom", "SpawnItem", "SpawnDrops", "SpawnObject", "SpawnPolyps",
        };

        // Never level contents, or seeded by the game itself.
        private static readonly string[] SkippedPrefixes = { "UI_", "App_", "HandItem", "Perk", "ENT_Player", "CL_GameManager", "WorldLoader", "Projectile", "OS_" };

        private static readonly Stack<UnityEngine.Random.State> Saved = new Stack<UnityEngine.Random.State>();
        private static readonly Dictionary<int, int> CallCounts = new Dictionary<int, int>();
        private static readonly StringBuilder KeyBuilder = new StringBuilder(256);
        private static int _wrapped;

        public static bool Enabled = true;
        public static int SeededCalls;

        public static void Apply(Harmony harmony)
        {
            var prefix = new HarmonyMethod(typeof(SpawnSeeding), nameof(Prefix));
            var finalizer = new HarmonyMethod(typeof(SpawnSeeding), nameof(Finalizer));
            var rollCache = new Dictionary<MethodBase, bool>();
            Assembly game = typeof(M_Level).Assembly;

            Type[] types;
            try { types = game.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = Array.FindAll(e.Types, t => t != null); }

            foreach (Type type in types)
            {
                if (!typeof(MonoBehaviour).IsAssignableFrom(type) || type.IsGenericTypeDefinition || Skipped(type.Name)) continue;
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (!EntryPoints.Contains(method.Name) || method.IsAbstract || method.ContainsGenericParameters || method.GetMethodBody() == null) continue;
                    if (!Rolls(method, rollCache, 0)) continue;
                    try
                    {
                        harmony.Patch(method, prefix: prefix, finalizer: finalizer);
                        _wrapped++;
                        if (RaceConfig.Verbose) Plugin.Log.LogDebug("Seeding " + type.Name + "." + method.Name);
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning("Could not seed " + type.Name + "." + method.Name + ": " + e.Message);
                    }
                }
            }
            Plugin.Log.LogInfo("Level contents: " + _wrapped + " spawn methods will roll from the match seed.");
        }

        // Called when a new world starts generating.
        public static void Reset()
        {
            CallCounts.Clear();
            Saved.Clear();
            SiblingCache.Clear();
            SeededCalls = 0;
        }

        private static bool Skipped(string name)
        {
            foreach (string prefix in SkippedPrefixes) if (name.StartsWith(prefix, StringComparison.Ordinal)) return true;
            return false;
        }

        // Does this method draw random numbers, directly or through helpers of its own class (a few calls deep)?
        private static bool Rolls(MethodBase method, Dictionary<MethodBase, bool> cache, int depth)
        {
            bool known;
            if (cache.TryGetValue(method, out known)) return known;
            cache[method] = false;

            bool result = false;
            List<CodeInstruction> body;
            try { body = PatchProcessor.GetOriginalInstructions(method); }
            catch (Exception) { return false; }

            foreach (CodeInstruction instruction in body)
            {
                var callee = instruction.operand as MethodBase;
                if (callee == null || (instruction.opcode != System.Reflection.Emit.OpCodes.Call && instruction.opcode != System.Reflection.Emit.OpCodes.Callvirt)) continue;
                Type owner = callee.DeclaringType;
                if (owner == typeof(UnityEngine.Random) || IsSpawnRoll(callee))
                {
                    result = true;
                    break;
                }
                if (depth < 3 && owner != null && owner.Assembly == method.DeclaringType.Assembly && callee.GetMethodBody() != null &&
                    (owner == method.DeclaringType || owner.IsAssignableFrom(method.DeclaringType)) && Rolls(callee, cache, depth + 1))
                {
                    result = true;
                    break;
                }
            }
            cache[method] = result;
            return result;
        }

        private static bool IsSpawnRoll(MethodBase callee)
        {
            string owner = callee.DeclaringType != null ? callee.DeclaringType.Name : "";
            return (owner == "SpawnSettings" && callee.Name == "RandomCheck") ||
                   (owner == "SpawnTable" && callee.Name == "GetRandomSpawnObject") ||
                   (owner == "SpawnTableAsset" && callee.Name == "GetRandomPrefab");
        }

        private static void Prefix(MethodBase __originalMethod, object __instance, out bool __state)
        {
            __state = false;
            if (!Enabled || !RaceRules.InRaceWorld) return;
            var component = __instance as Component;
            if (component == null) return;
            try
            {
                M_Level level = component.GetComponentInParent<M_Level>(true);
                if (level == null) return;
                long started = Perf.Start();
                int seed = SeedFor(level, component.transform, __originalMethod);
                Perf.Seed(started);
                Saved.Push(UnityEngine.Random.state);
                UnityEngine.Random.InitState(seed);
                SeededCalls++;
                __state = true;
            }
            catch (Exception e)
            {
                if (RaceConfig.Verbose) Plugin.Log.LogDebug("Seeding skipped for " + __originalMethod.Name + ": " + e.Message);
            }
        }

        private static void Finalizer(bool __state)
        {
            if (__state && Saved.Count > 0) UnityEngine.Random.state = Saved.Pop();
        }

        // Race seed + level (name and position in the shared world) + path to the object inside the level + method + how often
        // this exact call has happened before. All of that is the same for both racers no matter when the level loads.
        private static int SeedFor(M_Level level, Transform target, MethodBase method)
        {
            Vector3 p = GameBridge.ToShared(level.transform.position);
            uint hash = Fnv1a.Add(Fnv1a.Offset, level.levelName);
            hash = Fnv1a.Add(hash, Mathf.RoundToInt(p.x));
            hash = Fnv1a.Add(hash, Mathf.RoundToInt(p.y));
            hash = Fnv1a.Add(hash, Mathf.RoundToInt(p.z));
            hash = Fnv1a.Add(hash, PathInLevel(level.transform, target));
            hash = Fnv1a.Add(hash, method.DeclaringType.Name);
            hash = Fnv1a.Add(hash, method.Name);

            int key = unchecked((int)hash);
            int count;
            CallCounts.TryGetValue(key, out count);
            CallCounts[key] = count + 1;
            hash = Fnv1a.Add(hash, count);
            return Patches.Mix(Fnv1a.Add(hash, RaceRules.Seed));
        }

        // "/Props#0/Crate#1/Loot#0": names from the level root down, each with its index among same-named siblings.
        private static string PathInLevel(Transform root, Transform target)
        {
            KeyBuilder.Length = 0;
            for (Transform t = target; t != null && t != root; t = t.parent)
            {
                KeyBuilder.Insert(0, Piece(t));
            }
            return KeyBuilder.ToString();
        }

        // One object's piece of the path. A parent's children are named and numbered in one pass and kept, because a
        // level asks for hundreds of its objects in the frame it switches on, and reading a name from Unity makes a
        // new string every time. The pieces are numbered again whenever the parent's children change.
        private sealed class Siblings
        {
            public int ChildCount;
            public readonly Dictionary<int, KeyValuePair<int, string>> Pieces = new Dictionary<int, KeyValuePair<int, string>>();
        }

        private static readonly Dictionary<int, Siblings> SiblingCache = new Dictionary<int, Siblings>();
        private static readonly Dictionary<string, int> NameCounts = new Dictionary<string, int>();

        private static string Piece(Transform t)
        {
            Transform parent = t.parent;
            if (parent == null) return "/" + t.name + "#0";

            int id = t.GetInstanceID();
            int count = parent.childCount;
            Siblings siblings;
            KeyValuePair<int, string> known;
            if (SiblingCache.TryGetValue(parent.GetInstanceID(), out siblings))
            {
                if (siblings.ChildCount == count && siblings.Pieces.TryGetValue(id, out known) && known.Key == t.GetSiblingIndex()) return known.Value;
            }
            else
            {
                SiblingCache[parent.GetInstanceID()] = siblings = new Siblings();
            }

            siblings.ChildCount = count;
            siblings.Pieces.Clear();
            NameCounts.Clear();
            for (int i = 0; i < count; i++)
            {
                Transform child = parent.GetChild(i);
                string name = child.name;
                int index;
                NameCounts.TryGetValue(name, out index);
                NameCounts[name] = index + 1;
                siblings.Pieces[child.GetInstanceID()] = new KeyValuePair<int, string>(i, "/" + name + "#" + index);
            }
            return siblings.Pieces.TryGetValue(id, out known) ? known.Value : "/" + t.name + "#0";
        }
    }

    // Spawn chances and perk offers that read the player's save (unlocks, campaign wins, XP) read "everything unlocked"
    // during a race, so a new player and a veteran see the same pools. Outside those checks nothing changes.
    internal static class RaceUnlocks
    {
        private static int _depth;
        private static int _perkDepth;
        private static int _savedExperience;

        public static void Apply(Harmony harmony)
        {
            Type settings = AccessTools.Inner(typeof(SpawnTable), "SpawnSettings");
            Wrap(harmony, settings == null ? null : AccessTools.Method(settings, "GetEffectiveSpawnChance"), nameof(Enter), nameof(Exit));
            Wrap(harmony, AccessTools.Method(typeof(Perk), "CanSpawn"), nameof(EnterPerk), nameof(ExitPerk));
            Wrap(harmony, AccessTools.Method(typeof(CL_ProgressionManager), "HasProgressionUnlock"), null, nameof(Unlocked));
            Wrap(harmony, AccessTools.Method(typeof(M_Gamemode), "HasWonGamemode"), null, nameof(Unlocked));
        }

        private static void Wrap(Harmony harmony, MethodBase target, string prefix, string postfixOrFinalizer)
        {
            if (target == null)
            {
                Plugin.Log.LogError("Patch target missing for " + (prefix ?? postfixOrFinalizer) + ". The game may have been updated.");
                return;
            }
            bool isFinalizer = prefix != null;
            harmony.Patch(target,
                prefix: prefix == null ? null : new HarmonyMethod(typeof(RaceUnlocks), prefix),
                postfix: isFinalizer ? null : new HarmonyMethod(typeof(RaceUnlocks), postfixOrFinalizer),
                finalizer: isFinalizer ? new HarmonyMethod(typeof(RaceUnlocks), postfixOrFinalizer) : null);
        }

        private static void Enter(out bool __state)
        {
            __state = RaceRules.InRaceWorld;
            if (__state) _depth++;
        }

        private static void Exit(bool __state)
        {
            if (__state) _depth--;
        }

        private static void EnterPerk(out bool __state)
        {
            __state = RaceRules.InRaceWorld;
            if (!__state) return;
            _depth++;
            if (_perkDepth++ == 0) _savedExperience = CL_ProgressionManager.playerExperience;
            CL_ProgressionManager.playerExperience = int.MaxValue / 2;
        }

        private static void ExitPerk(bool __state)
        {
            if (!__state) return;
            _depth--;
            if (--_perkDepth == 0) CL_ProgressionManager.playerExperience = _savedExperience;
        }

        private static void Unlocked(ref bool __result)
        {
            if (_depth > 0) __result = true;
        }
    }
}
