using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Motherboards;
using Assets.Scripts.Objects.Pipes;
using IZLang.Binding;
using IZLang.Devices;

namespace IZCode.Mod.Devices
{
    /// <summary>
    /// Scans the game's prefabs to build the catalog of devices and properties.
    ///
    /// It has to run inside the game: <c>CanLogicRead</c> and <c>CanLogicWrite</c> are
    /// instance methods that query state coming from Unity's asset bundles
    /// (HasOnOffState, HasPowerState and friends). There is no way to deduce that by
    /// reading Assembly-CSharp.dll alone.
    ///
    /// The scan runs once per game version and the result goes to disk.
    /// </summary>
    public static class CatalogScanner
    {
        /// <summary>
        /// Walks <c>Prefab.AllPrefabs</c> and probes every LogicType on every device.
        ///
        /// That is hundreds of prefabs times 358 LogicTypes. It costs a few seconds,
        /// which is acceptable once per game update, on the loading screen.
        /// </summary>
        public static DeviceCatalog Scan(Action<string>? log = null)
        {
            var devices = new List<DeviceInfo>();
            var logicTypes = BuildProbeList(GameEnums.LogicTypeByName);
            var slotTypes = BuildProbeList(GameEnums.LogicSlotTypeByName);

            int skipped = 0;

            foreach (var thing in Prefab.AllPrefabs)
            {
                if (!(thing is ILogicable logicable)) continue;

                try
                {
                    var info = ScanDevice(thing, logicable, logicTypes, slotTypes);
                    // A prefab that exposes no property at all is useless for
                    // completion, and would only make the file bigger.
                    if (info != null) devices.Add(info);
                }
                catch (Exception ex)
                {
                    // One troublesome prefab must not abort the whole catalog.
                    skipped++;
                    log?.Invoke("prefab '" + SafeName(thing) + "' failed during the scan: " + ex.Message);
                }
            }

            devices.Sort((a, b) => string.CompareOrdinal(a.PrefabName, b.PrefabName));

            if (skipped > 0) log?.Invoke(skipped + " prefabs skipped because of scan errors");

            return new DeviceCatalog(devices, GetGameVersion());
        }

        private static DeviceInfo? ScanDevice(Thing thing, ILogicable logicable,
                                              List<KeyValuePair<string, int>> logicTypes,
                                              List<KeyValuePair<string, int>> slotTypes)
        {
            var properties = new List<LogicProperty>();

            foreach (var pair in logicTypes)
            {
                var type = (LogicType)pair.Value;

                var access = LogicAccess.None;
                if (SafeCanRead(logicable, type)) access |= LogicAccess.Read;
                if (SafeCanWrite(logicable, type)) access |= LogicAccess.Write;

                if (access != LogicAccess.None)
                    properties.Add(new LogicProperty(pair.Key, pair.Value, access));
            }

            int slotCount = SafeSlotCount(logicable);
            var slotProperties = new List<SlotProperty>();

            if (slotCount > 0)
            {
                foreach (var pair in slotTypes)
                {
                    // Slot 0 as a sample: in the game the slots of a given device all
                    // expose the same set of properties.
                    if (SafeCanReadSlot(logicable, (LogicSlotType)pair.Value, 0))
                        slotProperties.Add(new SlotProperty(pair.Key, pair.Value));
                }
            }

            if (properties.Count == 0 && slotProperties.Count == 0) return null;

            string prefabName = SafeName(thing);
            if (string.IsNullOrEmpty(prefabName)) return null;

            return new DeviceInfo(
                prefabName,
                thing.PrefabHash,
                SafeDisplayName(thing, prefabName),
                slotCount,
                properties.ToArray(),
                slotProperties.ToArray());
        }

        // ------------------------------------------------------------------
        //  Defensive probing
        // ------------------------------------------------------------------
        //  These methods are virtual and implemented by hundreds of classes. Some of
        //  them assume world state that does not exist on a loose prefab, so any of
        //  them can throw. We treat an exception as "not supported".

        private static bool SafeCanRead(ILogicable logicable, LogicType type)
        {
            try { return logicable.CanLogicRead(type); }
            catch { return false; }
        }

        private static bool SafeCanWrite(ILogicable logicable, LogicType type)
        {
            try { return logicable.CanLogicWrite(type); }
            catch { return false; }
        }

        private static bool SafeCanReadSlot(ILogicable logicable, LogicSlotType type, int slotIndex)
        {
            try { return logicable.CanLogicRead(type, slotIndex); }
            catch { return false; }
        }

        private static int SafeSlotCount(ILogicable logicable)
        {
            try { return Math.Max(0, logicable.TotalSlots); }
            catch { return 0; }
        }

        private static string SafeName(Thing thing)
        {
            try { return thing.PrefabName ?? string.Empty; }
            catch { return string.Empty; }
        }

        private static string SafeDisplayName(Thing thing, string fallback)
        {
            try
            {
                string name = thing.DisplayName;
                return string.IsNullOrEmpty(name) ? fallback : name;
            }
            catch { return fallback; }
        }

        public static string GetGameVersion()
        {
            try { return GameManager.GetGameVersion() ?? string.Empty; }
            catch { return string.Empty; }
        }

        /// <summary>Probe list without the value 0 (LogicType.None is not a property).</summary>
        private static List<KeyValuePair<string, int>> BuildProbeList(IReadOnlyDictionary<string, int> source)
        {
            var list = new List<KeyValuePair<string, int>>(source.Count);
            foreach (var pair in source)
            {
                if (pair.Value == 0) continue;
                list.Add(pair);
            }
            list.Sort((a, b) => a.Value.CompareTo(b.Value));
            return list;
        }
    }
}
