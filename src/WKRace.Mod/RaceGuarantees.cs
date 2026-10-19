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
        public const int RulesVersion = 3;

        // Which of the rules below the current race uses (its curated-seed modifiers). Seeding the perk consoles that
        // have no seed of their own is not a rule: every race needs it for identical worlds.
        public static MatchRules Rules = MatchRules.Ranked;

        private static bool On(MatchRules rule)
        {
            return (Rules & rule) != 0;
        }

        // ---- the rules -----------------------------------------------------------------------------
        // Level names match by prefix, so "M1_Silos_Storage_" covers every Deep Storage level and an interlude name covers its numbered variants.

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
        private const string DeepStorageLevels = "M1_Silos_Storage_";
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
        private static readonly MethodInfo VendingUpdateButtons = AccessTools.Method(typeof(ENV_VendingMachine), "UpdateButtons");

        public static void Apply(Harmony harmony)
        {
            Patch(harmony, AccessTools.Method(typeof(UT_SpawnChance), "Check"), null, nameof(AfterSpawnCheck));
            Patch(harmony, AccessTools.Method(typeof(ENV_VendingMachine), "GenerateOptions"), null, nameof(AfterVendingGenerated));
            Patch(harmony, AccessTools.Method(typeof(App_PerkPage), "Start"), nameof(BeforePerkPageStart), null);
            Patch(harmony, AccessTools.Method(typeof(App_PerkPage), "GenerateCards"), nameof(BeforePerkCards), nameof(AfterPerkCards));
            Patch(harmony, AccessTools.Method(typeof(M_Subregion), "GetGenerationLevels"), null, nameof(AfterGenerationLevels));
        }

        private static void Patch(Harmony harmony, MethodBase target, string prefix, string postfix)
        {
            if (target == null)
            {
                Plugin.Log.LogError("Patch target missing for " + (prefix ?? postfix) + ". The game may have been updated.");
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
            if (On(MatchRules.DeepStorageFood) && __instance.gameObject.activeSelf && Matches(level, DeepStorageLevels) && IsSecretRoomWall(__instance.gameObject.name))
            {
                __instance.gameObject.SetActive(false);
                Plugin.Log.LogInfo("Guarantee: the secret room in " + level.levelName + " is open");
            }
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
        private static void AfterGenerationLevels(M_Subregion __instance, List<M_Level.LevelAssetHolder> __result)
        {
            if (!RaceRules.InRaceWorld || !On(MatchRules.DeepStorageFood) || __instance == null || __result == null) return;
            Safe(() => FoodFirst(__instance, __result));
        }

        private static void FoodFirst(M_Subregion subregion, List<M_Level.LevelAssetHolder> levels)
        {
            int start = subregion.startLevelReferences != null ? subregion.startLevelReferences.Count : 0;
            int end = levels.Count - (subregion.endLevelReferences != null ? subregion.endLevelReferences.Count : 0);
            if (start < 0 || end - start < 1 || end > levels.Count) return;

            List<M_Level.LevelAssetHolder> pool = PoolOf(subregion);
            if (!pool.Exists(h => NameOf(h).StartsWith(DeepStorageLevels, StringComparison.OrdinalIgnoreCase))) return;

            M_Level.LevelAssetHolder first = levels[start];
            if (HasFoodSpot(first)) return;

            for (int i = start + 1; i < end; i++)
            {
                if (!HasFoodSpot(levels[i])) continue;
                M_Level.LevelAssetHolder moved = levels[i];
                levels.RemoveAt(i);
                levels.Insert(start, moved);
                Plugin.Log.LogInfo("Guarantee: " + NameOf(moved) + " moved to the start of " + subregion.subregionName + " (it has food)");
                return;
            }

            var candidates = new List<M_Level.LevelAssetHolder>();
            foreach (M_Level.LevelAssetHolder holder in pool)
            {
                if (levels.Contains(holder) || candidates.Exists(c => NameOf(c) == NameOf(holder))) continue;
                if (holder.spawnSettings != null && holder.spawnSettings.spawnChance <= 0f) continue;
                if (HasFoodSpot(holder)) candidates.Add(holder);
            }
            if (candidates.Count == 0) return;
            List<M_Level.LevelAssetHolder> sameHeight = candidates.FindAll(c => Mathf.Abs(c.length - first.length) < 0.5f);
            if (sameHeight.Count > 0) candidates = sameHeight;
            candidates.Sort((a, b) => string.CompareOrdinal(NameOf(a), NameOf(b)));
            M_Level.LevelAssetHolder chosen = candidates[Pick("firstlevel/" + subregion.subregionName, candidates.Count)];
            levels[start] = chosen;
            Plugin.Log.LogInfo("Guarantee: " + NameOf(chosen) + " replaces " + NameOf(first) + " at the start of " + subregion.subregionName + " (it has food)");
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
                    if (level == null || !Matches(level, DeepStorageLevels)) continue;
                    bool food = false;
                    foreach (UT_SpawnChance chance in level.GetComponentsInChildren<UT_SpawnChance>(true))
                        if (Array.IndexOf(FoodItems, BaseName(chance.gameObject.name)) >= 0) { food = true; break; }
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
            if (level != null)
            {
                foreach (UT_SpawnChance chance in level.GetComponentsInChildren<UT_SpawnChance>(true))
                {
                    if (Array.IndexOf(FoodItems, BaseName(chance.gameObject.name)) < 0) continue;
                    has = true;
                    break;
                }
            }
            FoodLevels[name] = has;
            return has;
        }

        private static void EnforceSpawns(M_Level level)
        {
            foreach (SpawnRule rule in SpawnRules)
            {
                if (!On(rule.Modifier) || !Matches(level, rule.Level)) continue;
                var candidates = new List<UT_SpawnChance>();
                bool present = false;
                foreach (UT_SpawnChance chance in level.GetComponentsInChildren<UT_SpawnChance>(true))
                {
                    if (BaseName(chance.gameObject.name) != rule.Item) continue;
                    Transform parent = chance.transform.parent;
                    if (parent != null && !parent.gameObject.activeInHierarchy) continue;   // a branch of the level that is not in use
                    if (chance.gameObject.activeSelf) present = true;
                    candidates.Add(chance);
                }
                if (present || candidates.Count == 0) continue;

                candidates.Sort((a, b) => string.CompareOrdinal(PathOf(level.transform, a.transform), PathOf(level.transform, b.transform)));
                UT_SpawnChance pick = candidates[Pick(level.levelName + "/" + rule.Item, candidates.Count)];
                pick.gameObject.SetActive(true);
                pick.onSuccess?.Invoke();
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

        // Some perk consoles are never given a seed by their level and would deal cards from an unseeded random
        // generator, so the two racers would see different perks. Give them one derived from the race seed.
        private static void BeforePerkPageStart(App_PerkPage __instance)
        {
            if (!RaceRules.InRaceWorld) return;
            OS_Computer_Interface computer = ComputerOf(__instance);
            if (computer == null || computer.GetSeed() != -1) return;
            M_Level level = computer.GetComponentInParent<M_Level>(true);
            string where = (level != null ? level.levelName + PathOf(level.transform, computer.transform) : computer.name) + "/" + computer.computerID;
            computer.SetSeed(Pick("computer/" + where, int.MaxValue));
        }

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
                               (computer != null ? computer.computerID + " seed " + computer.GetSeed() : "?") + "]: " + string.Join(", ", titles));
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
                    options.Add(asset);
                }
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

        // Puts a perk on one of the page's cards: a card of the same kind (free or paid) where possible, else the last
        // one, and never a card that already shows one of the rule's perks.
        private static void PlacePerk(App_PerkPage page, List<App_PerkPage_Card> cards, Perk chosen, PerkRule rule, M_Level level)
        {
            int target = -1;
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
