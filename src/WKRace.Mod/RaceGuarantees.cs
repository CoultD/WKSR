using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using WKRace.Protocol;
using UnityEngine;

namespace WKRace.Mod
{
    // Race-only rules that make every seed playable at speed: certain items, perks and spawns are always present.
    // Every choice the rules make is derived from the race seed, so both racers get exactly the same result.
    internal static class RaceGuarantees
    {
        // Bump whenever a rule changes: it is part of the world fingerprint, so racers on different rules never race.
        // 4: match worlds no longer read the player's save (MatchSave), food rule on the game's new level lists.
        // 5: The Core update: the food rule knows the new Deep Storage levels (spawners by item id, Construct levels).
        public const int RulesVersion = 5;

        // Which of the rules below the current race uses (its curated-seed modifiers). Seeding the perk consoles that
        // have no seed of their own is not a rule: every race needs it for identical worlds.
        public static MatchRules Rules = MatchRules.Ranked;

        private static bool On(MatchRules rule)
        {
            return (Rules & rule) != 0;
        }

        // ---- the rules -----------------------------------------------------------------------------
        // Level names match by prefix, so an interlude name covers its numbered variants; "M1_Silos_Storage_" stands for
        // every level of the Deep Storage sub-region (see InDeepStorage).

        // "At least one of these spawns in each such level" (only where the level has spawn points for it).
        private static readonly SpawnRule[] SpawnRules =
        {
            new SpawnRule("M1_Silos_Storage_", "Item_Food_Bar", MatchRules.DeepStorageFood),
            new SpawnRule("M1_Silos_Storage_", "Item_Pillbottle", MatchRules.DeepStorageFood),
            new SpawnRule("Campaign_Interlude_Silo_To_Pipeworks_01", "Denizen_SlugGrub", MatchRules.GuaranteedItems),
        };

        // "The perk page in this level always offers one of these" (by perk title), or all of them. (Guaranteed perks.)
        // A refresh never brings back a perk the page showed just before: where two are listed it offers the other
        // one, and a perk that was refreshed away is gone until the refresh after.
        private static readonly PerkRule[] PerkRules =
        {
            new PerkRule("M1_Silos_SafeArea_01", App_PerkPage.PerkPageType.unstable, false, "Conditioned Polyphagia", "Metabolic Overdrive"),
            new PerkRule("Campaign_Interlude_Silo_To_Pipeworks_01", App_PerkPage.PerkPageType.regular, false, "Metabolic Stasis"),
            new PerkRule("Campaign_Interlude_Chute_To_Habitation", null, true, "Rabbit DNA", "Pulse Organ"),
            new PerkRule("Campaign_Interlude_Diver_To_Nest_Intro_01", null, true, "Rabbit DNA", "Pulse Organ"),
            new PerkRule("Campaign_Interlude_Abyss_To_Nest_01_SafeArea", null, false, "Rabbit DNA", "Pulse Organ"),
        };

        // Deep Storage food also makes the first Deep Storage level one that has a food bar or pill bottle spot, and
        // keeps the secret room of a level open (its pill is always there, but the entrance is walled up at random).
        // A Deep Storage level is one of the Deep Storage sub-region (since The Core update that includes levels not
        // named M1_Silos_Storage_, such as M1_Silos_Construct_01), or one with that name.
        private const string DeepStorageLevels = "M1_Silos_Storage_";
        private const string DeepStorageName = "Deep Storage";
        private static readonly string[] FoodItems = { "Item_Food_Bar", "Item_Pillbottle" };
        private static readonly string[] SecretRoomWalls = { "Level_Secret_entrace_Blocked", "Level_Secret_Entrance_Blocked" };

        // "A vending machine in one of these levels always sells this item." (Guaranteed items.)
        private static readonly VendingRule[] VendingRules =
        {
            new VendingRule("Item_BlinkEye", "M1_Silos_SafeArea_01", "Campaign_Interlude_Silo_To_Pipeworks_01"),
        };

