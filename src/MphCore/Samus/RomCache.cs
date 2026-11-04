// RomCache - finds the player's Metroid Prime Hunters ROM, extracts it once (MphRead's extractor) into the
// mod's cache folder, and points MphRead's readers at it. The mod never ships ROM data.
// Every stage logs (an early in-game run failed without saying where).
using System;
using System.IO;
using MphRead;

namespace MphSamus.Core
{
    public static class RomCache
    {
        // ---- TUNABLE CONSTANTS (cache) ----
        public const int CacheVersion = 2;        // bump when the extraction output changes
        private const string MarkerName = "mphsamus_cache.txt";
        private const string CheckFile = @"_archives\localSamus\SamusGun_Model.bin";   // must exist after extraction

        public static string? FileSystemPath { get; private set; }
        public static bool Ready => FileSystemPath != null;

        // The .nds next to the mod DLL (exactly one expected). Null if none.
        public static string? FindRom(string dllDir)
        {
            string[] roms = Directory.GetFiles(dllDir, "*.nds");
            return roms.Length > 0 ? roms[0] : null;
        }

        private static bool IsValidCache(string fs, Action<string>? log)
        {
            try
            {
                if (!Directory.Exists(fs))
                {
                    return false;
                }
                string marker = Path.Combine(fs, MarkerName);
                if (!File.Exists(marker) || File.ReadAllText(marker).Trim() != CacheVersion.ToString())
                {
                    return false;
                }
                return File.Exists(Paths.Combine(fs, CheckFile));
            }
            catch (Exception ex)
            {
                log?.Invoke($"cache check {fs}: {ex.GetType().Name} {ex.Message}");
                return false;
            }
        }

        // Returns null on success, else a message for the player. Safe to call on a worker thread.
        public static string? Prepare(string romPath, string cacheRoot, Action<string>? log = null)
        {
            string stage = "start";
            try
            {
                stage = "create cache folder";
                Directory.CreateDirectory(cacheRoot);
                stage = "check existing cache";
                foreach (string code in new[] { "AMHE1", "AMHE0" })
                {
                    string fs = Path.Combine(cacheRoot, code);
                    if (IsValidCache(fs, log))
                    {
                        Use(fs);
                        log?.Invoke($"ROM cache ready: {fs}");
                        return null;
                    }
                }
                log?.Invoke($"Extracting {Path.GetFileName(romPath)} into {cacheRoot} (first launch only)...");
                stage = "set export path";
                Paths.SetPath("Export", Path.Combine(cacheRoot, "export"));
                stage = "extract";
                var sw = System.Diagnostics.Stopwatch.StartNew();
                string? error = Extract.ExtractTo(romPath, cacheRoot, out string fsPath, log);
                if (error != null)
                {
                    return error;
                }
                log?.Invoke($"extracted in {sw.ElapsedMilliseconds} ms to {fsPath}");
                stage = "verify";
                if (!Directory.Exists(fsPath))
                {
                    return $"ROM extraction wrote nothing to {fsPath} (cache folder has: {string.Join(", ", Directory.GetFileSystemEntries(cacheRoot))})";
                }
                if (!File.Exists(Paths.Combine(fsPath, CheckFile)))
                {
                    return $"ROM extraction incomplete: {CheckFile} missing in {fsPath}";
                }
                stage = "write marker";
                File.WriteAllText(Path.Combine(fsPath, MarkerName), CacheVersion.ToString());
                Use(fsPath);
                log?.Invoke($"ROM cache ready: {fsPath}");
                return null;
            }
            catch (Exception ex)
            {
                return $"ROM extraction failed at '{stage}': {ex}";
            }
        }

        public static void Use(string fsPath)
        {
            string key = Path.GetFileName(fsPath) == "AMHE0" ? Ver.AMHE0 : Ver.AMHE1;
            Paths.SetPath(key, fsPath);
            Paths.MphKey = key;
            FileSystemPath = fsPath;
        }

        public static string Combine(params string[] parts)
        {
            return Paths.Combine(FileSystemPath ?? "", Path.Combine(parts));
        }
    }
}
