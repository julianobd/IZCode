using System;
using System.IO;
using IZCode.Mod.Diagnostics;
using IZLang.Devices;

namespace IZCode.Mod.Devices
{
    /// <summary>
    /// Keeps the catalog on disk and decides when to scan again.
    ///
    /// It lives in the user's folder, not the mod's: a mod coming from the Workshop
    /// may sit in a read-only directory, and the catalog depends on the installation
    /// (mods that add devices change the result).
    /// </summary>
    public static class CatalogStore
    {
        private const string CatalogFileName = "devices.txt";
        private const string JsonFileName = "devices.json";

        public static DeviceCatalog Current { get; private set; } = DeviceCatalog.Empty;

        /// <summary>The same folder as the log and the configuration.</summary>
        public static string Folder => IZPaths.Folder;

        public static string CatalogPath => Path.Combine(Folder, CatalogFileName);
        public static string JsonPath => Path.Combine(Folder, JsonFileName);

        /// <summary>
        /// Loads the catalog from disk; when it does not exist, or was generated on a
        /// different game version, it scans again and writes it out.
        /// </summary>
        public static DeviceCatalog LoadOrScan(Action<string>? log = null)
        {
            string version = CatalogScanner.GetGameVersion();

            var cached = TryLoad(log);
            if (cached != null && !cached.IsEmpty &&
                string.Equals(cached.GameVersion, version, StringComparison.Ordinal))
            {
                log?.Invoke("catalog loaded from disk: " + cached.Devices.Count + " devices");
                Current = cached;
                return cached;
            }

            if (cached != null && !cached.IsEmpty)
                log?.Invoke("the game changed version (" + cached.GameVersion + " -> " + version + "); rescanning");

            var scanned = CatalogScanner.Scan(log);
            Current = scanned;

            Save(scanned, log);
            log?.Invoke("catalog generated: " + scanned.Devices.Count + " devices in " + CatalogPath);
            return scanned;
        }

        private static DeviceCatalog? TryLoad(Action<string>? log)
        {
            try
            {
                if (!File.Exists(CatalogPath)) return null;
                return CatalogFormat.Read(File.ReadAllText(CatalogPath));
            }
            catch (Exception ex)
            {
                // A corrupted file must not stop the game from opening: rescan.
                log?.Invoke("could not read the catalog on disk (" + ex.Message + "); rescanning");
                return null;
            }
        }

        /// <summary>Writes the line format and, alongside it, a JSON file for external use.</summary>
        public static bool Save(DeviceCatalog catalog, Action<string>? log = null)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(CatalogPath, CatalogFormat.Write(catalog));
                File.WriteAllText(JsonPath, CatalogFormat.WriteJson(catalog));
                return true;
            }
            catch (Exception ex)
            {
                log?.Invoke("could not write the catalog: " + ex.Message);
                return false;
            }
        }

        /// <summary>Forces a fresh scan, ignoring what is on disk.</summary>
        public static DeviceCatalog Rescan(Action<string>? log = null)
        {
            var scanned = CatalogScanner.Scan(log);
            Current = scanned;
            Save(scanned, log);
            return scanned;
        }
    }
}
