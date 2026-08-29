using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Assets.Scripts.Objects;
using IZCode.Mod.Diagnostics;
using IZCode.Mod.Patches;
using IZLang.Vm;
using UnityEngine;

namespace IZCode.Mod
{
    /// <summary>
    /// The mod's entry point.
    ///
    /// It uses the standard StationeersLaunchPad entry point: a MonoBehaviour with an
    /// <c>OnLoaded</c> method taking at least one parameter. We do not derive from
    /// <c>BaseUnityPlugin</c> so we do not depend on BepInEx APIs - LaunchPad calls
    /// this directly, and the mod ends up with a smaller dependency surface.
    /// </summary>
    public sealed class IZCodePlugin : MonoBehaviour
    {
        public const string ModId = "izcode";
        public const string ModName = "IZCode";

        private const string HarmonyId = "com.stationeers.izcode";

        private static Harmony? _harmony;

        /// <summary>
        /// IZ instructions per tick. Well above IC10's 128 because one IZ line turns
        /// into several bytecode instructions - the goal is parity of useful work per
        /// tick, not of instruction count.
        /// </summary>
        public static int OpsPerTick { get; private set; } = IZLimits.DefaultOpsPerTick;

        /// <summary>
        /// The StationeersLaunchPad entry point.
        ///
        /// The parameter is not decoration: LaunchPad looks for an <c>OnLoaded</c> on a
        /// <c>MonoBehaviour</c> and <b>discards the method when it has no parameters at
        /// all</b> (<c>DefaultEntrypoint.Parse</c>: <c>if (parameters.Length ==
        /// 0) return null;</c>). An empty <c>OnLoaded()</c> raises no error - it gives
        /// "Found 0 Entrypoints" in the log and a loaded mod that never runs.
        ///
        /// The accepted types are <c>List&lt;Assembly&gt;</c>,
        /// <c>List&lt;GameObject&gt;</c>, <c>ConfigFile</c> and <c>ModData</c>, in any
        /// order. We ask for the assembly list because it is the only one that answers
        /// something useful: whether IZLang.dll came along.
        /// </summary>
        public void OnLoaded(List<Assembly> assemblies)
        {
            Initialize("OnLoaded (StationeersLaunchPad)");

            var names = new StringBuilder();
            foreach (var assembly in assemblies ?? new List<Assembly>())
            {
                if (names.Length > 0) names.Append(", ");
                names.Append(assembly.GetName().Name);
            }
            IZLog.Info(IZLogArea.Load, "mod assemblies: " +
                                       (names.Length > 0 ? names.ToString() : "none"));
        }

        /// <summary>
        /// Safety net: LaunchPad instantiates the component before calling
        /// <c>OnLoaded</c>, so in practice this is what fires first. It also covers any
        /// loader that merely adds the component.
        /// </summary>
        private void Awake()
        {
            Initialize("Awake (component added by the loader)");
        }

        private static bool _initialized;

        /// <summary>
        /// Marks the domain, not this assembly, as having brought IZCode up.
        ///
        /// A static field would not do: when the same mod is installed twice, the game
        /// loads IZCode.dll twice, and each copy gets statics of its own. The domain is
        /// the one thing both copies share.
        /// </summary>
        private const string DomainKey = "izcode.initialized";

        /// <summary>
        /// Brings the mod up. <paramref name="entryPoint"/> goes to the log because both
        /// paths exist, and knowing which one the game came in through already explains
        /// half the loading problems.
        /// </summary>
        private static void Initialize(string entryPoint)
        {
            if (_initialized) return;
            _initialized = true;

            if (RefuseSecondCopy()) return;

            // The first line always comes out, log configuration or not: if it does not
            // show up in Player.log, the mod was not loaded - and in that case the
            // problem is in the loader (BepInEx + StationeersLaunchPad), not here.
            IZLog.Banner("======================================================");
            IZLog.Banner(ModName + " " + AssemblyVersion() + " starting through " + entryPoint);

            // Now the configurable log; everything below here may fail.
            IZLog.LoadConfig();
            IZLog.Banner("log: " + IZLog.Describe());
            IZLog.Banner("log file: " + IZLog.LogPath);
            IZLog.Banner("config file: " + IZLog.ConfigPath);

            try
            {
                ChipAccess.Initialize();
                if (!string.IsNullOrEmpty(ChipAccess.Missing))
                {
                    // It does not abort: the patches still partially work, and the log
                    // points at exactly what the game renamed.
                    IZLog.Warn(IZLogArea.Load, "game members not found (did Stationeers change?): " +
                                               ChipAccess.Missing);
                }

                UI.EditorContext.Initialize();
                if (!string.IsNullOrEmpty(UI.EditorContext.Missing))
                {
                    IZLog.Warn(IZLogArea.Load, "UI members not found; completion and hover may end up " +
                                               "without device context: " + UI.EditorContext.Missing);
                }

                ApplyPatches();
                RegisterCommands();
                ScheduleCatalogScan();

                IZLog.Banner("loaded. Write '" + Runtime.IZChipRuntime.Marker +
                             "' on the first line of a chip to program in IZ.");
                IZLog.Banner("======================================================");
            }
            catch (Exception ex)
            {
                IZLog.Exception(IZLogArea.Load, "failed to load", ex);
                IZLog.Banner("INCOMPLETE LOAD: see the error above. The mod is loaded but may be inert.");
            }
        }

