using System;
using System.Collections.Generic;
using BepInEx.Bootstrap;
using WKRace.Protocol;

namespace WKRace.Mod
{
    // The other BepInEx plugins loaded in this game, for the match server's mods list (WKSR passes them on). The server
    // decides which mods are approved; a game with one that is not gets no ranked match.
    internal static class ModList
    {
        public static List<ModEntry> Loaded()
        {
            var mods = new List<ModEntry>();
            try
            {
                foreach (KeyValuePair<string, BepInEx.PluginInfo> pair in Chainloader.PluginInfos)
                {
                    BepInEx.BepInPlugin meta = pair.Value != null ? pair.Value.Metadata : null;
                    string id = meta != null && !string.IsNullOrEmpty(meta.GUID) ? meta.GUID : pair.Key;
                    if (string.IsNullOrEmpty(id) || string.Equals(id, PluginInfo.Guid, StringComparison.OrdinalIgnoreCase)) continue;
                    mods.Add(new ModEntry
                    {
                        Id = id,
                        Name = meta != null && !string.IsNullOrEmpty(meta.Name) ? meta.Name : id,
                        Version = meta != null && meta.Version != null ? meta.Version.ToString() : "",
                    });
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Could not list the loaded mods: " + e.Message);
            }
            mods.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            return mods;
        }
    }
}
