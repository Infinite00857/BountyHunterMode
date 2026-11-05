// MPH Samus for ULTRAKILL - BepInEx entry point.
// Play as Samus (Metroid Prime Hunters) in ULTRAKILL's levels: enable cheats and turn on "Samus Mode" in the cheat
// menu (SamusModeCheat). The mod reads the player's own MPH ROM (.nds next to this DLL), extracts it once into
// BepInEx/cache/MphSamus, and builds models/HUD art/sounds from it at runtime.
// MPH logic comes from MphRead (MIT, NoneGiven) - see MphCore.
using System;
using System.IO;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using MphSamus.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MphSamus
{
    [BepInPlugin(Guid, "MPH Samus", Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "infinite.mphsamus";
        public const string Version = "1.0.2";

        public static Plugin Instance { get; private set; }
        public static ManualLogSource Log { get; private set; }

        // the "Samus Mode" cheat (ULTRAKILL's cheat menu): Samus replaces V1 while it's on
        public static bool SamusMode { get; set; }

        public enum RomStatus { Missing, Preparing, Ready, Failed }
        public static volatile RomStatus Rom = RomStatus.Missing;
        public static string RomMessage = "";
        public static string DllDir = "";

        // ---- config (BepInEx/config/infinite.mphsamus.cfg) ----
        public static ConfigEntry<float> PlayerScale;     // MPH units -> ULTRAKILL units (0 = from V1's collider)
        public static ConfigEntry<float> DamageDealtScale; // MPH damage -> ULTRAKILL damage
        public static ConfigEntry<float> DamageTakenScale; // ULTRAKILL damage -> MPH energy
        public static ConfigEntry<float> ChargedKnockback;
        public static ConfigEntry<float> SfxVolume;
        public static ConfigEntry<bool> PopupStyleAlways;
        public static ConfigEntry<bool> DebugDump;
        public static ConfigEntry<bool> DebugEvents;
        public static ConfigEntry<string> GunShader;
        public static ConfigEntry<float> ThirdPersonDistance;
        public static ConfigEntry<float> StepHeight;
        public static ConfigEntry<float> SlopeLimit;
        public static ConfigEntry<float> MissileForce;
        public static ConfigEntry<int> MissileCapacity;
        public static ConfigEntry<float> MissileSplashScale;
        public static ConfigEntry<int> MissileSelfDamage;
        public static ConfigEntry<float> SplashSelfDamage;
        public static ConfigEntry<float> JudicatorSlow;
        public static ConfigEntry<float> JudicatorChill;
        public static ConfigEntry<float> ShockCoilZapDamage;
        public static ConfigEntry<float> MagmaulChargedIgnite;
        public static ConfigEntry<float> ChargeBeamSplash;
        public static ConfigEntry<float> ChargeBeamSplashDamage;
        public static ConfigEntry<string> RomPath;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            DllDir = Path.GetDirectoryName(Info.Location) ?? Paths.PluginPath;
            RomPath = Config.Bind("Samus", "RomPath", "",
                "Your Metroid Prime Hunters (USA) .nds file. Empty = look in the ULTRAKILL folder (and next to the mod's DLL); " +
                "if none is found the mod asks with a file picker and remembers the choice here.");
            PlayerScale = Config.Bind("Samus", "PlayerScale", 0f,
                "MPH units -> ULTRAKILL units for Samus (speeds, jump height, gun size). 0 = automatic from V1's collider height.");
            DamageDealtScale = Config.Bind("Balance", "DamageDealtScale", 1f / 12f,
                "MPH beam damage x this = ULTRAKILL damage (Power Beam: 6 uncharged / 40 charged). 1/12 makes a tap 0.5 and a full charge 3.3.");
            DamageTakenScale = Config.Bind("Balance", "DamageTakenScale", 0.75f,
                "ULTRAKILL damage x this = MPH energy lost (V1 has 100 HP, Samus starts with 99 energy and no blood healing).");
            ChargedKnockback = Config.Bind("Balance", "ChargedKnockback", 25000f,
                "Force a full Charge Beam shot pushes enemies with.");
            StepHeight = Config.Bind("Movement", "StepHeight", 0.75f,
                "Ledges up to this tall (ULTRAKILL units; V1 is 3.5 tall) are stepped onto while walking or rolling, and " +
                "floors this far below are snapped down to (stairs, walking down slopes).");
            SlopeLimit = Config.Bind("Movement", "SlopeLimit", 50f,
                "Steepest floor (degrees) Samus stands on; she walks along floors up to this steep at full horizontal speed.");
            MissileForce = Config.Bind("Balance", "MissileForce", 50000f,
                "Force a missile blast pushes enemies with (ULTRAKILL explosions use 50000).");
            MissileSelfDamage = Config.Bind("Balance", "MissileSelfDamage", 5,
                "Energy Samus loses when her own missile blast catches her (charged: double). 0 = off.");
            JudicatorSlow = Config.Bind("Balance", "JudicatorSlow", 0.5f,
                "Judicator hits chill enemies (icy blue): they move at this fraction of their speed.");
            JudicatorChill = Config.Bind("Balance", "JudicatorChill", 3f,
                "Seconds a Judicator hit chills an enemy (charged x1.5, splash x0.5).");
            ShockCoilZapDamage = Config.Bind("Balance", "ShockCoilZapDamage", 8f,
                "Holding the Shock Coil on one enemy for 5 s (like the red nailgun's cable) electrocutes it for this much " +
                "ULTRAKILL damage (sooner if it dies).");
            MagmaulChargedIgnite = Config.Bind("Balance", "MagmaulChargedIgnite", 2.5f,
                "A charged Magmaul blast sets enemies on fire within its splash radius x this (ignition only, no extra damage).");
            SplashSelfDamage = Config.Bind("Balance", "SplashSelfDamage", 1f,
                "Splash weapons (Battlehammer, charged Volt Driver, Judicator, Magmaul) hurt Samus when she's in their splash, as " +
                "in MPH (their splash energy at her distance) x this. 0 = off. Missiles use MissileSelfDamage instead.");
            MissileSplashScale = Config.Bind("Balance", "MissileSplashScale", 1.75f,
                "Missile blast radius x this (MPH: 1.5 units uncharged, 2.25 charged; ULTRAKILL enemies crowd more).");
            ChargeBeamSplash = Config.Bind("Balance", "ChargeBeamSplash", 1.25f,
                "Not MPH: a charged Power Beam shot splashes enemies near what it hits, within this radius in MPH units " +
                "(half for a barely charged shot). 0 = off.");
            ChargeBeamSplashDamage = Config.Bind("Balance", "ChargeBeamSplashDamage", 0.5f,
                "Charged Power Beam splash damage at the center, as a fraction of the shot's damage (falls off to 0 at the edge).");
            MissileCapacity = Config.Bind("Samus", "MissileCapacity", 5,
                "Missiles the launcher holds before expansions (MPH story start: 5; Samus Mode adds all 9 expansions, +10 each).");
            ThirdPersonDistance = Config.Bind("Samus", "ThirdPersonDistance", 1f,
                "F5 third-person view: camera distance multiplier (1 = default over-the-shoulder distance).");
            SfxVolume = Config.Bind("Audio", "SfxVolume", 1f, "MPH sound effect volume (on top of ULTRAKILL's SFX slider).");
            PopupStyleAlways = Config.Bind("UI", "PopupStyleAlways", false,
                "Use the MPH message box for ULTRAKILL's popups even when not playing as Samus.");
            GunShader = Config.Bind("Debug", "GunShader", "",
                "Force a shader by name for MPH models (empty = automatic; the log lists what was found).");
            DebugDump = Config.Bind("Debug", "DumpHierarchy", false,
                "Write the player object hierarchy to BepInEx/LogOutput.log (troubleshooting).");
            DebugEvents = Config.Bind("Debug", "LogEvents", false,
                "Log gameplay events (shots, hits, damage taken, deaths, pickups) to BepInEx/MphSamus.log for bug reports.");

            try
            {
                BepInEx.Logging.Logger.Listeners.Add(new ModLogFile(Path.Combine(Paths.BepInExRootPath, "MphSamus.log")));
            }
            catch (Exception ex)
            {
                Log.LogWarning($"own log file: {ex.Message}");
            }
            Log.LogInfo($"MPH Samus {Version} loaded from {DllDir}");
            try
            {
                new Harmony(Guid).PatchAll(typeof(Plugin).Assembly);
            }
            catch (Exception ex)
            {
                Log.LogError($"Harmony patches failed: {ex}");
            }
            SceneManager.sceneLoaded += OnSceneLoaded;
            // our own hidden, persistent object drives the per-frame work (the BepInEx manager object can be
            // disabled/destroyed by games; ULTRAKILL never called our Update in the first playtest)
            var runner = new GameObject("MphSamus Runner") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(runner);
            runner.AddComponent<Runner>();
            StartRomPreparation();
        }

        // the file picker's choice (main thread): remembered in the config, then prepared
        internal static void UseRom(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) { return; }
            if (Rom == RomStatus.Preparing || Rom == RomStatus.Ready) { return; }
            RomPath.Value = path;
            Log.LogInfo($"ROM chosen: {path}");
            Instance.StartRomPreparation();
        }

        // no usable ROM: ask for one with a file picker (on the main menu once per launch, and whenever Samus Mode is
        // turned on without one)
        internal static void AskForRom()
        {
            if (Rom == RomStatus.Missing || Rom == RomStatus.Failed) { RomPicker.Show(); }
        }
        private static bool _askedOnMenu;

        private void StartRomPreparation()
        {
            // the player's own ROM: the config's RomPath (the file picker's choice), else in the ULTRAKILL folder (next to
            // ULTRAKILL.exe), next to this DLL, or in the BepInEx folder
            string rom = null;
            string chosen = RomPath.Value;
            if (!string.IsNullOrEmpty(chosen))
            {
                if (File.Exists(chosen)) { rom = chosen; }
                else { Log.LogWarning($"RomPath {chosen} doesn't exist: searching the usual places"); }
            }
            foreach (string dir in rom != null ? new string[0] : new[] { Paths.GameRootPath, DllDir, Paths.BepInExRootPath, Paths.PluginPath })
            {
                try { rom = !string.IsNullOrEmpty(dir) && Directory.Exists(dir) ? RomCache.FindRom(dir) : null; }
                catch (Exception ex) { Log.LogWarning($"ROM search in {dir}: {ex.Message}"); }
                if (rom != null) { break; }
            }
            if (rom == null)
            {
                Rom = RomStatus.Missing;
                RomMessage = $"Samus Mode needs your Metroid Prime Hunters (USA) .nds file: choose it in the file picker, or put it in the ULTRAKILL folder ({Paths.GameRootPath})";
                Log.LogWarning(RomMessage);
                return;
            }
            Rom = RomStatus.Preparing;
            string cacheRoot = Path.Combine(Paths.CachePath, "MphSamus");
            var thread = new Thread(() =>
            {
                string error = RomCache.Prepare(rom, cacheRoot, s => Log.LogInfo(s));
                if (error != null)
                {
                    RomMessage = "Couldn't read that ROM (it must be the USA Metroid Prime Hunters .nds): " + error.Split('\n')[0];
                    Rom = RomStatus.Failed;
                    Log.LogError(error);
                    return;
                }
                try
                {
                    SoundBank.Load();
                }
                catch (Exception ex)
                {
                    Log.LogError($"Sound table: {ex}");
                }
                Rom = RomStatus.Ready;
            }) { IsBackground = true, Name = "MphSamus ROM" };
            thread.Start();
        }

        internal static void Event(string msg)
        {
            if (DebugEvents.Value) { Log.LogInfo(msg); }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            Log.LogInfo($"Scene loaded: {scene.name} (SceneHelper: {SceneHelper.CurrentScene}), Samus mode {(SamusMode ? "on" : "off")}");
        }

        internal static void Tick()
        {
            if (Rom == RomStatus.Ready)
            {
                MphAssets.EnsureLoaded();
            }
            RomPicker.Tick();
            if (!_askedOnMenu && SceneHelper.CurrentScene == "Main Menu" && (Rom == RomStatus.Missing || Rom == RomStatus.Failed))
            {
                _askedOnMenu = true;
                AskForRom();
            }
            SamusController.TryAttach();
        }

        private sealed class Runner : MonoBehaviour
        {
            private bool _said;
            private float _errorCooldown;

            private void Update()
            {
                if (!_said)
                {
                    _said = true;
                    Log.LogInfo("runner alive");
                }
                try
                {
                    Tick();
                }
                catch (Exception ex)
                {
                    // log once every few seconds instead of every frame
                    if (Time.unscaledTime >= _errorCooldown)
                    {
                        _errorCooldown = Time.unscaledTime + 5;
                        Log.LogError($"Tick: {ex}");
                    }
                }
            }
        }

        // copies this mod's log lines to BepInEx/MphSamus.log
        private sealed class ModLogFile : ILogListener
        {
            private readonly StreamWriter _w;
            public ModLogFile(string path)
            {
                _w = new StreamWriter(path, false) { AutoFlush = true };
            }
            public void LogEvent(object sender, LogEventArgs e)
            {
                if (e.Source != null && e.Source.SourceName == "MPH Samus")
                {
                    lock (_w) { _w.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{e.Level}] {e.Data}"); }
                }
            }
            public void Dispose() { _w.Dispose(); }
        }
    }
}
