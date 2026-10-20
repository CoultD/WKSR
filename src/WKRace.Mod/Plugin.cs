using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WKRace.Mod
{
    internal static class PluginInfo
    {
        public const string Guid = "com.wkrace.mod";
        public const string Name = "White Knuckle Race";
#if DEBUG
        // Test builds outrank a release copy of the same version installed through a mod manager.
        public const string Version = "0.9.6.9999";
#else
        public const string Version = "0.9.6";
#endif
    }

    [BepInPlugin(PluginInfo.Guid, PluginInfo.Name, PluginInfo.Version)]
    [BepInDependency("com.prideunique.imuibepinex")]
    [BepInDependency("com.monksilly.WKLib")]
    public sealed class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;
            RaceConfig.Bind(Config);

            _harmony = new Harmony(PluginInfo.Guid);
            Patches.Apply(_harmony);

            gameObject.AddComponent<RaceController>();
            MenuUi.Init();
            Log.LogInfo(PluginInfo.Name + " " + PluginInfo.Version + " loaded.");
        }

        private void OnDestroy()
        {
            if (_harmony != null) _harmony.UnpatchSelf();
        }
    }
}