        /// <summary>
        /// Stops a second copy of the mod from loading on top of the first.
        ///
        /// It happens the moment IZCode is installed both from the Workshop and by
        /// hand in the mods folder: the game loads IZCode.dll twice, and the two
        /// copies are two different assemblies with the same class names. The
        /// symptoms are ugly and hard to read - the loader's own entry point fails
        /// with "Object does not match target type", because it finds the method on
        /// one copy and Unity resolves the component to the other - and if it got
        /// past that, every Harmony patch would be applied twice.
        ///
        /// So the first copy in wins and the second says exactly what to do about it.
        /// </summary>
        private static bool RefuseSecondCopy()
        {
            var domain = AppDomain.CurrentDomain;
            if (domain.GetData(DomainKey) == null)
            {
                domain.SetData(DomainKey, Assembly.GetExecutingAssembly().Location ?? "?");
                return false;
            }

            IZLog.Banner("======================================================");
            IZLog.Banner(ModName + " is installed twice, and only the first copy is running.");
            IZLog.Banner("  already loaded: " + (domain.GetData(DomainKey) as string ?? "?"));
            IZLog.Banner("  this copy:      " + Where(Assembly.GetExecutingAssembly()));
            IZLog.Banner("Disable one of them in the mods menu, or delete its folder. Two");
            IZLog.Banner("copies would patch the game twice and program the chip twice over.");
            IZLog.Banner("======================================================");
            return true;
        }

        private static string Where(Assembly assembly)
        {
            try { return assembly.Location; }
            catch { return "?"; }
        }

        /// <summary>
        /// Installs the Harmony patches and counts what actually took.
        ///
        /// The count matters: a patch that does not match throws no exception at all -
        /// it simply does not exist, and the symptom further down the line is "the mod
        /// does nothing", the hardest thing to diagnose without this line in the log.
        /// </summary>
        private static void ApplyPatches()
        {
            _harmony = new Harmony(HarmonyId);
            _harmony.PatchAll(Assembly.GetExecutingAssembly());

            var patched = new List<MethodBase>(_harmony.GetPatchedMethods());
            var sb = new StringBuilder();
            sb.Append(patched.Count).Append(" patched methods");

            foreach (var method in patched)
                sb.Append("\n  - ").Append(method.DeclaringType?.Name).Append('.').Append(method.Name);

            if (patched.Count == 0)
                IZLog.Error(IZLogArea.Load, "no Harmony patch took: the mod is loaded but inert");
            else
                IZLog.Info(IZLogArea.Load, sb.ToString());
        }

        private static void RegisterCommands()
        {
            TryAddCommand(Devices.ExportDevicesCommand.Key, new Devices.ExportDevicesCommand());
            TryAddCommand(LogCommand.Key, new LogCommand());
        }

        private static void TryAddCommand(string key, Util.Commands.CommandBase command)
        {
            try
            {
                Util.Commands.CommandLine.AddCommand(key, command);
                IZLog.Info(IZLogArea.Load, "console command '" + key + "' registered");
            }
            catch (Exception ex)
            {
                // A console command is a convenience; it is not worth failing the load.
                IZLog.Warn(IZLogArea.Load, "could not register '" + key + "': " + ex.Message);
            }
        }

        /// <summary>
        /// The device catalog can only be built after the prefabs exist - the scan
        /// reads state that comes from the asset bundles.
        /// </summary>
        private static void ScheduleCatalogScan()
        {
            try
            {
                if (Prefab.AllPrefabs != null && Prefab.AllPrefabs.Count > 0)
                {
                    // They loaded before the mod came in.
                    IZLog.Debug(IZLogArea.Catalog, "prefabs already loaded: scanning now");
                    LoadCatalog();
                    return;
                }
                IZLog.Debug(IZLogArea.Catalog, "prefabs not loaded yet: scan scheduled");
                Prefab.OnPrefabsLoaded += LoadCatalog;
            }
            catch (Exception ex)
            {
                IZLog.Exception(IZLogArea.Catalog, "could not schedule the device scan", ex);
            }
        }

        private static bool _catalogLoaded;

        private static void LoadCatalog()
        {
            if (_catalogLoaded) return;
            _catalogLoaded = true;

            try
            {
                // It costs a few seconds the first time on each game version; after
                // that it comes from disk.
                var catalog = Devices.CatalogStore.LoadOrScan(CatalogLog);
                IZLog.Info(IZLogArea.Catalog, "device catalog ready: " + catalog.Devices.Count +
                                              " devices in " + Devices.CatalogStore.Folder);
            }
            catch (Exception ex)
            {
                IZLog.Exception(IZLogArea.Catalog, "failed to build the device catalog", ex);
            }
        }

        private static void CatalogLog(string message) => IZLog.Info(IZLogArea.Catalog, message);

        private static string AssemblyVersion()
        {
            try { return Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?"; }
            catch { return "?"; }
        }
    }
}
