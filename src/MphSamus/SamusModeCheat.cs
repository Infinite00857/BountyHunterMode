// SamusModeCheat - "Samus Mode" in ULTRAKILL's cheat menu (category METROID PRIME HUNTERS). On: Samus replaces V1 in
// the current level (every MPH weapon, every expansion; ULTRAKILL's FOV and other settings stay). Off: V1 comes back.
// Like ULTRAKILL's own cheats it lasts the level, or every level with "Keep cheats enabled" on.
using System.Collections;
using HarmonyLib;

namespace MphSamus
{
    public sealed class SamusModeCheat : ICheat
    {
        public const string Id = "mphsamus.samus-mode";
        private const string Category = "metroid prime hunters";

        public string LongName => "Samus Mode";
        public string Identifier => Id;
        public string ButtonEnabledOverride => null;
        public string ButtonDisabledOverride => null;
        public string Icon => null;
        public bool DefaultState => false;
        public StatePersistenceMode PersistenceMode => StatePersistenceMode.Persistent;
        public bool IsActive { get; private set; }

        public void Enable(CheatsManager manager)
        {
            IsActive = true;
            Plugin.SamusMode = true;
            if (Plugin.Rom != Plugin.RomStatus.Ready)
            {
                string why = Plugin.Rom == Plugin.RomStatus.Preparing ? "Preparing Metroid Prime Hunters data, one moment..." : Plugin.RomMessage;
                if (MonoSingleton<HudMessageReceiver>.TryGetInstance(out HudMessageReceiver h) && h != null) { h.SendHudMessage(why); }
            }
            Plugin.Log.LogInfo("Samus Mode on");
        }

        public void Disable()
        {
            IsActive = false;
            Plugin.SamusMode = false;
            SamusController c = SamusController.Current;
            if (c != null) { c.Detach(); }
            Plugin.Log.LogInfo("Samus Mode off");
        }

        public IEnumerator Coroutine(CheatsManager manager)
        {
            yield break;
        }

        // registered before ULTRAKILL's own so it builds into the menu (and its key binds) with them
        [HarmonyPatch(typeof(CheatsManager), "Start")]
        private static class Register
        {
            private static void Prefix(CheatsManager __instance)
            {
                try
                {
                    __instance.RegisterCheat(new SamusModeCheat(), Category);
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogError($"Samus Mode cheat: {ex}");
                }
            }
        }
    }
}