        private sealed class SpawnRule
        {
            public readonly string Level, Item;
            public readonly MatchRules Modifier;
            public SpawnRule(string level, string item, MatchRules modifier) { Level = level; Item = item; Modifier = modifier; }
        }

        private sealed class PerkRule
        {
            public readonly string Level;
            public readonly App_PerkPage.PerkPageType? PageType;
            // True: the page offers every one of Titles. False: at least one of them.
            public readonly bool All;
            public readonly string[] Titles;
            public PerkRule(string level, App_PerkPage.PerkPageType? pageType, bool all, params string[] titles) { Level = level; PageType = pageType; All = all; Titles = titles; }
        }

        private sealed class VendingRule
        {
            public readonly string Item;
            public readonly string[] Levels;   // in climbing order
            public bool Satisfied;
            public VendingRule(string item, params string[] levels) { Item = item; Levels = levels; }
        }

        // ---- state ----------------------------------------------------------------------------------

        private static readonly Dictionary<int, LevelState> Levels = new Dictionary<int, LevelState>();

        private sealed class LevelState
        {
            public M_Level Level;
            public int LastRollFrame = -1;
            public bool SpawnsDone;
            public bool VendingDone;
        }

        private static readonly AccessTools.FieldRef<OS_Window, OS_Manager> WindowOs = AccessTools.FieldRefAccess<OS_Window, OS_Manager>("os");
        private static readonly AccessTools.FieldRef<App_PerkPage, List<App_PerkPage_Card>> PageCards = AccessTools.FieldRefAccess<App_PerkPage, List<App_PerkPage_Card>>("cards");
        private static readonly AccessTools.FieldRef<UT_SpawnEntityByID, bool> SpawnerSpawning = AccessTools.FieldRefAccess<UT_SpawnEntityByID, bool>("spawning");
        private static readonly MethodInfo VendingUpdateButtons = AccessTools.Method(typeof(ENV_VendingMachine), "UpdateButtons");

        public static void Apply(Harmony harmony)
        {
            Patch(harmony, AccessTools.Method(typeof(UT_SpawnChance), "Check"), null, nameof(AfterSpawnCheck));
            Patch(harmony, AccessTools.Method(typeof(UT_SpawnEntityByID), "Start"), null, nameof(AfterEntitySpawnerStart));
            Patch(harmony, AccessTools.Method(typeof(ENV_VendingMachine), "GenerateOptions"), null, nameof(AfterVendingGenerated));
            Patch(harmony, AccessTools.Method(typeof(App_PerkPage), "GenerateCards"), nameof(BeforePerkCards), nameof(AfterPerkCards));
            Patch(harmony, AccessTools.Method(typeof(M_Subregion), "GetGenerationLevels"), null, nameof(AfterGenerationLevels));
        }

        private static void Patch(Harmony harmony, MethodBase target, string prefix, string postfix)
        {
            if (target == null)
            {
                HookHealth.Fail(prefix ?? postfix, "not found in the game");
                return;
            }
            harmony.Patch(target,
                prefix: prefix == null ? null : new HarmonyMethod(typeof(RaceGuarantees), prefix),
                postfix: postfix == null ? null : new HarmonyMethod(typeof(RaceGuarantees), postfix));
        }

        // Called when a new world starts generating.
        public static void Reset()
        {
            Levels.Clear();
            FoodLevels.Clear();
            foreach (VendingRule rule in VendingRules) rule.Satisfied = false;
        }

        // Runs once per frame while racing. A level's spawn points all roll in the same frame (their Start), so two
        // frames later the outcome is final and the rules can top it up.
        public static void Update()
        {
            if (!RaceRules.InRaceWorld || Levels.Count == 0) return;
            int frame = Time.frameCount;
            foreach (LevelState state in Levels.Values)
            {
                if (state.Level == null || state.LastRollFrame < 0 || frame < state.LastRollFrame + 2) continue;
                if (!state.Level.gameObject.activeInHierarchy) continue;
                if (!state.SpawnsDone)
                {
                    state.SpawnsDone = true;
                    Safe(() => EnforceSpawns(state.Level));
                }
                if (!state.VendingDone)
                {
                    state.VendingDone = true;
                    Safe(() => EnforceVending(state.Level));
                }
            }
        }

        private static LevelState StateFor(M_Level level)
        {
            LevelState state;
            if (!Levels.TryGetValue(level.GetInstanceID(), out state))
            {
                state = new LevelState { Level = level };
                Levels[level.GetInstanceID()] = state;
            }
            return state;
        }

        // ---- spawn points ----------------------------------------------------------------------------

        private static void AfterSpawnCheck(UT_SpawnChance __instance)
        {
            if (!RaceRules.InRaceWorld || __instance == null) return;
            M_Level level = __instance.GetComponentInParent<M_Level>(true);
            if (level == null) return;
            StateFor(level).LastRollFrame = Time.frameCount;
            if (On(MatchRules.DeepStorageFood) && __instance.gameObject.activeSelf && InDeepStorage(level) && IsSecretRoomWall(__instance.gameObject.name))
            {
                __instance.gameObject.SetActive(false);
                Plugin.Log.LogInfo("Guarantee: the secret room in " + level.levelName + " is open");
            }
        }

        // Since The Core update newer levels place items with spawners that spawn an item by its id when they start
        // (UT_SpawnEntityByID, at the game's own seeded chance). The rules count them as spawn points too.
        private static void AfterEntitySpawnerStart(UT_SpawnEntityByID __instance)
        {
            if (!RaceRules.InRaceWorld || __instance == null || !__instance.spawnOnStart) return;
            M_Level level = __instance.GetComponentInParent<M_Level>(true);
            if (level != null) StateFor(level).LastRollFrame = Time.frameCount;
        }

        private static bool IsFoodSpawner(UT_SpawnEntityByID spawner)
        {
            return spawner != null && spawner.spawnOnStart && Array.IndexOf(FoodItems, spawner.idToSpawn) >= 0;
        }

        private static bool InDeepStorage(M_Level level)
        {
            if (level == null) return false;
            if (level.subRegion != null && string.Equals(level.subRegion.subregionName, DeepStorageName, StringComparison.OrdinalIgnoreCase)) return true;
            return Matches(level, DeepStorageLevels);
        }

        private static bool IsSecretRoomWall(string name)
        {
            foreach (string wall in SecretRoomWalls)
                if (name != null && name.StartsWith(wall, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ---- the first Deep Storage level ----------------------------------------------------------------

        private static readonly Dictionary<string, bool> FoodLevels = new Dictionary<string, bool>();

        // The game has just picked a sub-region's levels. In Deep Storage the first of them becomes one with a food bar
        // or pill bottle spot: the first such level that was picked moves to the front, so the same levels are climbed
        // and the height stays the same. When none was picked, the first level is exchanged for one that has a spot
        // (of the same height where there is one). No random numbers are drawn, so the rest of the world is unchanged.
        // Since game 0.62a the subregion returns its start, middle and end levels separately and the region builds the
        // climb from startLevels + midLevels; fullList (used for transitions) is kept in step after the change.
        private static void AfterGenerationLevels(M_Subregion __instance, M_Subregion.GenerationLevelsOutput __result)
        {
            if (!RaceRules.InRaceWorld || !On(MatchRules.DeepStorageFood) || __instance == null || __result.midLevels == null) return;
            Safe(() =>
            {
                if (!FoodFirst(__instance, __result.midLevels, 0, __result.midLevels.Count) || __result.fullList == null) return;
                __result.fullList.Clear();
                if (__result.startLevels != null) __result.fullList.AddRange(__result.startLevels);
                __result.fullList.AddRange(__result.midLevels);
                if (__result.endLevels != null) __result.fullList.AddRange(__result.endLevels);
            });
        }

        // Puts a level with a food spot first among levels[start..end). True if the list changed.
        private static bool FoodFirst(M_Subregion subregion, List<M_Level.LevelAssetHolder> levels, int start, int end)
        {
            if (start < 0 || end - start < 1 || end > levels.Count) return false;

            List<M_Level.LevelAssetHolder> pool = PoolOf(subregion);
            if (!pool.Exists(h => NameOf(h).StartsWith(DeepStorageLevels, StringComparison.OrdinalIgnoreCase))) return false;

            M_Level.LevelAssetHolder first = levels[start];
            if (HasFoodSpot(first)) return false;

            for (int i = start + 1; i < end; i++)
            {
                if (!HasFoodSpot(levels[i])) continue;
                M_Level.LevelAssetHolder moved = levels[i];
                levels.RemoveAt(i);
                levels.Insert(start, moved);
                Plugin.Log.LogInfo("Guarantee: " + NameOf(moved) + " moved to the start of " + subregion.subregionName + " (it has food)");
                return true;
            }

            var candidates = new List<M_Level.LevelAssetHolder>();
            foreach (M_Level.LevelAssetHolder holder in pool)
            {
                if (levels.Contains(holder) || candidates.Exists(c => NameOf(c) == NameOf(holder))) continue;
                // Only levels the game itself could pick here (it reads the level's own spawn settings).
                M_Level candidate = holder.GetLevel();
                if (candidate == null || (candidate.spawnSettings != null && candidate.spawnSettings.GetEffectiveSpawnChance() <= 0f)) continue;
                if (HasFoodSpot(holder)) candidates.Add(holder);
            }
            if (candidates.Count == 0) return false;
            List<M_Level.LevelAssetHolder> sameHeight = candidates.FindAll(c => Mathf.Abs(c.length - first.length) < 0.5f);
            if (sameHeight.Count > 0) candidates = sameHeight;
            candidates.Sort((a, b) => string.CompareOrdinal(NameOf(a), NameOf(b)));
            M_Level.LevelAssetHolder chosen = candidates[Pick("firstlevel/" + subregion.subregionName, candidates.Count)];
            levels[start] = chosen;
            Plugin.Log.LogInfo("Guarantee: " + NameOf(chosen) + " replaces " + NameOf(first) + " at the start of " + subregion.subregionName + " (it has food)");
            return true;
        }

        // One line per world for the log: the Deep Storage level the climb really starts with, and whether the rule
        // above holds for it (the game builds its level list more than once while it generates a world).
        public static void LogFirstLevel()
        {
            if (!RaceRules.InRaceWorld || !On(MatchRules.DeepStorageFood)) return;
            Safe(() =>
            {
                WorldLoader loader = WorldLoader.instance;
                WorldLoader.BranchInfo branch = loader != null ? loader.GetCurrentBranch() : null;
                if (branch == null || branch.levelTracker == null) return;
                foreach (WorldLoader.LevelInfo info in branch.levelTracker)
                {
                    M_Level level = info != null ? info.GetLevel() : null;
                    if (level == null || !InDeepStorage(level)) continue;
                    bool food = LevelHasFoodSpot(level);
                    if (food) Plugin.Log.LogInfo("Guarantee: the first Deep Storage level is " + level.levelName + " (it has food)");
                    else Plugin.Log.LogWarning("Guarantee: the first Deep Storage level is " + level.levelName + " and has no food bar or pill bottle spot");
                    return;
                }
            });
        }

        private static List<M_Level.LevelAssetHolder> PoolOf(M_Subregion subregion)
        {
            var pool = new List<M_Level.LevelAssetHolder>();
            if (subregion.levelGroups != null)
                foreach (M_Subregion.LevelGroup group in subregion.levelGroups)
                    if (group != null && group.levelReferences != null) pool.AddRange(group.levelReferences);
            if (subregion.levelReferences != null) pool.AddRange(subregion.levelReferences);
            pool.RemoveAll(h => h == null);
            return pool;
        }

        private static string NameOf(M_Level.LevelAssetHolder holder)
        {
            M_Level level = holder != null ? holder.GetLevel() : null;
            if (level == null) return holder != null && holder.id != null ? holder.id : "";
            return string.IsNullOrEmpty(level.levelName) ? level.name : level.levelName;
        }

        private static bool HasFoodSpot(M_Level.LevelAssetHolder holder)
        {
            string name = NameOf(holder);
            bool has;
            if (FoodLevels.TryGetValue(name, out has)) return has;
            M_Level level = holder != null ? holder.GetLevel() : null;
            has = level != null && LevelHasFoodSpot(level);
            FoodLevels[name] = has;
            return has;
        }

        // A food bar or pill bottle spot: an item with a spawn chance (older levels) or a spawner of one (newer ones).
        private static bool LevelHasFoodSpot(M_Level level)
        {
            foreach (UT_SpawnChance chance in level.GetComponentsInChildren<UT_SpawnChance>(true))
                if (Array.IndexOf(FoodItems, BaseName(chance.gameObject.name)) >= 0) return true;
            foreach (UT_SpawnEntityByID spawner in level.GetComponentsInChildren<UT_SpawnEntityByID>(true))
                if (IsFoodSpawner(spawner)) return true;
            return false;
        }

        private static void EnforceSpawns(M_Level level)
        {
            foreach (SpawnRule rule in SpawnRules)
            {
                bool where = rule.Level == DeepStorageLevels ? InDeepStorage(level) : Matches(level, rule.Level);
                if (!On(rule.Modifier) || !where) continue;
                // Spawn points of the item: items with a spawn chance, and spawners of it by id (newer levels).
                var candidates = new List<Component>();
                bool present = false;
                foreach (UT_SpawnChance chance in level.GetComponentsInChildren<UT_SpawnChance>(true))
                {
                    if (BaseName(chance.gameObject.name) != rule.Item) continue;
                    Transform parent = chance.transform.parent;
                    if (parent != null && !parent.gameObject.activeInHierarchy) continue;   // a branch of the level that is not in use
                    if (chance.gameObject.activeSelf) present = true;
                    candidates.Add(chance);
                }
                foreach (UT_SpawnEntityByID spawner in level.GetComponentsInChildren<UT_SpawnEntityByID>(true))
                {
                    if (spawner == null || !spawner.spawnOnStart || spawner.idToSpawn != rule.Item) continue;
                    if (!spawner.gameObject.activeInHierarchy) continue;
                    if (spawner.hasSpawned || SpawnerSpawning(spawner)) present = true;
                    candidates.Add(spawner);
                }
                if (present || candidates.Count == 0) continue;

                candidates.Sort((a, b) => string.CompareOrdinal(PathOf(level.transform, a.transform), PathOf(level.transform, b.transform)));
                Component pick = candidates[Pick(level.levelName + "/" + rule.Item, candidates.Count)];
                if (pick is UT_SpawnEntityByID chosenSpawner)
                {
                    chosenSpawner.SpawnCustom(chosenSpawner.idToSpawn);
                }
                else
                {
                    UT_SpawnChance chance = (UT_SpawnChance)pick;
                    chance.gameObject.SetActive(true);
                    chance.onSuccess?.Invoke();
                }
                Plugin.Log.LogInfo("Guarantee: " + rule.Item + " spawned in " + level.levelName + " at " + PathOf(level.transform, pick.transform));
            }
        }

        // ---- vending machines --------------------------------------------------------------------------

        private static void AfterVendingGenerated(ENV_VendingMachine __instance)
        {
            if (!RaceRules.InRaceWorld || __instance == null) return;
            M_Level level = __instance.GetComponentInParent<M_Level>(true);
            if (level != null) StateFor(level).LastRollFrame = Time.frameCount;
        }

        private static void EnforceVending(M_Level level)
        {
            if (!On(MatchRules.GuaranteedItems)) return;
            foreach (VendingRule rule in VendingRules)
            {
                int position = Array.FindIndex(rule.Levels, l => Matches(level, l));
                if (position < 0 || rule.Satisfied) continue;

                var machines = new List<ENV_VendingMachine>();
                foreach (ENV_VendingMachine m in level.GetComponentsInChildren<ENV_VendingMachine>(false))
                {
                    if (m.buttons == null || m.buttons.Length == 0 || FindPurchase(m, rule.Item) == null) continue;
                    machines.Add(m);
                    foreach (ENV_VendingMachine.VendingButton b in m.buttons)
                        if (b != null && b.purchase != null && b.purchase.itemObject != null && b.purchase.itemObject.name == rule.Item) rule.Satisfied = true;
                }
                if (rule.Satisfied)
                {
                    Plugin.Log.LogInfo("Guarantee: " + rule.Item + " already sold in " + level.levelName);
                    continue;
                }
                if (machines.Count == 0) continue;

                // Not here naturally. Earlier levels in the rule may pass it on (decided by the seed); the last one must provide it.
                bool last = position == rule.Levels.Length - 1;
                bool early = false;
#if DEBUG
                early = RaceConfig.DebugGuaranteeEarly.Value;
#endif
                if (!last && !early && Pick(rule.Item + "/defer/" + level.levelName, 2) == 0)
                {
                    Plugin.Log.LogInfo("Guarantee: " + rule.Item + " not sold in " + level.levelName + "; a later level will provide it");
                    continue;
                }

                machines.Sort((a, b) => string.CompareOrdinal(PathOf(level.transform, a.transform), PathOf(level.transform, b.transform)));
                ENV_VendingMachine machine = machines[Pick(rule.Item + "/machine/" + level.levelName, machines.Count)];
                int slot = Pick(rule.Item + "/slot/" + level.levelName, machine.buttons.Length);
                machine.buttons[slot].purchase = FindPurchase(machine, rule.Item);
                VendingUpdateButtons?.Invoke(machine, null);
                rule.Satisfied = true;
                Plugin.Log.LogInfo("Guarantee: " + rule.Item + " stocked in " + level.levelName + " at " + PathOf(level.transform, machine.transform) + " slot " + slot);
            }
        }

        private static ENV_VendingMachine.Purchase FindPurchase(ENV_VendingMachine machine, string item)
        {
            if (machine.purchaseList == null) return null;
            foreach (ENV_VendingMachine.Purchase p in machine.purchaseList)
                if (p != null && p.itemObject != null && p.itemObject.name == item) return p;
            return null;
        }

        // ---- perk pages ------------------------------------------------------------------------------------

        // Perk consoles need no seeding from the mod: since The Core update (Beta 0.70) the game seeds every computer
        // itself from its level's seed and its place in the level (OS_Computer_Interface.rng), the same for everyone.

        // On a refresh: the titles on the cards that are about to be replaced.
        private static void BeforePerkCards(App_PerkPage __instance, bool refresh, out List<string> __state)
        {
            __state = null;
            if (!refresh || !RaceRules.InRaceWorld || __instance == null) return;
            try
            {
                List<App_PerkPage_Card> cards = PageCards(__instance);
                if (cards == null) return;
                __state = new List<string>();
                foreach (App_PerkPage_Card c in cards) if (c != null && c.perk != null && c.perk.title != null) __state.Add(c.perk.title);
            }
            catch (Exception) { __state = null; }
        }

        private static void AfterPerkCards(App_PerkPage __instance, List<string> __state)
        {
            if (!RaceRules.InRaceWorld) return;
            if (On(MatchRules.GuaranteedPerks)) Safe(() => EnforcePerks(__instance, __state));
            Safe(() => LogCards(__instance));
        }

        // One line per dealt perk page, so a race's perk offers can be checked in LogOutput.log.
        private static void LogCards(App_PerkPage page)
        {
            OS_Computer_Interface computer = ComputerOf(page);
            M_Level level = computer != null ? computer.GetComponentInParent<M_Level>(true) : null;
            List<App_PerkPage_Card> cards = PageCards(page);
            if (cards == null) return;
            var titles = new List<string>();
            foreach (App_PerkPage_Card c in cards) if (c != null && c.perk != null) titles.Add(c.perk.title + " (" + c.perk.cost + ")");
            Plugin.Log.LogInfo("Perk page at " + (level != null ? level.levelName : "?") + " [" + page.perkPageType + ", computer " +
                               (computer != null ? computer.computerID + " seed " + computer.rng.inc : "?") + "]: " + string.Join(", ", titles));
        }

        // justShown: on a refresh, the titles the page showed before it (never offered again by a rule), else null.
        private static void EnforcePerks(App_PerkPage page, List<string> justShown)
        {
            OS_Computer_Interface computer = ComputerOf(page);
            M_Level level = computer != null ? computer.GetComponentInParent<M_Level>(true) : null;
            List<App_PerkPage_Card> cards = PageCards(page);
            if (level == null || cards == null || cards.Count == 0) return;

            foreach (PerkRule rule in PerkRules)
            {
                if (!Matches(level, rule.Level) || (rule.PageType.HasValue && rule.PageType.Value != page.perkPageType)) continue;

                // Offerable options: known perks the player can still take, that this page did not just show.
                var options = new List<Perk>();
                foreach (string title in rule.Titles)
                {
                    if (justShown != null && justShown.Exists(t => SameTitle(t, title))) continue;
                    Perk asset = PerkByTitle(title);
                    if (asset == null) { Plugin.Log.LogWarning("Guarantee: no perk titled \"" + title + "\""); continue; }
                    ENT_Player player = ENT_Player.playerObject;
                    Perk owned = player != null ? player.GetPerk(asset.id) : null;
                    if (owned != null && (!asset.canStack || (asset.stackMax > 0 && owned.stackAmount >= asset.stackMax))) continue;
                    // Perks another held perk rules out (or that need one), as the game's own deal does.
                    if (player != null && asset.perkBlacklist != null && player.HasPerkInList(asset.perkBlacklist)) continue;
                    if (asset.perkWhitelist != null && asset.perkWhitelist.Count > 0 && (player == null || !player.HasPerkInList(asset.perkWhitelist))) continue;
                    options.Add(asset);
                }
                // An unstable page deals one kind of perk per card: prefer perks whose card this page has.
                List<Perk> fitting = options.FindAll(p => SlotOf(page, p) < cards.Count);
                if (fitting.Count > 0) options = fitting;
                if (options.Count == 0) continue;

                if (rule.All)
                {
                    foreach (Perk wanted in options)
                        if (!Offered(cards, wanted.title)) PlacePerk(page, cards, wanted, rule, level);
                    continue;
                }
                if (cards.Exists(c => c != null && c.perk != null && Array.Exists(rule.Titles, t => SameTitle(c.perk.title, t)))) continue;
                PlacePerk(page, cards, options[Pick("perk/" + level.levelName + "/" + page.perkPageType, options.Count)], rule, level);
            }
        }

        private static bool Offered(List<App_PerkPage_Card> cards, string title)
        {
            return cards.Exists(c => c != null && c.perk != null && SameTitle(c.perk.title, title));
        }

        // Since The Core update an unstable page deals its cards by kind: card 0 an item perk, card 1 a timed one,
        // card 2 a mixed one (by the perk's tags), any kind after that. -1 for any card.
        private static int SlotOf(App_PerkPage page, Perk perk)
        {
            if (page.perkPageType != App_PerkPage.PerkPageType.unstable || perk == null || perk.tags == null) return -1;
            if (perk.tags.Contains("unstable-item")) return 0;
            if (perk.tags.Contains("unstable-timed")) return 1;
            if (perk.tags.Contains("unstable-mixed")) return 2;
            return -1;
        }

        // Puts a perk on one of the page's cards: on an unstable page the card of the perk's kind, else a card of the
        // same kind (free or paid) where possible, else the last one, and never a card that already shows one of the
        // rule's perks.
        private static void PlacePerk(App_PerkPage page, List<App_PerkPage_Card> cards, Perk chosen, PerkRule rule, M_Level level)
        {
            int target = -1;
            int slot = SlotOf(page, chosen);
            if (slot >= 0 && slot < cards.Count && cards[slot] != null &&
                !(cards[slot].perk != null && Array.Exists(rule.Titles, t => SameTitle(cards[slot].perk.title, t))))
                target = slot;
            for (int pass = 0; pass < 2 && target < 0; pass++)
            {
                for (int i = cards.Count - 1; i >= 0; i--)
                {
                    App_PerkPage_Card c = cards[i];
                    if (c == null) continue;
                    if (c.perk != null && Array.Exists(rule.Titles, t => SameTitle(c.perk.title, t))) continue;
                    if (pass == 0 && (c.perk == null || (c.perk.cost > 0) != (chosen.cost > 0))) continue;
                    target = i;
                    break;
                }
            }
            if (target < 0) return;

            App_PerkPage_Card card = cards[target];
            int costModifier = 0;
            if (card.perk != null)
            {
                Perk replacedAsset = CL_AssetManager.GetPerkAsset(card.perk.id);
                if (replacedAsset != null) costModifier = card.perk.cost - replacedAsset.cost;
            }
            Perk instance = UnityEngine.Object.Instantiate(chosen);
            instance.cost = Mathf.Min(chosen.cost + costModifier, 99);
            card.Initialize(page, instance);
            if (target == 0) page.SubmitCard(card);
            Plugin.Log.LogInfo("Guarantee: perk \"" + chosen.title + "\" offered at " + level.levelName + " (" + page.perkPageType + " page, card " + target + ")");
        }

        private static OS_Computer_Interface ComputerOf(App_PerkPage page)
        {
            OS_Window window = page.GetComponent<OS_Window>();
            OS_Manager os = window != null ? WindowOs(window) : null;
            return os != null ? os.worldInterface : null;
        }

        private static Perk PerkByTitle(string title)
        {
            try
            {
                foreach (Perk p in CL_AssetManager.GetFullCombinedAssetDatabase().perkAssets)
                    if (p != null && SameTitle(p.title, title)) return p;
            }
            catch (Exception) { }
            return null;
        }

        // ---- helpers ------------------------------------------------------------------------------------------

        private static bool Matches(M_Level level, string prefix)
        {
            return level != null && level.levelName != null && level.levelName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static bool SameTitle(string a, string b)
        {
            return a != null && b != null && string.Equals(a.Replace(" ", ""), b.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
        }

        // "Item_Food_Bar.01 (Clone)" -> "Item_Food_Bar"
        private static string BaseName(string name)
        {
            string s = name.Replace("(Clone)", "").Trim();
            int dot = s.LastIndexOf('.');
            if (dot > 0 && dot < s.Length - 1 && char.IsDigit(s[dot + 1])) s = s.Substring(0, dot);
            return s;
        }

        // A number in [0, count) from the race seed and a label: the same for both racers.
        private static int Pick(string label, int count)
        {
            if (count <= 1) return 0;
            return Patches.MixSeed(RaceRules.Seed, "guarantee/" + label) % count;
        }

        private static string PathOf(Transform root, Transform t)
        {
            string path = "";
            for (; t != null && t != root; t = t.parent) path = "/" + t.name + path;
            return path;
        }

        private static void Safe(Action action)
        {
            try { action(); }
            catch (Exception e) { Plugin.Log.LogError("Guarantee failed: " + e); }
        }
    }
}
